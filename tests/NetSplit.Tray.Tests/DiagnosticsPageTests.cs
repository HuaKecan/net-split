using System.Diagnostics;
using System.Text.Json;
using NetSplit.Tray;

namespace NetSplit.Tray.Tests;

public sealed class DiagnosticsPageTests
{
    [Fact]
    public void ElevatedPowerShellStartInfoUsesStructuredArguments()
    {
        const string scriptPath = @"C:\Program Files\net-split\p0-observe.ps1";
        const string outputDirectory =
            @"C:\Program Files\net-split\artifacts\p0\gui-test with spaces";

        var startInfo = DiagnosticsPage.CreateElevatedPowerShellStartInfo(
            scriptPath,
            "-SampleSeconds",
            "8",
            "-OutputDirectory",
            outputDirectory);

        Assert.True(Path.IsPathFullyQualified(startInfo.FileName));
        Assert.EndsWith(
            @"WindowsPowerShell\v1.0\powershell.exe",
            startInfo.FileName,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.Equal(
            new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath,
                "-SampleSeconds",
                "8",
                "-OutputDirectory",
                outputDirectory
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void LatestObservationReportHandlesMissingDirectoryAndSelectsNewest()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"net-split-p0-report-{Guid.NewGuid():N}");
        Assert.Null(
            DiagnosticsPage.FindLatestP0ObservationReport(outputDirectory));

        Directory.CreateDirectory(outputDirectory);
        try
        {
            var older = Path.Combine(
                outputDirectory,
                "p0-observe-20260819-100000.json");
            var newer = Path.Combine(
                outputDirectory,
                "p0-observe-20260819-100001.json");
            File.WriteAllText(older, "{}");
            File.WriteAllText(newer, "{}");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-1));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

            Assert.Equal(
                newer,
                DiagnosticsPage.FindLatestP0ObservationReport(outputDirectory));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void ObservationReportParserReadsEvidenceSummary()
    {
        var json = JsonSerializer.Serialize(new
        {
            CaptureReady = true,
            BindingEvidenceObserved = true,
            Mihomo = new
            {
                HashMatchesExpected = true
            },
            ConnectionSummary = new
            {
                DirectAdapterTcp = 13,
                ProxyAdapterTcp = 45
            }
        });

        var summary = DiagnosticsPage.ParseP0ObservationReport(json);

        Assert.True(summary.CaptureReady);
        Assert.True(summary.BindingEvidenceObserved);
        Assert.True(summary.MihomoHashVerified);
        Assert.Equal(13, summary.DirectAdapterTcpCount);
        Assert.Equal(45, summary.ProxyAdapterTcpCount);
    }

    [Fact]
    public void ObservationReportParserRejectsMissingRequiredState()
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => DiagnosticsPage.ParseP0ObservationReport(
                """{"CaptureReady":true}"""));

        Assert.Contains("BindingEvidenceObserved", exception.Message);
    }
}
