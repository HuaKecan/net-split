using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NetSplit.Core;

public static class ResidentialProxyValidator
{
    public static string NormalizeHost(string value)
    {
        var host = value.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("请填写住宅代理服务器地址。");
        }

        if (host.Contains("://", StringComparison.Ordinal)
            || host.Contains('/')
            || host.Contains('\\')
            || host.Contains(',')
            || host.Contains('\r')
            || host.Contains('\n')
            || host.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("住宅代理服务器只能填写域名或 IPv4 地址。");
        }

        if (IPAddress.TryParse(host, out var address))
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException("首版住宅代理仅支持 IPv4 地址或域名。");
            }

            return address.ToString();
        }

        string asciiHost;
        try
        {
            asciiHost = new IdnMapping().GetAscii(host);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("住宅代理服务器域名无效。", exception);
        }

        if (asciiHost.Length > 253 || Uri.CheckHostName(asciiHost) != UriHostNameType.Dns)
        {
            throw new InvalidOperationException("住宅代理服务器域名无效。");
        }

        return asciiHost.ToLowerInvariant();
    }

    public static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("住宅代理端口必须位于 1 到 65535。");
        }
    }
}
