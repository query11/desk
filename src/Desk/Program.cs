using System.Windows.Forms;

namespace Desk;

internal static class Program
{
    /// <summary>Set by a second Desk.exe to ask the running one to show its window.</summary>
    public const string ShowWindowEventName = @"Local\Desk.Host.ShowWindow";

    [STAThread]
    private static int Main(string[] args)
    {
        bool autostart = args.Any(a => string.Equals(a, Autostart.Argument, StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, @"Local\Desk.Host", out bool created);
        if (!created)
        {
            // Already running: bring its window up, unless this is the
            // sign-in autostart racing a copy the user started.
            if (!autostart && EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var show))
            {
                using (show)
                {
                    // This process was started by the user, so it may hand
                    // the right to come to the front to the running Desk.
                    NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                    show.Set();
                }
            }
            return 0;
        }

        Log.Init();
        Log.Info($"Desk {Application.ProductVersion} starting{(autostart ? " (autostart)" : "")}");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        using var app = new DeskApp(showWindow: !autostart);
        Application.Run(app);
        return 0;
    }
}
