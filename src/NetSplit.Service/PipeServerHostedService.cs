using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32.SafeHandles;
using NetSplit.Core;

namespace NetSplit.Service;

public sealed class PipeServerHostedService : BackgroundService
{
    private const int MaximumConcurrentConnections = 8;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private readonly NetSplitCoordinator _coordinator;
    private readonly FileLogBuffer _logs;
    private readonly AppPaths _paths;
    private readonly string _pipeName;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Create(false);

    public PipeServerHostedService(
        NetSplitCoordinator coordinator,
        FileLogBuffer logs,
        AppPaths paths)
        : this(coordinator, logs, paths, NamedPipeRpcClient.PipeName)
    {
    }

    public PipeServerHostedService(
        NetSplitCoordinator coordinator,
        FileLogBuffer logs,
        AppPaths paths,
        string pipeName)
    {
        _coordinator = coordinator;
        _logs = logs;
        _paths = paths;
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? NamedPipeRpcClient.PipeName
            : pipeName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var handlers = new HashSet<Task>();
        using var connectionSlots = new SemaphoreSlim(
            MaximumConcurrentConnections,
            MaximumConcurrentConnections);
        _paths.EnsureDirectories();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await connectionSlots.WaitAsync(stoppingToken).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = CreatePipe();
                    await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                    handlers.RemoveWhere(task => task.IsCompleted);
                    handlers.Add(HandleConnectionAndReleaseAsync(
                        pipe,
                        connectionSlots,
                        stoppingToken));
                }
                catch
                {
                    if (pipe is not null)
                    {
                        await pipe.DisposeAsync().ConfigureAwait(false);
                    }

                    connectionSlots.Release();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await Task.WhenAll(handlers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task HandleConnectionAndReleaseAsync(
        NamedPipeServerStream pipe,
        SemaphoreSlim connectionSlots,
        CancellationToken serviceCancellationToken)
    {
        try
        {
            await using var ownedPipe = pipe;
            try
            {
                if (!IsAuthorizedInteractiveClient(pipe))
                {
                    await _logs.WriteAsync(
                        "WARN",
                        "Rejected a Named Pipe client outside the active console session.",
                        serviceCancellationToken).ConfigureAwait(false);
                    return;
                }

                await HandleConnectionAsync(pipe, serviceCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !serviceCancellationToken.IsCancellationRequested)
            {
                await _logs.WriteAsync(
                    "WARN",
                    "Named Pipe request timed out.",
                    serviceCancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
            }
            catch (IOException) when (!serviceCancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (!serviceCancellationToken.IsCancellationRequested)
            {
                await _logs.WriteAsync(
                    "ERROR",
                    $"Named Pipe request failed: {exception.Message}",
                    serviceCancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            connectionSlots.Release();
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        string json;
        using (var readTimeoutSource =
               CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            readTimeoutSource.CancelAfter(IoTimeout);
            json = await RpcWireProtocol.ReadFrameAsync(
                pipe,
                RpcWireProtocol.MaximumRequestBytes,
                readTimeoutSource.Token).ConfigureAwait(false);
        }

        RpcRequest? request = null;
        RpcResponse response;
        try
        {
            request = JsonSerializer.Deserialize<RpcRequest>(json, _jsonOptions)
                ?? throw new InvalidOperationException("请求格式无效。");
            using var commandTimeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            commandTimeoutSource.CancelAfter(CommandTimeout);
            var data = await DispatchAsync(request, commandTimeoutSource.Token).ConfigureAwait(false);
            response = new RpcResponse
            {
                Id = request.Id,
                Success = true,
                Data = data
            };
        }
        catch (Exception exception)
        {
            response = new RpcResponse
            {
                Id = request?.Id ?? Guid.Empty,
                Success = false,
                Data = RpcPayload.Null(),
                Error = exception.Message
            };
        }

        using var writeTimeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writeTimeoutSource.CancelAfter(IoTimeout);
        await RpcWireProtocol.WriteFrameAsync(
            pipe,
            JsonSerializer.Serialize(response, _jsonOptions),
            RpcWireProtocol.MaximumResponseBytes,
            writeTimeoutSource.Token).ConfigureAwait(false);
    }

    private async Task<JsonElement> DispatchAsync(
        RpcRequest request,
        CancellationToken cancellationToken)
    {
        if (!_coordinator.IsReady && !IsReadOnlyDiagnosticCommand(request.Command))
        {
            throw new InvalidOperationException(
                "net-split service initialization has not completed; only diagnostics are available.");
        }

        switch (request.Command)
        {
            case RpcCommands.Discover:
                return RpcPayload.From(_coordinator.DiscoverAdapters());
            case RpcCommands.GetStatus:
                return RpcPayload.From(_coordinator.Status);
            case RpcCommands.GetSettings:
                return RpcPayload.From(_coordinator.ClientSettings);
            case RpcCommands.GetLogs:
                return RpcPayload.From(_logs.Snapshot());
            case RpcCommands.GetDiagnostics:
                return RpcPayload.From(
                    await _coordinator.GetDiagnosticsAsync(cancellationToken)
                        .ConfigureAwait(false));
            case RpcCommands.Validate:
                return RpcPayload.From(
                    await _coordinator.ValidateRuntimeAsync(cancellationToken).ConfigureAwait(false));
            case RpcCommands.UpdateBindings:
                await _coordinator.UpdateBindingsAsync(
                    RequiredPayload<UpdateBindingsRequest>(request),
                    cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.UpdateResidentialProxy:
                await _coordinator.UpdateResidentialProxyAsync(
                    RequiredPayload<UpdateResidentialProxyRequest>(request),
                    cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.AddSubscription:
                await _coordinator.AddSubscriptionAsync(
                    RequiredPayload<SubscriptionInput>(request),
                    cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.RemoveSubscription:
                await _coordinator.RemoveSubscriptionAsync(
                    RequiredPayload<RemoveItemRequest>(request).Id,
                    cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.AddRule:
                await _coordinator.AddRuleAsync(
                    RequiredPayload<CustomRule>(request),
                    cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.RemoveRule:
                await _coordinator.RemoveRuleAsync(
                    RequiredPayload<RemoveItemRequest>(request).Id,
                    cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.Enable:
                await _coordinator.EnableAsync(cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.Disable:
                await _coordinator.DisableAsync(cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.Repair:
                await _coordinator.RepairAsync(cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.Rollback:
                await _coordinator.RollbackAsync(cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.RefreshSubscriptions:
                await _coordinator.RefreshSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.SelectProxy:
                await _coordinator.SelectProxyAsync(
                    RequiredPayload<SelectProxyRequest>(request).Name,
                    cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.SetProxyExitMode:
                await _coordinator.SetProxyExitModeAsync(
                    RequiredPayload<SetProxyExitModeRequest>(request),
                    cancellationToken).ConfigureAwait(false);
                break;
            case RpcCommands.GetTrafficHistory:
                return RpcPayload.From(_coordinator.TrafficHistorySnapshot);
            default:
                throw new InvalidOperationException($"未知命令：{request.Command}");
        }

        return RpcPayload.From(new { ok = true });
    }

    private static bool IsReadOnlyDiagnosticCommand(string command)
    {
        return command is RpcCommands.Discover
            or RpcCommands.GetStatus
            or RpcCommands.GetSettings
            or RpcCommands.GetLogs
            or RpcCommands.GetDiagnostics
            or RpcCommands.GetTrafficHistory;
    }

    private static T RequiredPayload<T>(RpcRequest request)
    {
        return RpcPayload.To<T>(request.Payload)
            ?? throw new InvalidOperationException("请求缺少必要参数。");
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        var serviceIdentity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User;
        if (serviceIdentity is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                serviceIdentity,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            MaximumConcurrentConnections,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            security);
    }

    private bool IsAuthorizedInteractiveClient(NamedPipeServerStream pipe)
    {
        if (File.Exists(_paths.AuthorizedUserSidFile))
        {
            try
            {
                var expectedSid = File.ReadAllText(_paths.AuthorizedUserSidFile).Trim();
                string? clientSid = null;
                pipe.RunAsClient(() =>
                {
                    clientSid = WindowsIdentity.GetCurrent(TokenAccessLevels.Query)
                        .User?
                        .Value;
                });
                return !string.IsNullOrWhiteSpace(expectedSid)
                    && expectedSid.Equals(clientSid, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
        {
            return false;
        }

        var activeSessionId = WTSGetActiveConsoleSessionId();
        if (activeSessionId == uint.MaxValue)
        {
            return false;
        }

        try
        {
            using var clientProcess = Process.GetProcessById(checked((int)clientProcessId));
            return clientProcess.SessionId == checked((int)activeSessionId);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
