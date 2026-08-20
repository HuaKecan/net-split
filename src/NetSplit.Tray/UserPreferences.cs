using Microsoft.Win32;

namespace NetSplit.Tray;

public static class UserPreferences
{
    private const string RegistryPath = @"Software\net-split";
    private const string SilentNotificationsValueName = "SilentNotifications";

    private static bool _silentNotifications = LoadSilentNotifications();

    public static event EventHandler? Changed;

    public static bool SilentNotifications
    {
        get => _silentNotifications;
        set
        {
            if (_silentNotifications == value)
            {
                return;
            }

            _silentNotifications = value;
            SaveSilentNotifications(value);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    internal static bool ParseBoolean(object? value)
    {
        return value switch
        {
            int number => number != 0,
            long number => number != 0,
            string text when bool.TryParse(text, out var parsed) => parsed,
            string text when int.TryParse(text, out var number) => number != 0,
            _ => false
        };
    }

    private static bool LoadSilentNotifications()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return ParseBoolean(key?.GetValue(SilentNotificationsValueName));
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException
                or UnauthorizedAccessException
                or IOException)
        {
            return false;
        }
    }

    private static void SaveSilentNotifications(bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue(
                SilentNotificationsValueName,
                value ? 1 : 0,
                RegistryValueKind.DWord);
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException
                or UnauthorizedAccessException
                or IOException)
        {
            // User preferences are best effort and must never block the tray.
        }
    }
}
