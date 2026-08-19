using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NetSplit.Core;

public interface ISubscriptionLoader
{
    Task<IReadOnlyList<SubscriptionDocument>> LoadAllAsync(
        IReadOnlyList<SubscriptionSettings> subscriptions,
        bool forceRefresh,
        CancellationToken cancellationToken = default);

    Task<SubscriptionDocument> LoadAsync(
        SubscriptionSettings subscription,
        bool forceRefresh,
        CancellationToken cancellationToken = default);

    Task CommitGenerationAsync(
        IReadOnlyList<SubscriptionDocument> documents,
        CancellationToken cancellationToken = default);
}

public sealed record SubscriptionCacheManifest
{
    public string Generation { get; init; } = string.Empty;
    public Dictionary<string, string> Entries { get; init; } = new();
}

public sealed class SubscriptionLoader : ISubscriptionLoader
{
    private const int MaximumSubscriptionBytes = 16 * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly ISecretProtector _secretProtector;
    private readonly HttpClient _httpClient;

    public SubscriptionLoader(
        AppPaths paths,
        ISecretProtector secretProtector,
        HttpClient httpClient)
    {
        _paths = paths;
        _secretProtector = secretProtector;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("net-split", "0.1"));
    }

    public async Task<IReadOnlyList<SubscriptionDocument>> LoadAllAsync(
        IReadOnlyList<SubscriptionSettings> subscriptions,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        var documents = new List<SubscriptionDocument>();
        foreach (var subscription in subscriptions.Where(item => item.Enabled))
        {
            documents.Add(await LoadAsync(subscription, forceRefresh, cancellationToken).ConfigureAwait(false));
        }

        return documents;
    }

    public async Task<SubscriptionDocument> LoadAsync(
        SubscriptionSettings subscription,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var cachePath = ResolveCachePath(subscription.Id);
        var shouldRefresh = forceRefresh
            || cachePath is null
            || subscription.LastUpdated is null
            || DateTimeOffset.UtcNow - subscription.LastUpdated.Value
                >= TimeSpan.FromMinutes(Math.Max(subscription.UpdateIntervalMinutes, 5));

        if (!shouldRefresh)
        {
            return new SubscriptionDocument
            {
                Id = subscription.Id,
                Name = subscription.Name,
                Yaml = await File.ReadAllTextAsync(cachePath!, cancellationToken).ConfigureAwait(false),
                FromCache = true
            };
        }

        try
        {
            var source = _secretProtector.Unprotect(subscription.ProtectedSource);
            var yaml = subscription.SourceKind switch
            {
                SubscriptionSourceKind.Url => await DownloadAsync(source, cancellationToken).ConfigureAwait(false),
                SubscriptionSourceKind.File => await ReadFileAsync(source, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(subscription))
            };

            EnsureLooksLikeYaml(yaml);
            return new SubscriptionDocument
            {
                Id = subscription.Id,
                Name = subscription.Name,
                Yaml = yaml
            };
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or FormatException)
        {
            if (cachePath is null || !File.Exists(cachePath))
            {
                throw;
            }

            return new SubscriptionDocument
            {
                Id = subscription.Id,
                Name = subscription.Name,
                Yaml = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false),
                FromCache = true,
                Warning = $"订阅“{subscription.Name}”更新失败，已使用上次缓存。"
            };
        }
    }

    public async Task CommitGenerationAsync(
        IReadOnlyList<SubscriptionDocument> documents,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var generation = Guid.NewGuid().ToString("N");
        var generationDirectory = Path.Combine(_paths.CacheGenerationsDirectory, generation);
        Directory.CreateDirectory(generationDirectory);
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var document in documents)
            {
                EnsureLooksLikeYaml(document.Yaml);
                var fileName = $"{document.Id:N}.yaml";
                var path = Path.Combine(generationDirectory, fileName);
                await WriteAtomicallyAsync(path, document.Yaml, cancellationToken).ConfigureAwait(false);
                entries[document.Id.ToString("N")] = Path.Combine(
                    "generations",
                    generation,
                    fileName);
            }

            var manifest = new SubscriptionCacheManifest
            {
                Generation = generation,
                Entries = entries
            };
            var json = JsonSerializer.Serialize(manifest, JsonDefaults.Create());
            await WriteAtomicallyAsync(
                _paths.CacheManifestFile,
                json,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDirectory(generationDirectory);
            throw;
        }
    }

    public static void ValidateManifest(
        string json,
        string cacheDirectory,
        bool requireFiles = true)
    {
        SubscriptionCacheManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SubscriptionCacheManifest>(
                json,
                JsonDefaults.Create())
                ?? throw new InvalidDataException("Subscription cache manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Subscription cache manifest is invalid.", exception);
        }

        if (string.IsNullOrWhiteSpace(manifest.Generation)
            || manifest.Generation.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || Path.GetFileName(manifest.Generation) != manifest.Generation)
        {
            throw new InvalidDataException("Subscription cache generation is invalid.");
        }

        if (manifest.Entries is null)
        {
            throw new InvalidDataException("Subscription cache manifest entries are missing.");
        }

        var cacheRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(cacheDirectory)) + Path.DirectorySeparatorChar;
        foreach (var relativePath in manifest.Entries.Values)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidDataException("Subscription cache manifest contains an empty path.");
            }

            var fullPath = Path.GetFullPath(Path.Combine(cacheDirectory, relativePath));
            if (!fullPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Subscription cache manifest contains an out-of-root path.");
            }

            if (requireFiles && !File.Exists(fullPath))
            {
                throw new InvalidDataException("Subscription cache manifest references a missing file.");
            }
        }
    }

    public static string? ReadGeneration(
        string? json,
        string cacheDirectory,
        bool requireFiles = true)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        ValidateManifest(json, cacheDirectory, requireFiles);
        var manifest = JsonSerializer.Deserialize<SubscriptionCacheManifest>(
            json,
            JsonDefaults.Create())
            ?? throw new InvalidDataException("Subscription cache manifest is empty.");
        return manifest.Generation;
    }

    private async Task<string> DownloadAsync(string source, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("订阅地址必须使用 HTTPS。");
        }

        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaximumSubscriptionBytes)
        {
            throw new InvalidOperationException("订阅内容超过 16 MiB 限制。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLimitedUtf8Async(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadFileAsync(string source, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(source);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("找不到订阅文件。", path);
        }

        if (info.Length > MaximumSubscriptionBytes)
        {
            throw new InvalidOperationException("订阅内容超过 16 MiB 限制。");
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadLimitedUtf8Async(
        Stream source,
        CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var output = new MemoryStream();
            while (true)
            {
                var count = await source.ReadAsync(rented, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                if (output.Length + count > MaximumSubscriptionBytes)
                {
                    throw new InvalidOperationException("订阅内容超过 16 MiB 限制。");
                }

                await output.WriteAsync(rented.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(output.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void EnsureLooksLikeYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)
            || (!yaml.Contains("proxies:", StringComparison.Ordinal)
                && !yaml.Contains("proxy-providers:", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("订阅不是受支持的 Clash/Mihomo YAML。");
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        await AtomicFile.WriteAsync(
            path,
            Encoding.UTF8.GetBytes(content),
            cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveCachePath(Guid subscriptionId)
    {
        if (File.Exists(_paths.CacheManifestFile))
        {
            var json = File.ReadAllText(_paths.CacheManifestFile, Encoding.UTF8);
            var manifest = JsonSerializer.Deserialize<SubscriptionCacheManifest>(
                json,
                JsonDefaults.Create())
                ?? throw new InvalidDataException("订阅缓存清单无效。");
            if (manifest.Entries.TryGetValue(subscriptionId.ToString("N"), out var relativePath))
            {
                var fullPath = Path.GetFullPath(Path.Combine(_paths.CacheDirectory, relativePath));
                var cacheRoot = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(_paths.CacheDirectory)) + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("订阅缓存清单包含越界路径。");
                }

                return File.Exists(fullPath) ? fullPath : null;
            }
        }

        var legacyPath = Path.Combine(_paths.CacheDirectory, $"{subscriptionId:N}.yaml");
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

}
