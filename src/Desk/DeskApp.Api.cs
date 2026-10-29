using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace Desk;

internal sealed record ThemeInfo(string Id, string Name, string Description, bool IsUserTheme);

/// <summary>Which part a theme parameter belongs to; decides its light/dark variant.</summary>
internal enum ParameterScope
{
    Taskbar,
    StartMenu,
}

/// <summary>What the settings window reads and changes. Used on the UI thread only.</summary>
internal sealed partial class DeskApp
{
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
            if (string.Equals(value, ThemeId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _settings.Theme = value;
            _settings.Save();
            LoadTheme();
            Apply();
            StateChanged?.Invoke();
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

    public bool AutostartEnabled => Autostart.IsEnabled;

    /// <summary>Turns autostart on or off in the background (it starts schtasks.exe).</summary>
    public Task SetAutostartAsync(bool enabled) =>
        Task.Run(() => Autostart.Set(_settings, enabled))
            .ContinueWith(_ => RunOnUi(() =>
            {
                _settings.Save();
                StateChanged?.Invoke();
            }));

    public EngineStatus TaskbarStatus
    {
        get
        {
            if (_targets == null)
            {
                return EngineStatus.Unavailable;
            }
            if (!_settings.Enabled)
            {
                return EngineStatus.Off;
            }
            return _targets.ExplorerInjected ? EngineStatus.Active : EngineStatus.Connecting;
        }
    }

    public EngineStatus StartMenuStatus
    {
        get
        {
            if (_targets == null)
            {
                return EngineStatus.Unavailable;
            }
            if (!_settings.Enabled || !_settings.StyleStartMenu)
            {
                return EngineStatus.Off;
            }
            if (_targets.StartMenuInjected)
            {
                return EngineStatus.Active;
            }
            return _targets.StartMenuPending ? EngineStatus.Waiting : EngineStatus.Connecting;
        }
    }

    /// <summary>Explorer still runs the engine of an older Desk version.</summary>
    public bool ExplorerOutdated => _targets?.ExplorerOutdated == true;

    public IReadOnlyList<ThemeInfo> GetThemes()
    {
        var themes = new List<ThemeInfo>();
        foreach (var file in ThemeFiles())
        {
            var (name, description) = Theme.ReadInfo(file);
            bool user = file.StartsWith(Paths.UserThemesDir, StringComparison.OrdinalIgnoreCase);
            themes.Add(new ThemeInfo(Path.GetFileNameWithoutExtension(file), name, description, user));
        }
        return themes;
    }

    // ------------------------------------------------------------ parameters

    public bool HasParameter(string name) => _theme?.HasConstant(name) == true;

    /// <summary>A numeric theme constant as currently used (with the user's changes).</summary>
    public double? GetParameter(ParameterScope scope, string name)
    {
        string? text = _theme?.GetConstant(name, IsLight(scope), _settings.Overrides);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    /// <summary>
    /// Changes a theme constant. With <paramref name="perMode"/> the change
    /// only applies to the current light or dark variant (colors need
    /// different values in each).
    /// </summary>
    public void SetParameter(ParameterScope scope, string name, double value, bool perMode)
    {
        string text = value.ToString("0.###", CultureInfo.InvariantCulture);
        if (perMode)
        {
            _settings.Overrides[$"{name}@{(IsLight(scope) ? "light" : "dark")}"] = text;
        }
        else
        {
            _settings.Overrides.Remove($"{name}@light");
            _settings.Overrides.Remove($"{name}@dark");
            _settings.Overrides[name] = text;
        }
        ApplySoon();
    }

    public bool HasChangedParameters(IEnumerable<string> names) =>
        names.Any(n => _settings.Overrides.ContainsKey(n) ||
                       _settings.Overrides.ContainsKey($"{n}@light") ||
                       _settings.Overrides.ContainsKey($"{n}@dark"));

    /// <summary>Goes back to the theme's own values.</summary>
    public void ResetParameters(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            _settings.Overrides.Remove(name);
            _settings.Overrides.Remove($"{name}@light");
            _settings.Overrides.Remove($"{name}@dark");
        }
        _settings.Save();
        Apply();
        StateChanged?.Invoke();
    }

    private bool IsLight(ParameterScope scope) => scope == ParameterScope.StartMenu ? StartMenuLight : _lightMode;

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
        _runtime.Invalidate();
        _targets?.Reset();
        _fastTicksUntil = DateTime.UtcNow.AddSeconds(30);
        Apply();
        Tick();
        StateChanged?.Invoke();
    }

    public void RestartExplorer()
    {
        _restartRequestedAt = DateTime.UtcNow;
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
