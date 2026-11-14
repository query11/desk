using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace Desk;

/// <summary>What the settings window reads and changes. Used on the UI thread only.</summary>
internal sealed partial class DeskApp : IAppearanceEditor, IThemeLibrary
{
    private readonly AppearanceHistory _appearanceHistory = new();
    public static string Version { get; } =
        typeof(DeskApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Other programs that also change the taskbar.</summary>
    public IReadOnlyList<string> Conflicts { get; private set; } = [];

    /// <summary>Why the engine can't run, if it can't.</summary>
    public string? EngineError => _engineError;

    public bool SystemLightMode => _lightMode;

    public bool StartMenuLightMode => StartMenuLight;

    public bool Enabled
    {
        get => _settings.Enabled;
        set
        {
            if (value != _settings.Enabled)
            {
                SetEnabled(value, notify: false);
            }
        }
    }

    public string ThemeId
    {
        get => _theme?.Id ?? _settings.Theme;
        set
        {
            if (string.Equals(value, ThemeId, StringComparison.OrdinalIgnoreCase) && _themeTrial.Pending == null)
            {
                return;
            }
            // Validate before changing the active id or saving. A bad user file must
            // leave the last working theme in place and report failure to the gallery.
            string path = ThemeFiles().FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), value,
                StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException("该方案已被移走，请刷新方案列表。");
            _ = Theme.Load(path);
            _themeTrial.Select(value);
        }
    }

    public string ThemeName => _theme?.Name ?? "";

    public string ThemeDescription => _theme?.Description ?? "";

    public MaximizeMode MaximizeMode
    {
        get => _settings.MaximizeMode;
        set => ChangeSetting(() => _settings.MaximizeMode = value);
    }

    public bool StyleStartMenu
    {
        get => _settings.StyleStartMenu;
        set
        {
            ChangeSetting(() => _settings.StyleStartMenu = value);
            Tick();
        }
    }

    public StartMenuScheme StartMenuScheme
    {
        get => _settings.StartMenuScheme;
        set => ChangeSetting(() => _settings.StartMenuScheme = value);
    }

    public bool ClickThrough
    {
        get => _settings.ClickThrough;
        set => ChangeSetting(() => _settings.ClickThrough = value);
    }

    public bool ShowMonitor
    {
        get => _settings.ShowMonitor;
        set => ChangeSetting(() => _settings.ShowMonitor = value);
    }

    public bool MonitorNetwork
    {
        get => _settings.MonitorNetwork;
        set => ChangeSetting(() => _settings.MonitorNetwork = value);
    }

    public bool MonitorCpu
    {
        get => _settings.MonitorCpu;
        set => ChangeSetting(() => _settings.MonitorCpu = value);
    }

    public bool MonitorMemory
    {
        get => _settings.MonitorMemory;
        set => ChangeSetting(() => _settings.MonitorMemory = value);
    }

    /// <summary>What the info island shows, in its order; empty when it is switched off.</summary>
    public IReadOnlyList<string> MonitorItems
    {
        get
        {
            var items = new List<string>(3);
            if (!_settings.ShowMonitor) return items;
            if (_settings.MonitorNetwork) items.Add("network");
            if (_settings.MonitorCpu) items.Add("cpu");
            if (_settings.MonitorMemory) items.Add("memory");
            return items;
        }
    }

    /// <summary>What was found the last time autostart was checked or changed.</summary>
    public AutostartStatus AutostartInfo { get; private set; } = AutostartStatus.Unchecked;

    /// <summary>Why the last change failed, or a warning about it; null when there is nothing to say.</summary>
    public string? AutostartMessage { get; private set; }

    public bool AutostartMessageIsError { get; private set; }

    /// <summary>
    /// Turns autostart on or off in the background (it starts schtasks.exe)
    /// and reports what is actually set up afterwards.
    /// </summary>
    public async Task<AutostartResult> SetAutostartAsync(bool enabled)
    {
        // A user's choice; the start-up check won't change it any more.
        _settings.AutostartConfigured = true;
        AutostartResult result;
        try
        {
            result = await Task.Run(() => _autostart.Set(enabled));
        }
        catch (Exception e)
        {
            Log.Error("Changing autostart failed", e);
            AutostartMessage = e.Message;
            AutostartMessageIsError = true;
            StateChanged?.Invoke();
            throw;
        }
        ApplyAutostart(result);
        return result;
    }

