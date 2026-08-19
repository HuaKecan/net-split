using System.Diagnostics;
using NetSplit.Core;

namespace NetSplit.Core.Tests;

public sealed class P0LocalValidationTests
{
    [P0Fact]
    public async Task ValidateGeneratedConfigWithInstalledMihomo()
    {
        var directName = RequiredEnvironmentVariable("NETSPLIT_P0_DIRECT");
        var proxyName = RequiredEnvironmentVariable("NETSPLIT_P0_PROXY");
        var profilePath = RequiredEnvironmentVariable("NETSPLIT_P0_PROFILE");
        var mihomoPath = RequiredEnvironmentVariable("NETSPLIT_P0_MIHOMO");
        var geoDataDirectory = RequiredEnvironmentVariable("NETSPLIT_P0_GEODATA");
        var adapters = new WindowsNetworkAdapterProvider().GetAdapters();
        var direct = adapters.Single(item => item.Name.Equals(directName, StringComparison.OrdinalIgnoreCase));
        var proxy = adapters.Single(item => item.Name.Equals(proxyName, StringComparison.OrdinalIgnoreCase));
        var tempRoot = Path.Combine(Path.GetTempPath(), "net-split-p0", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(tempRoot);

        try
        {
            paths.EnsureDirectories();
            foreach (var fileName in new[] { "geoip.dat", "geosite.dat", "Country.mmdb" })
            {
                var source = Path.Combine(geoDataDirectory, fileName);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(paths.RuntimeDirectory, fileName), true);
                }
            }

            var document = new SubscriptionDocument
            {
                Id = Guid.NewGuid(),
                Name = "P0",
                Yaml = await File.ReadAllTextAsync(profilePath).ConfigureAwait(true)
            };
            var settings = new SplitRouteSettings
            {
                MihomoPath = mihomoPath,
                ControllerSecret = "p0-validation-only",
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
                Subscriptions =
                [
                    new SubscriptionSettings
                    {
                        Name = "P0",
                        ProtectedSource = "not-used"
                    }
                ]
            };
            var result = MihomoConfigGenerator.Generate(
                [document],
                settings,
                direct,
                proxy,
                adapters);
            await File.WriteAllTextAsync(paths.RuntimeConfigFile, result.Yaml).ConfigureAwait(true);

            using var process = Process.Start(CreateValidationStartInfo(
                mihomoPath,
                paths.RuntimeConfigFile,
                paths.RuntimeDirectory))
                ?? throw new InvalidOperationException("无法启动 Mihomo P0 校验进程。");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(true);
            var output = Sanitize(
                string.Join(Environment.NewLine, await outputTask.ConfigureAwait(true), await errorTask.ConfigureAwait(true)));

            Assert.True(process.ExitCode == 0, output);
            Assert.Contains($"interface-name: {direct.Name}", result.Yaml, StringComparison.Ordinal);
            Assert.Contains($"interface-name: {proxy.Name}", result.Yaml, StringComparison.Ordinal);
            Assert.DoesNotContain("server: 127.0.0.1", result.Yaml, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    private static ProcessStartInfo CreateValidationStartInfo(
        string mihomoPath,
        string configPath,
        string dataDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = mihomoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(dataDirectory);
        return startInfo;
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"缺少环境变量 {name}。");
    }

    private static string Sanitize(string value)
    {
        return string.Join(
            Environment.NewLine,
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Contains("://", StringComparison.Ordinal)
                    ? "[已脱敏的 Mihomo 校验输出]"
                    : line));
    }
}

public sealed class P0FactAttribute : FactAttribute
{
    public P0FactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("NETSPLIT_RUN_P0"),
                "1",
                StringComparison.Ordinal))
        {
            Skip =
                "Set NETSPLIT_RUN_P0=1 and the P0 adapter/profile variables to run this external validation.";
        }
    }
}
