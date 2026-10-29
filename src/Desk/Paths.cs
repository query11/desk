namespace Desk;

internal static class Paths
{
    /// <summary>Where Desk.exe, DeskTap.dll and the built-in themes are.</summary>
    public static string AppDir => AppContext.BaseDirectory;

    public static string BuiltInThemesDir => Path.Combine(AppDir, "themes");

    /// <summary>%LOCALAPPDATA%\Desk: the engine's files.</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Desk");

    /// <summary>DeskTap.dll copies. The engine finds runtime\ and logs\ next to this.</summary>
    public static string BinDir => Path.Combine(DataDir, "bin");

    /// <summary>Resolved rules per target process, read by the engine.</summary>
    public static string RuntimeDir => Path.Combine(DataDir, "runtime");

    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>%APPDATA%\Desk: user settings and user themes.</summary>
    public static string SettingsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Desk");

    public static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    public static string UserThemesDir => Path.Combine(SettingsDir, "themes");
}
