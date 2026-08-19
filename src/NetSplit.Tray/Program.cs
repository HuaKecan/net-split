namespace NetSplit.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "Local\\NetSplit.Tray", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        ThemeManager.Initialize();
        Application.Run(new TrayApplicationContext(
            startMinimized: args.Contains("--background", StringComparer.OrdinalIgnoreCase)));
    }
}
