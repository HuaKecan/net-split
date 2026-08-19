using System.Diagnostics;
using System.Reflection;
using NetSplit.Core;
using NetSplit.Service;

namespace NetSplit.Service.Tests;

public sealed class MihomoProcessManagerTests
{
    [Fact]
    public async Task StartAsyncRejectsStartupDisableMarkerBeforeStartingProcess()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "net-split-process-tests",
            Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.StartupDisableMarkerFile,
            "install").ConfigureAwait(true);
        using var logs = new FileLogBuffer(paths);
        await using var manager = new MihomoProcessManager(
            paths,
            logs,
            new NoOpController());

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.StartAsync(
                    new SplitRouteSettings(),
                    CancellationToken.None)).ConfigureAwait(true);

            Assert.Contains("安装或恢复保护", exception.Message, StringComparison.Ordinal);
            Assert.False(manager.IsRunning);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task StopAsyncKillsProcessWhenTunDisableCancelsTheCaller()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "net-split-process-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AppPaths(root);
        using var logs = new FileLogBuffer(paths);
        using var cancellation = new CancellationTokenSource();
        var controller = new CancellingController(cancellation);
        await using var manager = new MihomoProcessManager(paths, logs, controller);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/c ping.exe 127.0.0.1 -n 30 > nul",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the test process.");
        var processId = process.Id;
        var processField = typeof(MihomoProcessManager).GetField(
            "_process",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(processField);
        processField!.SetValue(manager, process);

        try
        {
            await manager.StopAsync(
                new SplitRouteSettings(),
                cancellation.Token).ConfigureAwait(true);

            Assert.False(manager.IsRunning);
            Assert.True(HasExited(processId));
        }
        finally
        {
            if (!HasExited(processId))
            {
                using var remaining = Process.GetProcessById(processId);
                remaining.Kill(true);
                remaining.WaitForExit();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task StopAsyncCleansOrphanedManagedProcessFromPidFile()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "net-split-process-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        using var logs = new FileLogBuffer(paths);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/c ping.exe 127.0.0.1 -n 30 > nul",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the test process.");
        var processId = process.Id;
        var executablePath = process.MainModule?.FileName;
        Assert.False(string.IsNullOrWhiteSpace(executablePath));
        await File.WriteAllTextAsync(
            paths.MihomoPidFile,
            processId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await using var manager = new MihomoProcessManager(
            paths,
            logs,
            new NoOpController());
        try
        {
            await manager.StopAsync(
                new SplitRouteSettings { MihomoPath = executablePath! },
                CancellationToken.None).ConfigureAwait(true);

            Assert.True(HasExited(processId));
            Assert.False(File.Exists(paths.MihomoPidFile));
        }
        finally
        {
            if (!HasExited(processId))
            {
                process.Kill(true);
                process.WaitForExit();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task StopAsyncDoesNotKillPidFileProcessWhenExecutablePathDiffers()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "net-split-process-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AppPaths(root);
        paths.EnsureDirectories();
        using var logs = new FileLogBuffer(paths);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/c ping.exe 127.0.0.1 -n 30 > nul",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the test process.");
        var processId = process.Id;
        await File.WriteAllTextAsync(
            paths.MihomoPidFile,
            processId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await using var manager = new MihomoProcessManager(
            paths,
            logs,
            new NoOpController());
        try
        {
            await manager.StopAsync(
                new SplitRouteSettings
                {
                    MihomoPath = Path.Combine(root, "different-mihomo.exe")
                },
                CancellationToken.None).ConfigureAwait(true);

            Assert.False(HasExited(processId));
            Assert.True(File.Exists(paths.MihomoPidFile));
        }
        finally
        {
            if (!HasExited(processId))
            {
                process.Kill(true);
                process.WaitForExit();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class CancellingController : IMihomoControllerClient
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingController(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public Task<bool> WaitUntilReadyAsync(
            SplitRouteSettings settings,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task DisableTunAsync(
            SplitRouteSettings settings,
            CancellationToken cancellationToken)
        {
            _cancellation.Cancel();
            return Task.FromCanceled(_cancellation.Token);
        }

        public Task<MihomoApiSnapshot> GetSnapshotAsync(
            SplitRouteSettings settings,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new MihomoApiSnapshot(false, string.Empty, Array.Empty<string>()));
        }

        public Task<int?> MeasureDelayAsync(
            SplitRouteSettings settings,
            string proxyName,
            string url,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<int?>(null);
        }

        public Task SelectProxyAsync(
            SplitRouteSettings settings,
            string proxyName,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpController : IMihomoControllerClient
    {
        public Task<bool> WaitUntilReadyAsync(
            SplitRouteSettings settings,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task DisableTunAsync(
            SplitRouteSettings settings,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<MihomoApiSnapshot> GetSnapshotAsync(
            SplitRouteSettings settings,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new MihomoApiSnapshot(false, string.Empty, Array.Empty<string>()));
        }

        public Task<int?> MeasureDelayAsync(
            SplitRouteSettings settings,
            string proxyName,
            string url,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<int?>(null);
        }

        public Task SelectProxyAsync(
            SplitRouteSettings settings,
            string proxyName,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
