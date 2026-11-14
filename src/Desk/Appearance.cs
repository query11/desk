using System.Globalization;

namespace Desk;

internal enum ParameterScope { Taskbar, StartMenu }

/// <summary>
/// What the parameter pages edit. Material values that depend on the tint
/// are kept for the light and the dark mode separately; every read and write
/// says which mode it is for (<c>light</c>), so the mode Windows isn't in can
/// be edited too. Each mode has its own undo history.
/// </summary>
internal interface IAppearanceEditor
{
    string ThemeId { get; }
    bool SystemLightMode { get; }
    bool StartMenuLightMode { get; }
    bool HasParameter(string name);
    double? GetParameter(ParameterScope scope, string name, bool light);

    /// <summary>Any constant of the theme (colors too) as the desktop would use it in that mode, for previews.</summary>
    string? GetConstant(string name, bool light);

    /// <param name="perMode">The value only applies to the mode <paramref name="light"/>.</param>
    void SetParameter(ParameterScope scope, string name, double value, bool perMode, bool light);

    /// <param name="light">Only count values that apply in this mode; null: any.</param>
    bool HasChangedParameters(IEnumerable<string> names, bool? light = null);

    /// <param name="light">The mode being edited: the change goes into its undo history.</param>
    /// <param name="onlyThisMode">Reset only the value for that mode (constants kept per mode).</param>
    void ResetParameters(ParameterScope scope, IEnumerable<string> names, bool light, bool onlyThisMode = false);

    bool CanUndoAppearance(ParameterScope scope, bool light);
    void UndoAppearance(ParameterScope scope, bool light);
    void ApplyMaterialPreset(ParameterScope scope, MaterialPreset preset, bool light);
    string SaveAppearance(string name);
}

internal static class AppearanceEditorExtensions
{
    /// <summary>The mode a part shows on the desktop now: the Start menu can have its own colors.</summary>
    public static bool EffectiveLight(this IAppearanceEditor editor, ParameterScope scope) =>
        scope == ParameterScope.StartMenu ? editor.StartMenuLightMode : editor.SystemLightMode;
}

/// <summary>
/// The changes the parameter pages make to a set of overrides, recorded for
/// undo in the history of the mode being edited. Shared by the global
/// settings, each display's own and the UI tests. With <c>save</c>, a change
/// that can't be saved is taken back and reported (IOException).
/// </summary>
internal static class OverrideEdits
{
    public static void Set(Settings settings, AppearanceHistory history, string theme, ParameterScope scope,
        string name, double value, bool perMode, bool light, DateTime now, Func<bool>? save = null)
    {
        var before = new Dictionary<string, string>(settings.OverridesFor(theme));
        settings.SetOverride(theme, name, value.ToString("0.###", CultureInfo.InvariantCulture), perMode ? light : null);
        Commit(settings, theme, before, save);
        // A drag of the slider is one step of the history.
        history.Record(theme, scope, before, settings.OverridesFor(theme), name + "|" + light, now, light);
    }

    public static void Reset(Settings settings, AppearanceHistory history, string theme, ParameterScope scope,
        IEnumerable<string> names, bool light, bool onlyThisMode, DateTime now, Func<bool>? save = null)
    {
        var before = new Dictionary<string, string>(settings.OverridesFor(theme));
        settings.ResetOverrides(theme, names, onlyThisMode ? light : null);
        Commit(settings, theme, before, save);
        history.Record(theme, scope, before, settings.OverridesFor(theme), null, now, light);
    }

    /// <param name="has">Whether the theme has a constant; presets skip the ones it doesn't.</param>
    public static void Preset(Settings settings, AppearanceHistory history, string theme, ParameterScope scope,
        MaterialPreset preset, bool light, Func<string, bool> has, DateTime now, Func<bool>? save = null)
    {
        var before = new Dictionary<string, string>(settings.OverridesFor(theme));
        foreach (var (key, value) in preset.Values(scope, light))
        {
            var parts = key.Split('@');
            if (!has(parts[0])) continue;
            settings.SetOverride(theme, parts[0], value, parts.Length > 1 ? light : null);
        }
        Commit(settings, theme, before, save);
        history.Record(theme, scope, before, settings.OverridesFor(theme), null, now, light);
    }

    private static void Commit(Settings settings, string theme, Dictionary<string, string> before, Func<bool>? save)
    {
        if (save == null || save()) return;
        if (before.Count == 0) settings.ThemeOverrides.Remove(theme);
        else settings.ThemeOverrides[theme] = new Dictionary<string, string>(before, StringComparer.Ordinal);
        throw new IOException("调整没有保存成功，原来的数值已保留。");
    }
}

