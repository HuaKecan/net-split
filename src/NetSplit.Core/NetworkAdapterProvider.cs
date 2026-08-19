using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Globalization;

namespace NetSplit.Core;

public interface INetworkAdapterProvider
{
    IReadOnlyList<NetworkAdapterSnapshot> GetAdapters();
    NetworkAdapterSnapshot? Resolve(AdapterBinding? binding);
}

public sealed class WindowsNetworkAdapterProvider : INetworkAdapterProvider
{
    private static readonly StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    public IReadOnlyList<NetworkAdapterSnapshot> GetAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Select(CreateSnapshot)
            .OrderByDescending(adapter => adapter.IsSelectable)
            .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public NetworkAdapterSnapshot? Resolve(AdapterBinding? binding)
    {
        if (binding is null)
        {
            return null;
        }

        var adapters = GetAdapters();
        return adapters.FirstOrDefault(adapter => adapter.Id.Equals(binding.Id, Comparison))
            ?? adapters.FirstOrDefault(adapter =>
                !string.IsNullOrWhiteSpace(binding.MacAddress)
                && adapter.MacAddress.Equals(binding.MacAddress, Comparison));
    }

    private static NetworkAdapterSnapshot CreateSnapshot(NetworkInterface networkInterface)
    {
        IPInterfaceProperties properties;
        try
        {
            properties = networkInterface.GetIPProperties();
        }
        catch (NetworkInformationException)
        {
            return new NetworkAdapterSnapshot
            {
                Id = networkInterface.Id,
                Name = networkInterface.Name,
                Description = networkInterface.Description,
                MacAddress = FormatMac(networkInterface.GetPhysicalAddress()),
                IsUp = networkInterface.OperationalStatus == OperationalStatus.Up,
                IsTunnelOrLoopback = networkInterface.NetworkInterfaceType
                    is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback
            };
        }

        var ipv4Unicast = properties.UnicastAddresses
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();
        var gateways = properties.GatewayAddresses
            .Select(item => item.Address)
            .Where(address =>
                address.AddressFamily == AddressFamily.InterNetwork
                && !address.Equals(IPAddress.Any))
            .Select(address => address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var dnsServers = properties.DnsAddresses
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var isUp = networkInterface.OperationalStatus == OperationalStatus.Up;
        var isSelectable = isUp
            && ipv4Unicast.Length > 0
            && gateways.Length > 0
            && networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback
            && networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Tunnel;

        var (bytesReceived, bytesSent) = GetStatistics(networkInterface);
        var interfaceIndex = GetInterfaceIndex(properties);

        return new NetworkAdapterSnapshot
        {
            Id = networkInterface.Id,
            Name = networkInterface.Name,
            Description = networkInterface.Description,
            MacAddress = FormatMac(networkInterface.GetPhysicalAddress()),
            InterfaceIndex = interfaceIndex,
            IsUp = isUp,
            IsSelectable = isSelectable,
            IsF50Candidate = IsF50(networkInterface),
            IsTunnelOrLoopback = networkInterface.NetworkInterfaceType
                is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback,
            Ipv4Addresses = ipv4Unicast.Select(item => item.Address.ToString()).ToArray(),
            Gateways = gateways,
            DnsServers = dnsServers,
            ConnectedPrefixes = ipv4Unicast
                .Select(item => CidrUtility.ToNetworkPrefix(item.Address, item.PrefixLength))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            BytesReceived = bytesReceived,
            BytesSent = bytesSent
        };
    }

    private static int GetInterfaceIndex(IPInterfaceProperties properties)
    {
        try
        {
            return properties.GetIPv4Properties()?.Index ?? 0;
        }
        catch (NetworkInformationException)
        {
            return 0;
        }
    }

    private static (long BytesReceived, long BytesSent) GetStatistics(NetworkInterface networkInterface)
    {
        try
        {
            var statistics = networkInterface.GetIPv4Statistics();
            return (statistics.BytesReceived, statistics.BytesSent);
        }
        catch (NetworkInformationException)
        {
            return (0, 0);
        }
    }

    private static bool IsF50(NetworkInterface networkInterface)
    {
        var identity = $"{networkInterface.Name} {networkInterface.Description}";
        return identity.Contains("F50", Comparison)
            || identity.Contains("ZTE", Comparison)
            || identity.Contains("RNDIS", Comparison)
            || identity.Contains("Remote NDIS", Comparison);
    }

    private static string FormatMac(PhysicalAddress address)
    {
        return string.Join(
            "-",
            address.GetAddressBytes().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }
}
