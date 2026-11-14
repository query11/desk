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

    public StartMenuScheme StartMenuScheme { get; set; } = StartMenuScheme.System;

    /// <summary>The island with the network speed, CPU and memory usage on the left of the taskbar.</summary>
    public bool ShowMonitor { get; set; } = true;

    public bool MonitorNetwork { get; set; } = true;

    public bool MonitorCpu { get; set; } = true;

    public bool MonitorMemory { get; set; } = true;

    public bool ClickThrough { get; set; }

    public HashSet<string> FavoriteThemes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ThemeTrial? PendingThemeTrial { get; set; }
    public Dictionary<string, DisplayPreferences> Displays { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Theme constants changed in the settings window, per theme id (the
    /// file name without extension; a user theme with the name of a built-in
    /// one shares its id), then by constant name. A name ending in @light or
    /// @dark only applies in that Windows mode.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ThemeOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Before v0.4 the overrides applied to every theme. Read from old
    /// settings and moved to the theme they were made for; see
    /// <see cref="MigrateLegacyOverrides"/>.
    /// </summary>
    [JsonPropertyName("Overrides")]
    public Dictionary<string, string>? LegacyOverrides { get; set; }

    /// <summary>A copy of what the migration moved, and where to, so it can be recovered.</summary>
    public MigratedOverrides? MigratedOverrides { get; set; }

    /// <summary>Whether autostart was set up once; after that it's the user's choice.</summary>
    public bool AutostartConfigured { get; set; }

    /// <summary>The Desk.exe autostart was set up for, to notice when it moved.</summary>
    public string? AutostartPath { get; set; }

    public bool WelcomeShown { get; set; }

    /// <summary>Whether the tip about closing the window was shown.</summary>
    public bool CloseTipShown { get; set; }

    /// <summary>Where the settings window was closed, and on which page; null opens it in the middle, on the overview.</summary>
    public WindowPlacement? Window { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly IReadOnlyDictionary<string, string> NoOverrides = new Dictionary<string, string>();

    /// <summary>The user's changes for a theme; empty when there are none.</summary>
    public IReadOnlyDictionary<string, string> OverridesFor(string themeId) =>
        ThemeOverrides.TryGetValue(themeId, out var overrides) ? overrides : NoOverrides;

    /// <param name="lightMode">The Windows mode the value is for; null for both.</param>
    public void SetOverride(string themeId, string name, string value, bool? lightMode)
    {
        if (!ThemeOverrides.TryGetValue(themeId, out var overrides))
        {
            ThemeOverrides[themeId] = overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        if (lightMode is { } light)
        {
            overrides[$"{name}@{(light ? "light" : "dark")}"] = value;
        }
        else
        {
            overrides.Remove($"{name}@light");
            overrides.Remove($"{name}@dark");
            overrides[name] = value;
        }
    }

    /// <param name="lightMode">Only count values that apply in this mode; null: any.</param>
    public bool HasOverrides(string themeId, IEnumerable<string> names, bool? lightMode = null)
    {
        var overrides = OverridesFor(themeId);
        return names.Any(n => overrides.ContainsKey(n) ||
                              (lightMode != false && overrides.ContainsKey($"{n}@light")) ||
                              (lightMode != true && overrides.ContainsKey($"{n}@dark")));
    }

    /// <summary>Back to the theme's own values for these constants, for this theme only.</summary>
    /// <param name="lightMode">
    /// Only the value for this mode; a value set for both modes stays for the
    /// other one. Null: both modes.
    /// </param>
    public void ResetOverrides(string themeId, IEnumerable<string> names, bool? lightMode = null)
    {
        if (!ThemeOverrides.TryGetValue(themeId, out var overrides))
        {
            return;
        }
        foreach (var name in names)
        {
            if (lightMode is { } light)
            {
                if (overrides.Remove(name, out var both))
                {
                    overrides.TryAdd($"{name}@{(light ? "dark" : "light")}", both);
                }
                overrides.Remove($"{name}@{(light ? "light" : "dark")}");
                continue;
            }
            overrides.Remove(name);
            overrides.Remove($"{name}@light");
            overrides.Remove($"{name}@dark");
        }
        if (overrides.Count == 0)
        {
            ThemeOverrides.Remove(themeId);
        }
    }

    /// <summary>
    /// Moves the overrides of older versions, which applied to every theme,
    /// to the theme the settings had selected: they were made with it. Doesn't
    /// look at theme files, so a user theme that is missing right now still
    /// gets them (not the theme Desk falls back to). Values already stored
    /// for that theme win, so running it again (or after an interrupted
    /// save) changes nothing. Without a selected theme the old values stay
    /// where they are. Returns true when something was moved.
    /// </summary>
    public bool MigrateLegacyOverrides()
    {
        if (LegacyOverrides is not { Count: > 0 } legacy)
        {
            LegacyOverrides = null;
            return false;
        }
        if (string.IsNullOrWhiteSpace(Theme))
        {
            Log.Warn($"Keeping {legacy.Count} old theme adjustments: no theme was selected");
            return false;
        }

        foreach (var (name, value) in legacy)
        {
            if (!ThemeOverrides.TryGetValue(Theme, out var overrides))
            {
                ThemeOverrides[Theme] = overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            overrides.TryAdd(name, value);
        }
        MigratedOverrides = new MigratedOverrides(Theme, new Dictionary<string, string>(legacy, StringComparer.Ordinal));
        LegacyOverrides = null;
        Log.Info($"Moved {legacy.Count} theme adjustments to theme {Theme}");
        return true;
    }

    public static Settings Load() => Load(Paths.SettingsFile);

    public static Settings Load(string path)
    {
        Settings settings = new();
        try
        {
            if (File.Exists(path))
            {
                settings = Parse(File.ReadAllText(path));
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Settings could not be read, using defaults: {e.Message}");
            settings = new Settings();
            settings.Normalize();
        }
        if (settings.MigrateLegacyOverrides())
        {
            settings.Save(path);
        }
        return settings;
    }

    /// <summary>Settings from their JSON, as Load reads them (without moving old adjustments).</summary>
    /// <exception cref="JsonException">The text isn't settings JSON.</exception>
    public static Settings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();
        settings.Normalize();
        return settings;
    }

    /// <summary>The settings as Save writes them.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Comparers aren't stored: theme ids are file names (any case), constant names are exact.</summary>
    private void Normalize()
    {
        var themes = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (theme, overrides) in ThemeOverrides ?? [])
        {
            if (overrides is { Count: > 0 })
            {
                themes[theme] = new Dictionary<string, string>(overrides, StringComparer.Ordinal);
            }
        }
        ThemeOverrides = themes;
        FavoriteThemes = new HashSet<string>(FavoriteThemes ?? [], StringComparer.OrdinalIgnoreCase);
        Displays = new Dictionary<string, DisplayPreferences>(Displays ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Displays.Values)
            profile.ThemeOverrides = new Dictionary<string, Dictionary<string, string>>(profile.ThemeOverrides ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Takes the settings of a backup, except what belongs to this computer
    /// or this moment: the styling switch (Enabled), autostart
    /// (AutostartConfigured, AutostartPath), the window's place (Window), the
    /// tips already shown, and a trial that was running when the backup was
    /// made (its theme from before the trial is taken instead).
    /// </summary>
    public void RestoreFrom(Settings backup)
    {
        Theme = backup.PendingThemeTrial?.OriginalTheme ?? backup.Theme;
        PendingThemeTrial = null;
        MaximizeMode = backup.MaximizeMode;
        StyleStartMenu = backup.StyleStartMenu;
        StartMenuScheme = backup.StartMenuScheme;
        ShowMonitor = backup.ShowMonitor;
        MonitorNetwork = backup.MonitorNetwork;
        MonitorCpu = backup.MonitorCpu;
        MonitorMemory = backup.MonitorMemory;
        ClickThrough = backup.ClickThrough;
        FavoriteThemes = new HashSet<string>(backup.FavoriteThemes, StringComparer.OrdinalIgnoreCase);
        Displays = new Dictionary<string, DisplayPreferences>(backup.Displays, StringComparer.OrdinalIgnoreCase);
        ThemeOverrides = new Dictionary<string, Dictionary<string, string>>(backup.ThemeOverrides, StringComparer.OrdinalIgnoreCase);
        LegacyOverrides = backup.LegacyOverrides;
        MigratedOverrides = backup.MigratedOverrides;
        MigrateLegacyOverrides();
    }

    public bool Save() => Save(Paths.SettingsFile);

    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, ToJson());
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Settings could not be saved: {e.Message}");
            return false;
        }
    }
}

/// <summary>Adjustments an older version applied to every theme, and the theme they were moved to.</summary>
internal sealed record MigratedOverrides(string Theme, Dictionary<string, string> Values);
