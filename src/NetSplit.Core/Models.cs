using System.Text.Json.Serialization;

namespace NetSplit.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubscriptionSourceKind
{
    Url,
    File
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuleMatchType
{
    Domain,
    DomainSuffix,
    IpCidr,
    ProcessName,
    ProcessPath
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuleAction
{
    Direct,
    Proxy,
    Block
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResidentialProxyRouteMode
{
    ThroughAirport,
    DirectNic2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProxyExitMode
{
    Airport,
    Residential
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuntimeMode
{
    Disabled,
    Starting,
    Healthy,
    DirectUnavailable,
    ProxyUnavailable,
    CoreUnavailable,
    Misconfigured,
    Stopping
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoordinatorReadiness
{
    Starting,
    Ready,
    RecoveryRequired
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProxyRouteFailureReason
{
    None,
    Starting,
    CoreUnavailable,
    ControllerUnavailable,
    ProxyAdapterUnavailable,
    ResidentialProxyUnavailable,
    NoHealthyProxy,
    HealthCheckPending,
    ConfigurationInvalid
}

public sealed record AdapterBinding
{
    public string Id { get; init; } = string.Empty;
    public string MacAddress { get; init; } = string.Empty;
    public string LastKnownName { get; init; } = string.Empty;
}

public sealed record NetworkAdapterSnapshot
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string MacAddress { get; init; } = string.Empty;
    public int InterfaceIndex { get; init; }
    public bool IsUp { get; init; }
    public bool IsSelectable { get; init; }
    public bool IsF50Candidate { get; init; }
    public bool IsTunnelOrLoopback { get; init; }
    public IReadOnlyList<string> Ipv4Addresses { get; init; } = [];
    public IReadOnlyList<string> Gateways { get; init; } = [];
    public IReadOnlyList<string> DnsServers { get; init; } = [];
    public IReadOnlyList<string> ConnectedPrefixes { get; init; } = [];
    public long BytesReceived { get; init; }
    public long BytesSent { get; init; }

    public override string ToString()
    {
        var address = Ipv4Addresses.Count > 0 ? Ipv4Addresses[0] : "无 IPv4";
        var gateway = Gateways.Count > 0 ? Gateways[0] : "无网关";
        return $"{Name} - {Description} ({address}, 网关 {gateway})";
    }
}

public sealed record SubscriptionSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public SubscriptionSourceKind SourceKind { get; init; }
    public string ProtectedSource { get; init; } = string.Empty;
    public string DisplaySource { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public int UpdateIntervalMinutes { get; init; } = 360;
    public DateTimeOffset? LastUpdated { get; init; }
}

public sealed record SubscriptionSummary
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public SubscriptionSourceKind SourceKind { get; init; }
    public string DisplaySource { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public int UpdateIntervalMinutes { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
}

public sealed record SubscriptionInput
{
    public string Name { get; init; } = string.Empty;
    public SubscriptionSourceKind SourceKind { get; init; }
    public string Source { get; init; } = string.Empty;
    public int UpdateIntervalMinutes { get; init; } = 360;
}

public sealed record SubscriptionDocument
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Yaml { get; init; }
    public bool FromCache { get; init; }
    public string? Warning { get; init; }
}

public sealed record CustomRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public RuleMatchType MatchType { get; init; }
    public RuleAction Action { get; init; }
    public string Value { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
}

public sealed record ResidentialProxySettings
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 1080;
    public bool AuthenticationEnabled { get; init; } = true;
    public string ProtectedUsername { get; init; } = string.Empty;
    public string ProtectedPassword { get; init; } = string.Empty;
    public ResidentialProxyRouteMode RouteMode { get; init; } =
        ResidentialProxyRouteMode.ThroughAirport;
}

public sealed record SplitRouteSettings
{
    public int SchemaVersion { get; init; } = 2;
    public bool Enabled { get; init; }
    public AdapterBinding? DirectAdapter { get; init; }
    public AdapterBinding? ProxyAdapter { get; init; }
    public string MihomoPath { get; init; } = string.Empty;
    public string GeoDataDirectory { get; init; } = string.Empty;
    public int ControllerPort { get; init; } = 19097;
    public int MixedPort { get; init; } = 17897;
    public string ControllerSecret { get; init; } = string.Empty;
    public int HealthCheckSeconds { get; init; } = 30;
    public ResidentialProxySettings ResidentialProxy { get; init; } = new();
    public IReadOnlyList<SubscriptionSettings> Subscriptions { get; init; } = [];
    public IReadOnlyList<CustomRule> Rules { get; init; } = [];
}

public sealed record AdapterTraffic
{
    public long ReceiveBytesPerSecond { get; init; }
    public long SendBytesPerSecond { get; init; }
}

public sealed record TrafficPoint
{
    public DateTimeOffset Timestamp { get; init; }
    public long DirectReceiveBps { get; init; }
    public long DirectSendBps { get; init; }
    public long ProxyReceiveBps { get; init; }
    public long ProxySendBps { get; init; }
}

public sealed record RuntimeStatus
{
    public RuntimeMode Mode { get; init; } = RuntimeMode.Disabled;
    public bool Enabled { get; init; }
    public bool MihomoRunning { get; init; }
    public bool TunEnabled { get; init; }
    public bool DnsEnabled { get; init; }
    public bool DnsStatusKnown { get; init; }
    public bool DirectAdapterAvailable { get; init; }
    public bool ProxyAdapterAvailable { get; init; }
    public bool ProxyRouteAvailable { get; init; }
    public bool ProxyRouteHealthKnown { get; init; }
    public ProxyRouteFailureReason ProxyRouteFailure { get; init; }
    public int HealthyProxyCount { get; init; }
    public string DirectAdapterName { get; init; } = string.Empty;
    public string ProxyAdapterName { get; init; } = string.Empty;
    public AdapterTraffic DirectTraffic { get; init; } = new();
    public AdapterTraffic ProxyTraffic { get; init; } = new();
    public string CurrentProxy { get; init; } = string.Empty;
    public string EffectiveProxy { get; init; } = string.Empty;
    public IReadOnlyList<string> AvailableProxies { get; init; } = [];
    public IReadOnlyList<string> HealthyProxies { get; init; } = [];
    public int? DirectDelayMilliseconds { get; init; }
    public int? ProxyDelayMilliseconds { get; init; }
    public string LastError { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ClientSettingsSnapshot
{
    public bool Enabled { get; init; }
    public AdapterBinding? DirectAdapter { get; init; }
    public AdapterBinding? ProxyAdapter { get; init; }
    public string MihomoPath { get; init; } = string.Empty;
    public string GeoDataDirectory { get; init; } = string.Empty;
    public bool MihomoAvailable { get; init; }
    public bool GeoDataAvailable { get; init; }
    public ResidentialProxySummary ResidentialProxy { get; init; } = new();
    public IReadOnlyList<SubscriptionSummary> Subscriptions { get; init; } = [];
    public IReadOnlyList<CustomRule> Rules { get; init; } = [];
}

public sealed record DiagnosticsSnapshot
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ApplicationVersion { get; init; } = string.Empty;
    public bool ServiceReady { get; init; }
    public CoordinatorReadiness Readiness { get; init; } = CoordinatorReadiness.Starting;
    public bool StartupDisableActive { get; init; }
    public RuntimeStatus Runtime { get; init; } = new();
    public DiagnosticsSettingsSummary Settings { get; init; } = new();
    public IReadOnlyList<NetworkAdapterSnapshot> Adapters { get; init; } = [];
    public IReadOnlyList<DiagnosticsFileSnapshot> Files { get; init; } = [];
    public IReadOnlyList<string> RecentLogs { get; init; } = [];
}

public sealed record DiagnosticsSettingsSummary
{
    public bool Enabled { get; init; }
    public string DirectAdapterName { get; init; } = string.Empty;
    public string ProxyAdapterName { get; init; } = string.Empty;
    public bool MihomoAvailable { get; init; }
    public bool GeoDataAvailable { get; init; }
    public int HealthCheckSeconds { get; init; }
    public int SubscriptionCount { get; init; }
    public int EnabledSubscriptionCount { get; init; }
    public int RuleCount { get; init; }
    public bool ResidentialProxyEnabled { get; init; }
    public ResidentialProxyRouteMode ResidentialProxyRouteMode { get; init; } =
        ResidentialProxyRouteMode.ThroughAirport;
    public bool ResidentialProxyHasCredentials { get; init; }
}

public sealed record DiagnosticsFileSnapshot
{
    public string Name { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public long Length { get; init; }
    public DateTimeOffset? LastWriteTimeUtc { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}

public sealed record ResidentialProxySummary
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 1080;
    public bool AuthenticationEnabled { get; init; } = true;
    public bool HasCredentials { get; init; }
    public ResidentialProxyRouteMode RouteMode { get; init; } =
        ResidentialProxyRouteMode.ThroughAirport;
}

public sealed record ResidentialProxyCredentials(
    string Username,
    string Password);

public sealed record ConfigurationValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record MihomoConfigResult
{
    public required string Yaml { get; init; }
    public required IReadOnlyList<string> ProxyNames { get; init; }
    public required IReadOnlyList<string> ProviderNames { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record UpdateBindingsRequest
{
    public required string DirectAdapterId { get; init; }
    public required string ProxyAdapterId { get; init; }
}

public sealed record UpdateResidentialProxyRequest
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 1080;
    public bool AuthenticationEnabled { get; init; } = true;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public bool ReplaceCredentials { get; init; }
    public ResidentialProxyRouteMode RouteMode { get; init; } =
        ResidentialProxyRouteMode.ThroughAirport;
}

public sealed record SelectProxyRequest
{
    public required string Name { get; init; }
}

public sealed record SetProxyExitModeRequest
{
    public required ProxyExitMode Mode { get; init; }
}

public sealed record RemoveItemRequest
{
    public required Guid Id { get; init; }
}
