using System.Security.AccessControl;
using System.Security.Principal;

namespace NetSplit.Core;

public sealed class AppPaths
{
    private readonly bool _enforceRestrictedAcl;
    private int _securityApplied;

    public AppPaths(string? root = null, bool enforceRestrictedAcl = true)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("NETSPLIT_DATA_DIR");
        Root = root
            ?? configuredRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "net-split");
        _enforceRestrictedAcl = enforceRestrictedAcl
            && OperatingSystem.IsWindows()
            && root is null
            && string.IsNullOrWhiteSpace(configuredRoot);
    }

    public string Root { get; }
    public string RuntimeDirectory => Path.Combine(Root, "runtime");
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string CacheGenerationsDirectory => Path.Combine(CacheDirectory, "generations");
    public string CacheManifestFile => Path.Combine(CacheDirectory, "current.json");
    public string LogDirectory => Path.Combine(Root, "logs");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string AuthorizedUserSidFile => Path.Combine(Root, "authorized-user.sid");
    public string RuntimeConfigFile => Path.Combine(RuntimeDirectory, "config.yaml");
    public string CandidateConfigFile => Path.Combine(RuntimeDirectory, "config.candidate.yaml");
    public string LastKnownGoodDirectory => Path.Combine(RuntimeDirectory, "lkg");
    public string LastKnownGoodManifestFile => Path.Combine(LastKnownGoodDirectory, "current.json");
    public string TransactionJournalFile => Path.Combine(RuntimeDirectory, "transaction.pending.json");
    public string TransactionRuntimeBackupFile => Path.Combine(RuntimeDirectory, "transaction.previous.yaml");
    public string StartupDisableMarkerFile => Path.Combine(RuntimeDirectory, "startup.force-disabled");
    public string MihomoPidFile => Path.Combine(RuntimeDirectory, "mihomo.pid");
    public string ServiceLogFile => Path.Combine(LogDirectory, "net-split.log");

    public void EnsureDirectories()
    {
        if (_enforceRestrictedAcl && Directory.Exists(Root))
        {
            ValidateExistingRoot();
        }

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(CacheGenerationsDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(LastKnownGoodDirectory);

        if (!_enforceRestrictedAcl
            || Interlocked.CompareExchange(ref _securityApplied, 1, 0) != 0)
        {
            return;
        }

        try
        {
            ApplyRestrictedAcl(Root);
            ApplyRestrictedAcl(RuntimeDirectory);
            ApplyRestrictedAcl(CacheDirectory);
            ApplyRestrictedAcl(CacheGenerationsDirectory);
            ApplyRestrictedAcl(LogDirectory);
            ApplyRestrictedAcl(LastKnownGoodDirectory);
            SecureChildrenRecursively(Root);
        }
        catch
        {
            Volatile.Write(ref _securityApplied, 0);
            throw;
        }
    }

    private static void ApplyRestrictedAcl(string path)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(systemSid);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            systemSid,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void ApplyRestrictedFileAcl(string path)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(systemSid);
        security.AddAccessRule(new FileSystemAccessRule(
            systemSid,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void SecureChildrenRecursively(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(entry);
                if (Directory.Exists(entry))
                {
                    ApplyRestrictedAcl(entry);
                    pending.Push(entry);
                }
                else
                {
                    ApplyRestrictedFileAcl(entry);
                }
            }
        }
    }

    private void ValidateExistingRoot()
    {
        RejectReparsePoint(Root);
        var owner = new DirectoryInfo(Root)
            .GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (owner is null || !owner.Equals(systemSid) && !owner.Equals(administratorsSid))
        {
            throw new UnauthorizedAccessException(
                "ProgramData\\net-split 必须由 SYSTEM 或 Administrators 所有。");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                $"受保护的数据路径不能是重解析点：{path}");
        }
    }
}
