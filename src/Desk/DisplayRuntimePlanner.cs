using System.Globalization;

namespace Desk;

internal static class DisplayRuntimePlanner
{
    public static DisplayRuntimeConfig Build(Theme theme, Settings settings, DisplayPreferences? preferences,
        string device, bool maximized, bool light, bool centered)
    {
        var custom = preferences?.Enabled == true ? preferences : null;
        var values = custom?.Resolve(theme.Id, settings.OverridesFor(theme.Id)) ?? settings.OverridesFor(theme.Id);
        bool merge = maximized && (custom?.MaximizeMode ?? settings.MaximizeMode) == MaximizeMode.Merge;
        double Number(string key, double fallback) => double.TryParse(theme.GetConstant(key, light, values), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : fallback;
        string Color(string key, string fallback) => theme.GetConstant(key, light, values) ?? fallback;
        bool network = custom?.MonitorNetwork ?? settings.MonitorNetwork;
        bool cpu = custom?.MonitorCpu ?? settings.MonitorCpu;
        bool memory = custom?.MonitorMemory ?? settings.MonitorMemory;
        bool show = settings.Enabled && (custom?.ShowMonitor ?? settings.ShowMonitor) && (network || cpu || memory) && !(merge && !centered);
        var monitor = new MonitorConfig(show, network, cpu, memory, "CPU", "内存", Color("monitorText", light ? "#E4000000" : "#FFFFFFFF"),
            Color("monitorSecondaryText", light ? "#8A000000" : "#B3FFFFFF"), Color("monitorWarning", light ? "#FFC42B1C" : "#FFFF99A4"));
        var rules = settings.Enabled ? theme.Compile(ThemeTarget.Taskbar, light, merge, values) : [];
        if (show) rules.Add(new RuleSpec("Taskbar.AugmentedEntryPointButton#AugmentedEntryPointButton", ["Visibility=Collapsed"]));
        return new DisplayRuntimeConfig(device, rules, monitor,
            new TaskbarLayoutConfig(settings.Enabled && !merge && Number("adaptiveLayout", 0) == 1, Number("buttonWidth", 48), Number("iconSize", 28)));
    }
}
