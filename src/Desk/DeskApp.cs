using System.Drawing;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Desk;

/// <summary>
/// The tray application: owns the settings, the theme and the engine, and
/// opens the settings window on demand.
/// </summary>
internal sealed partial class DeskApp : ApplicationContext
{
    private const string DefaultTheme = "desk-islands";

    private readonly Settings _settings;
    private readonly Control _invoker = new();
    private readonly NotifyIcon _tray;
    private readonly MessageWindow _messages;
    private readonly WindowMonitor _windows;
    private readonly AutoHideController _autoHide = new(new WindowsAppBarState());
    private readonly HostSession _host = HostSession.ForCurrentProcess();
    private readonly RuntimeWriter _runtime = new();
    private readonly EngineStatusReader _engineReports = new(Paths.StateDir);
    private readonly TargetManager? _targets;
    private readonly string? _engineError;
    private readonly string? _dllPath;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Windows.Forms.Timer _themeReload = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _applySoon = new() { Interval = 100 };
    private readonly List<FileSystemWatcher> _themeWatchers = [];
    private readonly RestartGuard _restartGuard = new();
    private readonly Icon _iconEnabled = TrayIcons.Create(true);
    private readonly Icon _iconDisabled = TrayIcons.Create(false);
    private readonly EventWaitHandle _showWindowEvent;
    private readonly RegisteredWaitHandle _showWindowWait;
    private readonly EventWaitHandle _restoreEvent;
    private readonly RegisteredWaitHandle _restoreWait;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    private Theme? _theme;
    private bool _lightMode;
    private DateTime _fastTicksUntil;
    private (TargetStatus Taskbar, TargetStatus StartMenu) _lastStatus;
    private DateTime _nextStatusCleanup;
    private bool _windowLoaded;
    private bool _outdatedNoticeShown;
    private bool _exiting;

    /// <summary>Settings, theme or Windows mode changed.</summary>
    public event Action? StateChanged;

    /// <summary>The engine status of the taskbar or the Start menu changed.</summary>
    public event Action? StatusChanged;

    public DeskApp(bool showWindow)
    {
        _ = _invoker.Handle; // Create it now, to marshal callbacks to this thread.
        _settings = Settings.Load();
        _lightMode = ReadLightMode();

        // Get the engine going first; the taskbar should change as soon as
        // possible after sign-in.
        PrepareDirectories();
        LoadTheme();

        // Every configuration names this Desk.exe; the engines restore by
        // themselves when it's gone (also when it crashes or is killed).
        Acl.AllowAppContainersToWatchThisProcess();
        _runtime.Owner = _host.Lease;
        _host.StartHeartbeat(TimeSpan.FromSeconds(5));
        Log.Info($"Host session {_host.Lease.Session}");

        // Auto-hide left on by a previous Desk that didn't exit normally.
        if (_autoHide.Restore() != AutoHideRestore.NothingToDo)
        {
            Log.Info("Restored auto-hide changed by a previous run");
        }

        bool arm64 = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                     System.Runtime.InteropServices.Architecture.Arm64;
        try
        {
            if (arm64)
            {
                throw new PlatformNotSupportedException("ARM64 Windows is not supported yet");
            }
            var injector = TapInjector.Deploy();
            _dllPath = injector.DllPath;
            var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            // The intent is read when an attempt would start, so that a
            // change made by an event in the same tick (crash protection)
            // counts right away.
            _targets = new TargetManager(new WindowsTargetPlatform(injector), new ThreadPoolScheduler(ui),
                TimeProvider.System, injector.DllPath,
                explorerWanted: () => !_exiting && _settings.Enabled,
                startMenuWanted: () => !_exiting && _settings.Enabled && _settings.StyleStartMenu);
            _targets.ExplorerChanged += OnExplorerChanged;
        }
        catch (Exception e)
        {
            Log.Error("DeskTap.dll could not be deployed", e);
            _engineError = arm64
                ? "这个版本只支持 x64 的 Windows，ARM64 版本还在计划中。"
                : "没有找到或无法加载 DeskTap.dll，请确认它和 Desk.exe 在同一个文件夹。";
        }

        _windows = new WindowMonitor();
        Apply();
        _fastTicksUntil = DateTime.UtcNow.AddSeconds(90);
        Tick();

        // Then everything else.
        _messages = new MessageWindow();
        _messages.HotkeyPressed += () => SetEnabled(!_settings.Enabled, notify: true);
        _messages.TaskbarCreated += () =>
        {
            _fastTicksUntil = DateTime.UtcNow.AddSeconds(60);
            Tick();
        };
        if (!_messages.RegisterHotkey())
        {
            Log.Warn("Ctrl+Alt+Shift+D is taken by another program");
        }

        _windows.MaximizedChanged += Apply;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        WatchThemes();

        _applySoon.Tick += (_, _) =>
        {
            _applySoon.Stop();
            _settings.Save();
            Apply();
        };

        _tray = new NotifyIcon
        {
            Text = "Desk",
            Icon = _settings.Enabled ? _iconEnabled : _iconDisabled,
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindow();
            }
        };
        UpdateTray();

