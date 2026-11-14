using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Desk.Ui;

/// <summary>
/// Puts the settings window in the Windows app mode and keeps it there while
/// it is open. Not WPF UI's ApplySystemTheme or SystemThemeWatcher: they look
/// at the name of the Windows theme file first ("aero.theme" counts as light)
/// and at the app mode only when they don't recognize it, so with the default
/// Windows theme the window stayed light in dark app mode.
/// </summary>
internal static class WindowTheme
{
    private const int WM_SYSCOLORCHANGE = 0x0015;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;
    private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    public static ApplicationTheme Current => AppMode.Read(SystemParameters.HighContrast) switch
    {
        WindowMode.Dark => ApplicationTheme.Dark,
        WindowMode.HighContrast => ApplicationTheme.HighContrast,
        _ => ApplicationTheme.Light,
    };

    /// <summary>Loads the colors of the current app mode and the accent color, for every window.</summary>
    public static void Apply() => ApplicationThemeManager.Apply(Current, WindowBackdropType.Mica, updateAccent: true);

    /// <summary>Follows the app mode, the accent color and high contrast while the window is open.</summary>
    public static void Watch(Window window)
    {
        if (window.IsLoaded)
        {
            Hook(window);
        }
        else
        {
            window.Loaded += OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = (Window)sender;
        window.Loaded -= OnLoaded;
        Hook(window);
    }

    private static void Hook(Window window)
    {
        if (HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) is not { } source)
        {
            return;
        }
        HwndSourceHook hook = (IntPtr _, int msg, IntPtr _, IntPtr lParam, ref bool _) =>
        {
            if (msg is WM_SYSCOLORCHANGE or WM_THEMECHANGED or WM_DWMCOLORIZATIONCOLORCHANGED ||
                (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
                 Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet"))
            {
                var theme = Current;
                var backdrop = window is FluentWindow fluent ? fluent.WindowBackdropType : WindowBackdropType.Mica;
                ApplicationThemeManager.Apply(theme, backdrop, updateAccent: true);
                WindowBackgroundManager.UpdateBackground(window, theme, backdrop);
            }
            return IntPtr.Zero;
        };
        source.AddHook(hook);
        window.Closed += (_, _) => source.RemoveHook(hook);
    }
}
