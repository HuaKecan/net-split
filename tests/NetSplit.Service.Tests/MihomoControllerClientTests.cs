using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NetSplit.Core;
using NetSplit.Service;

namespace NetSplit.Service.Tests;

public sealed class MihomoControllerClientTests
{
    [Fact]
    public void DependencyInjectionResolvesControllerWithDefaultDnsProbe()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<IMihomoControllerClient, MihomoControllerClient>();
        using var provider = services.BuildServiceProvider();

        var controller = provider.GetRequiredService<IMihomoControllerClient>();

        Assert.IsType<MihomoControllerClient>(controller);
    }

    [Fact]
    public async Task WaitUntilReadyAcceptsMihomoConfigsWithoutDnsProperty()
    {
        var configRequests = 0;
        var dnsProbeCalls = 0;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/version")
            {
                return JsonResponse("""{"meta":true}""");
            }

            if (request.RequestUri?.AbsolutePath == "/configs")
            {
                configRequests++;
                return JsonResponse(configRequests == 1
                    ? """{"tun":{"enable":false}}"""
                    : """{"tun":{"enable":true}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var client = new MihomoControllerClient(
            httpClient,
            dnsListenerProbe: (port, _) =>
            {
                Assert.Equal(MihomoConfigGenerator.DnsListenPort, port);
                dnsProbeCalls++;
                return Task.FromResult(true);
            });

        var ready = await client.WaitUntilReadyAsync(
            new SplitRouteSettings
            {
                ControllerPort = 19097,
                ControllerSecret = "secret"
            },
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(ready);
        Assert.True(configRequests >= 2);
        Assert.Equal(1, dnsProbeCalls);
    }

    [Fact]
    public async Task WaitUntilReadyRejectsEnabledTunWhenDnsListenerIsUnavailable()
    {
        var dnsProbeCalls = 0;
        var stopwatch = Stopwatch.StartNew();
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/version")
            {
                return JsonResponse("""{"meta":true}""");
            }

            if (request.RequestUri?.AbsolutePath == "/configs")
            {
                return JsonResponse("""{"tun":{"enable":true}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var client = new MihomoControllerClient(
            httpClient,
            dnsListenerProbe: (_, _) =>
            {
                dnsProbeCalls++;
                return Task.FromResult(false);
            });

        var ready = await client.WaitUntilReadyAsync(
            new SplitRouteSettings
            {
                ControllerPort = 19097,
                ControllerSecret = "secret"
            },
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);
        stopwatch.Stop();

        Assert.False(ready);
        Assert.True(dnsProbeCalls > 0);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task WaitUntilReadyBoundsSlowDnsProbeByOverallDeadline()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
            request.RequestUri?.AbsolutePath switch
            {
                "/version" => JsonResponse("""{"meta":true}"""),
                "/configs" => JsonResponse("""{"tun":{"enable":true}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }));
        var client = new MihomoControllerClient(
            httpClient,
            dnsListenerProbe: async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            });
        var stopwatch = Stopwatch.StartNew();

        var ready = await client.WaitUntilReadyAsync(
            new SplitRouteSettings
            {
                ControllerPort = 19097,
                ControllerSecret = "secret"
            },
            TimeSpan.FromMilliseconds(75),
            CancellationToken.None);
        stopwatch.Stop();

        Assert.False(ready);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task WaitUntilReadyObservesPreCanceledTokenWithZeroTimeout()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("No request should be sent.")));
        var client = new MihomoControllerClient(httpClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.WaitUntilReadyAsync(
                new SplitRouteSettings(),
                TimeSpan.Zero,
                cancellation.Token));
    }

    [Fact]
    public async Task GetSnapshotResolvesSelectedGroupHealthAndHealthyNodes()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/configs")
            {
                return JsonResponse("""{"tun":{"enable":true},"dns":{"enable":true}}""");
            }

            if (request.RequestUri?.AbsolutePath == "/proxies")
            {
                return JsonResponse(
                    """
                    {
                      "proxies": {
                        "NETSPLIT-PROXY": {
                          "type": "Selector",
                          "now": "NETSPLIT-AUTO",
                          "all": ["NETSPLIT-AUTO", "manual-node"]
                        },
                        "NETSPLIT-AUTO": {
                          "type": "URLTest",
                          "now": "manual-node",
                          "all": ["manual-node", "offline-node"]
                        },
                        "manual-node": {
                          "type": "Shadowsocks",
                          "alive": true
                        },
                        "offline-node": {
                          "type": "Shadowsocks",
                          "alive": false
                        }
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var client = new MihomoControllerClient(
            httpClient,
            dnsListenerProbe: (_, _) => Task.FromResult(true));

        var snapshot = await client.GetSnapshotAsync(
            new SplitRouteSettings
            {
                ControllerPort = 19097,
                ControllerSecret = "secret"
            },
            CancellationToken.None);

        Assert.True(snapshot.TunEnabled);
        Assert.True(snapshot.DnsEnabled);
        Assert.Equal("NETSPLIT-AUTO", snapshot.CurrentProxy);
        Assert.Equal("manual-node", snapshot.EffectiveProxy);
        Assert.Equal(true, snapshot.SelectedProxyHealthy);
        Assert.Contains("manual-node", snapshot.HealthyProxies);
        Assert.DoesNotContain("offline-node", snapshot.HealthyProxies);
    }

    [Fact]
    public async Task GetSnapshotReportsDnsFromListenerProbeInsteadOfConfigsPayload()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/configs")
            {
                return JsonResponse("""{"tun":{"enable":true},"dns":{"enable":true}}""");
            }

            if (request.RequestUri?.AbsolutePath == "/proxies")
            {
                return JsonResponse(
                    """
                    {
                      "proxies": {
                        "NETSPLIT-PROXY": {
                          "type": "Selector",
                          "now": "node",
                          "all": ["node"]
                        },
                        "node": {
                          "type": "Shadowsocks",
                          "alive": true
                        }
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var client = new MihomoControllerClient(
            httpClient,
            dnsListenerProbe: (_, _) => Task.FromResult(false));

        var snapshot = await client.GetSnapshotAsync(
            new SplitRouteSettings
            {
                ControllerPort = 19097,
                ControllerSecret = "secret"
            },
            CancellationToken.None);

        Assert.True(snapshot.TunEnabled);
        Assert.False(snapshot.DnsEnabled);
    }

    [Fact]
    public async Task GetSnapshotBoundsSlowDnsProbeByRemainingRequestTimeout()
    {
        using var httpClient = new HttpClient(new StubHandler(ReadySnapshotResponse));
        var client = new MihomoControllerClient(
            httpClient,
            TimeSpan.FromMilliseconds(75),
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            });
        var stopwatch = Stopwatch.StartNew();

        var snapshot = await client.GetSnapshotAsync(
            new SplitRouteSettings
            {
                ControllerPort = 19097,
                ControllerSecret = "secret"
            },
            CancellationToken.None);
        stopwatch.Stop();

        Assert.False(snapshot.DnsEnabled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task GetSnapshotPropagatesCallerCancellationDuringDnsProbe()
    {
        using var httpClient = new HttpClient(new StubHandler(ReadySnapshotResponse));
        var client = new MihomoControllerClient(
            httpClient,
            TimeSpan.FromSeconds(5),
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetSnapshotAsync(
                new SplitRouteSettings
                {
                    ControllerPort = 19097,
                    ControllerSecret = "secret"
                },
                cancellation.Token));
    }

    [Fact]
    public async Task GetSnapshotUsesSingleTimeoutBudgetAcrossControllerRequests()
    {
        using var httpClient = new HttpClient(new AsyncStubHandler(
            async (request, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken);
                return ReadySnapshotResponse(request);
            }));
        var client = new MihomoControllerClient(
            httpClient,
            TimeSpan.FromMilliseconds(100),
            (_, _) => Task.FromResult(true));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetSnapshotAsync(
                new SplitRouteSettings
                {
                    ControllerPort = 19097,
                    ControllerSecret = "secret"
                },
                CancellationToken.None));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSnapshotChecksResidentialFinalRouteInsteadOfAirportGroup()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/configs")
            {
                return JsonResponse("""{"tun":{"enable":true}}""");
            }

            if (request.RequestUri?.AbsolutePath == "/proxies")
            {
                return JsonResponse(
                    """
                    {
                      "proxies": {
                        "NETSPLIT-PROXY": {
                          "type": "Selector",
                          "now": "NETSPLIT-AUTO",
                          "all": ["NETSPLIT-AUTO", "manual-node"]
                        },
                        "NETSPLIT-AUTO": {
                          "type": "URLTest",
                          "now": "manual-node",
                          "all": ["manual-node", "offline-node"]
                        },
                        "manual-node": {
                          "type": "Shadowsocks",
                          "alive": true
                        },
                        "offline-node": {
                          "type": "Shadowsocks",
                          "alive": false
                        },
                        "NETSPLIT-RESIDENTIAL": {
                          "type": "Socks5",
                          "alive": false
                        }
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var client = new MihomoControllerClient(
            httpClient,
            dnsListenerProbe: (_, _) => Task.FromResult(true));

        var snapshot = await client.GetSnapshotAsync(
            new SplitRouteSettings
            {
                ControllerPort = 19097,
                ControllerSecret = "secret",
                ResidentialProxy = new ResidentialProxySettings
                {
                    Enabled = true
                }
            },
            CancellationToken.None);

        Assert.False(snapshot.SelectedProxyHealthy);
        Assert.Equal(
            MihomoConfigGenerator.ResidentialProxyName,
            snapshot.EffectiveProxy);
        Assert.Contains("manual-node", snapshot.HealthyProxies);
    }

    [Fact]
    public async Task GetSnapshotFailsClosedWhenResidentialFinalRouteIsMissing()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/configs")
            {
                return JsonResponse("""{"tun":{"enable":true}}""");
            }

            if (request.RequestUri?.AbsolutePath == "/proxies")
            {
                return JsonResponse(
                    """
                    {
                      "proxies": {
                        "NETSPLIT-PROXY": {
                          "type": "Selector",
                          "now": "NETSPLIT-AUTO",
                          "all": ["NETSPLIT-AUTO", "manual-node"]
                        },
                        "NETSPLIT-AUTO": {
                          "type": "URLTest",
                          "now": "manual-node",
                          "all": ["manual-node"]
                        },
                        "manual-node": {
                          "type": "Shadowsocks",
                          "alive": true
                        }
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var client = new MihomoControllerClient(
            httpClient,
            dnsListenerProbe: (_, _) => Task.FromResult(true));

        var snapshot = await client.GetSnapshotAsync(
            new SplitRouteSettings
            {
                ControllerPort = 19097,
                ControllerSecret = "secret",
                ResidentialProxy = new ResidentialProxySettings
                {
                    Enabled = true
                }
            },
            CancellationToken.None);

        Assert.False(snapshot.SelectedProxyHealthy);
        Assert.Equal(
            MihomoConfigGenerator.ResidentialProxyName,
            snapshot.EffectiveProxy);
    }

    [Fact]
    public async Task GetSnapshotReportsSelectedGroupUnavailableWhenAllNodesAreDead()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/configs")
            {
                return JsonResponse("""{"tun":{"enable":true}}""");
            }

            if (request.RequestUri?.AbsolutePath == "/proxies")
            {
                return JsonResponse(
                    """
                    {
                      "proxies": {
                        "NETSPLIT-PROXY": {
                          "type": "Selector",
                          "now": "NETSPLIT-AUTO",
                          "all": ["NETSPLIT-AUTO"]
                        },
                        "NETSPLIT-AUTO": {
                          "type": "URLTest",
                          "now": "offline-node",
                          "all": ["offline-node"]
                        },
                        "offline-node": {
                          "type": "Shadowsocks",
                          "alive": false
                        }
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var client = new MihomoControllerClient(
            httpClient,
            dnsListenerProbe: (_, _) => Task.FromResult(true));

        var snapshot = await client.GetSnapshotAsync(
            new SplitRouteSettings
            {
                ControllerPort = 19097,
                ControllerSecret = "secret"
            },
            CancellationToken.None);

        Assert.Equal(false, snapshot.SelectedProxyHealthy);
        Assert.Empty(snapshot.HealthyProxies);
    }

    [Fact]
    public async Task SlowControllerRequestIsBoundedByPerRequestTimeout()
    {
        using var httpClient = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(1)));
        var client = new MihomoControllerClient(
            httpClient,
            TimeSpan.FromMilliseconds(50));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetSnapshotAsync(
                new SplitRouteSettings
                {
                    ControllerPort = 19097,
                    ControllerSecret = "secret"
                },
                CancellationToken.None));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToTimeoutError()
    {
        using var httpClient = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(1)));
        var client = new MihomoControllerClient(
            httpClient,
            TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetSnapshotAsync(
                new SplitRouteSettings
                {
                    ControllerPort = 19097,
                    ControllerSecret = "secret"
                },
                cancellation.Token));
    }

    [Fact]
    public async Task MissingProxyPayloadIsClassifiedAsControllerFailure()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/configs")
            {
                return JsonResponse("""{"tun":{"enable":true}}""");
            }

            if (request.RequestUri?.AbsolutePath == "/proxies")
            {
                return JsonResponse("""{"unexpected":{}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var client = new MihomoControllerClient(
            httpClient,
            dnsListenerProbe: (_, _) => Task.FromResult(true));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetSnapshotAsync(
                new SplitRouteSettings
                {
                    ControllerPort = 19097,
                    ControllerSecret = "secret"
                },
                CancellationToken.None));

        Assert.Contains("invalid response", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedJsonPayloadIsClassifiedAsControllerFailure()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/configs")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"tun":{"enable":true}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var client = new MihomoControllerClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetSnapshotAsync(
                new SplitRouteSettings
                {
                    ControllerPort = 19097,
                    ControllerSecret = "secret"
                },
                CancellationToken.None));

        Assert.Contains("invalid response", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedDelayPayloadIsClassifiedAsControllerFailure()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            JsonResponse("""{"delay":"not-a-number"}""")));
        var client = new MihomoControllerClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.MeasureDelayAsync(
                new SplitRouteSettings
                {
                    ControllerPort = 19097,
                    ControllerSecret = "secret"
                },
                "node",
                "https://example.test",
                CancellationToken.None));

        Assert.Contains("invalid response", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage ReadySnapshotResponse(HttpRequestMessage request)
    {
        return request.RequestUri?.AbsolutePath switch
        {
            "/configs" => JsonResponse("""{"tun":{"enable":true}}"""),
            "/proxies" => JsonResponse(
                """
                {
                  "proxies": {
                    "NETSPLIT-PROXY": {
                      "type": "Selector",
                      "now": "node",
                      "all": ["node"]
                    },
                    "node": {
                      "type": "Shadowsocks",
                      "alive": true
                    }
                  }
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return JsonResponse("""{"tun":{"enable":true}}""");
        }
    }

    private sealed class AsyncStubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request, cancellationToken);
        }
    }
}
