using NetSplit.Core;

namespace NetSplit.Core.Tests;

public sealed class ConfigurationValidatorTests
{
    [Fact]
    public void ValidateRejectsSameAdapterAndConflictingRule()
    {
        var adapter = new NetworkAdapterSnapshot
        {
            Id = "same",
            Name = "adapter",
            MacAddress = "AA-BB",
            IsUp = true,
            IsSelectable = true,
            Ipv4Addresses = ["192.168.1.2"],
            Gateways = ["192.168.1.1"]
        };
        var settings = new SplitRouteSettings
        {
            DirectAdapter = new AdapterBinding { Id = adapter.Id },
            ProxyAdapter = new AdapterBinding { Id = adapter.Id },
            Subscriptions =
            [
                new SubscriptionSettings
                {
                    Name = "sub",
                    ProtectedSource = "value"
                }
            ],
            Rules =
            [
                new CustomRule
                {
                    MatchType = RuleMatchType.Domain,
                    Action = RuleAction.Direct,
                    Value = "example.com"
                },
                new CustomRule
                {
                    MatchType = RuleMatchType.Domain,
                    Action = RuleAction.Proxy,
                    Value = "example.com"
                }
            ]
        };

        var result = ConfigurationValidator.Validate(settings, [adapter]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("必须是不同接口", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("不能同时配置", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsInvalidResidentialProxyAndMissingCredentials()
    {
        var direct = Adapter("direct", "主宽带");
        var proxy = Adapter("proxy", "F50");
        var settings = Settings(direct, proxy) with
        {
            ResidentialProxy = new ResidentialProxySettings
            {
                Enabled = true,
                Host = "https://proxy.example/path",
                Port = 70000,
                AuthenticationEnabled = true
            }
        };

        var result = ConfigurationValidator.Validate(settings, [direct, proxy]);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("域名或 IPv4", StringComparison.Ordinal));
        Assert.Contains(
            result.Errors,
            error => error.Contains("用户名和密码", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("PROXY.EXAMPLE", "proxy.example")]
    [InlineData("198.51.100.20", "198.51.100.20")]
    public void NormalizeResidentialProxyHostAcceptsDnsAndIpv4(string value, string expected)
    {
        Assert.Equal(expected, ResidentialProxyValidator.NormalizeHost(value));
    }

    [Theory]
    [InlineData("10.0.0.1", 24, "10.0.0.0/24")]
    [InlineData("192.168.6.2", 24, "192.168.6.0/24")]
    [InlineData("172.16.7.9", 16, "172.16.0.0/16")]
    public void ToNetworkPrefixReturnsExpectedNetwork(
        string address,
        int prefix,
        string expected)
    {
        Assert.Equal(
            expected,
            CidrUtility.ToNetworkPrefix(System.Net.IPAddress.Parse(address), prefix));
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
                LastKnownName = direct.Name
            },
            ProxyAdapter = new AdapterBinding
            {
                Id = proxy.Id,
                LastKnownName = proxy.Name
            },
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

    private static NetworkAdapterSnapshot Adapter(string id, string name)
    {
        return new NetworkAdapterSnapshot
        {
            Id = id,
            Name = name,
            IsUp = true,
            IsSelectable = true,
            Ipv4Addresses = ["192.168.1.2"],
            Gateways = ["192.168.1.1"]
        };
    }
}
