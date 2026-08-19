using System.Text.Json;

namespace NetSplit.Core;

public static class RpcCommands
{
    public const string Discover = "discover";
    public const string GetStatus = "get-status";
    public const string GetSettings = "get-settings";
    public const string GetLogs = "get-logs";
    public const string GetDiagnostics = "get-diagnostics";
    public const string Validate = "validate";
    public const string UpdateBindings = "update-bindings";
    public const string UpdateResidentialProxy = "update-residential-proxy";
    public const string AddSubscription = "add-subscription";
    public const string RemoveSubscription = "remove-subscription";
    public const string AddRule = "add-rule";
    public const string RemoveRule = "remove-rule";
    public const string Enable = "enable";
    public const string Disable = "disable";
    public const string Repair = "repair";
    public const string Rollback = "rollback";
    public const string RefreshSubscriptions = "refresh-subscriptions";
    public const string SelectProxy = "select-proxy";
    public const string SetProxyExitMode = "set-proxy-exit-mode";
    public const string GetTrafficHistory = "get-traffic-history";
}

public sealed record RpcRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Command { get; init; }
    public JsonElement Payload { get; init; }
}

public sealed record RpcResponse
{
    public required Guid Id { get; init; }
    public bool Success { get; init; }
    public JsonElement Data { get; init; }
    public string Error { get; init; } = string.Empty;
}

public static class RpcPayload
{
    public static JsonElement Null()
    {
        return JsonSerializer.SerializeToElement<object?>(null, JsonDefaults.Create());
    }

    public static JsonElement From<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, JsonDefaults.Create());
    }

    public static T? To<T>(JsonElement value)
    {
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default
            : value.Deserialize<T>(JsonDefaults.Create());
    }
}
