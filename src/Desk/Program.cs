using System.Windows.Forms;

namespace Desk;

internal static class Program
{
    /// <summary>Set by a second Desk.exe to ask the running one to show its window.</summary>
    public const string ShowWindowEventName = @"Local\Desk.Host.ShowWindow";

    /// <summary>Held by the running tray app.</summary>
    public const string HostMutexName = @"Local\Desk.Host";

    [STAThread]
    private static int Main(string[] args)
    {
        bool Has(string argument) => args.Any(a => string.Equals(a, argument, StringComparison.OrdinalIgnoreCase));
        bool autostart = Has(Autostart.Argument);

        // Emergency restore: works without the tray app, the settings window
        // or a responsive Desk.exe, so it comes before the single-instance check.
        if (Has(EmergencyRestore.Argument))
        {
            Log.Init("restore");
            var report = EmergencyRestore.Run(new WindowsRestoreEnvironment());
            Log.Flush();
            if (!Has("--quiet"))
            {
                MessageBox.Show(report.Describe(), report.Succeeded ? "Desk 已还原" : "Desk 还原未完成",
                    MessageBoxButtons.OK, report.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            return report.Succeeded ? 0 : 1;
        }

        using var mutex = new Mutex(true, HostMutexName, out bool created);
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
        {
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);
            Log.Flush();
        };

        ApplicationConfiguration.Initialize();
        using var app = new DeskApp(showWindow: !autostart);
        Application.Run(app);
        return 0;
    }
}
