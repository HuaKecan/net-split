using NetSplit.Core;

namespace NetSplit.Core.Tests;

public sealed class TrafficHistoryBufferTests
{
    [Fact]
    public void SnapshotReturnsOldestFirstAndKeepsOnlyCapacity()
    {
        var buffer = new TrafficHistoryBuffer();

        for (var index = 0; index < TrafficHistoryBuffer.Capacity + 3; index++)
        {
            buffer.Add(new TrafficPoint
            {
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index),
                DirectReceiveBps = index
            });
        }

        var snapshot = buffer.Snapshot();

        Assert.Equal(TrafficHistoryBuffer.Capacity, snapshot.Count);
        Assert.Equal(3, snapshot[0].DirectReceiveBps);
        Assert.Equal(
            TrafficHistoryBuffer.Capacity + 2,
            snapshot[^1].DirectReceiveBps);
    }

    [Fact]
    public void SnapshotIsIndependentFromLaterWrites()
    {
        var buffer = new TrafficHistoryBuffer();
        buffer.Add(new TrafficPoint
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            ProxyReceiveBps = 10
        });

        var snapshot = buffer.Snapshot();
        buffer.Add(new TrafficPoint
        {
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(1),
            ProxyReceiveBps = 20
        });

        Assert.Single(snapshot);
        Assert.Equal(10, snapshot[0].ProxyReceiveBps);
        Assert.Equal(2, buffer.Snapshot().Count);
    }
}
