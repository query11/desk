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

internal sealed class Settings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Theme file name without extension.</summary>
    public string Theme { get; set; } = "desk-islands";

    public MaximizeMode MaximizeMode { get; set; } = MaximizeMode.Merge;

    public bool StyleStartMenu { get; set; } = true;

    public bool ClickThrough { get; set; }

    public bool WelcomeShown { get; set; }

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
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), Options)
                       ?? new Settings();
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
            File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception e)
        {
            Log.Warn($"Settings could not be saved: {e.Message}");
        }
    }
}
