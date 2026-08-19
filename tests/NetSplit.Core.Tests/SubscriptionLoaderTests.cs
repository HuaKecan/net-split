using NetSplit.Core;

namespace NetSplit.Core.Tests;

public sealed class SubscriptionLoaderTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "net-split-subscription-tests",
        Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task CommitGenerationKeepsImmutableGenerationsAndLoadsLatestCache()
    {
        var paths = new AppPaths(_root);
        var sourcePath = Path.Combine(_root, "subscription.yaml");
        var subscription = new SubscriptionSettings
        {
            Name = "test",
            SourceKind = SubscriptionSourceKind.File,
            ProtectedSource = sourcePath
        };
        var loader = new SubscriptionLoader(
            paths,
            new PassthroughSecretProtector(),
            new HttpClient());

        await File.WriteAllTextAsync(
            sourcePath,
            """
            proxies:
              - name: old
            """);
        var oldDocument = await loader.LoadAsync(
            subscription,
            forceRefresh: true).ConfigureAwait(true);
        await loader.CommitGenerationAsync([oldDocument]).ConfigureAwait(true);
        var oldManifest = await File.ReadAllTextAsync(
            paths.CacheManifestFile).ConfigureAwait(true);
        var oldGeneration = SubscriptionLoader.ReadGeneration(
            oldManifest,
            paths.CacheDirectory);

        await File.WriteAllTextAsync(
            sourcePath,
            """
            proxies:
              - name: new
            """);
        var newDocument = await loader.LoadAsync(
            subscription,
            forceRefresh: true).ConfigureAwait(true);
        await loader.CommitGenerationAsync([newDocument]).ConfigureAwait(true);
        var newManifest = await File.ReadAllTextAsync(
            paths.CacheManifestFile).ConfigureAwait(true);
        var newGeneration = SubscriptionLoader.ReadGeneration(
            newManifest,
            paths.CacheDirectory);

        Assert.NotEqual(oldGeneration, newGeneration);
        Assert.Equal(2, Directory.GetDirectories(paths.CacheGenerationsDirectory).Length);
        SubscriptionLoader.ValidateManifest(oldManifest, paths.CacheDirectory);
        SubscriptionLoader.ValidateManifest(newManifest, paths.CacheDirectory);

        var cached = await loader.LoadAsync(
            subscription with { LastUpdated = DateTimeOffset.UtcNow },
            forceRefresh: false).ConfigureAwait(true);
        Assert.True(cached.FromCache);
        Assert.Contains("name: new", cached.Yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateManifestRejectsPathsOutsideCacheRoot()
    {
        var paths = new AppPaths(_root);
        paths.EnsureDirectories();
        var manifest = $$"""
        {
          "generation": "test",
          "entries": {
            "{{Guid.NewGuid():N}}": "..\\outside.yaml"
          }
        }
        """;

        Assert.Throws<InvalidDataException>(() =>
            SubscriptionLoader.ValidateManifest(
                manifest,
                paths.CacheDirectory,
                requireFiles: false));
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
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
}
