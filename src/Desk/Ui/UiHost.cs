using System.Windows;
using System.Windows.Forms.Integration;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
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

    /// <summary>Opens a page of the settings window.</summary>
    public static void Navigate(Type page) => _window?.Navigation.Navigate(page);

    /// <summary>A short confirmation at the bottom of the window that goes away by itself.</summary>
    public static void Toast(string title, string message, SymbolRegular icon = SymbolRegular.CheckmarkCircle24,
        ControlAppearance appearance = ControlAppearance.Secondary) =>
        _window?.Toast(title, message, icon, appearance);

    /// <summary>
    /// Asks before something that can't be taken back. True when the user
    /// chose <paramref name="confirm"/>.
    /// </summary>
    public static Task<bool> ConfirmAsync(string title, string message, string confirm, bool dangerous = false) =>
        _window?.ConfirmAsync(title, message, confirm, dangerous) ?? Task.FromResult(false);

    public static Task<string?> PromptNameAsync(string title, string message, string initial) =>
        _window?.PromptNameAsync(title, message, initial) ?? Task.FromResult<string?>(null);

    public static Task ShowContentAsync(string title, object content) => _window?.ShowContentAsync(title, content) ?? Task.CompletedTask;

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
        // Desk's own tokens and styles; after WPF UI's, which they build on.
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Desk;component/Ui/Styles.xaml", UriKind.Absolute),
        });
        ApplicationThemeManager.ApplySystemTheme(true);
    }
}
