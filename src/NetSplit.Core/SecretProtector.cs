using System.Security.Cryptography;
using System.Text;

namespace NetSplit.Core;

public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string protectedValue);
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("net-split:v1");

    public string Protect(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        var bytes = Convert.FromBase64String(protectedValue);
        var unprotectedBytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(unprotectedBytes);
    }
}
