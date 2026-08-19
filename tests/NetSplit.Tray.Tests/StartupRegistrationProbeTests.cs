using NetSplit.Tray;

namespace NetSplit.Tray.Tests;

public sealed class StartupRegistrationProbeTests
{
    [Fact]
    public void ParseOutputReadsHealthyStartupAndRuntimeEvidence()
    {
        const string json = """
            {
              "Startup": {
                "RegistrationHealthy": true,
                "Issues": [],
                "Service": {
                  "Exists": true,
                  "State": "Running",
                  "StartMode": "Auto",
                  "DelayedAutoStart": true,
                  "ExecutableMatches": true
                },
                "TrayTask": {
                  "Registered": true,
                  "State": "Running",
                  "Enabled": true,
                  "LogonDelay": "PT15S",
                  "StartWhenAvailable": "true",
                  "RestartCount": "5",
                  "RestartInterval": "PT1M",
                  "LastTaskResult": "0x00041301"
                },
                "TrayProcess": {
                  "Count": 1,
                  "Running": true
                }
              },
              "Runtime": {
                "Reachable": true,
                "Mode": "Healthy",
                "Enabled": true,
                "MihomoRunning": true,
                "TunEnabled": true,
                "DnsEnabled": true
              }
            }
            """;

        var result = StartupRegistrationProbe.ParseOutput(json, 0, string.Empty);

        Assert.True(result.Available);
        Assert.True(result.RegistrationHealthy);
        Assert.Equal("Running", result.Service.State);
        Assert.Equal("PT15S", result.TrayTask.LogonDelay);
        Assert.True(result.TrayProcess.Running);
        Assert.Equal("Healthy", result.Runtime.Mode);
        Assert.True(result.Runtime.TunEnabled);
    }

    [Fact]
    public void ParseOutputKeepsRegistrationFailureEvidence()
    {
        const string json = """
            {
              "Startup": {
                "RegistrationHealthy": false,
                "Issues": ["tray task is disabled"],
                "Service": {},
                "TrayTask": {
                  "Registered": true,
                  "Enabled": false
                },
                "TrayProcess": {}
              },
              "Runtime": {
                "Reachable": false,
                "Mode": "unknown"
              }
            }
            """;

        var result = StartupRegistrationProbe.ParseOutput(
            json,
            2,
            "registration check failed");

        Assert.True(result.Available);
        Assert.False(result.RegistrationHealthy);
        Assert.Single(result.Issues);
        Assert.Equal("registration check failed", result.Error);
        Assert.False(result.Runtime.Reachable);
    }
}
