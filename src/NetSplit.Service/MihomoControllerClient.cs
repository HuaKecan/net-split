using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using NetSplit.Core;

namespace NetSplit.Service;

public interface IMihomoControllerClient
{
    Task<bool> WaitUntilReadyAsync(
        SplitRouteSettings settings,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task DisableTunAsync(SplitRouteSettings settings, CancellationToken cancellationToken);

    Task<MihomoApiSnapshot> GetSnapshotAsync(
        SplitRouteSettings settings,
        CancellationToken cancellationToken);

    Task<int?> MeasureDelayAsync(
        SplitRouteSettings settings,
        string proxyName,
        string url,
        CancellationToken cancellationToken);

    Task SelectProxyAsync(
        SplitRouteSettings settings,
        string proxyName,
        CancellationToken cancellationToken);
}

public sealed class MihomoControllerClient : IMihomoControllerClient
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(8);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<int, CancellationToken, Task<bool>> _dnsListenerProbe;

    public MihomoControllerClient(
        HttpClient httpClient,
        TimeSpan? requestTimeout = null,
        Func<int, CancellationToken, Task<bool>>? dnsListenerProbe = null)
    {
        _httpClient = httpClient;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _dnsListenerProbe = dnsListenerProbe ?? DnsReadinessProbe.ProbeAsync;
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "Mihomo controller request timeout must be positive.");
        }
    }

    public async Task<bool> WaitUntilReadyAsync(
        SplitRouteSettings settings,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = CreateRequest(settings, HttpMethod.Get, "/version");
                using var response = await SendAsync(
                    request,
                    cancellationToken,
                    RemainingTimeout(deadline)).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var configRequest = CreateRequest(settings, HttpMethod.Get, "/configs");
                    using var configResponse = await SendAsync(
                        configRequest,
                        cancellationToken,
                        RemainingTimeout(deadline)).ConfigureAwait(false);
                    if (configResponse.IsSuccessStatusCode)
                    {
                        using var configJson = await ReadJsonAsync(
                            configResponse,
                            cancellationToken).ConfigureAwait(false);
                        if (IsTunEnabled(configJson.RootElement)
                            && await IsDnsListenerReadyAsync(
                                RemainingTimeout(deadline),
                                cancellationToken).ConfigureAwait(false))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException)
            {
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(250)
                    ? remaining
                    : TimeSpan.FromMilliseconds(250),
                cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public async Task DisableTunAsync(
        SplitRouteSettings settings,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(settings, HttpMethod.Patch, "/configs");
        request.Content = JsonContent.Create(new
        {
            tun = new
            {
                enable = false
            }
        });
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.OK)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task<MihomoApiSnapshot> GetSnapshotAsync(
        SplitRouteSettings settings,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _requestTimeout;
        using var configRequest = CreateRequest(settings, HttpMethod.Get, "/configs");
        using var configResponse = await SendAsync(
            configRequest,
            cancellationToken,
            RemainingTimeout(deadline)).ConfigureAwait(false);
        configResponse.EnsureSuccessStatusCode();
        using var configJson = await ReadJsonAsync(configResponse, cancellationToken)
            .ConfigureAwait(false);
        if (configJson.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse("configs payload is not a JSON object.");
        }

        using var proxyRequest = CreateRequest(settings, HttpMethod.Get, "/proxies");
        using var proxyResponse = await SendAsync(
            proxyRequest,
            cancellationToken,
            RemainingTimeout(deadline)).ConfigureAwait(false);
        proxyResponse.EnsureSuccessStatusCode();
        using var proxyJson = await ReadJsonAsync(proxyResponse, cancellationToken)
            .ConfigureAwait(false);

        var tunEnabled = IsTunEnabled(configJson.RootElement);
        if (proxyJson.RootElement.ValueKind != JsonValueKind.Object
            || !proxyJson.RootElement.TryGetProperty("proxies", out var proxies)
            || proxies.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse("proxies payload is missing the proxies object.");
        }

        foreach (var proxyEntry in proxies.EnumerateObject())
        {
            if (proxyEntry.Value.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse($"proxy entry '{proxyEntry.Name}' is not a JSON object.");
            }
        }

        if (!proxies.TryGetProperty(MihomoConfigGenerator.ProxyGroupName, out var group)
            || group.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse(
                $"proxy group '{MihomoConfigGenerator.ProxyGroupName}' is missing.");
        }

        var currentProxy = string.Empty;
        var available = new List<string>();
        if (group.TryGetProperty("now", out var now)
            && now.ValueKind == JsonValueKind.String)
        {
            currentProxy = now.GetString() ?? string.Empty;
        }

        if (group.TryGetProperty("all", out var all)
            && all.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in all.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw InvalidResponse("proxy group member is not a string.");
                }

                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    available.Add(name);
                }
            }
        }
        else
        {
            throw InvalidResponse(
                $"proxy group '{MihomoConfigGenerator.ProxyGroupName}' has no member list.");
        }

        var proxyEntries = proxies.EnumerateObject()
            .ToDictionary(
                item => item.Name,
                item => item.Value,
                StringComparer.Ordinal);
        var routeProxyName = settings.ResidentialProxy.Enabled
            ? MihomoConfigGenerator.ResidentialProxyName
            : MihomoConfigGenerator.ProxyGroupName;
        var selectedProxyHealthy = proxyEntries.ContainsKey(routeProxyName)
            ? ResolveProxyHealth(
                routeProxyName,
                proxyEntries,
                new HashSet<string>(StringComparer.Ordinal))
            : false;
        var effectiveProxy = settings.ResidentialProxy.Enabled
            ? MihomoConfigGenerator.ResidentialProxyName
            : string.IsNullOrWhiteSpace(currentProxy)
                ? string.Empty
                : ResolveEffectiveProxy(
                    currentProxy,
                    proxyEntries,
                    new HashSet<string>(StringComparer.Ordinal));
        var healthyProxies = available
            .Where(name => !name.Equals(
                MihomoConfigGenerator.AutoProxyGroupName,
                StringComparison.Ordinal))
            .Where(name => !name.Equals(
                MihomoConfigGenerator.ProxyGroupName,
                StringComparison.Ordinal))
            .Where(name => ResolveProxyHealth(
                name,
                proxyEntries,
                new HashSet<string>(StringComparer.Ordinal)) is true)
            .ToArray();
        var dnsEnabled = await IsDnsListenerReadyAsync(
            RemainingTimeout(deadline),
            cancellationToken).ConfigureAwait(false);

        return new MihomoApiSnapshot(tunEnabled, currentProxy, available)
        {
            DnsEnabled = dnsEnabled,
            SelectedProxyHealthy = selectedProxyHealthy,
            EffectiveProxy = effectiveProxy,
            HealthyProxies = healthyProxies
        };
    }

    public async Task<int?> MeasureDelayAsync(
        SplitRouteSettings settings,
        string proxyName,
        string url,
        CancellationToken cancellationToken)
    {
        var path = $"/proxies/{Uri.EscapeDataString(proxyName)}/delay"
            + $"?timeout=5000&url={Uri.EscapeDataString(url)}";
        using var request = CreateRequest(settings, HttpMethod.Get, path);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || !json.RootElement.TryGetProperty("delay", out var delay)
            || delay.ValueKind != JsonValueKind.Number
            || !delay.TryGetInt32(out var delayMilliseconds))
        {
            throw InvalidResponse("delay payload is missing a numeric delay.");
        }

        return delayMilliseconds;
    }

    public async Task SelectProxyAsync(
        SplitRouteSettings settings,
        string proxyName,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            settings,
            HttpMethod.Put,
            $"/proxies/{Uri.EscapeDataString(MihomoConfigGenerator.ProxyGroupName)}");
        request.Content = JsonContent.Create(new { name = proxyName });
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? _requestTimeout);
        try
        {
            return await _httpClient.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(
                "Mihomo controller request timed out.",
                exception);
        }
    }

    private TimeSpan RemainingTimeout(DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(1);
        }

        return remaining < _requestTimeout ? remaining : _requestTimeout;
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw InvalidResponse("body is not valid JSON.", exception);
        }
    }

    private static HttpRequestMessage CreateRequest(
        SplitRouteSettings settings,
        HttpMethod method,
        string path)
    {
        var request = new HttpRequestMessage(
            method,
            new Uri($"http://127.0.0.1:{settings.ControllerPort}{path}", UriKind.Absolute));
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ControllerSecret);
        return request;
    }

    private static bool IsTunEnabled(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("tun", out var tun)
            && tun.ValueKind == JsonValueKind.Object
            && tun.TryGetProperty("enable", out var enabled)
            && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False
            && enabled.GetBoolean();
    }

    private async Task<bool> IsDnsListenerReadyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await _dnsListenerProbe(
                MihomoConfigGenerator.DnsListenPort,
                timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool? ResolveProxyHealth(
        string proxyName,
        IReadOnlyDictionary<string, JsonElement> proxies,
        ISet<string> visiting)
    {
        if (!proxies.TryGetValue(proxyName, out var proxy)
            || !visiting.Add(proxyName))
        {
            return null;
        }

        try
        {
            if (proxy.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse($"proxy entry '{proxyName}' is not a JSON object.");
            }

            if (proxy.TryGetProperty("alive", out var alive)
                && alive.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return alive.GetBoolean();
            }

            if (proxy.TryGetProperty("now", out var now)
                && now.ValueKind == JsonValueKind.String)
            {
                var current = now.GetString();
                if (!string.IsNullOrWhiteSpace(current))
                {
                    var currentHealth = ResolveProxyHealth(current, proxies, visiting);
                    if (currentHealth is not null)
                    {
                        return currentHealth;
                    }
                }
            }

            if (proxy.TryGetProperty("all", out var all)
                && all.ValueKind == JsonValueKind.Array)
            {
                var sawUnknown = false;
                foreach (var item in all.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        sawUnknown = true;
                        continue;
                    }

                    var childName = item.GetString();
                    if (string.IsNullOrWhiteSpace(childName))
                    {
                        sawUnknown = true;
                        continue;
                    }

                    var childHealth = ResolveProxyHealth(childName, proxies, visiting);
                    if (childHealth is true)
                    {
                        return true;
                    }

                    sawUnknown |= childHealth is null;
                }

                return sawUnknown ? null : false;
            }

            return null;
        }
        finally
        {
            visiting.Remove(proxyName);
        }
    }

    private static string ResolveEffectiveProxy(
        string proxyName,
        IReadOnlyDictionary<string, JsonElement> proxies,
        ISet<string> visiting)
    {
        if (!proxies.TryGetValue(proxyName, out var proxy)
            || !visiting.Add(proxyName))
        {
            return proxyName;
        }

        try
        {
            if (proxy.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse($"proxy entry '{proxyName}' is not a JSON object.");
            }

            if (proxy.TryGetProperty("now", out var now)
                && now.ValueKind == JsonValueKind.String)
            {
                var current = now.GetString();
                if (!string.IsNullOrWhiteSpace(current))
                {
                    return ResolveEffectiveProxy(current, proxies, visiting);
                }
            }

            return proxyName;
        }
        finally
        {
            visiting.Remove(proxyName);
        }
    }

    private static HttpRequestException InvalidResponse(
        string detail,
        Exception? innerException = null)
    {
        return new HttpRequestException(
            $"Mihomo controller returned an invalid response: {detail}",
            innerException);
    }
}

public sealed record MihomoApiSnapshot(
    bool TunEnabled,
    string CurrentProxy,
    IReadOnlyList<string> AvailableProxies)
{
    public bool DnsEnabled { get; init; }
    public bool? SelectedProxyHealthy { get; init; }
    public string EffectiveProxy { get; init; } = string.Empty;
    public IReadOnlyList<string> HealthyProxies { get; init; } = [];
}