    private long _autostartTicket;

    /// <summary>Looks at what is set up now, in the background (the settings page shows it).</summary>
    public void CheckAutostart()
    {
        Task.Run(_autostart.Inspect).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully)
            {
                RunOnUi(() => ApplyAutostart(task.Result));
            }
            else
            {
                Log.Error("Checking autostart failed", task.Exception);
            }
        });
    }

    /// <summary>Takes a result on the UI thread, unless a newer one already arrived.</summary>
    private void ApplyAutostart(AutostartResult result)
    {
        if (result.Ticket < _autostartTicket)
        {
            return;
        }
        _autostartTicket = result.Ticket;
        AutostartInfo = result.Status;
        AutostartMessage = result.Error ?? result.Warning;
        AutostartMessageIsError = result.Error != null;
        if (result.Configured)
        {
            _settings.AutostartConfigured = true;
        }
        if (result.Status.State == AutostartState.On)
        {
            _settings.AutostartPath = Environment.ProcessPath;
        }
        _settings.Save();
        StateChanged?.Invoke();
    }

    /// <summary>Whether the taskbar is styled, with the engine's own report as evidence.</summary>
    public TargetStatus Taskbar => _targets == null
        ? new TargetStatus(EngineStatus.Unavailable)
        : EngineStatusEvaluator.Evaluate(_settings.Enabled, _targets.Explorer,
            _engineReports.Read(RuntimeWriter.ExplorerFile, _targets.Explorer.Process),
            _runtime.LastSerial(RuntimeWriter.ExplorerFile), DateTime.UtcNow);

    public TargetStatus StartMenu => _targets == null
        ? new TargetStatus(EngineStatus.Unavailable)
        : EngineStatusEvaluator.Evaluate(_settings.Enabled && _settings.StyleStartMenu, _targets.StartMenu,
            _engineReports.Read(RuntimeWriter.StartMenuFile, _targets.StartMenu.Process),
            _runtime.LastSerial(RuntimeWriter.StartMenuFile), DateTime.UtcNow);

    public EngineStatus TaskbarStatus => Taskbar.Status;

    /// <summary>Explorer still runs the engine of an older Desk version.</summary>
    public bool ExplorerOutdated => _targets?.Explorer.Outdated == true || TaskbarStatus == EngineStatus.Outdated;

    public IReadOnlyList<ThemeOption> GetAppearanceOptions() => _library.List();

    /// <summary>The loaded theme with the user's changes, for a preview; no files are read.</summary>
    public ThemeOption? CurrentAppearance => _theme == null
        ? null
        : new ThemeOption(_theme.Id, _theme.Name, _theme.Description, false, _theme,
            new Dictionary<string, string>(Overrides));

    public event Action? TrialChanged;
    public ThemeTrial? PendingTrial => _themeTrial.Pending;
    public int TrialSecondsLeft => _themeTrial.SecondsLeft;
    public void StartThemeTrial(string id)
    {
        if (!Enabled) throw new InvalidOperationException("请先在概览中启用美化，再试用方案。");
        _ = Theme.Load(_library.Find(id)); _themeTrial.Begin(id);
    }
    public void KeepThemeTrial() => _themeTrial.Keep();
    public void CancelThemeTrial() => _themeTrial.Cancel();
    public void ToggleFavorite(string id) { _library.Favorite(id); StateChanged?.Invoke(); }
    public string DuplicateTheme(string id, string name)
    {
        string created = _library.Duplicate(id, name); StateChanged?.Invoke(); return created;
    }
    public void RenameTheme(string id, string name)
    {
        _library.Rename(id, name); if (id == ThemeId) LoadTheme(); StateChanged?.Invoke();
    }
    public void DeleteTheme(string id)
    {
        var option = GetAppearanceOptions().Single(t => t.Id == id);
        if (!option.IsUserTheme) throw new InvalidOperationException("不能删除内置方案。");
        if (PendingTrial is { } trial && (trial.OriginalTheme == id || trial.TrialTheme == id)) CancelThemeTrial();
        if (ThemeId == id) ThemeId = DefaultTheme;
        _library.Delete(id); StateChanged?.Invoke();
    }
    public string ImportTheme(string path) { string id = _library.Import(path); StateChanged?.Invoke(); return id; }
    public void ExportTheme(string id, string path) => _library.Export(id, path);
    public IReadOnlyList<ThemeAdjustment> GetThemeAdjustments(string id) => _library.Adjustments(id);
    public void ResetThemeAdjustment(string id, string key)
    {
        var before = new Dictionary<string, string>(_settings.OverridesFor(id));
        if (!_settings.ThemeOverrides.TryGetValue(id, out var values) || !values.Remove(key)) return;
        if (!_settings.Save()) { values[key] = before[key]; throw new IOException("修改没有保存成功。"); }
        var scope = key.StartsWith("menu", StringComparison.Ordinal) ? ParameterScope.StartMenu : ParameterScope.Taskbar;
        // Undone on the page showing that mode: the one of the value, or the current one.
        bool light = key.EndsWith("@light", StringComparison.Ordinal) || (!key.EndsWith("@dark", StringComparison.Ordinal) && IsLight(scope));
        _appearanceHistory.Record(id, scope, before, values, null, DateTime.UtcNow, light);
        if (id == ThemeId) Apply();
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------ parameters

    public bool HasParameter(string name) => _theme?.HasConstant(name) == true;

    /// <summary>
    /// The user's changes to the loaded theme. Kept per theme: switching to
    /// another theme shows its own values, switching back restores these.
    /// </summary>
    private IReadOnlyDictionary<string, string> Overrides =>
        _theme == null ? new Dictionary<string, string>() : _settings.OverridesFor(_theme.Id);

    /// <summary>A numeric theme constant as used in a mode (with the user's changes).</summary>
    public double? GetParameter(ParameterScope scope, string name, bool light) => Number(GetConstant(name, light));

    public string? GetConstant(string name, bool light) => _theme?.GetConstant(name, light, Overrides);

    /// <summary>A numeric constant as the desktop uses it right now.</summary>
    private double? CurrentNumber(ParameterScope scope, string name) => GetParameter(scope, name, IsLight(scope));

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    /// <summary>
    /// Changes a constant of the loaded theme. With <paramref name="perMode"/>
    /// the change only applies to the light or the dark variant (colors need
    /// different values in each), which need not be the one Windows shows.
    /// </summary>
    public void SetParameter(ParameterScope scope, string name, double value, bool perMode, bool light)
    {
        if (_theme == null)
        {
            return;
        }
        OverrideEdits.Set(_settings, _appearanceHistory, _theme.Id, scope, name, value, perMode, light, DateTime.UtcNow);
        ApplySoon();
    }

    public bool HasChangedParameters(IEnumerable<string> names, bool? light = null) =>
        _theme != null && _settings.HasOverrides(_theme.Id, names, light);

    /// <summary>Goes back to the theme's own values for these constants, in this theme only.</summary>
    public void ResetParameters(ParameterScope scope, IEnumerable<string> names, bool light, bool onlyThisMode = false)
    {
        if (_theme == null)
        {
            return;
        }
        OverrideEdits.Reset(_settings, _appearanceHistory, _theme.Id, scope, names, light, onlyThisMode, DateTime.UtcNow,
            _settings.Save);
        Apply();
        StateChanged?.Invoke();
    }

    private bool IsLight(ParameterScope scope) => scope == ParameterScope.StartMenu ? StartMenuLight : _lightMode;

    public bool CanUndoAppearance(ParameterScope scope, bool light) => _appearanceHistory.CanUndo(ThemeId, scope, light);

    public void UndoAppearance(ParameterScope scope, bool light)
    {
        if (!_appearanceHistory.Undo(_settings, ThemeId, scope, _settings.Save, light)) return;
        Apply();
        StateChanged?.Invoke();
    }

    public void ApplyMaterialPreset(ParameterScope scope, MaterialPreset preset, bool light)
    {
        if (_theme == null) return;
        OverrideEdits.Preset(_settings, _appearanceHistory, _theme.Id, scope, preset, light, _theme.HasConstant, DateTime.UtcNow,
            _settings.Save);
        Apply();
        StateChanged?.Invoke();
    }

    public string SaveAppearance(string name)
    {
        if (_theme == null) throw new InvalidOperationException("没有可保存的主题");
        Directory.CreateDirectory(Paths.UserThemesDir);
        string path = Path.Combine(Paths.UserThemesDir, "my-look-" + Guid.NewGuid().ToString("N")[..12] + ".json");
        _theme.SaveCopy(path, name, Overrides);
        StateChanged?.Invoke();
        return path;
    }

    // --------------------------------------------------------------- actions

    public void ShowWindow()
    {
        try
        {
            _windowLoaded = true;
            Ui.UiHost.ShowWindow(this);
        }
        catch (Exception e)
        {
            Log.Error("The settings window could not be opened", e);
            Notify("无法打开设置窗口", e.Message);
        }
    }

    /// <summary>Where the settings window was closed last time; null for the first time.</summary>
    public WindowPlacement? WindowPlacement => _settings.Window;

    /// <summary>Remembers where the settings window is closed, for the next time it opens (also after a restart).</summary>
    public void RememberWindow(WindowPlacement placement)
    {
        _settings.Window = placement;
        _settings.Save();
    }

    /// <summary>Called when the settings window was closed; Desk keeps running in the tray.</summary>
    public void OnWindowClosed()
    {
        if (!_exiting && !_settings.CloseTipShown)
        {
            _settings.CloseTipShown = true;
            _settings.Save();
            Notify("Desk 仍在后台运行", "美化会继续生效。单击托盘图标可以再次打开设置，右键可以退出。");
        }
    }

    public void Reapply()
    {
        LoadTheme();
        // Rewrites both files with a new serial, and asks engines whose
        // initialization failed to try again now.
        _runtime.RequestRecovery();
        _targets?.Reset();
        _fastTicksUntil = DateTime.UtcNow.AddSeconds(30);
        Apply();
        Tick();
        StateChanged?.Invoke();
    }

    public void RestartExplorer()
    {
        _restartGuard.RestartRequested(DateTime.UtcNow);
        _fastTicksUntil = DateTime.UtcNow.AddSeconds(60);
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                using (process)
                {
                    if (process.SessionId == session)
                    {
                        process.Kill();
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Could not stop explorer: {e.Message}");
        }

        // Windows normally restarts the shell by itself; make sure it does.
        var check = new System.Windows.Forms.Timer { Interval = 3000 };
        check.Tick += (_, _) =>
        {
            check.Stop();
            check.Dispose();
            if (NativeMethods.FindWindowW("Shell_TrayWnd", null) == IntPtr.Zero)
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
        };
        check.Start();
    }

    public void OpenThemesFolder()
    {
        Directory.CreateDirectory(Paths.UserThemesDir);
        // Start the user off with an editable copy of the current theme.
        if (_theme != null && !Directory.EnumerateFiles(Paths.UserThemesDir, "*.json").Any())
        {
            try
            {
                string copy = Path.Combine(Paths.UserThemesDir, "my-theme.json");
                string text = File.ReadAllText(_theme.FilePath);
                File.WriteAllText(copy, text.Replace($"\"{_theme.Name}\"", $"\"我的主题（基于 {_theme.Name}）\""));
            }
            catch (Exception e)
            {
                Log.Warn($"Could not copy the theme: {e.Message}");
            }
        }
        OpenFolder(Paths.UserThemesDir);
    }

    public void OpenLogsFolder() => OpenFolder(Paths.LogsDir);

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Warn($"Could not open {url}: {e.Message}");
        }
    }

    /// <summary>Restores the taskbar and the Start menu and quits.</summary>
    public void Exit() => ExitThread();

    private void ChangeSetting(Action change)
    {
        change();
        _settings.Save();
        Apply();
        StateChanged?.Invoke();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Warn($"Could not open {path}: {e.Message}");
        }
    }
}
