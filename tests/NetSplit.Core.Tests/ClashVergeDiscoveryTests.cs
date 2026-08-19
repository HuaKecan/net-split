using NetSplit.Core;

namespace NetSplit.Core.Tests;

public sealed class ClashVergeDiscoveryTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "net-split-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void FindGeoDataDirectoryReturnsFirstCompleteCandidate()
    {
        var incomplete = Path.Combine(_tempRoot, "incomplete");
        var complete = Path.Combine(_tempRoot, "complete");
        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(complete);
        File.WriteAllText(Path.Combine(incomplete, "geoip.dat"), "geoip");
        File.WriteAllText(Path.Combine(complete, "geoip.dat"), "geoip");
        File.WriteAllText(Path.Combine(complete, "geosite.dat"), "geosite");

        var result = ClashVergeDiscovery.FindGeoDataDirectory([incomplete, complete]);

        Assert.Equal(complete, result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, true);
        }
    }
}
