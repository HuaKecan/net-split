using NetSplit.Core;
using YamlDotNet.RepresentationModel;

namespace NetSplit.Core.Tests;

public sealed class MihomoConfigGeneratorTests
{
    [Fact]
    public void GenerateBindsDirectAndProxyTrafficToDifferentAdapters()
    {
        var direct = Adapter("direct-id", "主宽带", "192.168.6.2", "192.168.6.1", "192.168.6.1");
        var proxy = Adapter("proxy-id", "F50", "192.168.0.163", "192.168.0.1", "192.168.0.1");
        var settings = Settings(direct, proxy) with
        {
            Rules =
            [
                new CustomRule
                {
                    MatchType = RuleMatchType.Domain,
                    Action = RuleAction.Block,
                    Value = "ads.example"
                },
                new CustomRule
                {
                    MatchType = RuleMatchType.DomainSuffix,
                    Action = RuleAction.Proxy,
                    Value = "github.com"
                },
                new CustomRule
                {
                    MatchType = RuleMatchType.ProcessName,
                    Action = RuleAction.Direct,
                    Value = "game.exe"
                }
            ]
        };
        var subscription = new SubscriptionDocument
        {
            Id = Guid.NewGuid(),
            Name = "airport",
            Yaml = """
                proxies:
                  - name: node-a
                    type: ss
                    server: 203.0.113.10
                    port: 443
                    cipher: aes-128-gcm
                    password: test
                proxy-providers:
                  remote:
                    type: http
                    url: https://example.invalid/subscription
                    interval: 3600
                """
        };

        var result = MihomoConfigGenerator.Generate(
            [subscription],
            settings,
            direct,
            proxy,
            [direct, proxy]);
        var root = Parse(result.Yaml);

        var proxies = Sequence(root, "proxies").Children.Cast<YamlMappingNode>().ToArray();
        Assert.Equal("主宽带", Scalar(proxies[0], "interface-name"));
        Assert.Equal(
            MihomoConfigGenerator.ProxyBootstrapDirectName,
            Scalar(proxies[1], "name"));
        Assert.Equal("F50", Scalar(proxies[1], "interface-name"));
        Assert.Equal("F50", Scalar(proxies[2], "interface-name"));

        var providers = Mapping(root, "proxy-providers");
        var remote = (YamlMappingNode)providers.Children.Single().Value;
        Assert.Equal("F50", Scalar(Mapping(remote, "override"), "interface-name"));

        var tun = Mapping(root, "tun");
        Assert.Equal("false", Scalar(tun, "auto-detect-interface"));
        Assert.Equal("true", Scalar(tun, "strict-route"));
        Assert.Contains(
            Sequence(tun, "route-exclude-address").Children.Cast<YamlScalarNode>(),
            item => item.Value == "192.168.6.0/24");

        var dns = Mapping(root, "dns");
        Assert.Equal(
            $"127.0.0.1:{MihomoConfigGenerator.DnsListenPort}",
            Scalar(dns, "listen"));

        var rules = Sequence(root, "rules").Children
            .Cast<YamlScalarNode>()
            .Select(item => item.Value)
            .ToArray();
        Assert.StartsWith("DOMAIN,ads.example,REJECT", rules[0], StringComparison.Ordinal);
        Assert.StartsWith(
            $"DOMAIN-SUFFIX,github.com,{MihomoConfigGenerator.ProxyGroupName}",
            rules[1],
            StringComparison.Ordinal);
        Assert.StartsWith(
            $"PROCESS-NAME,game.exe,{MihomoConfigGenerator.DirectProxyName}",
            rules[2],
            StringComparison.Ordinal);
        Assert.Contains(
            $"GEOSITE,private,{MihomoConfigGenerator.DirectProxyName}",
            rules);
        Assert.DoesNotContain(
            rules,
            rule => rule is not null
                && (rule.EndsWith(",DIRECT", StringComparison.Ordinal)
                    || rule.Contains(",DIRECT,", StringComparison.Ordinal)));
        Assert.Equal($"MATCH,{MihomoConfigGenerator.ProxyGroupName}", rules[^1]);
    }

    [Fact]
    public void GenerateChainsResidentialProxyThroughAirportAndUsesItForDnsAndRules()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1", "192.168.0.1");
        var settings = Settings(direct, proxy) with
        {
            ResidentialProxy = new ResidentialProxySettings
            {
                Enabled = true,
                Host = "residential.example",
                Port = 1080,
                AuthenticationEnabled = true,
                ProtectedUsername = "protected-user",
                ProtectedPassword = "protected-password",
                RouteMode = ResidentialProxyRouteMode.ThroughAirport
            },
            Rules =
            [
                new CustomRule
                {
                    MatchType = RuleMatchType.DomainSuffix,
                    Action = RuleAction.Proxy,
                    Value = "github.com"
                }
            ]
        };

