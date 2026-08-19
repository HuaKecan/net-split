using System.Buffers.Binary;
using NetSplit.Core;

namespace NetSplit.Core.Tests;

public sealed class RpcWireProtocolTests
{
    [Fact]
    public void NullPayloadSerializesAsJsonNull()
    {
        var request = new RpcRequest
        {
            Command = RpcCommands.GetStatus,
            Payload = RpcPayload.Null()
        };

        var json = System.Text.Json.JsonSerializer.Serialize(request, JsonDefaults.Create(false));

        Assert.Contains("\"payload\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoundTripPreservesUtf8Payload()
    {
        await using var stream = new MemoryStream();
        await RpcWireProtocol.WriteFrameAsync(
            stream,
            "net-split 测试",
            RpcWireProtocol.MaximumRequestBytes,
            CancellationToken.None).ConfigureAwait(true);
        stream.Position = 0;

        var value = await RpcWireProtocol.ReadFrameAsync(
            stream,
            RpcWireProtocol.MaximumRequestBytes,
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("net-split 测试", value);
    }

    [Fact]
    public async Task OversizedHeaderIsRejectedBeforePayloadAllocation()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            header,
            RpcWireProtocol.MaximumRequestBytes + 1);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RpcWireProtocol.ReadFrameAsync(
                stream,
                RpcWireProtocol.MaximumRequestBytes,
                CancellationToken.None)).ConfigureAwait(true);
    }
}
