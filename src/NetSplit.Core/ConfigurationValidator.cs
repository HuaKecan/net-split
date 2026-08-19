using System.Globalization;
using System.Net;

namespace NetSplit.Core;

public static class ConfigurationValidator
{
    public static ConfigurationValidationResult Validate(
        SplitRouteSettings settings,
        IReadOnlyList<NetworkAdapterSnapshot> adapters)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var direct = Resolve(settings.DirectAdapter, adapters);
        var proxy = Resolve(settings.ProxyAdapter, adapters);

        if (settings.DirectAdapter is null)
        {
            errors.Add("请选择网卡1（国内直连出口）。");
        }
        else if (string.IsNullOrWhiteSpace(settings.DirectAdapter.LastKnownName))
        {
            errors.Add("网卡1缺少可用于离线恢复的接口名称，请重新选择。");
        }
        else if (direct is null)
        {
            warnings.Add("已保存的网卡1当前未出现；国内直连会保持阻断，网卡恢复后自动重试。");
        }

        if (settings.ProxyAdapter is null)
        {
            errors.Add("请选择网卡2（代理物理出口）。");
        }
        else if (string.IsNullOrWhiteSpace(settings.ProxyAdapter.LastKnownName))
        {
            errors.Add("网卡2缺少可用于离线恢复的接口名称，请重新选择。");
        }
        else if (proxy is null)
        {
            warnings.Add("已保存的网卡2当前未出现；国外流量会保持阻断，国内直连仍可使用。");
        }

        if (settings.DirectAdapter is not null
            && settings.ProxyAdapter is not null
            && settings.DirectAdapter.Id.Equals(
                settings.ProxyAdapter.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("网卡1和网卡2必须是不同接口。");
        }

        if (direct is { IsSelectable: false })
        {
            warnings.Add("网卡1当前没有可用的 IPv4 默认网关；国内直连将保持阻断。");
        }

        if (proxy is { IsSelectable: false })
        {
            warnings.Add("网卡2当前没有可用的 IPv4 默认网关；国外流量将保持阻断。");
        }

        if (!settings.Subscriptions.Any(item => item.Enabled))
        {
            errors.Add("至少需要一个已启用的 Clash/Mihomo YAML 订阅。");
        }

        if (settings.ControllerPort is < 1024 or > 65535)
        {
            errors.Add("Mihomo 控制端口必须位于 1024 到 65535。");
        }

        if (settings.MixedPort is < 1024 or > 65535 || settings.MixedPort == settings.ControllerPort)
        {
            errors.Add("Mihomo 混合端口无效或与控制端口冲突。");
        }

        var residentialProxy = settings.ResidentialProxy;
        if (residentialProxy.Enabled)
        {
            try
            {
                _ = ResidentialProxyValidator.NormalizeHost(residentialProxy.Host);
                ResidentialProxyValidator.ValidatePort(residentialProxy.Port);
            }
            catch (InvalidOperationException exception)
            {
                errors.Add(exception.Message);
            }

            if (residentialProxy.AuthenticationEnabled
                && (string.IsNullOrWhiteSpace(residentialProxy.ProtectedUsername)
                    || string.IsNullOrWhiteSpace(residentialProxy.ProtectedPassword)))
            {
                errors.Add("住宅代理已启用认证，但尚未保存完整的用户名和密码。");
            }
        }

        foreach (var rule in settings.Rules.Where(item => item.Enabled))
        {
            ValidateRule(rule, errors);
        }

        if (settings.Rules
            .Where(item => item.Enabled)
            .GroupBy(item => $"{item.MatchType}:{item.Value}", StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(item => item.Action).Distinct().Count() > 1))
        {
            errors.Add("同一匹配项不能同时配置多个动作。");
        }

        if (settings.HealthCheckSeconds < 10)
        {
            warnings.Add("健康检查间隔过短，已建议设置为至少 10 秒。");
        }

        return new ConfigurationValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings
        };
    }

    private static NetworkAdapterSnapshot? Resolve(
        AdapterBinding? binding,
        IReadOnlyList<NetworkAdapterSnapshot> adapters)
    {
        if (binding is null)
        {
            return null;
        }

        return adapters.FirstOrDefault(item => item.Id.Equals(binding.Id, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(binding.MacAddress)
                && item.MacAddress.Equals(binding.MacAddress, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateRule(CustomRule rule, List<string> errors)
    {
        var value = rule.Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add("自定义规则不能包含空值。");
            return;
        }

        if (value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
        {
            errors.Add($"规则值“{value}”不能包含逗号或换行。");
            return;
        }

        if (rule.MatchType == RuleMatchType.IpCidr && !CidrUtility.IsValidIpv4Cidr(value))
        {
            errors.Add($"“{value}”不是有效的 IPv4 CIDR。");
        }

        if (rule.MatchType is RuleMatchType.Domain or RuleMatchType.DomainSuffix)
        {
            var candidate = value.TrimStart('*', '.');
            try
            {
                candidate = new IdnMapping().GetAscii(candidate);
            }
            catch (ArgumentException)
            {
                errors.Add($"“{value}”不是有效域名。");
                return;
            }

            if (Uri.CheckHostName(candidate) == UriHostNameType.Unknown
                && !IPAddress.TryParse(candidate, out _))
            {
                errors.Add($"“{value}”不是有效域名。");
            }
        }
    }
}
