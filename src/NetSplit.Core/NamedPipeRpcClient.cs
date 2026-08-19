using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace NetSplit.Core;

public sealed class NamedPipeRpcClient
{
    public const string PipeName = "net-split-control-v1";

    private readonly string _pipeName;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Create(false);

    public NamedPipeRpcClient(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName;
    }

    public async Task<T?> SendAsync<T>(
        string command,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);

        var request = new RpcRequest
        {
            Command = command,
            Payload = payload is null ? RpcPayload.Null() : RpcPayload.From(payload)
        };
        var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
        await RpcWireProtocol.WriteFrameAsync(
            pipe,
            requestJson,
            RpcWireProtocol.MaximumRequestBytes,
            timeoutSource.Token).ConfigureAwait(false);
        var responseJson = await RpcWireProtocol.ReadFrameAsync(
            pipe,
            RpcWireProtocol.MaximumResponseBytes,
            timeoutSource.Token).ConfigureAwait(false);

        var response = JsonSerializer.Deserialize<RpcResponse>(responseJson, _jsonOptions)
            ?? throw new IOException("net-split 服务返回了无效响应。");
        if (response.Id != request.Id)
        {
            throw new IOException("net-split 服务响应标识不匹配。");
        }

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Error);
        }

        return RpcPayload.To<T>(response.Data);
    }

    public async Task SendAsync(
        string command,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await SendAsync<JsonElement>(command, payload, timeout, cancellationToken).ConfigureAwait(false);
    }
}
