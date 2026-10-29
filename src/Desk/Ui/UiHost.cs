using System.Windows;
using System.Windows.Forms.Integration;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace Desk.Ui;

/// <summary>
/// Opens the settings window. WPF is only loaded the first time, so starting
/// Desk at sign-in stays fast; the Windows Forms message loop of the tray app
/// also runs the WPF window.
/// </summary>
internal static class UiHost
{
    private static MainWindow? _window;

    public static DeskApp App { get; private set; } = null!;

    public static void ShowWindow(DeskApp app)
    {
        App = app;
        EnsureApplication();

        if (_window == null)
        {
            _window = new MainWindow();
            // Keyboard input (Tab, arrows, access keys) for a WPF window in
            // a Windows Forms message loop.
            ElementHost.EnableModelessKeyboardInterop(_window);
            _window.Closed += (_, _) =>
            {
                _window = null;
                app.OnWindowClosed();
            };
            System.Windows.Application.Current.MainWindow = _window;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
        // Activate alone doesn't always come to the front when started from
        // the tray or a second Desk.exe.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public static void CloseWindow() => _window?.Close();

    private static void EnsureApplication()
    {
        if (System.Windows.Application.Current != null)
        {
            return;
        }

        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // A bug in the window must not take the tray app (and the styling)
        // down with it.
        application.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception in the settings window", e.Exception);
            e.Handled = true;
        };
        application.Resources.MergedDictionaries.Add(new ThemesDictionary());
        application.Resources.MergedDictionaries.Add(new ControlsDictionary());
        ApplicationThemeManager.ApplySystemTheme(true);
    }
}
