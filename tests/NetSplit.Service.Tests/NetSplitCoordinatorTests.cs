using System.Text.Json;
using NetSplit.Core;
using NetSplit.Service;

namespace NetSplit.Service.Tests;

public sealed class NetSplitCoordinatorTests : IAsyncLifetime
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "net-split-tests",
        Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void TrafficHistorySamplingIsDueOnlyOncePerFiveSeconds()
    {
        var start = DateTimeOffset.UtcNow;

        Assert.True(
            NetSplitCoordinator.IsTrafficHistorySampleDue(
                DateTimeOffset.MinValue,
                start));
        Assert.False(
            NetSplitCoordinator.IsTrafficHistorySampleDue(
                start,
                start.AddSeconds(4.99)));
        Assert.True(
            NetSplitCoordinator.IsTrafficHistorySampleDue(
                start,
                start.AddSeconds(5)));
    }

    [Fact]
    public void ProxyDelayCacheExpiresAtFiveMinuteBoundary()
    {
        var measuredAt = DateTimeOffset.UtcNow;

        Assert.True(
            NetSplitCoordinator.IsProxyDelayCacheFresh(
                measuredAt,
                measuredAt.AddMinutes(4).AddSeconds(59)));
        Assert.False(
            NetSplitCoordinator.IsProxyDelayCacheFresh(
                measuredAt,
                measuredAt.AddMinutes(5)));
        Assert.False(
            NetSplitCoordinator.IsProxyDelayCacheFresh(
                measuredAt,
                measuredAt.AddSeconds(-1)));
    }

    [Fact]
    public async Task EnableGeneratesConfigStartsCoreAndReportsHealthy()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var adapterProvider = new FakeAdapterProvider([direct, proxy]);
        var process = new FakeProcessManager();
        var controller = new FakeController
        {
            SelectedProxyHealthy = true
        };
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            adapterProvider,
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(process.IsRunning);
        Assert.Equal(RuntimeMode.Healthy, coordinator.Status.Mode);
        Assert.Equal(
            ProxyRouteFailureReason.None,
            coordinator.Status.ProxyRouteFailure);
        Assert.True(File.Exists(paths.RuntimeConfigFile));
        var yaml = await File.ReadAllTextAsync(paths.RuntimeConfigFile).ConfigureAwait(true);
        Assert.Contains("interface-name: 主宽带", yaml, StringComparison.Ordinal);
        Assert.Contains("interface-name: F50", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, "Mihomo TUN")]
    [InlineData(true, false, "Mihomo DNS")]
    public async Task EnableRequiresTunAndDnsBeforeReportingHealthy(
        bool tunEnabled,
        bool dnsEnabled,
        string expectedError)
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var controller = new FakeController
        {
            TunEnabled = tunEnabled,
            DnsEnabled = dnsEnabled,
            SelectedProxyHealthy = true
        };
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(RuntimeMode.CoreUnavailable, coordinator.Status.Mode);
        Assert.Equal(tunEnabled, coordinator.Status.TunEnabled);
        Assert.Equal(dnsEnabled, coordinator.Status.DnsEnabled);
        Assert.True(coordinator.Status.DnsStatusKnown);
        Assert.False(coordinator.Status.ProxyRouteAvailable);
        Assert.Equal(
            ProxyRouteFailureReason.CoreUnavailable,
            coordinator.Status.ProxyRouteFailure);
        Assert.Contains(
            expectedError,
            coordinator.Status.LastError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeWhenDisabledStopsAnyResidualCore()
    {
        var direct = Adapter("direct", "direct-adapter", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var adapterProvider = new FakeAdapterProvider([direct, proxy]);
        var process = new FakeProcessManager();
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        var settings = Settings(direct, proxy);
        await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        await process.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            adapterProvider,
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController
            {
                SelectedProxyHealthy = true
            },
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.False(process.IsRunning);
        Assert.False(coordinator.Status.Enabled);
        Assert.False(coordinator.Status.TunEnabled);
        Assert.Equal(RuntimeMode.Disabled, coordinator.Status.Mode);
    }

    [Fact]
    public async Task InitializeWhenDisabledCleansOrphanedCoreStateWithoutTrackedProcess()
    {
        var direct = Adapter("direct", "direct-adapter", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var process = new FakeProcessManager();
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(1, process.StopAttempts);
        Assert.Equal(RuntimeMode.Disabled, coordinator.Status.Mode);
    }

    [Fact]
    public async Task EnableIsRejectedWhileStartupDisableMarkerExists()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.StartupDisableMarkerFile,
            "install").ConfigureAwait(true);
        var process = new FakeProcessManager();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.EnableAsync(CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("安装或恢复保护", exception.Message, StringComparison.Ordinal);
        Assert.True(coordinator.IsReady);
        Assert.False(process.IsRunning);
        Assert.Equal(0, process.StartAttempts);
        Assert.False(coordinator.ClientSettings.Enabled);
        Assert.False((await settingsStore.LoadAsync().ConfigureAwait(true)).Enabled);
        Assert.True(File.Exists(paths.StartupDisableMarkerFile));

        File.Delete(paths.StartupDisableMarkerFile);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(process.IsRunning);
        Assert.Equal(1, process.StartAttempts);
        Assert.True(coordinator.ClientSettings.Enabled);
    }

    [Fact]
    public async Task DisposeStopsResidualCoreEvenWhenSettingsAreDisabled()
    {
        var direct = Adapter("direct", "direct-adapter", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var process = new FakeProcessManager();
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        var settings = Settings(direct, proxy);
        await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await process.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);

        await coordinator.DisposeAsync().ConfigureAwait(true);

        Assert.False(process.IsRunning);
    }

    [Fact]
    public async Task MaintainWhenProxyAdapterDropsKeepsCoreAndReportsProxyUnavailable()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var adapterProvider = new FakeAdapterProvider([direct, proxy]);
        var process = new FakeProcessManager();
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            adapterProvider,
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        adapterProvider.Adapters =
        [
            direct,
            proxy with
            {
                IsUp = false,
                IsSelectable = false,
                Gateways = []
            }
        ];
        await coordinator.MaintainAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(process.IsRunning);
        Assert.Equal(RuntimeMode.ProxyUnavailable, coordinator.Status.Mode);
        Assert.True(coordinator.Status.DirectAdapterAvailable);
        Assert.False(coordinator.Status.ProxyAdapterAvailable);
        Assert.False(coordinator.Status.ProxyRouteHealthKnown);
        Assert.False(coordinator.Status.ProxyRouteAvailable);
        Assert.Equal(
            ProxyRouteFailureReason.ProxyAdapterUnavailable,
            coordinator.Status.ProxyRouteFailure);
        Assert.Empty(coordinator.Status.HealthyProxies);
        Assert.Equal(0, coordinator.Status.HealthyProxyCount);
    }

    [Fact]
    public async Task MaintainWhenControllerHealthCheckFailsReportsCoreUnavailable()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var adapterProvider = new FakeAdapterProvider([direct, proxy]);
        var process = new FakeProcessManager();
        var controller = new FakeController();
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            adapterProvider,
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        controller.FailHealthChecks = true;
        await coordinator.MaintainAsync(CancellationToken.None).ConfigureAwait(true);
        var callsAfterFailure = controller.SnapshotCalls;
        await coordinator.MaintainAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(RuntimeMode.CoreUnavailable, coordinator.Status.Mode);
        Assert.Equal(
            ProxyRouteFailureReason.ControllerUnavailable,
            coordinator.Status.ProxyRouteFailure);
        Assert.Contains(
            "simulated controller outage",
            coordinator.Status.LastError,
            StringComparison.Ordinal);
        Assert.False(coordinator.Status.ProxyRouteHealthKnown);
        Assert.Empty(coordinator.Status.HealthyProxies);
        Assert.True(coordinator.Status.MihomoRunning);
        Assert.Equal(callsAfterFailure, controller.SnapshotCalls);
        Assert.Contains(
            logs.Snapshot(),
            entry => entry.Contains(
                "simulated controller outage",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task DelayProbeFailureDoesNotMarkCoreUnavailable()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var adapterProvider = new FakeAdapterProvider([direct, proxy]);
        var process = new FakeProcessManager();
        var controller = new FakeController
        {
            FailProxyDelay = true
        };
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            adapterProvider,
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.MaintainAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(RuntimeMode.Healthy, coordinator.Status.Mode);
        Assert.True(coordinator.Status.MihomoRunning);
        Assert.Null(coordinator.Status.ProxyDelayMilliseconds);
        Assert.DoesNotContain(
            "simulated proxy delay failure",
            coordinator.Status.LastError,
            StringComparison.Ordinal);
        Assert.Contains(
            logs.Snapshot(),
            entry => entry.Contains(
                "simulated proxy delay failure",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnableWhenInitialControllerSnapshotFailsDoesNotReportHealthy()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var adapterProvider = new FakeAdapterProvider([direct, proxy]);
        var process = new FakeProcessManager();
        var controller = new FakeController
        {
            FailNextSnapshot = true
        };
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            adapterProvider,
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(process.IsRunning);
        Assert.Equal(RuntimeMode.CoreUnavailable, coordinator.Status.Mode);
        Assert.Contains(
            "simulated initial snapshot failure",
            coordinator.Status.LastError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllProxyNodesUnavailableBlocksForeignRouteButKeepsDirectRoute()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var adapterProvider = new FakeAdapterProvider([direct, proxy]);
        var process = new FakeProcessManager();
        var controller = new FakeController
        {
            SelectedProxyHealthy = false
        };
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            adapterProvider,
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(process.IsRunning);
        Assert.Equal(RuntimeMode.ProxyUnavailable, coordinator.Status.Mode);
        Assert.True(coordinator.Status.DirectAdapterAvailable);
        Assert.True(coordinator.Status.ProxyAdapterAvailable);
        Assert.False(coordinator.Status.ProxyRouteAvailable);
        Assert.True(coordinator.Status.ProxyRouteHealthKnown);
        Assert.Equal(
            ProxyRouteFailureReason.NoHealthyProxy,
            coordinator.Status.ProxyRouteFailure);
        Assert.Empty(coordinator.Status.HealthyProxies);

        await coordinator.DisableAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(RuntimeMode.Disabled, coordinator.Status.Mode);
        Assert.False(coordinator.Status.ProxyRouteAvailable);
        Assert.False(coordinator.Status.ProxyRouteHealthKnown);
        Assert.Equal(0, coordinator.Status.HealthyProxyCount);

        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        controller.SelectedProxyHealthy = true;
        await coordinator.RepairAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(RuntimeMode.Healthy, coordinator.Status.Mode);
        Assert.True(coordinator.Status.ProxyRouteAvailable);
        Assert.Equal("node", coordinator.Status.EffectiveProxy);
        Assert.Contains("node", coordinator.Status.HealthyProxies);
    }

    [Fact]
    public async Task SelectingProxyUpdatesDisplayedSelectionBeforeNextHealthCheck()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var controller = new FakeController
        {
            SelectedProxyHealthy = true
        };
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        await coordinator.SelectProxyAsync("node", CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal("node", coordinator.Status.CurrentProxy);
        Assert.Equal("node", coordinator.Status.EffectiveProxy);
        Assert.False(coordinator.Status.ProxyRouteHealthKnown);
        Assert.Equal(
            ProxyRouteFailureReason.HealthCheckPending,
            coordinator.Status.ProxyRouteFailure);
        Assert.Empty(coordinator.Status.HealthyProxies);
        Assert.Contains("node", coordinator.Status.AvailableProxies);
    }

    [Fact]
    public async Task MeasureProxyDelaysCachesResultsAndInvalidatesAfterCoreRebuild()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var controller = new FakeController
        {
            SelectedProxyHealthy = true,
            AvailableProxyNames =
            [
                MihomoConfigGenerator.AutoProxyGroupName,
                "slow-node",
                "fast-node",
                "offline-node"
            ]
        };
        controller.DelayByProxyName["slow-node"] = 240;
        controller.DelayByProxyName["fast-node"] = 35;
        controller.DelayByProxyName["offline-node"] = null;
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        var first = await coordinator.MeasureProxyDelaysAsync(CancellationToken.None)
            .ConfigureAwait(true);
        var second = await coordinator.MeasureProxyDelaysAsync(CancellationToken.None)
            .ConfigureAwait(true);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(first.MeasuredAt, second.MeasuredAt);
        Assert.Equal(3, first.Results.Count);
        Assert.Equal(
            35,
            first.Results.Single(result => result.Name == "fast-node")
                .DelayMilliseconds);
        Assert.Null(
            first.Results.Single(result => result.Name == "offline-node")
                .DelayMilliseconds);
        Assert.Equal(3, controller.MeasuredProxyNames.Count);

        await coordinator.RepairAsync(CancellationToken.None).ConfigureAwait(true);
        var afterRebuild = await coordinator.MeasureProxyDelaysAsync(CancellationToken.None)
            .ConfigureAwait(true);

        Assert.False(afterRebuild.FromCache);
        Assert.Equal(6, controller.MeasuredProxyNames.Count);
    }

    [Fact]
    public async Task SwitchingExitModeRebuildsFinalRouteBetweenAirportAndResidential()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var controller = new FakeController
        {
            SelectedProxyHealthy = true,
            ResidentialProxyHealthy = true
        };
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(
            Settings(direct, proxy) with
            {
                ResidentialProxy = new ResidentialProxySettings
                {
                    Enabled = true,
                    Host = "residential.example",
                    Port = 1080,
                    AuthenticationEnabled = false,
                    RouteMode = ResidentialProxyRouteMode.ThroughAirport
                }
            }).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(
            MihomoConfigGenerator.ResidentialProxyName,
            coordinator.Status.EffectiveProxy);

        await coordinator.SetProxyExitModeAsync(
            new SetProxyExitModeRequest { Mode = ProxyExitMode.Airport },
            CancellationToken.None).ConfigureAwait(true);

        Assert.False(coordinator.ClientSettings.ResidentialProxy.Enabled);
        Assert.NotEqual(
            MihomoConfigGenerator.ResidentialProxyName,
            coordinator.Status.EffectiveProxy);
        var airportConfig = await File.ReadAllTextAsync(paths.RuntimeConfigFile)
            .ConfigureAwait(true);
        Assert.Contains(
            $"MATCH,{MihomoConfigGenerator.ProxyGroupName}",
            airportConfig,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"MATCH,{MihomoConfigGenerator.ResidentialProxyName}",
            airportConfig,
            StringComparison.Ordinal);

        await coordinator.SetProxyExitModeAsync(
            new SetProxyExitModeRequest { Mode = ProxyExitMode.Residential },
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(coordinator.ClientSettings.ResidentialProxy.Enabled);
        Assert.Equal(
            MihomoConfigGenerator.ResidentialProxyName,
            coordinator.Status.EffectiveProxy);
        var residentialConfig = await File.ReadAllTextAsync(paths.RuntimeConfigFile)
            .ConfigureAwait(true);
        Assert.Contains(
            $"MATCH,{MihomoConfigGenerator.ResidentialProxyName}",
            residentialConfig,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchingExitModePreservesStoredResidentialCredentials()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(
            Settings(direct, proxy) with
            {
                ResidentialProxy = new ResidentialProxySettings
                {
                    Enabled = true,
                    Host = "residential.example",
                    Port = 1080,
                    AuthenticationEnabled = true,
                    ProtectedUsername = "protected-user",
                    ProtectedPassword = "protected-password",
                    RouteMode = ResidentialProxyRouteMode.ThroughAirport
                }
            }).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        await coordinator.SetProxyExitModeAsync(
            new SetProxyExitModeRequest { Mode = ProxyExitMode.Airport },
            CancellationToken.None).ConfigureAwait(true);
        await coordinator.SetProxyExitModeAsync(
            new SetProxyExitModeRequest { Mode = ProxyExitMode.Residential },
            CancellationToken.None).ConfigureAwait(true);

        var stored = await settingsStore.LoadAsync().ConfigureAwait(true);
        Assert.Equal("protected-user", stored.ResidentialProxy.ProtectedUsername);
        Assert.Equal("protected-password", stored.ResidentialProxy.ProtectedPassword);
    }

    [Fact]
    public async Task SwitchingExitModeKeepsCommittedRouteWhenPostCommitWorkIsCanceled()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var controller = new FakeController
        {
            SelectedProxyHealthy = true,
            ResidentialProxyHealthy = true
        };
        var process = new FakeProcessManager();
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(
            Settings(direct, proxy) with
            {
                ResidentialProxy = new ResidentialProxySettings
                {
                    Enabled = true,
                    Host = "residential.example",
                    Port = 1080,
                    AuthenticationEnabled = false,
                    RouteMode = ResidentialProxyRouteMode.ThroughAirport
                }
            }).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        controller.CancelNextSnapshot = true;

        await coordinator.SetProxyExitModeAsync(
            new SetProxyExitModeRequest { Mode = ProxyExitMode.Airport },
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(process.IsRunning);
        Assert.False(coordinator.ClientSettings.ResidentialProxy.Enabled);
        Assert.False((await settingsStore.LoadAsync().ConfigureAwait(true))
            .ResidentialProxy.Enabled);
        Assert.False(File.Exists(paths.TransactionJournalFile));
        var config = await File.ReadAllTextAsync(paths.RuntimeConfigFile)
            .ConfigureAwait(true);
        Assert.Contains(
            $"MATCH,{MihomoConfigGenerator.ProxyGroupName}",
            config,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"MATCH,{MihomoConfigGenerator.ResidentialProxyName}",
            config,
            StringComparison.Ordinal);

        using var callerCancellation = new CancellationTokenSource();
        controller.OnNextSnapshot = callerCancellation.Cancel;
        await coordinator.SetProxyExitModeAsync(
            new SetProxyExitModeRequest { Mode = ProxyExitMode.Residential },
            callerCancellation.Token).ConfigureAwait(true);

        Assert.True(callerCancellation.IsCancellationRequested);
        Assert.True(coordinator.ClientSettings.ResidentialProxy.Enabled);
        Assert.True((await settingsStore.LoadAsync().ConfigureAwait(true))
            .ResidentialProxy.Enabled);
        var residentialConfig = await File.ReadAllTextAsync(paths.RuntimeConfigFile)
            .ConfigureAwait(true);
        Assert.Contains(
            $"MATCH,{MihomoConfigGenerator.ResidentialProxyName}",
            residentialConfig,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchingToResidentialExitRequiresSavedConfiguration()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.SetProxyExitModeAsync(
                new SetProxyExitModeRequest { Mode = ProxyExitMode.Residential },
                CancellationToken.None));

        Assert.Contains("住宅 SOCKS5", exception.Message, StringComparison.Ordinal);
        Assert.False(coordinator.ClientSettings.ResidentialProxy.Enabled);
    }

    [Fact]
    public async Task ResidentialProxyHealthUsesFinalRouteAndKeepsAirportHealthVisible()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var controller = new FakeController
        {
            SelectedProxyHealthy = true,
            ResidentialProxyHealthy = false
        };
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(
            Settings(direct, proxy) with
            {
                ResidentialProxy = new ResidentialProxySettings
                {
                    Enabled = true,
                    Host = "residential.example",
                    AuthenticationEnabled = false,
                    RouteMode = ResidentialProxyRouteMode.DirectNic2
                }
            }).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.MaintainAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(RuntimeMode.ProxyUnavailable, coordinator.Status.Mode);
        Assert.True(coordinator.Status.ProxyRouteHealthKnown);
        Assert.False(coordinator.Status.ProxyRouteAvailable);
        Assert.Equal(
            ProxyRouteFailureReason.ResidentialProxyUnavailable,
            coordinator.Status.ProxyRouteFailure);
        Assert.Equal(
            MihomoConfigGenerator.ResidentialProxyName,
            coordinator.Status.EffectiveProxy);
        Assert.Contains("node", coordinator.Status.HealthyProxies);
        Assert.Contains(
            MihomoConfigGenerator.ResidentialProxyName,
            controller.MeasuredProxyNames);
    }

    [Fact]
    public async Task EnableWithMissingProxyAdapterKeepsDomesticCoreRunning()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var adapterProvider = new FakeAdapterProvider([direct]);
        var process = new FakeProcessManager();
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            adapterProvider,
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(process.IsRunning);
        Assert.Equal(RuntimeMode.ProxyUnavailable, coordinator.Status.Mode);
        Assert.True(coordinator.Status.DirectAdapterAvailable);
        Assert.False(coordinator.Status.ProxyAdapterAvailable);
        var yaml = await File.ReadAllTextAsync(paths.RuntimeConfigFile).ConfigureAwait(true);
        Assert.Contains("interface-name: F50", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRefreshRestoresLastKnownGoodConfigAndRestartsCore()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var adapterProvider = new FakeAdapterProvider([direct, proxy]);
        var process = new FakeProcessManager();
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            adapterProvider,
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        var lastKnownGood = await File.ReadAllTextAsync(
            paths.RuntimeConfigFile).ConfigureAwait(true);
        process.FailStartsRemaining = 1;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RefreshSubscriptionsAsync(CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("已恢复上次可用配置", exception.Message, StringComparison.Ordinal);
        Assert.Contains("simulated start failure", exception.Message, StringComparison.Ordinal);
        Assert.True(process.IsRunning);
        Assert.Equal(3, process.StartAttempts);
        Assert.Equal(
            lastKnownGood,
            await File.ReadAllTextAsync(paths.RuntimeConfigFile).ConfigureAwait(true));
    }

    [Fact]
    public async Task RollbackRestoresSnapshotCacheAfterActiveGenerationChanges()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var loader = new FakeSubscriptionLoader
        {
            CachePaths = paths,
            CacheManifestContent = "{\"generation\":\"baseline\",\"entries\":{}}"
        };
        var process = new FakeProcessManager();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            loader,
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        var baselineConfig = await File.ReadAllTextAsync(
            paths.RuntimeConfigFile).ConfigureAwait(true);
        var baselineCacheManifest = await File.ReadAllTextAsync(
            paths.CacheManifestFile).ConfigureAwait(true);

        await coordinator.DisableAsync(CancellationToken.None).ConfigureAwait(true);
        loader.CacheManifestContent = "{\"generation\":\"candidate\",\"entries\":{}}";
        await coordinator.RefreshSubscriptionsAsync(CancellationToken.None).ConfigureAwait(true);
        Assert.NotEqual(
            baselineCacheManifest,
            await File.ReadAllTextAsync(paths.CacheManifestFile).ConfigureAwait(true));

        await coordinator.RollbackAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(
            baselineCacheManifest,
            await File.ReadAllTextAsync(paths.CacheManifestFile).ConfigureAwait(true));
        Assert.Equal(
            baselineConfig,
            await File.ReadAllTextAsync(paths.RuntimeConfigFile).ConfigureAwait(true));
        Assert.False(process.IsRunning);
        Assert.False(coordinator.ClientSettings.Enabled);
        Assert.False(File.Exists(paths.TransactionJournalFile));
        Assert.False(File.Exists(paths.TransactionRuntimeBackupFile));
    }

    [Fact]
    public async Task RollbackRefreshesProxyHealthInsteadOfKeepingPreRollbackState()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var process = new FakeProcessManager();
        var controller = new FakeController
        {
            SelectedProxyHealthy = true
        };
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(RuntimeMode.Healthy, coordinator.Status.Mode);

        controller.SelectedProxyHealthy = false;
        await coordinator.RollbackAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(RuntimeMode.ProxyUnavailable, coordinator.Status.Mode);
        Assert.True(coordinator.Status.ProxyRouteHealthKnown);
        Assert.False(coordinator.Status.ProxyRouteAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackRejectsMissingOrTamperedSnapshotCacheManifest(bool deleteSnapshot)
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var loader = new FakeSubscriptionLoader
        {
            CachePaths = paths,
            CacheManifestContent = "{\"generation\":\"baseline\",\"entries\":{}}"
        };
        var process = new FakeProcessManager();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            loader,
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        var snapshotCachePath = ReadSnapshotCacheManifestPath(paths);
        if (deleteSnapshot)
        {
            File.Delete(snapshotCachePath);
        }
        else
        {
            await File.WriteAllTextAsync(snapshotCachePath, "tampered").ConfigureAwait(true);
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            coordinator.RollbackAsync(CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("订阅缓存清单", exception.Message, StringComparison.Ordinal);
        Assert.True(process.IsRunning);
        Assert.False(File.Exists(paths.TransactionJournalFile));
    }

    [Fact]
    public async Task FailedRollbackRestoresPreviousConfigSettingsAndCache()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var loader = new FakeSubscriptionLoader
        {
            CachePaths = paths,
            CacheManifestContent = "{\"generation\":\"baseline\",\"entries\":{}}"
        };
        var process = new FakeProcessManager();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            loader,
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        const string previousRuntimeConfig = "previous runtime config";
        const string previousCacheManifest = "{\"generation\":\"candidate\",\"entries\":{}}";
        await File.WriteAllTextAsync(
            paths.RuntimeConfigFile,
            previousRuntimeConfig).ConfigureAwait(true);
        await File.WriteAllTextAsync(
            paths.CacheManifestFile,
            previousCacheManifest).ConfigureAwait(true);
        var previousSettings = Settings(direct, proxy) with
        {
            Enabled = true,
            Rules =
            [
                new CustomRule
                {
                    MatchType = RuleMatchType.Domain,
                    Action = RuleAction.Block,
                    Value = "previous.example"
                }
            ]
        };
        await settingsStore.SaveAsync(previousSettings).ConfigureAwait(true);
        process.FailStartsRemaining = 1;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RollbackAsync(CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("已恢复回退前的运行状态", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            previousRuntimeConfig,
            await File.ReadAllTextAsync(paths.RuntimeConfigFile).ConfigureAwait(true));
        Assert.Equal(
            previousCacheManifest,
            await File.ReadAllTextAsync(paths.CacheManifestFile).ConfigureAwait(true));
        var restoredSettings = await settingsStore.LoadAsync().ConfigureAwait(true);
        Assert.Equal("previous.example", Assert.Single(restoredSettings.Rules).Value);
        Assert.True(process.IsRunning);
        Assert.False(File.Exists(paths.TransactionJournalFile));
        Assert.False(File.Exists(paths.TransactionRuntimeBackupFile));
    }

    [Fact]
    public async Task LegacySnapshotReconstructsItsCacheManifestWhenGenerationStillExists()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var loader = new FakeSubscriptionLoader
        {
            CachePaths = paths,
            CacheManifestContent = "{\"generation\":\"baseline\",\"entries\":{}}"
        };
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            loader,
            new FakeProcessManager(),
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        Directory.CreateDirectory(Path.Combine(paths.CacheGenerationsDirectory, "baseline"));
        await RewriteSnapshotAsLegacyAsync(paths).ConfigureAwait(true);
        await File.WriteAllTextAsync(
            paths.CacheManifestFile,
            "{\"generation\":\"candidate\",\"entries\":{}}").ConfigureAwait(true);

        await coordinator.RollbackAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(
            "baseline",
            SubscriptionLoader.ReadGeneration(
                await File.ReadAllTextAsync(paths.CacheManifestFile).ConfigureAwait(true),
                paths.CacheDirectory));
    }

    [Fact]
    public async Task LegacySnapshotExplainsWhenItsCacheGenerationCannotBeReconstructed()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var loader = new FakeSubscriptionLoader
        {
            CachePaths = paths,
            CacheManifestContent = "{\"generation\":\"baseline\",\"entries\":{}}"
        };
        var process = new FakeProcessManager();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            loader,
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        await RewriteSnapshotAsLegacyAsync(paths).ConfigureAwait(true);
        await File.WriteAllTextAsync(
            paths.CacheManifestFile,
            "{\"generation\":\"candidate\",\"entries\":{}}").ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            coordinator.RollbackAsync(CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("请先成功启用一次", exception.Message, StringComparison.Ordinal);
        Assert.True(process.IsRunning);
    }

    [Fact]
    public async Task RollbackIsIdempotentWhenCacheGenerationAlreadyMatches()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var loader = new FakeSubscriptionLoader
        {
            CachePaths = paths,
            CacheManifestContent = "{\"generation\":\"baseline\",\"entries\":{}}"
        };
        var process = new FakeProcessManager();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            loader,
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        var cacheManifest = await File.ReadAllTextAsync(
            paths.CacheManifestFile).ConfigureAwait(true);

        await coordinator.RollbackAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(process.IsRunning);
        Assert.Equal(
            cacheManifest,
            await File.ReadAllTextAsync(paths.CacheManifestFile).ConfigureAwait(true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task InterruptedTransactionRestoresPreviousGenerationAndHonorsStartupDisableMarker(
        bool startupDisableActive,
        bool expectedRunning)
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var baselineConfig = string.Empty;
        var baselineLkgManifest = string.Empty;
        var baselineCacheManifest = "{\"generation\":\"baseline\",\"entries\":{}}";

        using (var initialSettingsStore = new SettingsStore(paths))
        using (var initialLogs = new FileLogBuffer(paths))
        {
            await initialSettingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
            await using var coordinator = new NetSplitCoordinator(
                paths,
                initialSettingsStore,
                new FakeSecretProtector(),
                new FakeAdapterProvider([direct, proxy]),
                new ConfigurationValidatorFacade(),
                new FakeSubscriptionLoader(),
                new FakeProcessManager(),
                new FakeController(),
                initialLogs);
            await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

            baselineConfig = await File.ReadAllTextAsync(
                paths.RuntimeConfigFile).ConfigureAwait(true);
            baselineLkgManifest = await File.ReadAllTextAsync(
                paths.LastKnownGoodManifestFile).ConfigureAwait(true);
        }

        await File.WriteAllTextAsync(
            paths.CacheManifestFile,
            "{\"generation\":\"candidate\",\"entries\":{}}").ConfigureAwait(true);
        await File.WriteAllTextAsync(
            paths.RuntimeConfigFile,
            "candidate config").ConfigureAwait(true);
        var candidateSettings = Settings(direct, proxy) with
        {
            Enabled = true,
            Rules =
            [
                new CustomRule
                {
                    MatchType = RuleMatchType.Domain,
                    Action = RuleAction.Block,
                    Value = "candidate.example"
                }
            ]
        };
        using (var settingsStore = new SettingsStore(paths))
        {
            await settingsStore.SaveAsync(candidateSettings).ConfigureAwait(true);
        }

        await File.WriteAllTextAsync(
            paths.TransactionJournalFile,
            JsonSerializer.Serialize(
                new
                {
                    PreviousLkgManifestJson = baselineLkgManifest,
                    PreviousCacheManifestJson = baselineCacheManifest
                },
                JsonDefaults.Create())).ConfigureAwait(true);
        if (startupDisableActive)
        {
            await File.WriteAllTextAsync(
                paths.StartupDisableMarkerFile,
                "install").ConfigureAwait(true);
        }

        var process = new FakeProcessManager();
        using var recoverySettingsStore = new SettingsStore(paths);
        using var recoveryLogs = new FileLogBuffer(paths);
        await using var recoveryCoordinator = new NetSplitCoordinator(
            paths,
            recoverySettingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            recoveryLogs);

        await recoveryCoordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(expectedRunning, process.IsRunning);
        Assert.Equal(
            expectedRunning ? RuntimeMode.Healthy : RuntimeMode.Disabled,
            recoveryCoordinator.Status.Mode);
        Assert.Equal(expectedRunning, recoveryCoordinator.ClientSettings.Enabled);
        Assert.Equal(
            expectedRunning,
            (await recoverySettingsStore.LoadAsync().ConfigureAwait(true)).Enabled);
        Assert.Equal(
            baselineConfig,
            await File.ReadAllTextAsync(paths.RuntimeConfigFile).ConfigureAwait(true));
        Assert.Equal(
            baselineCacheManifest,
            await File.ReadAllTextAsync(paths.CacheManifestFile).ConfigureAwait(true));
        Assert.False(File.Exists(paths.TransactionJournalFile));
        Assert.Equal(
            startupDisableActive,
            File.Exists(paths.StartupDisableMarkerFile));
        Assert.Empty(recoveryCoordinator.ClientSettings.Rules);
        var diagnostics = await recoveryCoordinator.GetDiagnosticsAsync(
            CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(startupDisableActive, diagnostics.StartupDisableActive);
        Assert.Contains(
            diagnostics.Files,
            file => file.Name.Equals(
                        "startup.force-disabled",
                        StringComparison.Ordinal)
                    && file.Exists == startupDisableActive);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CommittedTransactionHonorsStartupDisableMarker(
        bool startupDisableActive,
        bool expectedRunning)
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var lkgManifest = string.Empty;
        var settingsJson = string.Empty;
        var runtimeConfig = string.Empty;

        using (var settingsStore = new SettingsStore(paths))
        using (var logs = new FileLogBuffer(paths))
        {
            await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
            await using var coordinator = new NetSplitCoordinator(
                paths,
                settingsStore,
                new FakeSecretProtector(),
                new FakeAdapterProvider([direct, proxy]),
                new ConfigurationValidatorFacade(),
                new FakeSubscriptionLoader(),
                new FakeProcessManager(),
                new FakeController(),
                logs);
            await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
            lkgManifest = await File.ReadAllTextAsync(
                paths.LastKnownGoodManifestFile).ConfigureAwait(true);
            settingsJson = await File.ReadAllTextAsync(
                paths.SettingsFile).ConfigureAwait(true);
            runtimeConfig = await File.ReadAllTextAsync(
                paths.RuntimeConfigFile).ConfigureAwait(true);
        }

        await File.WriteAllTextAsync(
            paths.TransactionJournalFile,
            JsonSerializer.Serialize(
                new
                {
                    Phase = "Committed",
                    PreviousSettingsJson = settingsJson,
                    PreviousLkgManifestJson = lkgManifest,
                    PreviousCacheManifestJson = (string?)null
                },
                JsonDefaults.Create())).ConfigureAwait(true);
        if (startupDisableActive)
        {
            await File.WriteAllTextAsync(
                paths.StartupDisableMarkerFile,
                "install").ConfigureAwait(true);
        }

        var process = new FakeProcessManager();
        using var recoverySettingsStore = new SettingsStore(paths);
        using var recoveryLogs = new FileLogBuffer(paths);
        await using var recoveryCoordinator = new NetSplitCoordinator(
            paths,
            recoverySettingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            recoveryLogs);

        await recoveryCoordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(recoveryCoordinator.IsReady);
        Assert.Equal(expectedRunning, process.IsRunning);
        Assert.Equal(
            expectedRunning ? RuntimeMode.Healthy : RuntimeMode.Disabled,
            recoveryCoordinator.Status.Mode);
        Assert.Equal(expectedRunning, recoveryCoordinator.ClientSettings.Enabled);
        Assert.Equal(
            expectedRunning,
            (await recoverySettingsStore.LoadAsync().ConfigureAwait(true)).Enabled);
        Assert.Equal(
            runtimeConfig,
            await File.ReadAllTextAsync(paths.RuntimeConfigFile).ConfigureAwait(true));
        Assert.False(File.Exists(paths.TransactionJournalFile));
        Assert.Equal(
            startupDisableActive,
            File.Exists(paths.StartupDisableMarkerFile));
    }

    [Fact]
    public async Task FailedCacheCommitRestoresPreviousCacheManifest()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var loader = new FakeSubscriptionLoader
        {
            CachePaths = paths,
            CacheManifestContent = "{\"generation\":\"baseline\",\"entries\":{}}"
        };
        var process = new FakeProcessManager();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            loader,
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        var baselineManifest = await File.ReadAllTextAsync(
            paths.CacheManifestFile).ConfigureAwait(true);

        loader.CacheManifestContent = "{\"generation\":\"candidate\",\"entries\":{}}";
        loader.ThrowAfterCommit = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RefreshSubscriptionsAsync(CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal(
            baselineManifest,
            await File.ReadAllTextAsync(paths.CacheManifestFile).ConfigureAwait(true));
        Assert.True(process.IsRunning);
        Assert.False(File.Exists(paths.TransactionJournalFile));
    }

    [Fact]
    public async Task DisabledRefreshRollsBackCacheAndSettingsTogether()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var loader = new FakeSubscriptionLoader
        {
            CachePaths = paths,
            CacheManifestContent = "{\"generation\":\"baseline\",\"entries\":{}}"
        };
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            loader,
            new FakeProcessManager(),
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.RefreshSubscriptionsAsync(CancellationToken.None).ConfigureAwait(true);

        var baselineManifest = await File.ReadAllTextAsync(
            paths.CacheManifestFile).ConfigureAwait(true);
        var baselineTimestamp = Assert.Single(
            coordinator.ClientSettings.Subscriptions).LastUpdated;

        loader.CacheManifestContent = "{\"generation\":\"candidate\",\"entries\":{}}";
        loader.ThrowAfterCommit = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RefreshSubscriptionsAsync(CancellationToken.None)).ConfigureAwait(true);

        Assert.Equal(
            baselineManifest,
            await File.ReadAllTextAsync(paths.CacheManifestFile).ConfigureAwait(true));
        Assert.Equal(
            baselineTimestamp,
            Assert.Single(coordinator.ClientSettings.Subscriptions).LastUpdated);
        Assert.False(coordinator.ClientSettings.Enabled);
        Assert.False(File.Exists(paths.TransactionJournalFile));
    }

    [Fact]
    public async Task InterruptedDisabledRefreshRestoresPreviousSettingsAndCache()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        paths.EnsureDirectories();
        var previousSettings = Settings(direct, proxy);
        var previousCacheManifest = "{\"generation\":\"baseline\",\"entries\":{}}";
        using (var settingsStore = new SettingsStore(paths))
        {
            await settingsStore.SaveAsync(previousSettings).ConfigureAwait(true);
        }

        await File.WriteAllTextAsync(
            paths.CacheManifestFile,
            "{\"generation\":\"candidate\",\"entries\":{}}").ConfigureAwait(true);
        await File.WriteAllTextAsync(
            paths.TransactionJournalFile,
            JsonSerializer.Serialize(
                new
                {
                    PreviousSettingsJson = JsonSerializer.Serialize(
                        previousSettings,
                        JsonDefaults.Create()),
                    PreviousLkgManifestJson = (string?)null,
                    PreviousCacheManifestJson = previousCacheManifest
                },
                JsonDefaults.Create())).ConfigureAwait(true);

        var process = new FakeProcessManager();
        using var settingsStoreAfterRestart = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStoreAfterRestart,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);

        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.True(coordinator.IsReady);
        Assert.False(coordinator.ClientSettings.Enabled);
        Assert.False(process.IsRunning);
        Assert.Equal(
            previousCacheManifest,
            await File.ReadAllTextAsync(paths.CacheManifestFile).ConfigureAwait(true));
        Assert.False(File.Exists(paths.TransactionJournalFile));
    }

    [Fact]
    public async Task CorruptLkgManifestBlocksRecoveryWithoutStartingCore()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var validLkgManifest = string.Empty;
        var validCacheManifest = "{\"generation\":\"baseline\",\"entries\":{}}";

        using (var initialSettingsStore = new SettingsStore(paths))
        using (var initialLogs = new FileLogBuffer(paths))
        {
            await initialSettingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
            await using var coordinator = new NetSplitCoordinator(
                paths,
                initialSettingsStore,
                new FakeSecretProtector(),
                new FakeAdapterProvider([direct, proxy]),
                new ConfigurationValidatorFacade(),
                new FakeSubscriptionLoader(),
                new FakeProcessManager(),
                new FakeController(),
                initialLogs);
            await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
            validLkgManifest = await File.ReadAllTextAsync(
                paths.LastKnownGoodManifestFile).ConfigureAwait(true);
        }
        await File.WriteAllTextAsync(
            paths.CacheManifestFile,
            validCacheManifest).ConfigureAwait(true);

        await File.WriteAllTextAsync(
            paths.RuntimeConfigFile,
            "candidate config").ConfigureAwait(true);
        await File.WriteAllTextAsync(
            paths.TransactionJournalFile,
            JsonSerializer.Serialize(
                new
                {
                    PreviousLkgManifestJson = "{not-json",
                    PreviousCacheManifestJson = "{not-json"
                },
                JsonDefaults.Create())).ConfigureAwait(true);

        var process = new FakeProcessManager();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await using var coordinatorWithCorruption = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);

        await coordinatorWithCorruption.InitializeAsync(CancellationToken.None)
            .ConfigureAwait(true);

        Assert.False(process.IsRunning);
        Assert.Equal(RuntimeMode.Misconfigured, coordinatorWithCorruption.Status.Mode);
        Assert.True(File.Exists(paths.TransactionJournalFile));
        Assert.Equal(
            validLkgManifest,
            await File.ReadAllTextAsync(paths.LastKnownGoodManifestFile).ConfigureAwait(true));
        Assert.Equal(
            validCacheManifest,
            await File.ReadAllTextAsync(paths.CacheManifestFile).ConfigureAwait(true));
    }

    [Fact]
    public async Task InitializationFailureStopsResidualCoreAndReportsFailClosedRuntime()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var settings = Settings(direct, proxy) with { Enabled = true };
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(settings).ConfigureAwait(true);
        await File.WriteAllTextAsync(
            paths.TransactionJournalFile,
            "{not-json").ConfigureAwait(true);

        var process = new FakeProcessManager();
        await process.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);

        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.False(process.IsRunning);
        Assert.False(coordinator.IsReady);
        Assert.Equal(RuntimeMode.Misconfigured, coordinator.Status.Mode);
        Assert.False(coordinator.Status.TunEnabled);
        Assert.False(coordinator.Status.DnsEnabled);
        Assert.False(coordinator.Status.ProxyRouteAvailable);

        var diagnostics = await coordinator.GetDiagnosticsAsync(
            CancellationToken.None).ConfigureAwait(true);
        Assert.False(diagnostics.ServiceReady);
        Assert.Equal(CoordinatorReadiness.RecoveryRequired, diagnostics.Readiness);
    }

    [Fact]
    public async Task CacheFallbackDoesNotAdvanceSubscriptionTimestampOrExposeSecrets()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader { ReturnFromCache = true },
            new FakeProcessManager(),
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        var subscription = Assert.Single(coordinator.ClientSettings.Subscriptions);
        Assert.Null(subscription.LastUpdated);
        var json = System.Text.Json.JsonSerializer.Serialize(coordinator.ClientSettings);
        Assert.DoesNotContain("ProtectedSource", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ControllerSecret", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResidentialProxyCredentialsAreProtectedAndExcludedFromClientSettingsAndLogs()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var protector = new EncodingSecretProtector();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            protector,
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        await coordinator.UpdateResidentialProxyAsync(
            new UpdateResidentialProxyRequest
            {
                Enabled = true,
                Host = "RESIDENTIAL.EXAMPLE",
                Port = 1080,
                AuthenticationEnabled = true,
                Username = "resident-user",
                Password = "resident-password",
                ReplaceCredentials = true,
                RouteMode = ResidentialProxyRouteMode.ThroughAirport
            },
            CancellationToken.None).ConfigureAwait(true);

        var stored = await settingsStore.LoadAsync().ConfigureAwait(true);
        Assert.Equal("residential.example", stored.ResidentialProxy.Host);
        Assert.NotEqual("resident-user", stored.ResidentialProxy.ProtectedUsername);
        Assert.NotEqual("resident-password", stored.ResidentialProxy.ProtectedPassword);
        var snapshotJson = JsonSerializer.Serialize(coordinator.ClientSettings);
        Assert.DoesNotContain("resident-user", snapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("resident-password", snapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedUsername", snapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedPassword", snapshotJson, StringComparison.Ordinal);
        Assert.True(coordinator.ClientSettings.ResidentialProxy.HasCredentials);

        await coordinator.UpdateResidentialProxyAsync(
            new UpdateResidentialProxyRequest
            {
                Enabled = true,
                Host = "residential.example",
                Port = 1080,
                AuthenticationEnabled = true,
                ReplaceCredentials = false,
                RouteMode = ResidentialProxyRouteMode.DirectNic2
            },
            CancellationToken.None).ConfigureAwait(true);
        var preserved = await settingsStore.LoadAsync().ConfigureAwait(true);
        Assert.Equal(
            stored.ResidentialProxy.ProtectedUsername,
            preserved.ResidentialProxy.ProtectedUsername);
        Assert.Equal(
            stored.ResidentialProxy.ProtectedPassword,
            preserved.ResidentialProxy.ProtectedPassword);

        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        var runtimeConfig = await File.ReadAllTextAsync(
            paths.RuntimeConfigFile).ConfigureAwait(true);
        Assert.Contains("resident-user", runtimeConfig, StringComparison.Ordinal);
        Assert.Contains("resident-password", runtimeConfig, StringComparison.Ordinal);
        var logText = string.Join(Environment.NewLine, logs.Snapshot());
        Assert.DoesNotContain("resident-user", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("resident-password", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("residential.example", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddingRuleWhileEnabledAppliesItImmediately()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var process = new FakeProcessManager();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        await coordinator.AddRuleAsync(
            new CustomRule
            {
                MatchType = RuleMatchType.Domain,
                Action = RuleAction.Block,
                Value = "ads.example"
            },
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, process.StartAttempts);
        Assert.Single(coordinator.ClientSettings.Rules);
        Assert.Contains(
            "DOMAIN,ads.example,REJECT",
            await File.ReadAllTextAsync(paths.RuntimeConfigFile).ConfigureAwait(true),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRuleApplyRestoresPreviousSettingsAndConfig()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var process = new FakeProcessManager();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        var baselineConfig = await File.ReadAllTextAsync(paths.RuntimeConfigFile).ConfigureAwait(true);
        process.FailStartsRemaining = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.AddRuleAsync(
                new CustomRule
                {
                    MatchType = RuleMatchType.Domain,
                    Action = RuleAction.Block,
                    Value = "ads.example"
                },
                CancellationToken.None)).ConfigureAwait(true);

        Assert.Empty(coordinator.ClientSettings.Rules);
        Assert.True(process.IsRunning);
        Assert.Equal(3, process.StartAttempts);
        Assert.Equal(
            baselineConfig,
            await File.ReadAllTextAsync(paths.RuntimeConfigFile).ConfigureAwait(true));
    }

    [Fact]
    public async Task FailedDisableRestoresRunningCoreAndSettings()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        var process = new FakeProcessManager();
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            process,
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);
        process.FailStopsRemaining = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.DisableAsync(CancellationToken.None)).ConfigureAwait(true);

        Assert.True(process.IsRunning);
        Assert.True(coordinator.ClientSettings.Enabled);
        Assert.True((await settingsStore.LoadAsync().ConfigureAwait(true)).Enabled);
    }

    [Fact]
    public async Task PipeServerQueuesMoreClientsThanItsListenerPool()
    {
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        var pipeName = $"net-split-test-{Guid.NewGuid():N}";
        using var server = new PipeServerHostedService(coordinator, logs, paths, pipeName);
        await server.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            var requests = Enumerable.Range(0, 32)
                .Select(_ => new NamedPipeRpcClient(pipeName).SendAsync<RuntimeStatus>(
                    RpcCommands.GetStatus,
                    timeout: TimeSpan.FromSeconds(20)))
                .ToArray();

            var responses = await Task.WhenAll(requests).ConfigureAwait(true);

            Assert.All(responses, response => Assert.NotNull(response));
            var diagnostics = await new NamedPipeRpcClient(pipeName).SendAsync<DiagnosticsSnapshot>(
                RpcCommands.GetDiagnostics,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            Assert.NotNull(diagnostics);
            Assert.True(diagnostics!.ServiceReady);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task PipeDispatchesProxyExitModeSwitch()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(
            Settings(direct, proxy) with
            {
                ResidentialProxy = new ResidentialProxySettings
                {
                    Enabled = true,
                    Host = "residential.example",
                    Port = 1080,
                    AuthenticationEnabled = false,
                    RouteMode = ResidentialProxyRouteMode.ThroughAirport
                }
            }).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        var pipeName = $"net-split-test-{Guid.NewGuid():N}";
        using var server = new PipeServerHostedService(coordinator, logs, paths, pipeName);
        await server.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            var client = new NamedPipeRpcClient(pipeName);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.SendAsync(
                    RpcCommands.SetProxyExitMode,
                    new { },
                    timeout: TimeSpan.FromSeconds(20))).ConfigureAwait(true);
            var unchangedSettings = await client.SendAsync<ClientSettingsSnapshot>(
                RpcCommands.GetSettings,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            Assert.NotNull(unchangedSettings);
            Assert.True(unchangedSettings!.ResidentialProxy.Enabled);

            await client.SendAsync(
                RpcCommands.SetProxyExitMode,
                new SetProxyExitModeRequest { Mode = ProxyExitMode.Airport },
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(true);

            var airportSettings = await client.SendAsync<ClientSettingsSnapshot>(
                RpcCommands.GetSettings,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            Assert.NotNull(airportSettings);
            Assert.False(airportSettings!.ResidentialProxy.Enabled);

            await client.SendAsync(
                RpcCommands.SetProxyExitMode,
                new SetProxyExitModeRequest { Mode = ProxyExitMode.Residential },
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(true);

            var residentialSettings = await client.SendAsync<ClientSettingsSnapshot>(
                RpcCommands.GetSettings,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            Assert.NotNull(residentialSettings);
            Assert.True(residentialSettings!.ResidentialProxy.Enabled);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task PipeDispatchesBatchProxyDelayMeasurement()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var controller = new FakeController
        {
            SelectedProxyHealthy = true,
            AvailableProxyNames =
            [
                MihomoConfigGenerator.AutoProxyGroupName,
                "fast-node",
                "offline-node"
            ]
        };
        controller.DelayByProxyName["fast-node"] = 28;
        controller.DelayByProxyName["offline-node"] = null;
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            controller,
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
        await coordinator.EnableAsync(CancellationToken.None).ConfigureAwait(true);

        var pipeName = $"net-split-test-{Guid.NewGuid():N}";
        using var server = new PipeServerHostedService(coordinator, logs, paths, pipeName);
        await server.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            var result = await new NamedPipeRpcClient(pipeName)
                .SendAsync<ProxyDelayBatchResult>(
                    RpcCommands.MeasureProxyDelays,
                    timeout: TimeSpan.FromSeconds(20))
                .ConfigureAwait(true);

            Assert.NotNull(result);
            Assert.Equal(2, result!.Results.Count);
            Assert.Equal(
                28,
                result.Results.Single(item => item.Name == "fast-node")
                    .DelayMilliseconds);
            Assert.Null(
                result.Results.Single(item => item.Name == "offline-node")
                    .DelayMilliseconds);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DiagnosticsSnapshotContainsSafeSummaryWithoutSecrets()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(
            Settings(direct, proxy) with
            {
                ControllerSecret = "controller-secret",
                ResidentialProxy = new ResidentialProxySettings
                {
                    Enabled = true,
                    Host = "proxy.example",
                    ProtectedUsername = "proxy-user-secret",
                    ProtectedPassword = "proxy-password-secret"
                },
                Subscriptions =
                [
                    new SubscriptionSettings
                    {
                        Name = "secret-subscription",
                        ProtectedSource = "https://user:password@example.test/sub"
                    }
                ]
            }).ConfigureAwait(true);

        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        var snapshot = await coordinator.GetDiagnosticsAsync(
            CancellationToken.None).ConfigureAwait(true);
        var json = JsonSerializer.Serialize(snapshot, JsonDefaults.Create());

        Assert.True(snapshot.ServiceReady);
        Assert.False(snapshot.Runtime.Enabled);
        Assert.Equal(2, snapshot.Adapters.Count);
        Assert.Equal(1, snapshot.Settings.SubscriptionCount);
        Assert.True(snapshot.Settings.ResidentialProxyHasCredentials);
        Assert.DoesNotContain("controller-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy-user-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy-password-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("https://user:password@example.test/sub", json, StringComparison.Ordinal);
        Assert.Contains(
            snapshot.Files,
            file => file.Name.Equals("runtime-config.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PipeRejectsMutationsWhenInitializationFailed()
    {
        var direct = Adapter("direct", "main", "192.168.6.2", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1");
        var paths = new AppPaths(_tempRoot);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.TransactionJournalFile,
            JsonSerializer.Serialize(
                new
                {
                    PreviousLkgManifestJson = "{not-json",
                    PreviousCacheManifestJson = (string?)null
                },
                JsonDefaults.Create())).ConfigureAwait(true);

        using var settingsStore = new SettingsStore(paths);
        using var logs = new FileLogBuffer(paths);
        await settingsStore.SaveAsync(Settings(direct, proxy)).ConfigureAwait(true);
        await using var coordinator = new NetSplitCoordinator(
            paths,
            settingsStore,
            new FakeSecretProtector(),
            new FakeAdapterProvider([direct, proxy]),
            new ConfigurationValidatorFacade(),
            new FakeSubscriptionLoader(),
            new FakeProcessManager(),
            new FakeController(),
            logs);
        await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.False(coordinator.IsReady);
        var pipeName = $"net-split-test-{Guid.NewGuid():N}";
        using var server = new PipeServerHostedService(coordinator, logs, paths, pipeName);
        await server.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            var client = new NamedPipeRpcClient(pipeName);
            var status = await client.SendAsync<RuntimeStatus>(
                RpcCommands.GetStatus,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            Assert.Equal(RuntimeMode.Misconfigured, status!.Mode);

            var history = await client.SendAsync<IReadOnlyList<TrafficPoint>>(
                RpcCommands.GetTrafficHistory,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            Assert.NotNull(history);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.SendAsync(
                    RpcCommands.AddRule,
                    new CustomRule
                    {
                        MatchType = RuleMatchType.Domain,
                        Action = RuleAction.Block,
                        Value = "blocked.example"
                    },
                    timeout: TimeSpan.FromSeconds(20))).ConfigureAwait(true);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private static string ReadSnapshotCacheManifestPath(AppPaths paths)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(paths.LastKnownGoodManifestFile));
        var fileName = document.RootElement
            .GetProperty("cacheManifestFileName")
            .GetString()
            ?? throw new InvalidDataException("Snapshot cache manifest file name is missing.");
        return Path.Combine(paths.LastKnownGoodDirectory, fileName);
    }

    private static async Task RewriteSnapshotAsLegacyAsync(AppPaths paths)
    {
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(paths.LastKnownGoodManifestFile).ConfigureAwait(false));
        var root = document.RootElement;
        var legacyManifest = new
        {
            Generation = root.GetProperty("generation").GetString(),
            ConfigFileName = root.GetProperty("configFileName").GetString(),
            SettingsFileName = root.GetProperty("settingsFileName").GetString(),
            ConfigSha256 = root.GetProperty("configSha256").GetString(),
            SettingsSha256 = root.GetProperty("settingsSha256").GetString(),
            CacheGeneration = root.GetProperty("cacheGeneration").GetString()
        };
        await File.WriteAllTextAsync(
            paths.LastKnownGoodManifestFile,
            JsonSerializer.Serialize(legacyManifest, JsonDefaults.Create())).ConfigureAwait(false);
    }

    private static SplitRouteSettings Settings(
        NetworkAdapterSnapshot direct,
        NetworkAdapterSnapshot proxy)
    {
        return new SplitRouteSettings
        {
            MihomoPath = @"C:\fake\mihomo.exe",
            ControllerSecret = "secret",
            DirectAdapter = new AdapterBinding
            {
                Id = direct.Id,
                MacAddress = direct.MacAddress,
                LastKnownName = direct.Name
            },
            ProxyAdapter = new AdapterBinding
            {
                Id = proxy.Id,
                MacAddress = proxy.MacAddress,
                LastKnownName = proxy.Name
            },
            Subscriptions =
            [
                new SubscriptionSettings
                {
                    Name = "test",
                    ProtectedSource = "test"
                }
            ]
        };
    }

    private static NetworkAdapterSnapshot Adapter(
        string id,
        string name,
        string address,
        string gateway)
    {
        var prefix = address.StartsWith("192.168.6.", StringComparison.Ordinal)
            ? "192.168.6.0/24"
            : "192.168.0.0/24";
        return new NetworkAdapterSnapshot
        {
            Id = id,
            Name = name,
            Description = name,
            MacAddress = id,
            IsUp = true,
            IsSelectable = true,
            Ipv4Addresses = [address],
            Gateways = [gateway],
            DnsServers = [gateway],
            ConnectedPrefixes = [prefix]
        };
    }

    private sealed class FakeAdapterProvider : INetworkAdapterProvider
    {
        public FakeAdapterProvider(IReadOnlyList<NetworkAdapterSnapshot> adapters)
        {
            Adapters = adapters;
        }

        public IReadOnlyList<NetworkAdapterSnapshot> Adapters { get; set; }

        public IReadOnlyList<NetworkAdapterSnapshot> GetAdapters()
        {
            return Adapters;
        }

        public NetworkAdapterSnapshot? Resolve(AdapterBinding? binding)
        {
            return binding is null
                ? null
                : Adapters.FirstOrDefault(item => item.Id == binding.Id);
        }
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string value)
        {
            return value;
        }

        public string Unprotect(string protectedValue)
        {
            return protectedValue;
        }
    }

    private sealed class EncodingSecretProtector : ISecretProtector
    {
        public string Protect(string value)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        }

        public string Unprotect(string protectedValue)
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
        }
    }

    private sealed class FakeSubscriptionLoader : ISubscriptionLoader
    {
        public bool ReturnFromCache { get; init; }
        public AppPaths? CachePaths { get; init; }
        public string CacheManifestContent { get; set; } =
            "{\"generation\":\"test\",\"entries\":{}}";
        public bool ThrowAfterCommit { get; set; }

        public Task<IReadOnlyList<SubscriptionDocument>> LoadAllAsync(
            IReadOnlyList<SubscriptionSettings> subscriptions,
            bool forceRefresh,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SubscriptionDocument> documents = subscriptions
                .Where(item => item.Enabled)
                .Select(item => new SubscriptionDocument
                {
                    Id = item.Id,
                    Name = item.Name,
                    Yaml = """
                        proxies:
                          - name: node
                            type: ss
                            server: 203.0.113.1
                            port: 443
                            cipher: aes-128-gcm
                            password: test
                        """,
                    FromCache = ReturnFromCache
                })
                .ToArray();
            return Task.FromResult(documents);
        }

        public async Task<SubscriptionDocument> LoadAsync(
            SubscriptionSettings subscription,
            bool forceRefresh,
            CancellationToken cancellationToken = default)
        {
            return (await LoadAllAsync([subscription], forceRefresh, cancellationToken)
                .ConfigureAwait(false))[0];
        }

        public Task CommitGenerationAsync(
            IReadOnlyList<SubscriptionDocument> documents,
            CancellationToken cancellationToken = default)
        {
            if (CachePaths is not null)
            {
                CachePaths.EnsureDirectories();
                File.WriteAllText(CachePaths.CacheManifestFile, CacheManifestContent);
                if (ThrowAfterCommit)
                {
                    throw new InvalidOperationException("simulated cache commit failure");
                }
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeProcessManager : IMihomoProcessManager
    {
        public event EventHandler? Exited
        {
            add { }
            remove { }
        }

        public bool IsRunning { get; private set; }
        public int FailStartsRemaining { get; set; }
        public int FailStopsRemaining { get; set; }
        public int StartAttempts { get; private set; }
        public int StopAttempts { get; private set; }

        public Task<ProcessValidationResult> ValidateAsync(
            SplitRouteSettings settings,
            string configPath,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProcessValidationResult(true, "ok"));
        }

        public Task StartAsync(SplitRouteSettings settings, CancellationToken cancellationToken)
        {
            StartAttempts++;
            if (FailStartsRemaining > 0)
            {
                FailStartsRemaining--;
                IsRunning = false;
                throw new InvalidOperationException("simulated start failure");
            }

            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(SplitRouteSettings settings, CancellationToken cancellationToken)
        {
            StopAttempts++;
            if (FailStopsRemaining > 0)
            {
                FailStopsRemaining--;
                throw new InvalidOperationException("simulated stop failure");
            }

            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeController : IMihomoControllerClient
    {
        public bool FailHealthChecks { get; set; }
        public bool FailNextSnapshot { get; set; }
        public bool FailDirectDelay { get; set; }
        public bool FailProxyDelay { get; set; }
        public bool FailResidentialDelay { get; set; }
        public bool CancelNextSnapshot { get; set; }
        public Action? OnNextSnapshot { get; set; }
        public bool TunEnabled { get; set; } = true;
        public bool DnsEnabled { get; set; } = true;
        public bool? SelectedProxyHealthy { get; set; }
        public bool? ResidentialProxyHealthy { get; set; }
        public List<string> MeasuredProxyNames { get; } = [];
        public IReadOnlyList<string> AvailableProxyNames { get; set; } =
            [MihomoConfigGenerator.AutoProxyGroupName, "node"];
        public Dictionary<string, int?> DelayByProxyName { get; } =
            new(StringComparer.Ordinal);
        public int SnapshotCalls { get; private set; }

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
            SnapshotCalls++;
            if (FailNextSnapshot)
            {
                FailNextSnapshot = false;
                throw new HttpRequestException("simulated initial snapshot failure");
            }

            if (CancelNextSnapshot)
            {
                CancelNextSnapshot = false;
                throw new OperationCanceledException("simulated post-commit snapshot cancellation");
            }

            var onNextSnapshot = OnNextSnapshot;
            OnNextSnapshot = null;
            onNextSnapshot?.Invoke();

            if (FailHealthChecks)
            {
                throw new HttpRequestException("simulated controller outage");
            }

            var selectedProxyHealthy = settings.ResidentialProxy.Enabled
                ? ResidentialProxyHealthy
                : SelectedProxyHealthy;
            return Task.FromResult(
                new MihomoApiSnapshot(
                    TunEnabled,
                    MihomoConfigGenerator.AutoProxyGroupName,
                    AvailableProxyNames)
                {
                    DnsEnabled = DnsEnabled,
                    SelectedProxyHealthy = selectedProxyHealthy,
                    EffectiveProxy = settings.ResidentialProxy.Enabled
                        ? MihomoConfigGenerator.ResidentialProxyName
                        : "node",
                    HealthyProxies = SelectedProxyHealthy is true
                        ? AvailableProxyNames.Where(name => !name.Equals(
                                MihomoConfigGenerator.AutoProxyGroupName,
                                StringComparison.Ordinal))
                            .ToArray()
                        : []
                });
        }

        public Task<int?> MeasureDelayAsync(
            SplitRouteSettings settings,
            string proxyName,
            string url,
            CancellationToken cancellationToken)
        {
            MeasuredProxyNames.Add(proxyName);
            if (proxyName.Equals(
                    MihomoConfigGenerator.ProxyGroupName,
                    StringComparison.Ordinal)
                && FailProxyDelay)
            {
                throw new HttpRequestException("simulated proxy delay failure");
            }

            if (proxyName.Equals(
                    MihomoConfigGenerator.DirectProxyName,
                    StringComparison.Ordinal)
                && FailDirectDelay)
            {
                throw new HttpRequestException("simulated direct delay failure");
            }

            if (proxyName.Equals(
                    MihomoConfigGenerator.ResidentialProxyName,
                    StringComparison.Ordinal)
                && FailResidentialDelay)
            {
                throw new HttpRequestException("simulated residential delay failure");
            }

            if (DelayByProxyName.TryGetValue(proxyName, out var delay))
            {
                return Task.FromResult(delay);
            }

            return Task.FromResult<int?>(25);
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
