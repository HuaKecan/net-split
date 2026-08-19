using System.Globalization;
using System.Net;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace NetSplit.Core;

public static class MihomoConfigGenerator
{
    public const int DnsListenPort = 1053;
    public const string DirectProxyName = "NETSPLIT-DIRECT-NIC1";
    public const string ProxyBootstrapDirectName = "NETSPLIT-DIRECT-NIC2";
    public const string AutoProxyGroupName = "NETSPLIT-AUTO";
    public const string ProxyGroupName = "NETSPLIT-PROXY";
    public const string ResidentialProxyName = "NETSPLIT-RESIDENTIAL";

    private static readonly string[] ReservedNames =
    [
        DirectProxyName,
        ProxyBootstrapDirectName,
        AutoProxyGroupName,
        ProxyGroupName,
        ResidentialProxyName,
        "DIRECT",
        "REJECT",
        "GLOBAL"
    ];

    public static MihomoConfigResult Generate(
        IReadOnlyList<SubscriptionDocument> subscriptions,
        SplitRouteSettings settings,
        NetworkAdapterSnapshot directAdapter,
        NetworkAdapterSnapshot proxyAdapter,
        IReadOnlyList<NetworkAdapterSnapshot> allAdapters,
        ResidentialProxyCredentials? residentialCredentials = null)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(directAdapter);
        ArgumentNullException.ThrowIfNull(proxyAdapter);

        if (subscriptions.Count == 0)
        {
            throw new InvalidOperationException("没有可用的订阅配置。");
        }

        var warnings = new List<string>();
        var proxies = new YamlSequenceNode();
        var proxyNames = new List<string>();
        var providerNames = new List<string>();
        var providers = new List<KeyValuePair<YamlNode, YamlNode>>();
        var usedProxyNames = new HashSet<string>(ReservedNames, StringComparer.OrdinalIgnoreCase);
        var usedProviderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        proxies.Add(CreateDirectProxy(DirectProxyName, directAdapter.Name));
        proxies.Add(CreateDirectProxy(ProxyBootstrapDirectName, proxyAdapter.Name));
        if (settings.ResidentialProxy.Enabled)
        {
            proxies.Add(CreateResidentialProxy(
                settings.ResidentialProxy,
                residentialCredentials,
                proxyAdapter.Name));
        }

        foreach (var subscription in subscriptions)
        {
            var sourceRoot = ParseRoot(subscription.Yaml, subscription.Name);
            ExtractProxies(
                sourceRoot,
                subscription.Name,
                proxyAdapter.Name,
                proxies,
                proxyNames,
                usedProxyNames,
                warnings);
            ExtractProviders(
                sourceRoot,
                subscription.Name,
                proxyAdapter.Name,
                providerNames,
                usedProviderNames,
                out var extractedProviders,
                warnings);

            foreach (var pair in extractedProviders)
            {
                providers.Add(pair);
            }

            if (!string.IsNullOrWhiteSpace(subscription.Warning))
            {
                warnings.Add(subscription.Warning);
            }
        }

        if (proxyNames.Count == 0 && providers.Count == 0)
        {
            throw new InvalidOperationException("订阅中没有可用的代理节点或代理提供者。");
        }

