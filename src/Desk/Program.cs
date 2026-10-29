namespace Desk;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(true, @"Local\Desk.Host", out bool created);
        if (!created)
        {
            // Already running; the tray icon is there.
            return 0;
        }

        Log.Init();
        Log.Info($"Desk {Application.ProductVersion} starting");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        using var app = new DeskApp();
        Application.Run(app);
        return 0;
    }
}