        var result = MihomoConfigGenerator.Generate(
            [
                new SubscriptionDocument
                {
                    Id = Guid.NewGuid(),
                    Name = "airport",
                    Yaml = """
                        proxies:
                          - name: node
                            type: ss
                            server: 203.0.113.10
                            port: 443
                            cipher: aes-128-gcm
                            password: test
                        """
                }
            ],
            settings,
            direct,
            proxy,
            [direct, proxy],
            new ResidentialProxyCredentials("resident-user", "resident-password"));
        var root = Parse(result.Yaml);
        var residential = Sequence(root, "proxies")
            .Children
            .Cast<YamlMappingNode>()
            .Single(item =>
                Scalar(item, "name") == MihomoConfigGenerator.ResidentialProxyName);

        Assert.Equal("socks5", Scalar(residential, "type"));
        Assert.Equal("residential.example", Scalar(residential, "server"));
        Assert.Equal("1080", Scalar(residential, "port"));
        Assert.Equal("resident-user", Scalar(residential, "username"));
        Assert.Equal("resident-password", Scalar(residential, "password"));
        Assert.Equal(MihomoConfigGenerator.ProxyGroupName, Scalar(residential, "dialer-proxy"));
        Assert.False(residential.Children.ContainsKey(new YamlScalarNode("interface-name")));

        var rules = Sequence(root, "rules").Children
            .Cast<YamlScalarNode>()
            .Select(item => item.Value)
            .ToArray();
        Assert.Equal(
            $"DOMAIN-SUFFIX,github.com,{MihomoConfigGenerator.ResidentialProxyName}",
            rules[0]);
        Assert.Equal($"MATCH,{MihomoConfigGenerator.ResidentialProxyName}", rules[^1]);

