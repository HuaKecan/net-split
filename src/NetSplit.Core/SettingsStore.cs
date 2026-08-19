using System.Security.Cryptography;
using System.Text.Json;

namespace NetSplit.Core;

public sealed class SettingsStore : IDisposable
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsStore(AppPaths paths)
    {
        _paths = paths;
        _jsonOptions = JsonDefaults.Create();
    }

    public async Task<SplitRouteSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectories();
            if (!File.Exists(_paths.SettingsFile))
            {
                return CreateDefaults();
            }

            await using var stream = File.OpenRead(_paths.SettingsFile);
            var settings = await JsonSerializer.DeserializeAsync<SplitRouteSettings>(
                stream,
                _jsonOptions,
                cancellationToken).ConfigureAwait(false);

            return EnsureDefaults(settings ?? CreateDefaults());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SplitRouteSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectories();
            var normalized = EnsureDefaults(settings);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, _jsonOptions);
            await AtomicFile.WriteAsync(
                _paths.SettingsFile,
                bytes,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SplitRouteSettings CreateDefaults()
    {
        return new SplitRouteSettings
        {
            ControllerSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()
        };
    }

    private static SplitRouteSettings EnsureDefaults(SplitRouteSettings settings)
    {
        var normalized = settings with
        {
            SchemaVersion = 2,
            ResidentialProxy = settings.ResidentialProxy ?? new ResidentialProxySettings()
        };
        return string.IsNullOrWhiteSpace(normalized.ControllerSecret)
            ? normalized with
            {
                ControllerSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()
            }
            : normalized;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
