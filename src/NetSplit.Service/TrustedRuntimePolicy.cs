using System.Security.Cryptography;
using System.Security.Principal;

namespace NetSplit.Service;

internal static class TrustedRuntimePolicy
{
    private const string LockedMihomoSha256 =
        "82CD796A23492F43A71C1EC27E4E5E0B3D58932014DA5A36E79ED9B11FEE8162";

    public static void EnsureTrustedExecutable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new FileNotFoundException("找不到 Mihomo 可执行文件。", executablePath);
        }

        if (!OperatingSystem.IsWindows() || !WindowsIdentity.GetCurrent().IsSystem)
        {
            return;
        }

        var fullPath = Path.GetFullPath(executablePath);
        var allowedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        if (!allowedRoots.Any(root => IsWithin(fullPath, root)))
        {
            throw new UnauthorizedAccessException(
                "LocalSystem 仅允许从 Program Files 启动 Mihomo。");
        }

        var hashFile = fullPath + ".sha256";
        if (!File.Exists(hashFile))
        {
            throw new InvalidDataException("Mihomo 缺少安装时生成的 SHA-256 清单。");
        }

        var expected = File.ReadAllText(hashFile)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new InvalidDataException("Mihomo 哈希清单为空。");
        }

        if (!expected.Equals(LockedMihomoSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Mihomo 哈希清单与应用内置版本锁不一致。");
        }

        using var stream = File.OpenRead(fullPath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Mihomo 文件哈希与安装清单不一致。");
        }
    }

    private static bool IsWithin(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
