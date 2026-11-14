using System.Globalization;
using System.Text.Json;

namespace Desk;

internal sealed partial class DeskApp : IDisplaySettings
{
    private readonly Dictionary<string, AppearanceHistory> _displayHistories = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<DesktopDisplay> GetDisplays() => DisplayTopology.Read();
    public DisplayPreferences GetDisplayPreferences(string id)
    {
        if (!_settings.Displays.TryGetValue(id, out var preferences))
            _settings.Displays[id] = preferences = new DisplayPreferences();
        return preferences;
    }
    public void UpdateDisplay(string id, Action<DisplayPreferences> change)
    {
        var preferences = GetDisplayPreferences(id);
        string snapshot = JsonSerializer.Serialize(preferences);
        change(preferences);
        if (!_settings.Save())
        {
            _settings.Displays[id] = JsonSerializer.Deserialize<DisplayPreferences>(snapshot)!;
            throw new IOException("显示器设置没有保存成功。");
        }
        Apply(); StateChanged?.Invoke();
    }
    public IAppearanceEditor GetDisplayEditor(string id) => new DisplayEditor(this, id);
    public DisplayDefaults DisplayDefaults => new(_settings.ShowMonitor, _settings.MonitorNetwork, _settings.MonitorCpu,
        _settings.MonitorMemory, _settings.MaximizeMode);

    private sealed class DisplayEditor(DeskApp app, string id) : IAppearanceEditor
    {
        public string ThemeId => app.ThemeId;
        public bool SystemLightMode => app.SystemLightMode;
        public bool StartMenuLightMode => app.StartMenuLightMode;
        private DisplayPreferences Preferences => app.GetDisplayPreferences(id);
        private Settings Proxy => new() { ThemeOverrides = Preferences.ThemeOverrides };
        private AppearanceHistory History
        {
            get
            {
                if (!app._displayHistories.TryGetValue(id, out var history)) app._displayHistories[id] = history = new();
                return history;
            }
        }
        public bool HasParameter(string name) => app.HasParameter(name);
        public double? GetParameter(ParameterScope scope, string name, bool light) => double.TryParse(GetConstant(name, light),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        public string? GetConstant(string name, bool light) => app._theme?.GetConstant(name, light, Preferences.Resolve(ThemeId, app.Overrides));

        /// <summary>
        /// Changes this display's overrides through a settings object over
        /// them, saved at once; taken back when saving fails.
        /// </summary>
        private void Edit(Action<Settings, AppearanceHistory, Func<bool>> change)
        {
            change(Proxy, History, app._settings.Save);
            app.Apply();
            app.StateChanged?.Invoke();
        }
        // Only the taskbar is set per display.
        public void SetParameter(ParameterScope scope, string name, double value, bool perMode, bool light) => Edit((proxy, history, save) =>
            OverrideEdits.Set(proxy, history, ThemeId, ParameterScope.Taskbar, name, value, perMode, light, DateTime.UtcNow, save));
        public bool HasChangedParameters(IEnumerable<string> names, bool? light = null) => Proxy.HasOverrides(ThemeId, names, light);
        public void ResetParameters(ParameterScope scope, IEnumerable<string> names, bool light, bool onlyThisMode = false) =>
            Edit((proxy, history, save) => OverrideEdits.Reset(proxy, history, ThemeId, ParameterScope.Taskbar, names, light, onlyThisMode, DateTime.UtcNow, save));
        public bool CanUndoAppearance(ParameterScope scope, bool light) => History.CanUndo(ThemeId, ParameterScope.Taskbar, light);
        public void UndoAppearance(ParameterScope scope, bool light)
        {
            if (!History.Undo(Proxy, ThemeId, ParameterScope.Taskbar, app._settings.Save, light)) return;
            app.Apply(); app.StateChanged?.Invoke();
        }
        public void ApplyMaterialPreset(ParameterScope scope, MaterialPreset preset, bool light) => Edit((proxy, history, save) =>
            OverrideEdits.Preset(proxy, history, ThemeId, ParameterScope.Taskbar, preset, light, app.HasParameter, DateTime.UtcNow, save));
        public string SaveAppearance(string name)
        {
            if (app._theme == null) throw new InvalidOperationException("没有可保存的主题。");
            Directory.CreateDirectory(Paths.UserThemesDir);
            string path = Path.Combine(Paths.UserThemesDir, "my-look-" + Guid.NewGuid().ToString("N") + ".json");
            app._theme.SaveCopy(path, name, Preferences.Resolve(ThemeId, app.Overrides));
            app.StateChanged?.Invoke(); return path;
        }
    }
}
