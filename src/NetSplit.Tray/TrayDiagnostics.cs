using System.Globalization;
using System.Text;

namespace NetSplit.Tray;

internal static class TrayDiagnostics
{
    private const long MaximumLogLength = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "net-split",
        "logs");
    private static readonly string LogPath = Path.Combine(
        LogDirectory,
        "tray.log");
    private static readonly string UserExitMarker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "net-split",
        "runtime",
        "tray.exit-requested");

    public static void WriteLifecycle(string eventName, string detail = "")
    {
        var line = $"event={NormalizeToken(eventName)}";
        if (!string.IsNullOrWhiteSpace(detail))
        {
            line += $" detail={NormalizeToken(detail)}";
        }

        WriteLine(line);
    }

    public static void WriteException(string source, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        WriteLine(
            $"event=exception source={NormalizeToken(source)} "
            + DescribeException(exception));
    }

    public static void MarkUserExit()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserExitMarker)!);
            File.WriteAllText(
                UserExitMarker,
                DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                new UTF8Encoding(false));
            WriteLifecycle("user-exit-requested");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            // The marker only prevents a short startup retry loop.
        }
    }

    internal static string DescribeException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var builder = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (builder.Length > 0)
            {
                builder.Append(" inner=");
            }

            builder.Append(current.GetType().FullName);
            builder.Append(" hresult=0x");
            builder.Append(
                unchecked((uint)current.HResult).ToString(
                    "X8",
                    CultureInfo.InvariantCulture));
            if (current.TargetSite is not null)
            {
                builder.Append(" target=");
                builder.Append(NormalizeToken(
                    $"{current.TargetSite.DeclaringType?.FullName}.{current.TargetSite.Name}"));
            }
        }

        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            builder.AppendLine();
            builder.Append(exception.StackTrace);
        }

        return builder.ToString();
    }

    private static void WriteLine(string value)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                File.AppendAllText(
                    LogPath,
                    $"{DateTimeOffset.Now:o} {value}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            // Diagnostics must never interfere with the tray lifecycle.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath)
            || new FileInfo(LogPath).Length < MaximumLogLength)
        {
            return;
        }

        var previousPath = $"{LogPath}.1";
        File.Delete(previousPath);
        File.Move(LogPath, previousPath);
    }

    private static string NormalizeToken(string value)
    {
        return string.Join(
            "_",
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
