using NetSplit.Core;
using NetSplit.Recovery;

namespace NetSplit.Recovery.Tests;

public sealed class RecoveryResultTests
{
    [Fact]
    public void AllCompletedStepsProduceSuccessfulRecovery()
    {
        var result = RecoveryResult.Evaluate(
            serviceStopped: true,
            mihomoStopped: true,
            runtimeFilesDeleted: true,
            dnsFlushed: true);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
    }

    [Theory]
    [InlineData(false, true, true, true, "服务")]
    [InlineData(true, false, true, true, "Mihomo")]
    [InlineData(true, true, false, true, "运行时文件")]
    [InlineData(true, true, true, false, "DNS")]
    public void FailedStepProducesNonSuccessfulRecovery(
        bool serviceStopped,
        bool mihomoStopped,
        bool runtimeFilesDeleted,
        bool dnsFlushed,
        string expectedFailure)
    {
        var result = RecoveryResult.Evaluate(
            serviceStopped,
            mihomoStopped,
            runtimeFilesDeleted,
            dnsFlushed);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(expectedFailure, StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeCleanupRemovesInterruptedTransactionState()
    {
        var paths = new AppPaths(Path.Combine(
            Path.GetTempPath(),
            "net-split-recovery-tests",
            Guid.NewGuid().ToString("N")));

        var files = RecoveryResult.RuntimeFiles(paths, deletePidFile: true);

        Assert.Contains(paths.RuntimeConfigFile, files);
        Assert.Contains(paths.CandidateConfigFile, files);
        Assert.Contains(paths.TransactionJournalFile, files);
        Assert.Contains(paths.TransactionRuntimeBackupFile, files);
        Assert.Contains(paths.MihomoPidFile, files);
        Assert.DoesNotContain(paths.StartupDisableMarkerFile, files);
        Assert.DoesNotContain(paths.LastKnownGoodManifestFile, files);
    }

    [Fact]
    public void UnsafePidOwnershipKeepsPidFile()
    {
        var paths = new AppPaths(Path.Combine(
            Path.GetTempPath(),
            "net-split-recovery-tests",
            Guid.NewGuid().ToString("N")));

        var files = RecoveryResult.RuntimeFiles(paths, deletePidFile: false);

        Assert.DoesNotContain(paths.MihomoPidFile, files);
        Assert.Contains(paths.TransactionJournalFile, files);
    }
}
