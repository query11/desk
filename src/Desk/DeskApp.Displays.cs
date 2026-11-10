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
        public double? GetParameter(ParameterScope scope, string name) => double.TryParse(app._theme?.GetConstant(name, SystemLightMode,
            Preferences.Resolve(ThemeId, app.Overrides)), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        private void Edit(Action<Settings> change, string? mergeKey = null)
        {
            var proxy = Proxy;
            var before = new Dictionary<string, string>(proxy.OverridesFor(ThemeId));
            app.UpdateDisplay(id, _ => change(proxy));
            History.Record(ThemeId, ParameterScope.Taskbar, before, proxy.OverridesFor(ThemeId), mergeKey, DateTime.UtcNow);
            app.StateChanged?.Invoke();
        }
        public void SetParameter(ParameterScope scope, string name, double value, bool perMode) =>
            Edit(proxy => proxy.SetOverride(ThemeId, name, value.ToString("0.###", CultureInfo.InvariantCulture), perMode ? SystemLightMode : null), name + "|" + SystemLightMode);
        public bool HasChangedParameters(IEnumerable<string> names) => Proxy.HasOverrides(ThemeId, names);
        public void ResetParameters(ParameterScope scope, IEnumerable<string> names) => Edit(proxy => proxy.ResetOverrides(ThemeId, names));
        public bool CanUndoAppearance(ParameterScope scope) => History.CanUndo(ThemeId, ParameterScope.Taskbar);
        public void UndoAppearance(ParameterScope scope)
        {
            if (!History.Undo(Proxy, ThemeId, ParameterScope.Taskbar, app._settings.Save)) return;
            app.Apply(); app.StateChanged?.Invoke();
        }
        public void ApplyMaterialPreset(ParameterScope scope, MaterialPreset preset) => Edit(proxy =>
        {
            foreach (var (key, value) in preset.Values(ParameterScope.Taskbar, SystemLightMode))
            {
                var parts = key.Split('@');
                proxy.SetOverride(ThemeId, parts[0], value, parts.Length > 1 ? SystemLightMode : null);
            }
        });
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
