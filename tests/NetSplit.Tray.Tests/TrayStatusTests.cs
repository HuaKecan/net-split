using NetSplit.Core;
using NetSplit.Tray;

namespace NetSplit.Tray.Tests;

public sealed class TrayStatusTests
{
    [Fact]
    public void BackgroundStartupSuppressesOfflineUntilGraceExpires()
    {
        var startedAt = new DateTimeOffset(
            2026,
            8,
            20,
            8,
            57,
            47,
            TimeSpan.FromHours(8));
        var grace = new TrayStartupGrace(
            enabled: true,
            startedAt,
            TimeSpan.FromMinutes(3));

        Assert.True(grace.ShouldSuppressOffline(startedAt.AddMinutes(2)));
        Assert.False(grace.ShouldSuppressOffline(startedAt.AddMinutes(3)));
    }

    [Fact]
    public void ManualStartupDoesNotSuppressOffline()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var grace = new TrayStartupGrace(
            enabled: false,
            startedAt,
            TimeSpan.FromMinutes(3));

        Assert.False(grace.ShouldSuppressOffline(startedAt));
    }

    [Fact]
    public void ConnectedServiceDisablesRemainingStartupGrace()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var grace = new TrayStartupGrace(
            enabled: true,
            startedAt,
            TimeSpan.FromMinutes(3));

        grace.ObserveConnected();

        Assert.False(grace.ShouldSuppressOffline(startedAt.AddSeconds(10)));
    }

    [Fact]
    public void HealthyPresentationIncludesEffectiveAutomaticNode()
    {
        var presentation = TrayStatusPresenter.FromStatus(new RuntimeStatus
        {
            Mode = RuntimeMode.Healthy,
            Enabled = true,
            MihomoRunning = true,
            TunEnabled = true,
            DnsEnabled = true,
            DnsStatusKnown = true,
            CurrentProxy = MihomoConfigGenerator.AutoProxyGroupName,
            EffectiveProxy = "Hong Kong 01"
        });

        Assert.Equal(TrayHealthLevel.Healthy, presentation.Health);
        Assert.Contains("Hong Kong 01", presentation.HeaderText, StringComparison.Ordinal);
        Assert.Contains("Hong Kong 01", presentation.ToolTipText, StringComparison.Ordinal);
        Assert.True(presentation.ToolTipText.Length <= 63);
    }

    [Fact]
    public void ProxyFailurePresentationExplainsFailClosedBehavior()
    {
        var presentation = TrayStatusPresenter.FromStatus(new RuntimeStatus
        {
            Mode = RuntimeMode.ProxyUnavailable,
            Enabled = true,
            MihomoRunning = true,
            TunEnabled = true,
            DnsEnabled = true,
            DnsStatusKnown = true,
            ProxyRouteFailure = ProxyRouteFailureReason.NoHealthyProxy
        });

        Assert.Equal(TrayHealthLevel.Degraded, presentation.Health);
        Assert.Contains("节点全部失效", presentation.HeaderText, StringComparison.Ordinal);
        Assert.Contains("国外流量已阻断", presentation.NotificationText, StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationTrackerDebouncesProblemsAndRecovery()
    {
        var tracker = new TrayNotificationTracker();
        var degraded = new TrayStatusPresentation(
            TrayHealthLevel.Degraded,
            "proxy-down",
            "proxy down",
            "proxy down",
            "proxy down");
        var transitioning = new TrayStatusPresentation(
            TrayHealthLevel.Transitioning,
            "starting",
            "starting",
            "starting",
            string.Empty);
        var healthy = new TrayStatusPresentation(
            TrayHealthLevel.Healthy,
            "healthy",
            "healthy",
            "healthy",
            string.Empty);

        Assert.Equal(TrayNotificationKind.None, tracker.Observe(degraded));
        Assert.Equal(TrayNotificationKind.Problem, tracker.Observe(degraded));
        Assert.Equal(TrayNotificationKind.None, tracker.Observe(degraded));
        Assert.Equal(TrayNotificationKind.None, tracker.Observe(transitioning));
        Assert.Equal(TrayNotificationKind.None, tracker.Observe(healthy));
        Assert.Equal(TrayNotificationKind.Recovered, tracker.Observe(healthy));
        Assert.Equal(TrayNotificationKind.None, tracker.Observe(healthy));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("unexpected", false)]
    [InlineData(null, false)]
    public void SilentNotificationPreferenceParsesRegistryValues(
        object? value,
        bool expected)
    {
        Assert.Equal(expected, UserPreferences.ParseBoolean(value));
    }

    [Fact]
    public void TrayIconSetCreatesIconsForEveryHealthLevel()
    {
        using var icons = new TrayIconSet();

        foreach (var health in Enum.GetValues<TrayHealthLevel>())
        {
            var icon = icons.Resolve(health);
            Assert.Equal(new Size(32, 32), icon.Size);
        }
    }
}
