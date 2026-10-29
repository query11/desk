using System.Text.Json;
using System.Text.Json.Serialization;

namespace Desk;

internal enum MaximizeMode
{
    /// <summary>Back to a full-width taskbar while a window is maximized.</summary>
    Merge,

    /// <summary>Auto-hide the taskbar while a window is maximized.</summary>
    AutoHide,

    /// <summary>Keep the floating islands.</summary>
    Keep,
}

/// <summary>Which colors the Start menu uses.</summary>
internal enum StartMenuScheme
{
    /// <summary>Follow the Windows mode (light or dark).</summary>
    System,

    /// <summary>Dark glass with white text, whatever the Windows mode.</summary>
    Dark,

    /// <summary>Light glass with black text, whatever the Windows mode.</summary>
    Light,
}

internal sealed class Settings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Theme file name without extension.</summary>
    public string Theme { get; set; } = "desk-islands";

    public MaximizeMode MaximizeMode { get; set; } = MaximizeMode.Merge;

    public bool StyleStartMenu { get; set; } = true;

    /// <summary>Dark by default: translucent dark glass with white text, whichever Windows mode is on.</summary>
    public StartMenuScheme StartMenuScheme { get; set; } = StartMenuScheme.Dark;

    public bool ClickThrough { get; set; }

    /// <summary>
    /// Theme constants changed in the settings window, by name. A name ending
    /// in @light or @dark only applies in that Windows mode.
    /// </summary>
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Whether autostart was set up once; after that it's the user's choice.</summary>
    public bool AutostartConfigured { get; set; }

    /// <summary>The Desk.exe autostart was set up for, to notice when it moved.</summary>
    public string? AutostartPath { get; set; }

    public bool WelcomeShown { get; set; }

    /// <summary>Whether the tip about closing the window was shown.</summary>
    public bool CloseTipShown { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), Options)
                               ?? new Settings();
                settings.Overrides = new Dictionary<string, string>(settings.Overrides ?? [], StringComparer.Ordinal);
                return settings;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Settings could not be read, using defaults: {e.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.SettingsDir);
            string temp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, Paths.SettingsFile, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Warn($"Settings could not be saved: {e.Message}");
        }
    }
}
