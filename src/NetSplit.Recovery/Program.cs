using System.Diagnostics;
using System.Globalization;
using System.ServiceProcess;
using NetSplit.Core;
using NetSplit.Recovery;

const string serviceName = "NetSplitService";

Console.WriteLine("net-split 紧急恢复");
Console.WriteLine("==================");

// Recovery runs as an administrator and must use the existing service ACLs
// without trying to reassign the ProgramData owner.
var paths = new AppPaths(enforceRestrictedAcl: false);
paths.EnsureDirectories();
using var settingsStore = new SettingsStore(paths);
var settings = await settingsStore.LoadAsync().ConfigureAwait(false);

await TryRequestDisableAsync().ConfigureAwait(false);
var serviceStopped = StopService(serviceName);
settings = settings with { Enabled = false };
await settingsStore.SaveAsync(settings).ConfigureAwait(false);
var canDeletePidFile = StopManagedMihomo(paths, settings);
var runtimeFilesDeleted = DeleteRuntimeFiles(paths, canDeletePidFile);
var dnsFlushed = FlushDnsCache();
var result = RecoveryResult.Evaluate(
    serviceStopped,
    canDeletePidFile,
    runtimeFilesDeleted,
    dnsFlushed);
if (!result.Succeeded)
{
    Console.Error.WriteLine(
        "恢复操作未完全成功：" + string.Join(" ", result.Failures));
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine("恢复操作完成。net-split 的订阅与网卡设置已保留。");
return;

static async Task TryRequestDisableAsync()
{
    try
    {
        var client = new NamedPipeRpcClient();
        await client.SendAsync(
            RpcCommands.Disable,
            timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Console.WriteLine("[完成] 服务已正常关闭 TUN。");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"[提示] 无法通过服务正常回滚：{exception.Message}");
    }
}

static bool StopService(string serviceName)
{
    try
    {
        using var service = new ServiceController(serviceName);
        _ = service.Status;
        if (service.Status is ServiceControllerStatus.Running
            or ServiceControllerStatus.StartPending
            or ServiceControllerStatus.PausePending
            or ServiceControllerStatus.Paused)
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }

        Console.WriteLine("[完成] NetSplit 服务已停止。");
        return true;
    }
    catch (InvalidOperationException exception) when (
        exception.InnerException is System.ComponentModel.Win32Exception
        {
            NativeErrorCode: 1060
        })
    {
        Console.WriteLine("[提示] NetSplit 服务未安装。");
        return true;
    }
    catch (Exception exception) when (
        exception is InvalidOperationException
            or System.ServiceProcess.TimeoutException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
    {
        Console.WriteLine($"[警告] NetSplit 服务未能停止：{exception.Message}");
        return false;
    }
}

static bool StopManagedMihomo(AppPaths paths, SplitRouteSettings settings)
{
    if (!File.Exists(paths.MihomoPidFile))
    {
        return true;
    }

    try
    {
        var text = File.ReadAllText(paths.MihomoPidFile).Trim();
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
        {
            return true;
        }

        using var process = Process.GetProcessById(processId);
        var actualPath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(settings.MihomoPath)
            || string.IsNullOrWhiteSpace(actualPath)
            || !string.Equals(
                Path.GetFullPath(actualPath),
                Path.GetFullPath(settings.MihomoPath),
                StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[警告] PID 已被其他程序占用，未终止该进程。");
            return false;
        }

        process.Kill(true);
        if (!process.WaitForExit(10000))
        {
            Console.WriteLine("[警告] Mihomo 进程在 10 秒内未退出。");
            return false;
        }

        Console.WriteLine("[完成] 托管的 Mihomo 进程已停止。");
        return true;
    }
    catch (ArgumentException)
    {
        Console.WriteLine("[提示] Mihomo 进程已经退出。");
        return true;
    }
    catch (Exception exception) when (
        exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
    {
        Console.WriteLine($"[警告] 无法终止 Mihomo：{exception.Message}");
        return false;
    }
}

static bool DeleteRuntimeFiles(AppPaths paths, bool deletePidFile)
{
    var success = true;
    foreach (var path in RecoveryResult.RuntimeFiles(paths, deletePidFile))
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[警告] 无法删除 {Path.GetFileName(path)}：{exception.Message}");
            success = false;
        }
    }

    return success;
}

static bool FlushDnsCache()
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ipconfig.exe",
            Arguments = "/flushdns",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null || !process.WaitForExit(5000))
        {
            Console.WriteLine("[提示] Windows DNS 缓存刷新超时。");
            return false;
        }

        if (process.ExitCode != 0)
        {
            Console.WriteLine(
                $"[提示] Windows DNS 缓存刷新返回错误码 {process.ExitCode}。");
            return false;
        }

        Console.WriteLine("[完成] Windows DNS 缓存已刷新。");
        return true;
    }
    catch (System.ComponentModel.Win32Exception exception)
    {
        Console.WriteLine($"[提示] DNS 缓存刷新失败：{exception.Message}");
        return false;
    }
}
