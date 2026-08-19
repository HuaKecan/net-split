using System.Diagnostics;
using System.Text.Json;
using NetSplit.Core;

namespace NetSplit.Tray;

internal sealed class StartupRegistrationProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);

    private readonly string _scriptPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "net-split",
        "startup-status.ps1");
    private readonly object _cacheGate = new();
    private StartupProbeResult? _cachedResult;
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;

    public void Invalidate()
    {
        lock (_cacheGate)
        {
            _cacheExpiresAt = DateTimeOffset.MinValue;
        }
    }

    public async Task<StartupProbeResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_cacheGate)
        {
            if (_cachedResult is not null
                && DateTimeOffset.UtcNow < _cacheExpiresAt)
            {
                return _cachedResult;
            }
        }

        var result = await ReadUncachedAsync(cancellationToken).ConfigureAwait(false);
        lock (_cacheGate)
        {
            _cachedResult = result;
            _cacheExpiresAt = DateTimeOffset.UtcNow + CacheDuration;
        }

        return result;
    }

    private async Task<StartupProbeResult> ReadUncachedAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_scriptPath))
        {
            return StartupProbeResult.Unavailable(
                "启动诊断脚本未安装，请重新安装或运行仓库中的 scripts\\repair-startup.ps1。");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(_scriptPath);

        try
        {
            if (!process.Start())
            {
                return StartupProbeResult.Unavailable(
                    "无法启动 Windows PowerShell 读取启动注册状态。");
            }

            using var timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(ProbeTimeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
            {
                return StartupProbeResult.Unavailable(
                    string.IsNullOrWhiteSpace(error)
                        ? "启动诊断脚本没有返回数据。"
                        : error.Trim());
            }

            return ParseOutput(output, process.ExitCode, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            return StartupProbeResult.Unavailable(
                "启动注册检查超时。");
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or JsonException
                or IOException)
        {
            TryTerminate(process);
            return StartupProbeResult.Unavailable(
                $"无法读取启动注册状态：{exception.Message}");
        }
    }

    internal static StartupProbeResult ParseOutput(
        string output,
        int exitCode,
        string error)
    {
        try
        {
            var report = JsonSerializer.Deserialize<StartupStatusReport>(
                output,
                JsonDefaults.Create(false));
            if (report?.Startup is null)
            {
                return StartupProbeResult.Unavailable(
                    "启动诊断脚本返回的数据格式无效。");
            }

            return new StartupProbeResult
            {
                Available = true,
                RegistrationHealthy = report.Startup.RegistrationHealthy,
                Issues = report.Startup.Issues ?? [],
                Service = report.Startup.Service ?? new StartupServiceStatus(),
                TrayTask = report.Startup.TrayTask ?? new StartupTaskStatus(),
                TrayProcess = report.Startup.TrayProcess ?? new StartupProcessStatus(),
                Runtime = report.Runtime ?? new StartupRuntimeStatus(),
                Error = exitCode == 0
                    ? string.Empty
                    : string.IsNullOrWhiteSpace(error)
                        ? "启动注册检查返回失败。"
                        : error.Trim()
            };
        }
        catch (JsonException)
        {
            return StartupProbeResult.Unavailable(
                "启动诊断脚本返回的数据格式无效。");
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The probe is best-effort and must not affect the tray process.
        }
    }

    private sealed record StartupStatusReport
    {
        public StartupStatusData? Startup { get; init; }
        public StartupRuntimeStatus? Runtime { get; init; }
    }

    private sealed record StartupStatusData
    {
        public bool RegistrationHealthy { get; init; }
        public IReadOnlyList<string> Issues { get; init; } = [];
        public StartupServiceStatus? Service { get; init; }
        public StartupTaskStatus? TrayTask { get; init; }
        public StartupProcessStatus? TrayProcess { get; init; }
    }
}

internal sealed record StartupProbeResult
{
    public bool Available { get; init; }
    public bool RegistrationHealthy { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = [];
    public StartupServiceStatus Service { get; init; } = new();
    public StartupTaskStatus TrayTask { get; init; } = new();
    public StartupProcessStatus TrayProcess { get; init; } = new();
    public StartupRuntimeStatus Runtime { get; init; } = new();
    public string Error { get; init; } = string.Empty;

    public static StartupProbeResult Unavailable(string error)
    {
        return new StartupProbeResult
        {
            Available = false,
            Error = error
        };
    }
}

internal sealed record StartupServiceStatus
{
    public bool Exists { get; init; }
    public string State { get; init; } = string.Empty;
    public string StartMode { get; init; } = string.Empty;
    public bool DelayedAutoStart { get; init; }
    public bool ExecutableMatches { get; init; }
}

internal sealed record StartupTaskStatus
{
    public bool Registered { get; init; }
    public string State { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string LogonDelay { get; init; } = string.Empty;
    public string StartWhenAvailable { get; init; } = string.Empty;
    public string RestartCount { get; init; } = string.Empty;
    public string RestartInterval { get; init; } = string.Empty;
    public string LastTaskResult { get; init; } = string.Empty;
}

internal sealed record StartupProcessStatus
{
    public int Count { get; init; }
    public bool Running { get; init; }
}

internal sealed record StartupRuntimeStatus
{
    public bool Reachable { get; init; }
    public string Mode { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool MihomoRunning { get; init; }
    public bool TunEnabled { get; init; }
    public bool DnsEnabled { get; init; }
}
