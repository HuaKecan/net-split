using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetSplit.Core;
using NetSplit.Service;

namespace NetSplit.Service.Tests;

public sealed class DnsReadinessProbeTests
{
    [LiveDnsFact]
    public async Task ProbeAcceptsExplicitlyEnabledLiveMihomoDnsListener()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ready = await DnsReadinessProbe.ProbeAsync(
            MihomoConfigGenerator.DnsListenPort,
            cancellation.Token);

        Assert.True(ready);
    }

    [Fact]
    public async Task ProbeAcceptsMatchingTcpAndUdpDnsResponses()
    {
        using var server = new LoopbackDnsServer();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = server.ServeSingleProbeAsync(cancellation.Token);

        var ready = await DnsReadinessProbe.ProbeAsync(
            server.Port,
            cancellation.Token);

        Assert.True(ready);
        await serverTask;
    }

    [Theory]
    [InlineData(DnsResponseKind.MismatchedTransactionId)]
    [InlineData(DnsResponseKind.ServFail)]
    [InlineData(DnsResponseKind.PublicAddress)]
    [InlineData(DnsResponseKind.WrongQuestion)]
    [InlineData(DnsResponseKind.HeaderOnly)]
    public async Task ProbeRejectsInvalidUdpDnsResponse(DnsResponseKind responseKind)
    {
        using var server = new LoopbackDnsServer(udpResponseKind: responseKind);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = server.ServeSingleProbeAsync(cancellation.Token);

        var ready = await DnsReadinessProbe.ProbeAsync(
            server.Port,
            cancellation.Token);

        Assert.False(ready);
        await serverTask;
    }

    [Fact]
    public async Task ProbeHonorsCancellationWhileTcpResponseStalls()
    {
        using var server = new LoopbackDnsServer(stallTcp: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var serverTask = server.ServeSingleProbeAsync(cancellation.Token);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DnsReadinessProbe.ProbeAsync(server.Port, cancellation.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        await ObserveCancellationAsync(serverTask);
    }

    [Fact]
    public async Task ProbeHonorsCancellationWhileUdpResponseStalls()
    {
        using var server = new LoopbackDnsServer(stallUdp: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var serverTask = server.ServeSingleProbeAsync(cancellation.Token);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DnsReadinessProbe.ProbeAsync(server.Port, cancellation.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        await ObserveCancellationAsync(serverTask);
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class LoopbackDnsServer : IDisposable
    {
        private readonly TcpListener _tcpListener;
        private readonly UdpClient _udpClient;
        private readonly DnsResponseKind _udpResponseKind;
        private readonly bool _stallTcp;
        private readonly bool _stallUdp;

        public LoopbackDnsServer(
            DnsResponseKind udpResponseKind = DnsResponseKind.Valid,
            bool stallTcp = false,
            bool stallUdp = false)
        {
            _tcpListener = new TcpListener(IPAddress.Loopback, 0);
            _tcpListener.Start();
            Port = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
            _udpResponseKind = udpResponseKind;
            _stallTcp = stallTcp;
            _stallUdp = stallUdp;
        }

        public int Port { get; }

        public Task ServeSingleProbeAsync(CancellationToken cancellationToken)
        {
            return Task.WhenAll(
                ServeTcpAsync(cancellationToken),
                ServeUdpAsync(cancellationToken));
        }

        public void Dispose()
        {
            _udpClient.Dispose();
            _tcpListener.Stop();
        }

        private async Task ServeTcpAsync(CancellationToken cancellationToken)
        {
            using var client = await _tcpListener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            var lengthBuffer = new byte[2];
            await stream.ReadExactlyAsync(lengthBuffer, cancellationToken);
            var requestLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
            var request = new byte[requestLength];
            await stream.ReadExactlyAsync(request, cancellationToken);
            if (_stallTcp)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            var response = CreateResponse(request, DnsResponseKind.Valid);
            var frame = new byte[response.Length + 2];
            BinaryPrimitives.WriteUInt16BigEndian(
                frame.AsSpan(0, 2),
                checked((ushort)response.Length));
            response.CopyTo(frame, 2);
            await stream.WriteAsync(frame, cancellationToken);
        }

        private async Task ServeUdpAsync(CancellationToken cancellationToken)
        {
            var request = await _udpClient.ReceiveAsync(cancellationToken);
            if (_stallUdp)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            var response = CreateResponse(request.Buffer, _udpResponseKind);
            await _udpClient.SendAsync(
                response,
                request.RemoteEndPoint,
                cancellationToken);
        }

        private static byte[] CreateResponse(
            byte[] request,
            DnsResponseKind responseKind)
        {
            var response = new byte[request.Length + 16];
            request.CopyTo(response, 0);
            response[2] = 0x81;
            response[3] = 0x80;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6, 2), 1);

            var answerOffset = request.Length;
            response[answerOffset] = 0xc0;
            response[answerOffset + 1] = 0x0c;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(answerOffset + 2, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(answerOffset + 4, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(answerOffset + 10, 2), 4);
            response[answerOffset + 12] = 198;
            response[answerOffset + 13] = 18;
            response[answerOffset + 14] = 0;
            response[answerOffset + 15] = 42;

            switch (responseKind)
            {
                case DnsResponseKind.MismatchedTransactionId:
                    response[1] ^= 0xff;
                    break;
                case DnsResponseKind.ServFail:
                    response[3] = 0x82;
                    break;
                case DnsResponseKind.PublicAddress:
                    response[answerOffset + 12] = 1;
                    response[answerOffset + 13] = 1;
                    response[answerOffset + 14] = 1;
                    response[answerOffset + 15] = 1;
                    break;
                case DnsResponseKind.WrongQuestion:
                    response[13] ^= 0x01;
                    break;
                case DnsResponseKind.HeaderOnly:
                    return response[..12].ToArray();
            }

            return response;
        }
    }

    public enum DnsResponseKind
    {
        Valid,
        MismatchedTransactionId,
        ServFail,
        PublicAddress,
        WrongQuestion,
        HeaderOnly
    }
}

public sealed class LiveDnsFactAttribute : FactAttribute
{
    public LiveDnsFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("NETSPLIT_RUN_LIVE_DNS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set NETSPLIT_RUN_LIVE_DNS=1 to probe an already-running local Mihomo DNS listener.";
        }
    }
}
