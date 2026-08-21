namespace NetSplit.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var background = args.Contains(
            "--background",
            StringComparer.OrdinalIgnoreCase);
        TrayDiagnostics.WriteLifecycle(
            "process-start",
            background ? "background" : "interactive");

        using var mutex = new Mutex(true, "Local\\NetSplit.Tray", out var createdNew);
        if (!createdNew)
        {
            TrayDiagnostics.WriteLifecycle("existing-instance");
            return;
        }

        ConfigureExceptionHandling();
        try
        {
            ApplicationConfiguration.Initialize();
            ThemeManager.Initialize();
            TrayDiagnostics.WriteLifecycle("message-loop-start");
            Application.Run(new TrayApplicationContext(startMinimized: background));
            TrayDiagnostics.WriteLifecycle(
                "message-loop-exit",
                $"exit-code-{Environment.ExitCode}");
        }
        catch (Exception exception)
        {
            TrayDiagnostics.WriteException("main", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void ConfigureExceptionHandling()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
        {
            TrayDiagnostics.WriteException("ui-thread", eventArgs.Exception);
            Environment.ExitCode = 1;
            Application.Exit();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                TrayDiagnostics.WriteException("app-domain", exception);
            }
            else
            {
                TrayDiagnostics.WriteLifecycle("app-domain-non-exception");
            }

            Environment.ExitCode = 1;
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            TrayDiagnostics.WriteException("unobserved-task", eventArgs.Exception);
            eventArgs.SetObserved();
        };
    }
}
