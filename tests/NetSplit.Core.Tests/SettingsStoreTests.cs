using System.Text.Json;
using NetSplit.Core;

namespace NetSplit.Core.Tests;

public sealed class SettingsStoreTests : IAsyncLifetime
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "net-split-settings-tests",
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
    public async Task LoadUpgradesLegacySettingsWithResidentialProxyDefaults()
    {
        var paths = new AppPaths(_tempRoot);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            JsonSerializer.Serialize(
                new
                {
                    SchemaVersion = 1,
                    Enabled = false,
                    ControllerSecret = "existing-secret"
                },
                JsonDefaults.Create())).ConfigureAwait(true);
        using var store = new SettingsStore(paths);

        var settings = await store.LoadAsync().ConfigureAwait(true);

        Assert.Equal(2, settings.SchemaVersion);
        Assert.NotNull(settings.ResidentialProxy);
        Assert.False(settings.ResidentialProxy.Enabled);
        Assert.Equal(1080, settings.ResidentialProxy.Port);
        Assert.Equal(
            ResidentialProxyRouteMode.ThroughAirport,
            settings.ResidentialProxy.RouteMode);
        Assert.Equal("existing-secret", settings.ControllerSecret);
    }
}
