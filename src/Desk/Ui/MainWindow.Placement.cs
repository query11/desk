using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Desk.Ui.Pages;

namespace Desk.Ui;

/// <summary>
/// Opening the window where it was closed: place, size, maximized, page.
/// The place is kept in screen pixels, the size in device-independent
/// units; UiHost saves and restores them, so a window made by the UI tests
/// always starts the default way.
/// </summary>
public partial class MainWindow
{
    private static readonly Type[] PageTypes =
        [typeof(HomePage), typeof(AppearancePage), typeof(TaskbarPage), typeof(StartMenuPage), typeof(DisplaysPage), typeof(SettingsPage)];

    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    /// <summary>The page the window opens on; the overview when not set.</summary>
    internal Type? StartPage { get; set; }

    /// <summary>The page shown now.</summary>
    internal Type? CurrentPage { get; private set; }

    // The last place and size while not maximized: what a maximized window
    // goes back to, and what opens next time.
    private (int X, int Y)? _normalPosition;
    private Size _normalSize;
    private (int X, int Y)? _exactPosition;

    private void TrackPlacement()
    {
        LocationChanged += (_, _) => RememberNormal();
        SizeChanged += (_, _) => RememberNormal();
        StateChanged += (_, _) => RememberNormal();
        SourceInitialized += (_, _) =>
        {
            // WPF finds the screen for a requested left/top by its own rules,
            // which can pick the wrong one when screens are scaled
            // differently; the exact pixels are set here, before it shows.
            if (_exactPosition is { } at)
                SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, at.X, at.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        };
    }

    private void RememberNormal()
    {
        if (WindowState != WindowState.Normal || !IsVisible) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
        _normalPosition = (rect.Left, rect.Top);
        _normalSize = new Size(ActualWidth, ActualHeight);
    }

    /// <summary>Where the window is now and which page it shows, to open it there next time.</summary>
    internal WindowPlacement Placement()
    {
        RememberNormal();
        return new WindowPlacement
        {
            X = _normalPosition?.X,
            Y = _normalPosition?.Y,
            Width = _normalSize.Width > 0 ? _normalSize.Width : Width,
            Height = _normalSize.Height > 0 ? _normalSize.Height : Height,
            Maximized = WindowState == WindowState.Maximized,
            Page = CurrentPage?.Name,
        };
    }

    /// <summary>
    /// Opens on the page and at the place it was closed; call before Show.
    /// When the screen it was on is gone, the place is the default one.
    /// </summary>
    internal void Restore(WindowPlacement saved)
    {
        StartPage = PageTypes.FirstOrDefault(type => type.Name == saved.Page);
        if (WindowPlacementRules.Fit(saved, WorkAreas()) is not { } fit) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = fit.Width;
        Height = fit.Height;
        Left = fit.X / fit.Scale;
        Top = fit.Y / fit.Scale;
        _exactPosition = (fit.X, fit.Y);
        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>The work areas of the screens there are now, in pixels, with their scaling.</summary>
    private static List<WorkArea> WorkAreas() => System.Windows.Forms.Screen.AllScreens.Select(screen =>
    {
        var area = screen.WorkingArea;
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = area.Left + area.Width / 2, Y = area.Top + area.Height / 2 },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        double scale = NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        return new WorkArea(area.Left, area.Top, area.Width, area.Height, scale);
    }).ToList();
}
