using System.Diagnostics;
using System.Security.AccessControl;
using Microsoft.Win32;

namespace Desk;

/// <summary>The tray application: owns the settings, the theme and the engine.</summary>
internal sealed class DeskApp : ApplicationContext
{
    private const string DefaultTheme = "desk-islands";

    private readonly Settings _settings;
    private readonly Control _invoker = new();
    private readonly NotifyIcon _tray;
    private readonly MessageWindow _messages;
    private readonly WindowMonitor _windows;
    private readonly AutoHideController _autoHide = new();
    private readonly RuntimeWriter _runtime = new();
    private readonly TargetManager? _targets;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _themeReload = new() { Interval = 300 };
    private readonly List<FileSystemWatcher> _themeWatchers = [];
    private readonly List<DateTime> _explorerRestarts = [];
    private readonly Icon _iconEnabled = TrayIcons.Create(true);
    private readonly Icon _iconDisabled = TrayIcons.Create(false);

    private Theme? _theme;
    private bool _lightMode;
    private DateTime _restartRequestedAt = DateTime.MinValue;

    private ToolStripMenuItem _enabledItem = null!;
    private ToolStripMenuItem _themeMenu = null!;
    private ToolStripMenuItem _maximizeMerge = null!;
    private ToolStripMenuItem _maximizeAutoHide = null!;
    private ToolStripMenuItem _maximizeKeep = null!;
    private ToolStripMenuItem _startMenuItem = null!;
    private ToolStripMenuItem _clickThroughItem = null!;
    private ToolStripMenuItem _autostartItem = null!;