        var resolvers = Sequence(Mapping(root, "dns"), "nameserver")
            .Children
            .Cast<YamlScalarNode>()
            .Select(item => item.Value)
            .ToArray();
        Assert.All(
            resolvers,
            resolver => Assert.Contains(
                $"#{MihomoConfigGenerator.ResidentialProxyName}",
                resolver,
                StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateCanBindResidentialProxyDirectlyToNic2WithoutAuthentication()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1", "192.168.0.1");
        var settings = Settings(direct, proxy) with
        {
            ResidentialProxy = new ResidentialProxySettings
            {
                Enabled = true,
                Host = "198.51.100.10",
                Port = 1080,
                AuthenticationEnabled = false,
                RouteMode = ResidentialProxyRouteMode.DirectNic2
            }
        };

        var result = MihomoConfigGenerator.Generate(
            [
                new SubscriptionDocument
                {
                    Id = Guid.NewGuid(),
                    Name = "airport",
                    Yaml = """
                        proxies:
                          - name: node
                            type: ss
                            server: 203.0.113.10
                            port: 443
                            cipher: aes-128-gcm
                            password: test
                        """
                }
            ],
            settings,
            direct,
            proxy,
            [direct, proxy]);
        var root = Parse(result.Yaml);
        var residential = Sequence(root, "proxies")
            .Children
            .Cast<YamlMappingNode>()
            .Single(item =>
                Scalar(item, "name") == MihomoConfigGenerator.ResidentialProxyName);

        Assert.Equal("F50", Scalar(residential, "interface-name"));
        Assert.False(residential.Children.ContainsKey(new YamlScalarNode("dialer-proxy")));
        Assert.False(residential.Children.ContainsKey(new YamlScalarNode("username")));
        Assert.False(residential.Children.ContainsKey(new YamlScalarNode("password")));
    }

    [Fact]
    public void GenerateNeverAddsDirectFallbackToProxyGroups()
    {
        var direct = Adapter("direct", "主宽带", "10.0.0.2", "10.0.0.1", "10.0.0.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1", "192.168.0.1");
        var result = MihomoConfigGenerator.Generate(
            [
                new SubscriptionDocument
                {
                    Id = Guid.NewGuid(),
                    Name = "sub",
                    Yaml = """
                        proxies:
                          - name: DIRECT
                            type: direct
                          - name: node
                            type: socks5
                            server: 203.0.113.20
                            port: 1080
                        """
                }
            ],
            Settings(direct, proxy),
            direct,
            proxy,
            [direct, proxy]);
        var root = Parse(result.Yaml);
        var groups = Sequence(root, "proxy-groups").Children.Cast<YamlMappingNode>();

        foreach (var group in groups)
        {
            if (TrySequence(group, "proxies", out var members))
            {
                Assert.DoesNotContain(
                    members.Children.Cast<YamlScalarNode>(),
                    item => string.Equals(item.Value, "DIRECT", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void GenerateSkipsAnnouncementEntriesWithRoutableServers()
    {
        var direct = Adapter("direct", "主宽带", "10.0.0.2", "10.0.0.1", "10.0.0.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1", "192.168.0.1");
        var result = MihomoConfigGenerator.Generate(
            [
                new SubscriptionDocument
                {
                    Id = Guid.NewGuid(),
                    Name = "sub",
                    Yaml = """
                        proxies:
                          - name: 您无法更新/使用 请从官网获取最新订阅
                            type: ss
                            server: 203.0.113.10
                            port: 443
                            cipher: aes-128-gcm
                            password: notice
                          - name: 香港 01
                            type: ss
                            server: 203.0.113.20
                            port: 443
                            cipher: aes-128-gcm
                            password: usable
                        """
                }
            ],
            Settings(direct, proxy),
            direct,
            proxy,
            [direct, proxy]);

        Assert.Equal(["香港 01"], result.ProxyNames);
        Assert.DoesNotContain(
            "您无法更新/使用 请从官网获取最新订阅",
            result.Yaml,
            StringComparison.Ordinal);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("公告而非代理节点", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateUsesProxyDnsByDefaultAndExcludesOnlyApprovedLanPrefixes()
    {
        var direct = Adapter("direct", "主宽带", "192.168.6.2", "192.168.6.1", "192.168.6.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1", "192.168.0.1");
        var publicAdapter = Adapter(
            "public",
            "Public",
            "203.0.113.2",
            "203.0.113.1",
            "203.0.113.1") with
        {
            ConnectedPrefixes = ["203.0.113.0/24"]
        };
        var tunAdapter = Adapter(
            "tun",
            "NetSplit",
            "198.18.0.1",
            "198.18.0.2",
            "198.18.0.2") with
        {
            ConnectedPrefixes = ["198.18.0.0/16"],
            IsTunnelOrLoopback = true
        };
        var cgnatAdapter = Adapter(
            "cgnat",
            "Mobile LAN",
            "100.64.10.2",
            "100.64.10.1",
            "100.64.10.1") with
        {
            ConnectedPrefixes = ["100.64.10.0/24"]
        };
        var result = MihomoConfigGenerator.Generate(
            [
                new SubscriptionDocument
                {
                    Id = Guid.NewGuid(),
                    Name = "sub",
                    Yaml = """
                        proxies:
                          - name: node
                            type: socks5
                            server: 203.0.113.20
                            port: 1080
                        """
                }
            ],
            Settings(direct, proxy),
            direct,
            proxy,
            [direct, proxy, publicAdapter, tunAdapter, cgnatAdapter]);
        var root = Parse(result.Yaml);

        var dns = Mapping(root, "dns");
        var defaultResolvers = Sequence(dns, "nameserver").Children
            .Cast<YamlScalarNode>()
            .Select(item => item.Value)
            .ToArray();
        Assert.All(
            defaultResolvers,
            resolver => Assert.Contains(
                $"#{MihomoConfigGenerator.ProxyGroupName}",
                resolver,
                StringComparison.Ordinal));

        var dnsPolicy = Mapping(dns, "nameserver-policy");
        var directPolicy = Assert.IsType<YamlSequenceNode>(
            dnsPolicy.Children[new YamlScalarNode("geosite:private,cn")]);
        Assert.All(
            directPolicy.Children.Cast<YamlScalarNode>(),
            resolver => Assert.Contains(
                $"#{MihomoConfigGenerator.DirectProxyName}",
                resolver.Value,
                StringComparison.Ordinal));
        var bootstrapResolvers = Sequence(dns, "proxy-server-nameserver").Children
            .Cast<YamlScalarNode>()
            .Select(item => item.Value)
            .ToArray();
        Assert.All(
            bootstrapResolvers,
            resolver => Assert.Contains(
                $"#{MihomoConfigGenerator.ProxyBootstrapDirectName}",
                resolver,
                StringComparison.Ordinal));

        var exclusions = Sequence(Mapping(root, "tun"), "route-exclude-address")
            .Children
            .Cast<YamlScalarNode>()
            .Select(item => item.Value)
            .ToArray();
        Assert.Contains("192.168.6.0/24", exclusions);
        Assert.Contains("192.168.0.0/24", exclusions);
        Assert.Contains("100.64.10.0/24", exclusions);
        Assert.DoesNotContain("203.0.113.0/24", exclusions);
        Assert.DoesNotContain("198.18.0.0/16", exclusions);
    }

    [Fact]
    public void GenerateSkipsUnsupportedDialerProxyNode()
    {
        var direct = Adapter("direct", "主宽带", "10.0.0.2", "10.0.0.1", "10.0.0.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1", "192.168.0.1");
        var result = MihomoConfigGenerator.Generate(
            [
                new SubscriptionDocument
                {
                    Id = Guid.NewGuid(),
                    Name = "sub",
                    Yaml = """
                        proxies:
                          - name: chained
                            type: socks5
                            server: 203.0.113.30
                            port: 1080
                            dialer-proxy: old-group
                          - name: usable
                            type: ss
                            server: 203.0.113.31
                            port: 443
                            cipher: aes-128-gcm
                            password: test
                        """
                }
            ],
            Settings(direct, proxy),
            direct,
            proxy,
            [direct, proxy]);

        Assert.DoesNotContain("chained", result.ProxyNames);
        Assert.Contains("usable", result.ProxyNames);
        Assert.Contains(result.Warnings, warning => warning.Contains("dialer-proxy", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateSkipsLoopbackAnnouncementNodes()
    {
        var direct = Adapter("direct", "主宽带", "10.0.0.2", "10.0.0.1", "10.0.0.1");
        var proxy = Adapter("proxy", "F50", "192.168.0.2", "192.168.0.1", "192.168.0.1");
        var result = MihomoConfigGenerator.Generate(
            [
                new SubscriptionDocument
                {
                    Id = Guid.NewGuid(),
                    Name = "sub",
                    Yaml = """
                        proxies:
                          - name: announcement
                            type: ss
                            server: 127.0.0.1
                            port: 10000
                            cipher: aes-128-gcm
                            password: test
                          - name: localhost-announcement
                            type: ss
                            server: localhost
                            port: 10000
                            cipher: aes-128-gcm
                            password: test
                          - name: usable
                            type: socks5
                            server: 203.0.113.20
                            port: 1080
                        """
                }
            ],
            Settings(direct, proxy),
            direct,
            proxy,
            [direct, proxy]);

        Assert.DoesNotContain("announcement", result.ProxyNames);
        Assert.DoesNotContain("localhost-announcement", result.ProxyNames);
        Assert.Contains("usable", result.ProxyNames);
        Assert.Single(result.ProxyNames);
        Assert.Equal(2, result.Warnings.Count(warning =>
            warning.Contains("回环/未指定地址", StringComparison.Ordinal)));
    }

    private static SplitRouteSettings Settings(
        NetworkAdapterSnapshot direct,
        NetworkAdapterSnapshot proxy)
    {
        return new SplitRouteSettings
        {
            DirectAdapter = new AdapterBinding
            {
                Id = direct.Id,
                MacAddress = direct.MacAddress,
                LastKnownName = direct.Name
            },
            ProxyAdapter = new AdapterBinding
            {
                Id = proxy.Id,
                MacAddress = proxy.MacAddress,
                LastKnownName = proxy.Name
            },
            ControllerSecret = "test-secret",
            Subscriptions =
            [
                new SubscriptionSettings
                {
                    Name = "test",
                    ProtectedSource = "protected"
                }
            ]
        };
    }

    private static NetworkAdapterSnapshot Adapter(
        string id,
        string name,
        string address,
        string gateway,
        string dns)
    {
        return new NetworkAdapterSnapshot
        {
            Id = id,
            Name = name,
            Description = name,
            MacAddress = id,
            InterfaceIndex = id.GetHashCode(StringComparison.Ordinal),
            IsUp = true,
            IsSelectable = true,
            Ipv4Addresses = [address],
            Gateways = [gateway],
            DnsServers = [dns],
            ConnectedPrefixes =
            [
                address.StartsWith("192.168.6.", StringComparison.Ordinal)
                    ? "192.168.6.0/24"
                    : address.StartsWith("192.168.0.", StringComparison.Ordinal)
                        ? "192.168.0.0/24"
                        : "10.0.0.0/24"
            ]
        };
    }

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static YamlMappingNode Mapping(YamlMappingNode mapping, string key)
    {
        return Assert.IsType<YamlMappingNode>(mapping.Children[new YamlScalarNode(key)]);
    }

    private static YamlSequenceNode Sequence(YamlMappingNode mapping, string key)
    {
        return Assert.IsType<YamlSequenceNode>(mapping.Children[new YamlScalarNode(key)]);
    }

    private static bool TrySequence(
        YamlMappingNode mapping,
        string key,
        out YamlSequenceNode sequence)
    {
        if (mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlSequenceNode found)
        {
            sequence = found;
            return true;
        }

        sequence = new YamlSequenceNode();
        return false;
    }

    private static string Scalar(YamlMappingNode mapping, string key)
    {
        return Assert.IsType<YamlScalarNode>(mapping.Children[new YamlScalarNode(key)]).Value
            ?? string.Empty;
    }
}
