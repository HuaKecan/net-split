using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetSplit.Service;

internal static class DnsReadinessProbe
{
    private const string ProbeName = "netsplit-health.invalid";
    private const int MaximumTcpResponseLength = 4096;

    public static async Task<bool> ProbeAsync(
        int port,
        CancellationToken cancellationToken)
    {
        var query = CreateQuery();
        try
        {
            if (!await ProbeTcpAsync(query, port, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            return await ProbeUdpAsync(query, port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is SocketException or IOException)
        {
            return false;
        }
    }

    private static async Task<bool> ProbeTcpAsync(
        byte[] query,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(
            IPAddress.Loopback,
            port,
            cancellationToken).ConfigureAwait(false);

        await using var stream = client.GetStream();
        var request = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(
            request.AsSpan(0, 2),
            checked((ushort)query.Length));
        query.CopyTo(request, 2);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        var lengthBuffer = new byte[2];
        await stream.ReadExactlyAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
        var responseLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
        if (responseLength is < 12 or > MaximumTcpResponseLength)
        {
            return false;
        }

        var response = new byte[responseLength];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        return IsMatchingResponse(query, response);
    }

    private static async Task<bool> ProbeUdpAsync(
        byte[] query,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Connect(IPAddress.Loopback, port);
        await client.SendAsync(query, cancellationToken).ConfigureAwait(false);
        var response = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return response.RemoteEndPoint.Address.Equals(IPAddress.Loopback)
            && response.RemoteEndPoint.Port == port
            && IsMatchingResponse(query, response.Buffer);
    }

    private static byte[] CreateQuery()
    {
        var transactionId = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        var labels = ProbeName.Split('.');
        var queryLength = 12
            + labels.Sum(label => 1 + Encoding.ASCII.GetByteCount(label))
            + 1
            + 4;
        var query = new byte[queryLength];
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(2, 2), 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1);

        var offset = 12;
        foreach (var label in labels)
        {
            var labelBytes = Encoding.ASCII.GetBytes(label);
            query[offset++] = checked((byte)labelBytes.Length);
            labelBytes.CopyTo(query, offset);
            offset += labelBytes.Length;
        }

        query[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(offset, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(offset + 2, 2), 1);
        return query;
    }

    private static bool IsMatchingResponse(
        ReadOnlySpan<byte> query,
        ReadOnlySpan<byte> response)
    {
        if (response.Length < 12
            || BinaryPrimitives.ReadUInt16BigEndian(response[..2])
            != BinaryPrimitives.ReadUInt16BigEndian(query[..2]))
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(2, 2));
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(4, 2));
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(6, 2));
        if ((flags & 0x8000) == 0
            || (flags & 0x0200) != 0
            || (flags & 0x000f) != 0
            || questionCount != 1
            || answerCount == 0)
        {
            return false;
        }

        var offset = 12;
        if (!TryReadName(response, ref offset, out var questionName)
            || !questionName.Equals(ProbeName, StringComparison.OrdinalIgnoreCase)
            || offset + 4 > response.Length
            || BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset, 2)) != 1
            || BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 2, 2)) != 1)
        {
            return false;
        }

        offset += 4;
        for (var index = 0; index < answerCount; index++)
        {
            if (!TryReadName(response, ref offset, out _)
                || offset + 10 > response.Length)
            {
                return false;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset, 2));
            var dnsClass = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 2, 2));
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 8, 2));
            offset += 10;
            if (offset + dataLength > response.Length)
            {
                return false;
            }

            if (type == 1
                && dnsClass == 1
                && dataLength == 4
                && response[offset] == 198
                && response[offset + 1] == 18)
            {
                return true;
            }

            offset += dataLength;
        }

        return false;
    }

    private static bool TryReadName(
        ReadOnlySpan<byte> message,
        ref int offset,
        out string name)
    {
        var labels = new List<string>();
        var cursor = offset;
        var nextOffset = -1;
        var pointerCount = 0;

        while (cursor < message.Length)
        {
            var length = message[cursor];
            if (length == 0)
            {
                cursor++;
                offset = nextOffset >= 0 ? nextOffset : cursor;
                name = string.Join('.', labels);
                return true;
            }

            if ((length & 0xc0) == 0xc0)
            {
                if (cursor + 1 >= message.Length || ++pointerCount > 16)
                {
                    break;
                }

                nextOffset = nextOffset >= 0 ? nextOffset : cursor + 2;
                cursor = ((length & 0x3f) << 8) | message[cursor + 1];
                continue;
            }

            if ((length & 0xc0) != 0
                || length > 63
                || cursor + 1 + length > message.Length
                || labels.Count >= 32)
            {
                break;
            }

            cursor++;
            labels.Add(Encoding.ASCII.GetString(message.Slice(cursor, length)));
            cursor += length;
        }

        name = string.Empty;
        return false;
    }
}
