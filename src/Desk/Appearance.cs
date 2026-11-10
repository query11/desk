using System.Globalization;

namespace Desk;

internal enum ParameterScope { Taskbar, StartMenu }

internal interface IAppearanceEditor
{
    string ThemeId { get; }
    bool SystemLightMode { get; }
    bool StartMenuLightMode { get; }
    bool HasParameter(string name);
    double? GetParameter(ParameterScope scope, string name);
    void SetParameter(ParameterScope scope, string name, double value, bool perMode);
    bool HasChangedParameters(IEnumerable<string> names);
    void ResetParameters(ParameterScope scope, IEnumerable<string> names);
    bool CanUndoAppearance(ParameterScope scope);
    void UndoAppearance(ParameterScope scope);
    void ApplyMaterialPreset(ParameterScope scope, MaterialPreset preset);
    string SaveAppearance(string name);
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

/// <summary>Undo only the keys an edit changed. Other pages and themes survive.</summary>
internal sealed class AppearanceHistory
{
    private sealed record Change(Dictionary<string, string?> Before,
        Dictionary<string, string?> After, string? MergeKey, DateTime At);

    private readonly Dictionary<string, List<Change>> _history = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string theme, ParameterScope scope) => theme + "|" + scope;

    public bool CanUndo(string theme, ParameterScope scope) =>
        _history.TryGetValue(Key(theme, scope), out var entries) && entries.Count > 0;

    public void Record(string theme, ParameterScope scope, IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after, string? mergeKey, DateTime now)
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
        string key = Key(theme, scope);
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

    public bool Undo(Settings settings, string theme, ParameterScope scope, Func<bool>? save = null)
    {
        if (!_history.TryGetValue(Key(theme, scope), out var entries) || entries.Count == 0) return false;
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