        _timer.Tick += (_, _) => Tick();
        _timer.Interval = 250;
        _timer.Start();

        // A second Desk.exe asks this one to show the window.
        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowWindowEventName);
        _showWindowWait = ThreadPool.RegisterWaitForSingleObject(_showWindowEvent,
            (_, _) => RunOnUi(ShowWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        // Desk.exe --restore asks this one to restore and quit.
        _restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WindowsRestoreEnvironment.RestoreEventName);
        _restoreWait = ThreadPool.RegisterWaitForSingleObject(_restoreEvent, (_, _) => RunOnUi(() =>
        {
            Log.Info("Emergency restore requested");
            _settings.Enabled = false;
            _settings.Save();
            Exit();
        }), null, Timeout.Infinite, executeOnlyOnce: true);

        if (_engineError != null)
        {
            Notify("Desk 无法启动引擎", _engineError);
        }
        else if (!showWindow && !_settings.WelcomeShown)
        {
            Notify("Desk 已启动", "单击托盘图标打开设置。按 Ctrl+Alt+Shift+D 可以随时停用或恢复美化。");
            _settings.WelcomeShown = true;
            _settings.Save();
        }

        if (showWindow)
        {
            // After the message loop has started.
            _invoker.BeginInvoke(ShowWindow);
        }

        // Slow checks, off the UI thread and after the engine got its turn.
        Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
        {
            try
            {
                Autostart.Refresh(_settings);
            }
            catch (Exception e)
            {
                Log.Error("Autostart check failed", e);
            }
            var conflicts = FindConflicts();
            RunOnUi(() =>
            {
                _settings.Save();
                Conflicts = conflicts;
                if (conflicts.Count > 0)
                {
                    Notify("可能有冲突", $"检测到 {string.Join("、", conflicts)} 正在运行。它们也会修改任务栏，可能和 Desk 冲突，建议先关闭它们的任务栏相关功能。");
                }
                StateChanged?.Invoke();
            });
        });
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
            // The engines report their status here, the Start menu's from an
            // app container.
            Directory.CreateDirectory(Paths.StateDir);
            Acl.GrantAppContainers(Paths.StateDir, FileSystemRights.Modify);
            Acl.SetLowIntegrity(Paths.StateDir);
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
            StateChanged?.Invoke();
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
            watcher.Deleted += changed;
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
                StateChanged?.Invoke();
            }
        });
    }

    // ----------------------------------------------------------------- apply

    /// <summary>Whether the Start menu uses the light variant of the theme.</summary>
    private bool StartMenuLight => _settings.StartMenuScheme switch
    {
        StartMenuScheme.Light => true,
        StartMenuScheme.Dark => false,
        _ => _lightMode,
    };

    /// <summary>Writes what the engine should show right now.</summary>
    private void Apply()
    {
        bool enabled = _settings.Enabled && _theme != null;
        bool maximized = _windows.HasMaximizedWindow;
        bool merge = maximized && _settings.MaximizeMode == MaximizeMode.Merge;

        var taskbarRules = enabled
            ? _theme!.Compile(ThemeTarget.Taskbar, _lightMode, merge, _settings.Overrides)
            : [];
        var monitor = MonitorFor(enabled, merge);
        if (monitor.Enabled)
        {
            // The widgets button (weather, news) sits where the island goes.
            taskbarRules.Add(new RuleSpec(
                "Taskbar.AugmentedEntryPointButton#AugmentedEntryPointButton", ["Visibility=Collapsed"]));
        }
        _runtime.Write(RuntimeWriter.ExplorerFile, enabled, enabled && _settings.ClickThrough, taskbarRules,
            monitor);

        bool startMenu = enabled && _settings.StyleStartMenu;
        var startRules = startMenu
            ? _theme!.Compile(ThemeTarget.StartMenu, StartMenuLight, false, _settings.Overrides)
            : [];
        string? requestedTheme = null;
        if (startMenu && _settings.StartMenuScheme != StartMenuScheme.System)
        {
            // The engine sets the theme on the window content, and every Grid
            // gets it too for parts that set their own; so text and controls
            // switch along with the glass. The Windows mode isn't changed.
            requestedTheme = StartMenuLight ? "Light" : "Dark";
            startRules.Insert(0, new RuleSpec("Grid", [$"RequestedTheme={requestedTheme}"]));
        }
        _runtime.Write(RuntimeWriter.StartMenuFile, startMenu, false, startRules, requestedTheme: requestedTheme);

        _autoHide.SetForced(enabled && maximized && _settings.MaximizeMode == MaximizeMode.AutoHide);
        UpdateTray();
    }

    /// <summary>The system figures island: the user's choices and the theme's colors.</summary>
    private MonitorConfig MonitorFor(bool enabled, bool merge)
    {
        bool show = enabled && _settings.ShowMonitor &&
                    (_settings.MonitorNetwork || _settings.MonitorCpu || _settings.MonitorMemory);
        // On the native full-width bar the island would cover the Start button
        // of a left-aligned taskbar.
        if (merge && !TaskbarCentered())
        {
            show = false;
        }

        string Color(string name, string light, string dark) =>
            _theme?.GetConstant(name, _lightMode, _settings.Overrides) ?? (_lightMode ? light : dark);

        return new MonitorConfig(
            show,
            _settings.MonitorNetwork,
            _settings.MonitorCpu,
            _settings.MonitorMemory,
            "CPU",
            "内存",
            Color("monitorText", "#E4000000", "#FFFFFFFF"),
            Color("monitorSecondaryText", "#8A000000", "#B3FFFFFF"),
            Color("monitorWarning", "#FFC42B1C", "#FFFF99A4"));
    }

    private static bool TaskbarCentered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        return key?.GetValue("TaskbarAl") is not int value || value != 0;
    }

    /// <summary>
    /// Applies shortly, at most every 100 ms: a slider drag updates the
    /// taskbar while it goes on, without rewriting the rules for every pixel.
    /// </summary>
    private void ApplySoon()
    {
        if (!_applySoon.Enabled)
        {
            _applySoon.Start();
        }
    }

    private void Tick()
    {
        _targets?.Tick();
        if (_tray == null)
        {
            return; // Still starting up.
        }

        // Poll quickly while waiting for the taskbar after sign-in or an
        // explorer restart, slowly afterwards.
        bool fast = DateTime.UtcNow < _fastTicksUntil && TaskbarStatus != EngineStatus.Active;
        int interval = fast ? 250 : 2000;
        if (_timer.Interval != interval)
        {
            _timer.Interval = interval;
        }

        var status = (Taskbar, StartMenu);
        if (status != _lastStatus)
        {
            if (status.Taskbar.Status != _lastStatus.Taskbar?.Status ||
                status.StartMenu.Status != _lastStatus.StartMenu?.Status)
            {
                Log.Info($"Status: taskbar {status.Taskbar.Status}{Suffix(status.Taskbar)}, " +
                         $"Start menu {status.StartMenu.Status}{Suffix(status.StartMenu)}");
            }
            if (status.Taskbar.Status == EngineStatus.Active && _lastStatus.Taskbar?.Status != EngineStatus.Active &&
                DateTime.UtcNow - _startedAt < TimeSpan.FromMinutes(5))
            {
                Log.Info($"Taskbar engine loaded and applied {(DateTime.UtcNow - _startedAt).TotalMilliseconds:0} ms after start");
            }
            _lastStatus = status;
            if (status.Taskbar.Status == EngineStatus.Outdated && !_outdatedNoticeShown)
            {
                _outdatedNoticeShown = true;
                Notify("Desk 已更新", "资源管理器里还在运行旧版本的引擎，重启资源管理器后新效果才会完全生效（设置窗口和托盘菜单里都可以重启）。");
            }
            StatusChanged?.Invoke();
        }

        if (DateTime.UtcNow >= _nextStatusCleanup)
        {
            _nextStatusCleanup = DateTime.UtcNow.AddMinutes(10);
            _engineReports.CleanUp((pid, start) =>
                WindowsTargetPlatform.ProcessStartTime(pid) is var actual && actual != 0 &&
                (start == 0 || actual == start));
        }
    }

    private static string Suffix(TargetStatus status) => status.Detail is { } detail ? $" ({detail})" : "";

    private void OnExplorerChanged(ProcessInstance process)
    {
        _fastTicksUntil = DateTime.UtcNow.AddSeconds(60);

        // Explorer restarting again and again right after DeskTap was loaded
        // most likely means the engine crashes it: stop. This runs inside
        // the target manager's tick, which reads the setting again before it
        // would load the engine into the new explorer.
        if (_restartGuard.OnRestart(DateTime.UtcNow) && _settings.Enabled)
        {
            SetEnabled(false, notify: false);
            Notify("Desk 已自动停用", "资源管理器在短时间内重启了多次，为了安全已停用美化。可以在设置窗口里重新启用，并把日志发给开发者。");
        }
    }

    private void SetEnabled(bool enabled, bool notify)
    {
        _settings.Enabled = enabled;
        _settings.Save();
        if (!enabled)
        {
            _targets?.Cancel();
        }
        Apply();
        Tick();
        StateChanged?.Invoke();
        if (notify)
        {
            Notify(enabled ? "美化已启用" : "美化已停用",
                enabled ? "按 Ctrl+Alt+Shift+D 可以再次停用。" : "任务栏和开始菜单已恢复原样。按 Ctrl+Alt+Shift+D 可以重新启用。");
        }
    }

    private static List<string> FindConflicts()
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
        }
        return others;
    }

    // ----------------------------------------------------------------- utils

    private void UpdateTray()
    {
        if (_tray == null)
        {
            return;
        }
        _tray.Icon = _settings.Enabled ? _iconEnabled : _iconDisabled;
        _tray.Text = _settings.Enabled ? "Desk：已启用" : "Desk：已停用";
    }

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
            if (_invoker.IsDisposed)
            {
                return;
            }
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
        _exiting = true;
        _timer.Stop();
        _applySoon.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _showWindowWait.Unregister(null);
        _showWindowEvent.Dispose();
        _restoreWait.Unregister(null);
        _restoreEvent.Dispose();

        if (_windowLoaded)
        {
            Ui.UiHost.CloseWindow();
        }

        // Tell the engine to put everything back. (If this doesn't happen,
        // because Desk.exe crashed or was ended, the engines notice that
        // their owner is gone and do the same.)
        _runtime.Write(RuntimeWriter.ExplorerFile, false, false, []);
        _runtime.Write(RuntimeWriter.StartMenuFile, false, false, []);
        _host.MarkExiting();
        _host.Dispose();
        _autoHide.SetForced(false);
        _autoHide.Restore();
        _settings.Save();

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