        var root = BuildRoot(
            settings,
            directAdapter,
            proxyAdapter,
            allAdapters,
            proxies,
            proxyNames,
            providers,
            providerNames);
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, false);

        return new MihomoConfigResult
        {
            Yaml = writer.ToString(),
            ProxyNames = proxyNames,
            ProviderNames = providerNames,
            Warnings = warnings
        };
    }

    private static YamlMappingNode BuildRoot(
        SplitRouteSettings settings,
        NetworkAdapterSnapshot directAdapter,
        NetworkAdapterSnapshot proxyAdapter,
        IReadOnlyList<NetworkAdapterSnapshot> allAdapters,
        YamlSequenceNode proxies,
        IReadOnlyList<string> proxyNames,
        IReadOnlyList<KeyValuePair<YamlNode, YamlNode>> providers,
        IReadOnlyList<string> providerNames)
    {
        var foreignProxyName = settings.ResidentialProxy.Enabled
            ? ResidentialProxyName
            : ProxyGroupName;
        var root = new YamlMappingNode
        {
            { "mixed-port", Scalar(settings.MixedPort) },
            { "allow-lan", Scalar(false) },
            { "bind-address", Scalar("*") },
            { "mode", Scalar("rule") },
            { "log-level", Scalar("info") },
            { "ipv6", Scalar(false) },
            { "external-controller", Scalar($"127.0.0.1:{settings.ControllerPort}") },
            { "secret", Scalar(settings.ControllerSecret) },
            { "find-process-mode", Scalar("strict") },
            { "unified-delay", Scalar(true) },
            { "tcp-concurrent", Scalar(true) },
            { "geodata-mode", Scalar(true) },
            { "geo-auto-update", Scalar(true) },
            { "geo-update-interval", Scalar(24) },
            { "tun", CreateTun(allAdapters) },
            { "dns", CreateDns(directAdapter, proxyAdapter, foreignProxyName) },
            { "proxies", proxies }
        };

        if (providers.Count > 0)
        {
            var providerMap = new YamlMappingNode();
            foreach (var pair in providers)
            {
                providerMap.Add(pair.Key, pair.Value);
            }

            root.Add("proxy-providers", providerMap);
        }

        root.Add("proxy-groups", CreateProxyGroups(proxyNames, providerNames));
        root.Add("rules", CreateRules(settings.Rules, foreignProxyName));
        return root;
    }

    private static YamlMappingNode CreateDirectProxy(string name, string interfaceName)
    {
        return new YamlMappingNode
        {
            { "name", Scalar(name) },
            { "type", Scalar("direct") },
            { "udp", Scalar(true) },
            { "interface-name", Scalar(interfaceName) }
        };
    }

    private static YamlMappingNode CreateResidentialProxy(
        ResidentialProxySettings settings,
        ResidentialProxyCredentials? credentials,
        string proxyInterfaceName)
    {
        var host = ResidentialProxyValidator.NormalizeHost(settings.Host);
        ResidentialProxyValidator.ValidatePort(settings.Port);
        var proxy = new YamlMappingNode
        {
            { "name", Scalar(ResidentialProxyName) },
            { "type", Scalar("socks5") },
            { "server", Scalar(host) },
            { "port", Scalar(settings.Port) },
            { "udp", Scalar(true) }
        };

        if (settings.AuthenticationEnabled)
        {
            if (credentials is null
                || string.IsNullOrWhiteSpace(credentials.Username)
                || string.IsNullOrEmpty(credentials.Password))
            {
                throw new InvalidOperationException("住宅代理凭据不完整，请重新保存。");
            }

            proxy.Add("username", Scalar(credentials.Username));
            proxy.Add("password", Scalar(credentials.Password));
        }

        if (settings.RouteMode == ResidentialProxyRouteMode.ThroughAirport)
        {
            proxy.Add("dialer-proxy", Scalar(ProxyGroupName));
        }
        else
        {
            proxy.Add("interface-name", Scalar(proxyInterfaceName));
        }

        return proxy;
    }

    private static YamlMappingNode CreateTun(IReadOnlyList<NetworkAdapterSnapshot> adapters)
    {
        var excludedPrefixes = adapters
            .Where(adapter =>
                adapter.IsUp
                && !adapter.IsTunnelOrLoopback
                && !adapter.Name.Equals("NetSplit", StringComparison.OrdinalIgnoreCase))
            .SelectMany(adapter => adapter.ConnectedPrefixes)
            .Where(IsPrivateOrLinkLocalPrefix)
            .Concat(["127.0.0.0/8", "169.254.0.0/16"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(prefix => prefix, StringComparer.OrdinalIgnoreCase)
            .Select(Scalar)
            .ToArray();

        return new YamlMappingNode
        {
            { "enable", Scalar(true) },
            { "device", Scalar("NetSplit") },
            { "stack", Scalar("mixed") },
            { "auto-route", Scalar(true) },
            { "strict-route", Scalar(true) },
            { "auto-detect-interface", Scalar(false) },
            { "dns-hijack", new YamlSequenceNode(Scalar("any:53")) },
            { "route-exclude-address", new YamlSequenceNode(excludedPrefixes) }
        };
    }

    private static YamlMappingNode CreateDns(
        NetworkAdapterSnapshot directAdapter,
        NetworkAdapterSnapshot proxyAdapter,
        string foreignProxyName)
    {
        var directDns = directAdapter.DnsServers
            .Where(IsIpv4)
            .DefaultIfEmpty("223.5.5.5")
            .Select(address => Scalar($"udp://{address}#{DirectProxyName}"))
            .ToArray();
        var proxyBootstrapDns = proxyAdapter.DnsServers
            .Where(IsIpv4)
            .DefaultIfEmpty(proxyAdapter.Gateways.Count > 0 ? proxyAdapter.Gateways[0] : "1.1.1.1")
            .Select(address => Scalar($"udp://{address}#{ProxyBootstrapDirectName}"))
            .ToArray();
        var proxyDns = new[]
        {
            Scalar($"https://1.1.1.1/dns-query#{foreignProxyName}"),
            Scalar($"https://8.8.8.8/dns-query#{foreignProxyName}")
        };

        var policy = new YamlMappingNode
        {
            {
                "geosite:private,cn",
                new YamlSequenceNode(directDns)
            },
            {
                "geosite:geolocation-!cn",
                new YamlSequenceNode(proxyDns)
            }
        };

        return new YamlMappingNode
        {
            { "enable", Scalar(true) },
            { "ipv6", Scalar(false) },
            { "listen", Scalar($"127.0.0.1:{DnsListenPort}") },
            { "enhanced-mode", Scalar("fake-ip") },
            { "fake-ip-range", Scalar("198.18.0.1/16") },
            { "respect-rules", Scalar(true) },
            {
                "fake-ip-filter",
                new YamlSequenceNode(
                    Scalar("*.lan"),
                    Scalar("*.local"),
                    Scalar("time.*.com"),
                    Scalar("+.msftconnecttest.com"),
                    Scalar("+.msftncsi.com"))
            },
            {
                "default-nameserver",
                new YamlSequenceNode(proxyBootstrapDns)
            },
            { "nameserver", new YamlSequenceNode(proxyDns) },
            { "direct-nameserver", new YamlSequenceNode(directDns) },
            { "proxy-server-nameserver", new YamlSequenceNode(proxyBootstrapDns) },
            { "nameserver-policy", policy }
        };
    }

    private static YamlSequenceNode CreateProxyGroups(
        IReadOnlyList<string> proxyNames,
        IReadOnlyList<string> providerNames)
    {
        var autoGroup = new YamlMappingNode
        {
            { "name", Scalar(AutoProxyGroupName) },
            { "type", Scalar("url-test") },
            { "url", Scalar("https://www.gstatic.com/generate_204") },
            { "interval", Scalar(300) },
            { "tolerance", Scalar(100) },
            { "lazy", Scalar(true) }
        };
        AddGroupMembers(autoGroup, proxyNames, providerNames);

        var selectGroup = new YamlMappingNode
        {
            { "name", Scalar(ProxyGroupName) },
            { "type", Scalar("select") },
            {
                "proxies",
                new YamlSequenceNode(
                    new[] { AutoProxyGroupName }
                        .Concat(proxyNames)
                        .Select(Scalar))
            }
        };

        if (providerNames.Count > 0)
        {
            selectGroup.Add("use", new YamlSequenceNode(providerNames.Select(Scalar)));
        }

        return new YamlSequenceNode(autoGroup, selectGroup);
    }

    private static void AddGroupMembers(
        YamlMappingNode group,
        IReadOnlyList<string> proxyNames,
        IReadOnlyList<string> providerNames)
    {
        if (proxyNames.Count > 0)
        {
            group.Add("proxies", new YamlSequenceNode(proxyNames.Select(Scalar)));
        }

        if (providerNames.Count > 0)
        {
            group.Add("use", new YamlSequenceNode(providerNames.Select(Scalar)));
        }
    }

    private static YamlSequenceNode CreateRules(
        IReadOnlyList<CustomRule> customRules,
        string foreignProxyName)
    {
        var rules = new List<string>();
        foreach (var rule in customRules
                     .Where(item => item.Enabled)
                     .OrderBy(item => RulePriority(item.Action))
                     .ThenBy(item => item.MatchType)
                     .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase))
        {
            rules.Add(ToRuleString(rule, foreignProxyName));
        }

        rules.AddRange(
        [
            $"GEOSITE,private,{DirectProxyName}",
            $"IP-CIDR,127.0.0.0/8,{DirectProxyName},no-resolve",
            $"IP-CIDR,10.0.0.0/8,{DirectProxyName},no-resolve",
            $"IP-CIDR,100.64.0.0/10,{DirectProxyName},no-resolve",
            $"IP-CIDR,169.254.0.0/16,{DirectProxyName},no-resolve",
            $"IP-CIDR,172.16.0.0/12,{DirectProxyName},no-resolve",
            $"IP-CIDR,192.168.0.0/16,{DirectProxyName},no-resolve",
            $"GEOSITE,cn,{DirectProxyName}",
            $"GEOIP,CN,{DirectProxyName},no-resolve",
            $"MATCH,{foreignProxyName}"
        ]);

        return new YamlSequenceNode(rules.Select(Scalar));
    }

    private static int RulePriority(RuleAction action)
    {
        return action switch
        {
            RuleAction.Block => 0,
            RuleAction.Proxy => 1,
            RuleAction.Direct => 2,
            _ => 3
        };
    }

    private static string ToRuleString(CustomRule rule, string foreignProxyName)
    {
        var value = rule.Value.Trim();
        var policy = rule.Action switch
        {
            RuleAction.Direct => DirectProxyName,
            RuleAction.Proxy => foreignProxyName,
            RuleAction.Block => "REJECT",
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };

        return rule.MatchType switch
        {
            RuleMatchType.Domain => $"DOMAIN,{NormalizeDomain(value)},{policy}",
            RuleMatchType.DomainSuffix => $"DOMAIN-SUFFIX,{NormalizeDomain(value.TrimStart('*', '.'))},{policy}",
            RuleMatchType.IpCidr => $"IP-CIDR,{value},{policy},no-resolve",
            RuleMatchType.ProcessName => $"PROCESS-NAME,{value},{policy}",
            RuleMatchType.ProcessPath => $"PROCESS-PATH,{value},{policy}",
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };
    }

    private static string NormalizeDomain(string value)
    {
        return new IdnMapping().GetAscii(value.Trim().TrimEnd('.')).ToLowerInvariant();
    }

    private static void ExtractProxies(
        YamlMappingNode root,
        string subscriptionName,
        string interfaceName,
        YamlSequenceNode target,
        List<string> names,
        ISet<string> usedNames,
        List<string> warnings)
    {
        if (!TryGet(root, "proxies", out var node) || node is not YamlSequenceNode sequence)
        {
            return;
        }

        foreach (var item in sequence.Children.OfType<YamlMappingNode>())
        {
            var clone = (YamlMappingNode)Clone(item);
            var originalName = GetScalar(clone, "name");
            var type = GetScalar(clone, "type");
            if (string.IsNullOrWhiteSpace(originalName) || string.IsNullOrWhiteSpace(type))
            {
                warnings.Add($"订阅“{subscriptionName}”包含缺少 name/type 的节点，已跳过。");
                continue;
            }

            if (type.Equals("direct", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("reject", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsAnnouncementProxyName(originalName))
            {
                warnings.Add(
                    $"订阅“{subscriptionName}”中的“{originalName}”看起来是公告而非代理节点，已跳过。");
                continue;
            }

            var server = GetScalar(clone, "server");
            if (string.IsNullOrWhiteSpace(server))
            {
                warnings.Add($"订阅“{subscriptionName}”中的节点“{originalName}”缺少服务器地址，已跳过。");
                continue;
            }

            if (IsLoopbackOrUnspecifiedHost(server))
            {
                warnings.Add(
                    $"订阅“{subscriptionName}”中的节点“{originalName}”指向本机回环/未指定地址 {server}，"
                    + "已跳过，避免公告节点污染自动测速。");
                continue;
            }

            if (TryGet(clone, "dialer-proxy", out _))
            {
                warnings.Add($"节点“{originalName}”使用 dialer-proxy，首版未导入该链式节点。");
                continue;
            }

            var name = MakeUniqueName(originalName, subscriptionName, usedNames);
            SetScalar(clone, "name", name);
            SetScalar(clone, "interface-name", interfaceName);
            target.Add(clone);
            names.Add(name);
        }
    }

    private static bool IsAnnouncementProxyName(string name)
    {
        string[] markers =
        [
            "无法更新/使用",
            "请从官网",
            "官网获取",
            "请查看公告",
            "请尽快更新软件",
            "协议将弃用",
            "剩余流量",
            "到期时间",
            "过期时间",
            "订阅到期"
        ];
        return markers.Any(marker =>
            name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static void ExtractProviders(
        YamlMappingNode root,
        string subscriptionName,
        string interfaceName,
        List<string> providerNames,
        ISet<string> usedNames,
        out IReadOnlyList<KeyValuePair<YamlNode, YamlNode>> providers,
        List<string> warnings)
    {
        var result = new List<KeyValuePair<YamlNode, YamlNode>>();
        if (!TryGet(root, "proxy-providers", out var node) || node is not YamlMappingNode mapping)
        {
            providers = result;
            return;
        }

        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode key || pair.Value is not YamlMappingNode value)
            {
                continue;
            }

            var clone = (YamlMappingNode)Clone(value);
            if (!TryGet(clone, "url", out _) && !TryGet(clone, "payload", out _))
            {
                warnings.Add($"代理提供者“{key.Value}”没有 URL 或内联内容，已跳过。");
                continue;
            }

            var providerName = MakeUniqueName(key.Value ?? "provider", subscriptionName, usedNames);
            var overrideNode = TryGet(clone, "override", out var existingOverride)
                && existingOverride is YamlMappingNode existingMapping
                    ? existingMapping
                    : new YamlMappingNode();
            SetScalar(overrideNode, "interface-name", interfaceName);
            SetNode(clone, "override", overrideNode);
            SetScalar(clone, "path", $"./providers/{SafeFileName(providerName)}.yaml");
            result.Add(new KeyValuePair<YamlNode, YamlNode>(Scalar(providerName), clone));
            providerNames.Add(providerName);
        }

        providers = result;
    }

    private static YamlMappingNode ParseRoot(string yaml, string sourceName)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw new InvalidOperationException($"订阅“{sourceName}”不是有效的 Mihomo YAML 映射。");
            }

            return root;
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException($"订阅“{sourceName}”的 YAML 无法解析。", exception);
        }
    }

    private static string MakeUniqueName(string original, string sourceName, ISet<string> usedNames)
    {
        var candidate = original.Trim();
        if (usedNames.Add(candidate))
        {
            return candidate;
        }

        candidate = $"[{sourceName.Trim()}] {candidate}";
        var suffix = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = $"[{sourceName.Trim()}] {original.Trim()} {suffix++}";
        }

        return candidate;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return safe.Length <= 80 ? safe : safe[..80];
    }

    private static bool IsIpv4(string value)
    {
        return IPAddress.TryParse(value, out var address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    private static bool IsLoopbackOrUnspecifiedHost(string value)
    {
        var host = value.Trim().TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("ip6-localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address)
            && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));
    }

    private static bool IsPrivateOrLinkLocalPrefix(string value)
    {
        var separator = value.IndexOf('/');
        var addressText = separator >= 0 ? value[..separator] : value;
        if (!IPAddress.TryParse(addressText, out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 127
            || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
            || bytes[0] == 169 && bytes[1] == 254
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168;
    }

    private static string? GetScalar(YamlMappingNode mapping, string key)
    {
        return TryGet(mapping, key, out var node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;
    }

    private static bool TryGet(YamlMappingNode mapping, string key, out YamlNode node)
    {
        return mapping.Children.TryGetValue(Scalar(key), out node!);
    }

    private static void SetScalar(YamlMappingNode mapping, string key, string value)
    {
        SetNode(mapping, key, Scalar(value));
    }

    private static void SetNode(YamlMappingNode mapping, string key, YamlNode value)
    {
        var yamlKey = Scalar(key);
        if (mapping.Children.ContainsKey(yamlKey))
        {
            mapping.Children[yamlKey] = value;
        }
        else
        {
            mapping.Add(yamlKey, value);
        }
    }

    private static YamlNode Clone(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar => new YamlScalarNode(scalar.Value) { Style = scalar.Style },
            YamlSequenceNode sequence => new YamlSequenceNode(sequence.Children.Select(Clone))
            {
                Style = sequence.Style
            },
            YamlMappingNode mapping => new YamlMappingNode(mapping.Children.Select(pair =>
                new KeyValuePair<YamlNode, YamlNode>(Clone(pair.Key), Clone(pair.Value))))
            {
                Style = mapping.Style
            },
            _ => throw new NotSupportedException($"Unsupported YAML node type {node.GetType().Name}.")
        };
    }

    private static YamlScalarNode Scalar(string value)
    {
        return new YamlScalarNode(value);
    }

    private static YamlScalarNode Scalar(bool value)
    {
        return new YamlScalarNode(value ? "true" : "false");
    }

    private static YamlScalarNode Scalar(int value)
    {
        return new YamlScalarNode(value.ToString(CultureInfo.InvariantCulture));
    }
}