    public DeskApp()
    {
        _ = _invoker.Handle; // Create it now, to marshal callbacks to this thread.
        _settings = Settings.Load();
        _lightMode = ReadLightMode();

        PrepareDirectories();
        LoadTheme();

        bool arm64 = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                     System.Runtime.InteropServices.Architecture.Arm64;
        try
        {
            if (arm64)
            {
                throw new PlatformNotSupportedException("ARM64 Windows is not supported yet");
            }
            var injector = TapInjector.Deploy();
            _targets = new TargetManager(injector, SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
            _targets.ExplorerChanged += OnExplorerChanged;
        }
        catch (Exception e)
        {
            Log.Error("DeskTap.dll could not be deployed", e);
        }

        _messages = new MessageWindow();
        _messages.HotkeyPressed += () => SetEnabled(!_settings.Enabled, notify: true);
        _messages.TaskbarCreated += () => _targets?.Tick(_settings.Enabled, _settings.Enabled && _settings.StyleStartMenu);
        if (!_messages.RegisterHotkey())
        {
            Log.Warn("Ctrl+Alt+Shift+D is taken by another program");
        }

        _windows = new WindowMonitor();
        _windows.MaximizedChanged += Apply;

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        WatchThemes();

        _tray = new NotifyIcon
        {
            Text = "Desk",
            Icon = _settings.Enabled ? _iconEnabled : _iconDisabled,
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => SetEnabled(!_settings.Enabled, notify: true);

        Apply();

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();

        if (_targets == null)
        {
            Notify("Desk 无法启动引擎", arm64
                ? "这个版本只支持 x64 的 Windows，ARM64 版本还在计划中。"
                : "没有找到或无法加载 DeskTap.dll，请确认它和 Desk.exe 在同一个文件夹。");
        }
        else if (!_settings.WelcomeShown)
        {
            Notify("Desk 已启动", "右键托盘图标可以调整设置。按 Ctrl+Alt+Shift+D 可以随时停用或恢复美化。");
            _settings.WelcomeShown = true;
            _settings.Save();
        }

        WarnAboutConflicts();
    }

    private void PrepareDirectories()
    {
        try
        {
            Directory.CreateDirectory(Paths.RuntimeDir);
            Acl.GrantAppContainers(Paths.RuntimeDir, FileSystemRights.Read);
            Directory.CreateDirectory(Paths.LogsDir);
            Acl.GrantAppContainers(Paths.LogsDir, FileSystemRights.Modify);
            Acl.SetLowIntegrity(Paths.LogsDir);
            Directory.CreateDirectory(Paths.UserThemesDir);
        }
        catch (Exception e)
        {
            Log.Error("Could not prepare the data folders", e);
        }
    }

    // ---------------------------------------------------------------- themes

    private static IEnumerable<string> ThemeFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // User themes win over built-in ones with the same name.
        foreach (var dir in new[] { Paths.UserThemesDir, Paths.BuiltInThemesDir })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(Path.GetFileNameWithoutExtension(file)))
                {
                    yield return file;
                }
            }
        }
    }

    private void LoadTheme()
    {
        var files = ThemeFiles().ToList();
        string? path = files.FirstOrDefault(f =>
                           string.Equals(Path.GetFileNameWithoutExtension(f), _settings.Theme, StringComparison.OrdinalIgnoreCase))
                       ?? files.FirstOrDefault(f =>
                           string.Equals(Path.GetFileNameWithoutExtension(f), DefaultTheme, StringComparison.OrdinalIgnoreCase))
                       ?? files.FirstOrDefault();
        if (path == null)
        {
            Log.Error("No theme found");
            _theme = null;
            return;
        }

        try
        {
            _theme = Theme.Load(path);
            Log.Info($"Theme: {_theme.Name} ({path})");
        }
        catch (Exception e)
        {
            Log.Error($"Theme {path} could not be loaded", e);
            Notify("主题加载失败", $"{Path.GetFileName(path)}：{e.Message}");
        }
    }

    private void WatchThemes()
    {
        _themeReload.Tick += (_, _) =>
        {
            _themeReload.Stop();
            LoadTheme();
            Apply();
        };

        foreach (var dir in new[] { Paths.UserThemesDir, Paths.BuiltInThemesDir })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }
            var watcher = new FileSystemWatcher(dir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (_, _) => RunOnUi(() =>
            {
                _themeReload.Stop();
                _themeReload.Start();
            });
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (s, e) => changed(s, e);
            _themeWatchers.Add(watcher);
        }
    }

    private static bool ReadLightMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is not int value || value != 0;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        RunOnUi(() =>
        {
            bool light = ReadLightMode();
            if (light != _lightMode)
            {
                _lightMode = light;
                Log.Info($"Windows mode changed to {(light ? "light" : "dark")}");
                Apply();
            }
        });
    }

    // ----------------------------------------------------------------- apply

    /// <summary>Writes what the engine should show right now.</summary>
    private void Apply()
    {
        bool enabled = _settings.Enabled && _theme != null;
        bool maximized = _windows.HasMaximizedWindow;
        bool merge = maximized && _settings.MaximizeMode == MaximizeMode.Merge;

        var taskbarRules = enabled ? _theme!.Compile(ThemeTarget.Taskbar, _lightMode, merge) : [];
        _runtime.Write(RuntimeWriter.ExplorerFile, enabled, enabled && _settings.ClickThrough, taskbarRules);

        bool startMenu = enabled && _settings.StyleStartMenu;
        var startRules = startMenu ? _theme!.Compile(ThemeTarget.StartMenu, _lightMode, false) : [];
        _runtime.Write(RuntimeWriter.StartMenuFile, startMenu, false, startRules);

        _autoHide.SetForced(enabled && maximized && _settings.MaximizeMode == MaximizeMode.AutoHide);

        if (_tray != null)
        {
            _tray.Icon = _settings.Enabled ? _iconEnabled : _iconDisabled;
            _tray.Text = _settings.Enabled ? "Desk：已启用" : "Desk：已停用";
        }
    }

    private void Tick()
    {
        _targets?.Tick(_settings.Enabled, _settings.Enabled && _settings.StyleStartMenu);
    }

    private void OnExplorerChanged(uint processId)
    {
        if (DateTime.UtcNow - _restartRequestedAt < TimeSpan.FromSeconds(20))
        {
            return;
        }

        // Explorer restarting again and again right after DeskTap was loaded
        // most likely means the engine crashes it: stop.
        var now = DateTime.UtcNow;
        _explorerRestarts.Add(now);
        _explorerRestarts.RemoveAll(t => now - t > TimeSpan.FromMinutes(3));
        Log.Warn($"Explorer restarted ({_explorerRestarts.Count} times in 3 minutes)");
        if (_explorerRestarts.Count >= 3 && _settings.Enabled)
        {
            _explorerRestarts.Clear();
            SetEnabled(false, notify: false);
            Notify("Desk 已自动停用", "资源管理器在短时间内重启了多次，为了安全已停用美化。可以在托盘菜单里重新启用，并把日志发给开发者。");
        }
    }

    private void SetEnabled(bool enabled, bool notify)
    {
        _settings.Enabled = enabled;
        _settings.Save();
        Apply();
        Tick();
        if (notify)
        {
            Notify(enabled ? "美化已启用" : "美化已停用",
                enabled ? "按 Ctrl+Alt+Shift+D 可以再次停用。" : "任务栏和开始菜单已恢复原样。按 Ctrl+Alt+Shift+D 可以重新启用。");
        }
    }

    // ------------------------------------------------------------------ menu

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var header = new ToolStripMenuItem($"Desk {Application.ProductVersion.Split('+')[0]}") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("启用美化", null, (_, _) => SetEnabled(!_settings.Enabled, notify: false));
        _themeMenu = new ToolStripMenuItem("主题");

        var maximizeMenu = new ToolStripMenuItem("窗口最大化时");
        _maximizeMerge = new ToolStripMenuItem("恢复成整条任务栏", null, (_, _) => SetMaximizeMode(MaximizeMode.Merge));
        _maximizeAutoHide = new ToolStripMenuItem("自动隐藏任务栏", null, (_, _) => SetMaximizeMode(MaximizeMode.AutoHide));
        _maximizeKeep = new ToolStripMenuItem("保持浮岛", null, (_, _) => SetMaximizeMode(MaximizeMode.Keep));
        maximizeMenu.DropDownItems.AddRange([_maximizeMerge, _maximizeAutoHide, _maximizeKeep]);

        _startMenuItem = new ToolStripMenuItem("美化开始菜单", null, (_, _) =>
        {
            _settings.StyleStartMenu = !_settings.StyleStartMenu;
            _settings.Save();
            Apply();
            Tick();
        });
        _clickThroughItem = new ToolStripMenuItem("空白处点击穿透（实验）", null, (_, _) =>
        {
            _settings.ClickThrough = !_settings.ClickThrough;
            _settings.Save();
            Apply();
        });
        _autostartItem = new ToolStripMenuItem("开机自动启动", null, (_, _) => Autostart.Set(!Autostart.IsEnabled));

        var reapply = new ToolStripMenuItem("重新应用", null, (_, _) => Reapply());
        var themesFolder = new ToolStripMenuItem("打开主题文件夹", null, (_, _) => OpenThemesFolder());
        var logsFolder = new ToolStripMenuItem("打开日志文件夹", null, (_, _) => OpenFolder(Paths.LogsDir));
        var restartExplorer = new ToolStripMenuItem("重启资源管理器", null, (_, _) => RestartExplorer());
        var exit = new ToolStripMenuItem("退出并还原", null, (_, _) => ExitThread());

        menu.Items.AddRange([
            header,
            new ToolStripSeparator(),
            _enabledItem,
            _themeMenu,
            maximizeMenu,
            _startMenuItem,
            _clickThroughItem,
            new ToolStripSeparator(),
            _autostartItem,
            reapply,
            themesFolder,
            logsFolder,
            restartExplorer,
            new ToolStripSeparator(),
            exit,
        ]);

        menu.Opening += (_, _) => RefreshMenu();
        return menu;
    }

    private void RefreshMenu()
    {
        _enabledItem.Checked = _settings.Enabled;
        _maximizeMerge.Checked = _settings.MaximizeMode == MaximizeMode.Merge;
        _maximizeAutoHide.Checked = _settings.MaximizeMode == MaximizeMode.AutoHide;
        _maximizeKeep.Checked = _settings.MaximizeMode == MaximizeMode.Keep;
        _startMenuItem.Checked = _settings.StyleStartMenu;
        _clickThroughItem.Checked = _settings.ClickThrough;
        _autostartItem.Checked = Autostart.IsEnabled;

        _themeMenu.DropDownItems.Clear();
        foreach (var file in ThemeFiles())
        {
            string id = Path.GetFileNameWithoutExtension(file);
            var item = new ToolStripMenuItem(Theme.ReadName(file), null, (_, _) =>
            {
                _settings.Theme = id;
                _settings.Save();
                LoadTheme();
                Apply();
            })
            {
                Checked = _theme != null && string.Equals(_theme.Id, id, StringComparison.OrdinalIgnoreCase),
            };
            _themeMenu.DropDownItems.Add(item);
        }
        if (_themeMenu.DropDownItems.Count == 0)
        {
            _themeMenu.DropDownItems.Add(new ToolStripMenuItem("（没有找到主题）") { Enabled = false });
        }
    }

    private void SetMaximizeMode(MaximizeMode mode)
    {
        _settings.MaximizeMode = mode;
        _settings.Save();
        Apply();
    }

    private void Reapply()
    {
        LoadTheme();
        _runtime.Invalidate();
        _targets?.Reset();
        Apply();
        Tick();
    }

    private void OpenThemesFolder()
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

    private void RestartExplorer()
    {
        _restartRequestedAt = DateTime.UtcNow;
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

    private void WarnAboutConflicts()
    {
        var others = new List<string>();
        foreach (var (process, name) in new[] { ("windhawk", "Windhawk"), ("TranslucentTB", "TranslucentTB") })
        {
            var running = Process.GetProcessesByName(process);
            if (running.Length > 0)
            {
                others.Add(name);
            }
            foreach (var p in running)
            {
                p.Dispose();
            }
        }
        if (others.Count > 0)
        {
            Log.Warn($"Possibly conflicting programs running: {string.Join(", ", others)}");
            Notify("可能有冲突", $"检测到 {string.Join("、", others)} 正在运行。它们也会修改任务栏，可能和 Desk 冲突，建议先关闭任务栏相关的功能。");
        }
    }

    // ----------------------------------------------------------------- utils

    private void Notify(string title, string text)
    {
        if (_tray is { Visible: true })
        {
            _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
        }
        else
        {
            RunOnUi(() => _tray?.ShowBalloonTip(5000, title, text, ToolTipIcon.Info));
        }
    }

    private void RunOnUi(Action action)
    {
        try
        {
            if (_invoker.InvokeRequired)
            {
                _invoker.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (Exception e)
        {
            Log.Error("UI callback failed", e);
        }
    }

    protected override void ExitThreadCore()
    {
        Log.Info("Exiting: restoring the taskbar and Start menu");
        _timer.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        // Tell the engine to put everything back.
        _runtime.Write(RuntimeWriter.ExplorerFile, false, false, []);
        _runtime.Write(RuntimeWriter.StartMenuFile, false, false, []);
        _autoHide.SetForced(false);

        foreach (var watcher in _themeWatchers)
        {
            watcher.Dispose();
        }
        _windows.Dispose();
        _messages.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _invoker.Dispose();
        base.ExitThreadCore();
    }
}
