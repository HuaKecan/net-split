using NetSplit.Core;

namespace NetSplit.Recovery;

internal sealed record RecoveryResult(
    bool Succeeded,
    IReadOnlyList<string> Failures)
{
    public static RecoveryResult Evaluate(
        bool serviceStopped,
        bool mihomoStopped,
        bool runtimeFilesDeleted,
        bool dnsFlushed)
    {
        var failures = new List<string>();
        if (!serviceStopped)
        {
            failures.Add("NetSplit 服务未能确认停止。");
        }
        if (!mihomoStopped)
        {
            failures.Add("托管的 Mihomo 进程未能安全停止，PID 文件已保留。");
        }
        if (!runtimeFilesDeleted)
        {
            failures.Add("部分运行时文件未能删除。");
        }
        if (!dnsFlushed)
        {
            failures.Add("Windows DNS 缓存刷新失败。");
        }

        return new RecoveryResult(failures.Count == 0, failures);
    }

    public static IReadOnlyList<string> RuntimeFiles(
        AppPaths paths,
        bool deletePidFile)
    {
        var files = new List<string>
        {
            paths.RuntimeConfigFile,
            paths.CandidateConfigFile,
            paths.TransactionJournalFile,
            paths.TransactionRuntimeBackupFile
        };
        if (deletePidFile)
        {
            files.Add(paths.MihomoPidFile);
        }

        return files;
    }
}
