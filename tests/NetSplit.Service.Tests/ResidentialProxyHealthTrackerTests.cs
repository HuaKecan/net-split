using NetSplit.Service;

namespace NetSplit.Service.Tests;

public sealed class ResidentialProxyHealthTrackerTests
{
    [Fact]
    public void SuccessfulDelayProbeOverridesStaleUnhealthySnapshot()
    {
        var tracker = new ResidentialProxyHealthTracker();

        Assert.Null(tracker.Observe(false, delayProbeSucceeded: false));
        Assert.False(tracker.Observe(false, delayProbeSucceeded: false));
        Assert.True(tracker.Observe(false, delayProbeSucceeded: true));
    }

    [Fact]
    public void TransientFailureDoesNotChangeStableHealth()
    {
        var tracker = new ResidentialProxyHealthTracker();

        Assert.True(tracker.Observe(true, delayProbeSucceeded: false));
        Assert.True(tracker.Observe(false, delayProbeSucceeded: false));
        Assert.True(tracker.Observe(true, delayProbeSucceeded: false));
    }

    [Fact]
    public void SustainedFailureAndRecoveryRequireConsecutiveObservations()
    {
        var tracker = new ResidentialProxyHealthTracker();

        Assert.True(tracker.Observe(true, delayProbeSucceeded: false));
        Assert.True(tracker.Observe(false, delayProbeSucceeded: false));
        Assert.False(tracker.Observe(false, delayProbeSucceeded: false));
        Assert.False(tracker.Observe(true, delayProbeSucceeded: false));
        Assert.True(tracker.Observe(true, delayProbeSucceeded: false));
    }

    [Fact]
    public void InitialFailureRemainsUnknownUntilConfirmed()
    {
        var tracker = new ResidentialProxyHealthTracker();

        Assert.Null(tracker.Observe(false, delayProbeSucceeded: false));
        Assert.False(tracker.Observe(false, delayProbeSucceeded: false));
    }
}
