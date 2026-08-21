using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using NetSplit.Core;

namespace NetSplit.Service;

public interface IMihomoProcessManager : IAsyncDisposable
{
    event EventHandler? Exited;
    bool IsRunning { get; }

    Task<ProcessValidationResult> ValidateAsync(
        SplitRouteSettings settings,
        string configPath,
        CancellationToken cancellationToken);

    Task StartAsync(
        SplitRouteSettings settings,
        CancellationToken cancellationToken,
        ILoopbackPortReservation? portReservation = null);
    Task StopAsync(SplitRouteSettings settings, CancellationToken cancellationToken);
}

public sealed class MihomoProcessManager : IMihomoProcessManager
{
    private readonly AppPaths _paths;
    private readonly FileLogBuffer _logs;
    private readonly IMihomoControllerClient _controller;
    private readonly ILoopbackPortManager _loopbackPorts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _recentOutput = new();

    private Process? _process;
    private WindowsJob? _job;

    internal MihomoProcessManager(
        AppPaths paths,
        FileLogBuffer logs,
        IMihomoControllerClient controller)
        : this(paths, logs, controller, new LoopbackPortManager())
    {
    }

    public MihomoProcessManager(
        AppPaths paths,
        FileLogBuffer logs,
        IMihomoControllerClient controller,
        ILoopbackPortManager loopbackPorts)
    {
        _paths = paths;
        _logs = logs;
        _controller = controller;
        _loopbackPorts = loopbackPorts;
    }

    public event EventHandler? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public async Task<ProcessValidationResult> ValidateAsync(
        SplitRouteSettings settings,
        string configPath,
        CancellationToken cancellationToken)
    {
        TrustedRuntimePolicy.EnsureTrustedExecutable(settings.MihomoPath);
        var startInfo = CreateStartInfo(settings.MihomoPath);
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(_paths.RuntimeDirectory);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Mihomo 配置验证进程。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessValidationResult(false, "Mihomo 配置验证超时。");
        }

