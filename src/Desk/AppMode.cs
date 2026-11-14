using Microsoft.Win32;

namespace Desk;

/// <summary>The colors of the settings window.</summary>
internal enum WindowMode
{
    Light,
    Dark,
    HighContrast,
}

/// <summary>
/// The settings window follows the Windows app mode ("Choose your default app
/// mode", AppsUseLightTheme), like other apps. The taskbar and the Start menu
/// follow the Windows mode instead (SystemUsesLightTheme, see DeskApp); the
/// two can differ.
/// </summary>
internal static class AppMode
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <param name="appsUseLightTheme">The registry value; null when it isn't there.</param>
    /// <param name="highContrast">Whether a high contrast theme is on; it wins.</param>
    public static WindowMode Decide(object? appsUseLightTheme, bool highContrast) =>
        highContrast ? WindowMode.HighContrast
        : appsUseLightTheme is int value && value == 0 ? WindowMode.Dark
        : WindowMode.Light;

    public static WindowMode Read(bool highContrast)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return Decide(key?.GetValue("AppsUseLightTheme"), highContrast);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Decide(null, highContrast);
        }
    }
}
