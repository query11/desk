namespace Desk;

/// <summary>The global settings a display follows where it has no choice of its own.</summary>
internal sealed record DisplayDefaults(bool ShowMonitor, bool MonitorNetwork, bool MonitorCpu, bool MonitorMemory,
    MaximizeMode MaximizeMode);

internal sealed class DisplayPreferences
{
    public bool Enabled { get; set; }
    // Overrides belong to a theme; switching themes never carries unrelated tweaks across.
    public Dictionary<string, Dictionary<string, string>> ThemeOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool? ShowMonitor { get; set; }
    public bool? MonitorNetwork { get; set; }
    public bool? MonitorCpu { get; set; }
    public bool? MonitorMemory { get; set; }
    public MaximizeMode? MaximizeMode { get; set; }

    /// <summary>What the info island shows on this display, in its order; empty when it is off.</summary>
    public IReadOnlyList<string> MonitorItems(DisplayDefaults global)
    {
        // Without its own settings a display follows the global ones throughout.
        bool own = Enabled;
        if (!((own ? ShowMonitor : null) ?? global.ShowMonitor)) return [];
        var items = new List<string>(3);
        if ((own ? MonitorNetwork : null) ?? global.MonitorNetwork) items.Add("network");
        if ((own ? MonitorCpu : null) ?? global.MonitorCpu) items.Add("cpu");
        if ((own ? MonitorMemory : null) ?? global.MonitorMemory) items.Add("memory");
        return items;
    }

    public IReadOnlyDictionary<string, string> Resolve(string theme, IReadOnlyDictionary<string, string> global)
    {
        var values = new Dictionary<string, string>(global);
        if (!Enabled || !ThemeOverrides.TryGetValue(theme, out var local)) return values;
        foreach (var (key, value) in local)
        {
            if (!key.Contains('@')) { values.Remove(key + "@light"); values.Remove(key + "@dark"); }
            values[key] = value;
        }
        return values;
    }
}