        var output = SanitizeValidationOutput(
            string.Join(Environment.NewLine, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false)));
        return new ProcessValidationResult(process.ExitCode == 0, output);
    }

    public async Task StartAsync(
        SplitRouteSettings settings,
        CancellationToken cancellationToken,
        ILoopbackPortReservation? portReservation = null)
    {
        if (portReservation is not null
            && portReservation.Ports != new LoopbackPortSelection(
                settings.ControllerPort,
                settings.MixedPort))
        {
            throw new ArgumentException(
                "Reserved ports do not match the Mihomo settings.",
                nameof(portReservation));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ILoopbackPortReservation? startReservation = portReservation;
        try
        {
            await StopCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            EnsureCoreStartAllowed();
            startReservation ??= _loopbackPorts.ReservePorts(
                settings.ControllerPort,
                settings.MixedPort);
            TrustedRuntimePolicy.EnsureTrustedExecutable(settings.MihomoPath);
            PrepareGeoData(settings.GeoDataDirectory);
            _recentOutput.Clear();

            var startInfo = CreateStartInfo(settings.MihomoPath);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(_paths.RuntimeConfigFile);
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(_paths.RuntimeDirectory);

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += ProcessOnOutputDataReceived;
            process.ErrorDataReceived += ProcessOnErrorDataReceived;
            process.Exited += ProcessOnExited;
            WindowsJob? job = null;
            try
            {
                EnsureCoreStartAllowed();
                startReservation.Release();
                if (!process.Start())
                {
                    throw new InvalidOperationException("无法启动 Mihomo。");
                }

                job = new WindowsJob();
                job.Assign(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await File.WriteAllTextAsync(
                    _paths.MihomoPidFile,
                    process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken).ConfigureAwait(false);

                _job = job;
                _process = process;
                job = null;

                if (!await _controller.WaitUntilReadyAsync(
                        settings,
                        TimeSpan.FromSeconds(15),
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(BuildReadinessFailure(process));
                }
            }
            catch
            {
                if (ReferenceEquals(_process, process))
                {
                    await StopCoreAsync(settings, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await CleanupFailedStartAsync(process, job).ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            startReservation?.Dispose();
            _gate.Release();
        }
    }

    private void EnsureCoreStartAllowed()
    {
        if (File.Exists(_paths.StartupDisableMarkerFile))
        {
            throw new InvalidOperationException(
                "安装或恢复保护正在生效，Mihomo 启动已被阻止。");
        }
    }

    public async Task StopAsync(
        SplitRouteSettings settings,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync(
        SplitRouteSettings settings,
        CancellationToken cancellationToken)
    {
        if (_process is null)
        {
            await StopOrphanedProcessAsync(settings, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_process.HasExited)
        {
            try
            {
                await _controller.DisableTunAsync(settings, cancellationToken).ConfigureAwait(false);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                    or TaskCanceledException
                    or InvalidOperationException)
            {
                try
                {
                    await _logs.WriteAsync(
                        "WARN",
                        $"关闭 TUN 时控制 API 不可用：{exception.Message}",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Cleanup must continue even when logging is unavailable.
                }
            }

            if (!_process.HasExited)
            {
                _process.Kill(true);
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        CleanupProcess();
    }

    private async Task StopOrphanedProcessAsync(
        SplitRouteSettings settings,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.MihomoPidFile))
        {
            return;
        }

        var pidText = await File.ReadAllTextAsync(
            _paths.MihomoPidFile,
            cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(
                pidText.Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var processId))
        {
            File.Delete(_paths.MihomoPidFile);
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                File.Delete(_paths.MihomoPidFile);
                return;
            }

            var actualPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(settings.MihomoPath)
                || string.IsNullOrWhiteSpace(actualPath)
                || !Path.GetFullPath(actualPath).Equals(
                    Path.GetFullPath(settings.MihomoPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                await _logs.WriteAsync(
                    "WARN",
                    "检测到孤儿 PID，但可执行文件路径不匹配，未终止该进程。",
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            File.Delete(_paths.MihomoPidFile);
        }
        catch (ArgumentException)
        {
            File.Delete(_paths.MihomoPidFile);
        }
        catch (InvalidOperationException)
        {
            File.Delete(_paths.MihomoPidFile);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            await _logs.WriteAsync(
                "WARN",
                $"无法检查孤儿 Mihomo 进程：{exception.Message}",
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void PrepareGeoData(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return;
        }

        foreach (var fileName in new[] { "geoip.dat", "geosite.dat", "Country.mmdb", "GeoSite.dat" })
        {
            var source = Path.Combine(sourceDirectory, fileName);
            if (!File.Exists(source))
            {
                continue;
            }

            File.Copy(source, Path.Combine(_paths.RuntimeDirectory, fileName), true);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        return new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        };
    }

    private async Task CleanupFailedStartAsync(Process process, WindowsJob? job)
    {
        process.OutputDataReceived -= ProcessOnOutputDataReceived;
        process.ErrorDataReceived -= ProcessOnErrorDataReceived;
        process.Exited -= ProcessOnExited;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }

        job?.Dispose();
        if (File.Exists(_paths.MihomoPidFile))
        {
            File.Delete(_paths.MihomoPidFile);
        }
    }

    private void ProcessOnOutputDataReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            RememberProcessOutput(eventArgs.Data);
            _ = _logs.WriteAsync("MIHOMO", eventArgs.Data);
        }
    }

    private void ProcessOnErrorDataReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            RememberProcessOutput(eventArgs.Data);
            _ = _logs.WriteAsync("MIHOMO-ERR", eventArgs.Data);
        }
    }

    private void ProcessOnExited(object? sender, EventArgs eventArgs)
    {
        if (sender is Process process)
        {
            var exitCode = TryGetExitCode(process);
            _ = _logs.WriteAsync(
                exitCode is 0 ? "MIHOMO" : "MIHOMO-ERR",
                $"Mihomo 进程退出，退出码 {exitCode?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? "未知"}。");
        }

        Exited?.Invoke(this, EventArgs.Empty);
    }

    private string BuildReadinessFailure(Process process)
    {
        if (process.HasExited)
        {
            var output = GetRecentFailureOutput();
            return string.IsNullOrWhiteSpace(output)
                ? $"Mihomo 进程已退出，退出码 {process.ExitCode}；控制器未就绪。"
                : $"Mihomo 进程已退出，退出码 {process.ExitCode}；核心输出：{output}";
        }

        var recentOutput = GetRecentFailureOutput();
        return string.IsNullOrWhiteSpace(recentOutput)
            ? "Mihomo 进程仍在运行，但控制器未在 15 秒内报告 TUN 和 DNS 就绪；请检查端口占用、TUN 驱动和核心日志。"
            : "Mihomo 进程仍在运行，但控制器未在 15 秒内报告 TUN 和 DNS 就绪；核心输出："
              + recentOutput;
    }

    private void RememberProcessOutput(string line)
    {
        _recentOutput.Enqueue(line);
        while (_recentOutput.Count > 32
               && _recentOutput.TryDequeue(out _))
        {
        }
    }

    private string GetRecentFailureOutput()
    {
        var lines = _recentOutput
            .Where(line =>
                line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("fatal", StringComparison.OrdinalIgnoreCase)
                || line.Contains("listen", StringComparison.OrdinalIgnoreCase)
                || line.Contains("tun", StringComparison.OrdinalIgnoreCase)
                || line.Contains("dns", StringComparison.OrdinalIgnoreCase)
                || line.Contains("config", StringComparison.OrdinalIgnoreCase))
            .TakeLast(6)
            .ToArray();
        return SanitizeValidationOutput(string.Join(Environment.NewLine, lines));
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void CleanupProcess()
    {
        if (_process is not null)
        {
            _process.OutputDataReceived -= ProcessOnOutputDataReceived;
            _process.ErrorDataReceived -= ProcessOnErrorDataReceived;
            _process.Exited -= ProcessOnExited;
            _process.Dispose();
            _process = null;
        }

        _job?.Dispose();
        _job = null;
        if (File.Exists(_paths.MihomoPidFile))
        {
            File.Delete(_paths.MihomoPidFile);
        }
    }

    private static string SanitizeValidationOutput(string output)
    {
        var lines = output.Split(
            ['\r', '\n'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(
            Environment.NewLine,
            lines.Select(line =>
                line.Contains("://", StringComparison.Ordinal)
                    || line.Contains("password", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("username", StringComparison.OrdinalIgnoreCase)
                    ? "[已脱敏的核心输出]"
                    : line.Length > 300
                        ? line[..300] + "…"
                        : line));
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            _process.Kill(true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        CleanupProcess();
        _gate.Dispose();
    }
}

public sealed record ProcessValidationResult(bool IsValid, string Output);