internal sealed record MaterialPreset(string Id, string Name, string Description,
    double Blur, double LightTint, double DarkTint, double Luminosity, double Saturation, double Noise)
{
    public IReadOnlyDictionary<string, string> Values(ParameterScope scope, bool light)
    {
        string prefix = scope == ParameterScope.Taskbar ? "island" : "menu";
        string mode = light ? "@light" : "@dark";
        static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        return new Dictionary<string, string>
        {
            [prefix + "Blur"] = Number(Blur),
            [prefix + "Opacity" + mode] = Number(light ? LightTint : DarkTint),
            [prefix + "Luminosity" + mode] = Number(Luminosity),
            [prefix + "Saturation"] = Number(Saturation),
            [prefix + "Noise"] = Number(Noise),
        };
    }
}

internal static class MaterialPresets
{
    public static readonly IReadOnlyList<MaterialPreset> All =
    [
        new("clear", "通透", "更多背景，轻盈玻璃", 18, .12, .16, .2, 1.15, .012),
        new("balanced", "均衡", "玻璃质感与可读性兼顾", 28, .3, .35, .45, 1.2, .02),
        new("readable", "清晰", "文字优先，背景更柔和", 30, .65, .65, .75, 1, .01),
    ];
}

/// <summary>
/// Undo only the keys an edit changed. Other pages and themes survive. With
/// a mode (<c>light</c>), edits made while editing that mode are undone
/// separately from the other's: undo works on what is shown.
/// </summary>
internal sealed class AppearanceHistory
{
    private sealed record Change(Dictionary<string, string?> Before,
        Dictionary<string, string?> After, string? MergeKey, DateTime At);

    private readonly Dictionary<string, List<Change>> _history = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string theme, ParameterScope scope, bool? light) =>
        theme + "|" + scope + light switch { true => "|light", false => "|dark", _ => "" };

    public bool CanUndo(string theme, ParameterScope scope, bool? light = null) =>
        _history.TryGetValue(Key(theme, scope, light), out var entries) && entries.Count > 0;

    public void Record(string theme, ParameterScope scope, IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after, string? mergeKey, DateTime now, bool? light = null)
    {
        var previous = new Dictionary<string, string?>();
        var next = new Dictionary<string, string?>();
        foreach (string name in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(name, out string? oldValue);
            after.TryGetValue(name, out string? newValue);
            if (oldValue == newValue) continue;
            previous[name] = oldValue;
            next[name] = newValue;
        }
        if (previous.Count == 0) return;
        string key = Key(theme, scope, light);
        if (!_history.TryGetValue(key, out var entries)) _history[key] = entries = [];
        // Continuous slider input is one edit, including its paired button size.
        if (mergeKey != null && entries.LastOrDefault() is { } last && last.MergeKey == mergeKey &&
            now >= last.At && now - last.At < TimeSpan.FromMilliseconds(800))
        {
            foreach (var pair in previous) last.Before.TryAdd(pair.Key, pair.Value);
            foreach (var pair in next) last.After[pair.Key] = pair.Value;
            entries[^1] = last with { At = now };
        }
        else
        {
            entries.Add(new Change(previous, next, mergeKey, now));
            if (entries.Count > 30) entries.RemoveAt(0);
        }
    }

    public bool Undo(Settings settings, string theme, ParameterScope scope, Func<bool>? save = null, bool? light = null)
    {
        if (!_history.TryGetValue(Key(theme, scope, light), out var entries) || entries.Count == 0) return false;
        var change = entries[^1];
        bool existed = settings.ThemeOverrides.TryGetValue(theme, out var original);
        var snapshot = original == null ? null : new Dictionary<string, string>(original);
        if (!settings.ThemeOverrides.TryGetValue(theme, out var values)) settings.ThemeOverrides[theme] = values = [];
        foreach (var (name, oldValue) in change.Before)
        {
            // A later edit elsewhere may have changed a shared key. Don't undo it.
            values.TryGetValue(name, out string? current);
            if (current != change.After[name]) continue;
            if (oldValue == null) values.Remove(name);
            else values[name] = oldValue;
        }
        if (values.Count == 0) settings.ThemeOverrides.Remove(theme);
        try
        {
            if (save != null && !save()) throw new IOException("撤销没有保存成功，原来的调整已保留。");
        }
        catch
        {
            if (existed) settings.ThemeOverrides[theme] = snapshot!;
            else settings.ThemeOverrides.Remove(theme);
            throw;
        }
        entries.RemoveAt(entries.Count - 1);
        if (entries.Count > 0) entries[^1] = entries[^1] with { MergeKey = null };
        return true;
    }
}
