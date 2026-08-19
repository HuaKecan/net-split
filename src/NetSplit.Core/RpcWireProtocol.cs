using System.Buffers.Binary;
using System.Text;

namespace NetSplit.Core;

public static class RpcWireProtocol
{
    public const int MaximumRequestBytes = 1024 * 1024;
    public const int MaximumResponseBytes = 4 * 1024 * 1024;

    public static async Task<string> ReadFrameAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximumBytes)
        {
            throw new InvalidDataException("RPC message length is invalid.");
        }

        var payload = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        string value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(value);
        if (payload.Length == 0 || payload.Length > maximumBytes)
        {
            throw new InvalidDataException("RPC message length is invalid.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
