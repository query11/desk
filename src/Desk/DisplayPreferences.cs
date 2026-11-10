namespace Desk;

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
