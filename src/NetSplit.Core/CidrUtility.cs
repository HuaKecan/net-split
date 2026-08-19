using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace NetSplit.Core;

public static class CidrUtility
{
    public static string ToNetworkPrefix(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 addresses are supported.", nameof(address));
        }

        if (prefixLength is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }

        var bytes = address.GetAddressBytes();
        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var network = value & mask;
        BinaryPrimitives.WriteUInt32BigEndian(bytes, network);
        return $"{new IPAddress(bytes)}/{prefixLength}";
    }

    public static bool IsValidIpv4Cidr(string value)
    {
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && IPAddress.TryParse(parts[0], out var address)
            && address.AddressFamily == AddressFamily.InterNetwork
            && int.TryParse(parts[1], out var prefix)
            && prefix is >= 0 and <= 32;
    }
}
