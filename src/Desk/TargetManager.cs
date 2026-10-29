using System.Diagnostics;
using static Desk.NativeMethods;

namespace Desk;

/// <summary>
/// Finds explorer's taskbar and the Start menu host, and makes sure DeskTap is
/// loaded into them (again after they restart).
/// </summary>
internal sealed class TargetManager
{
    private enum State
    {
        Unknown,
        Injecting,
        Pending,   // the loader thread was queued but hasn't run yet
        Injected,
        Failed,
    }

    private sealed class Target
    {
        public uint ProcessId;
        public State State = State.Unknown;
        public DateTime NextAttempt = DateTime.MinValue;
        public int Failures;
    }

    private readonly TapInjector _injector;
    private readonly SynchronizationContext _ui;
    private readonly Target _explorer = new();
    private readonly Target _startMenu = new();
    private readonly int _sessionId = Process.GetCurrentProcess().SessionId;
    private bool _startMenuRestarted;

    /// <summary>Raised with the new process id when the taskbar's explorer changed.</summary>
    public event Action<uint>? ExplorerChanged;

    public TargetManager(TapInjector injector, SynchronizationContext ui)
    {
        _injector = injector;
        _ui = ui;
    }

    public bool ExplorerInjected => _explorer.State == State.Injected;

    public bool StartMenuInjected => _startMenu.State == State.Injected;

    /// <summary>The Start menu host hasn't run the loader yet (it's suspended until opened).</summary>
    public bool StartMenuPending => _startMenu.State == State.Pending;

    /// <summary>
    /// Explorer still runs the engine of an older Desk, which only goes away
    /// when explorer restarts.
    /// </summary>
    public bool ExplorerOutdated { get; private set; }

    public void Reset()
    {
        _explorer.State = State.Unknown;
        _explorer.NextAttempt = DateTime.MinValue;
        _explorer.Failures = 0;
        _startMenu.State = State.Unknown;
        _startMenu.NextAttempt = DateTime.MinValue;
        _startMenu.Failures = 0;
    }

    /// <summary>Called periodically on the UI thread.</summary>
    public void Tick(bool injectExplorer, bool injectStartMenu)
    {
        TickExplorer(injectExplorer);
        TickStartMenu(injectStartMenu);
    }

    private void TickExplorer(bool inject)
    {
        IntPtr taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            return;
        }
        uint threadId = GetWindowThreadProcessId(taskbar, out uint processId);
        if (threadId == 0 || processId == 0)
        {
            return;
        }

        if (processId != _explorer.ProcessId)
        {
            bool hadExplorer = _explorer.ProcessId != 0;
            _explorer.ProcessId = processId;
            _explorer.State = State.Unknown;
            _explorer.NextAttempt = DateTime.MinValue;
            _explorer.Failures = 0;
            ExplorerOutdated = false;
            Log.Info($"Taskbar explorer is pid {processId}");
            if (hadExplorer)
            {
                ExplorerChanged?.Invoke(processId);
            }
        }

        if (!inject || !ShouldAttempt(_explorer))
        {
            return;
        }

        if (TapInjector.FindLoadedTap(processId) is { } loaded)
        {
            Log.Info($"DeskTap already loaded in explorer: {loaded}");
            _explorer.State = State.Injected;
            ExplorerOutdated = !IsCurrent(loaded);
            if (ExplorerOutdated)
            {
                Log.Warn("Explorer runs an older DeskTap; it is replaced when explorer restarts");
            }
            return;
        }

        _explorer.State = State.Injecting;
        Task.Run(() =>
        {
            bool ok = false;
            try
            {
                ok = _injector.InjectWithHook(processId, threadId, taskbar, TimeSpan.FromSeconds(10));
            }
            catch (Exception e)
            {
                Log.Error("Injecting into explorer failed", e);
            }
            _ui.Post(_ => Finish(_explorer, processId, ok ? State.Injected : State.Failed, "explorer"), null);
        });
    }

    private void TickStartMenu(bool inject)
    {
        uint processId = FindStartMenuProcess();
        if (processId == 0)
        {
            return;
        }

        if (processId != _startMenu.ProcessId)
        {
            _startMenu.ProcessId = processId;
            _startMenu.State = State.Unknown;
            _startMenu.NextAttempt = DateTime.MinValue;
            _startMenu.Failures = 0;
            Log.Info($"Start menu host is pid {processId}");
        }

        if (_startMenu.State == State.Pending)
        {
            if (TapInjector.FindLoadedTap(processId) != null)
            {
                Log.Info("DeskTap loaded into the Start menu host");
                _startMenu.State = State.Injected;
            }
            return;
        }

        if (!inject || !ShouldAttempt(_startMenu))
        {
            return;
        }

        if (TapInjector.FindLoadedTap(processId) is { } loaded)
        {
            Log.Info($"DeskTap already loaded in the Start menu host: {loaded}");
            if (!IsCurrent(loaded) && !_startMenuRestarted && RestartStartMenu(processId))
            {
                // Windows starts it again right away; the next tick loads
                // the current engine into the new process.
                return;
            }
            _startMenu.State = State.Injected;
            return;
        }

        _startMenu.State = State.Injecting;
        Task.Run(() =>
        {
            State result = State.Failed;
            try
            {
                if (_injector.InjectWithRemoteThread(processId, TimeSpan.FromSeconds(10)))
                {
                    result = State.Injected;
                }
                else if (TapInjector.FindLoadedTap(processId) == null)
                {
                    // Either it failed, or the process is suspended and the
                    // loader runs when the Start menu opens.
                    result = State.Pending;
                }
                else
                {
                    result = State.Injected;
                }
            }
            catch (Exception e)
            {
                Log.Error("Injecting into the Start menu host failed", e);
            }
            _ui.Post(_ => Finish(_startMenu, processId, result, "Start menu host"), null);
        });
    }

    private bool IsCurrent(string loadedPath) =>
        string.Equals(Path.GetFileName(loadedPath), Path.GetFileName(_injector.DllPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ends the Start menu host so that it picks up a newer engine. It's
    /// harmless: Windows restarts it, and it's closed most of the time.
    /// </summary>
    private bool RestartStartMenu(uint processId)
    {
        _startMenuRestarted = true;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            Log.Info("Restarting the Start menu host to load the updated engine");
            process.Kill();
            _startMenu.ProcessId = 0;
            _startMenu.State = State.Unknown;
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not restart the Start menu host: {e.Message}");
            return false;
        }
    }

    private static bool ShouldAttempt(Target target) =>
        target.State is State.Unknown or State.Failed && DateTime.UtcNow >= target.NextAttempt;

    private static void Finish(Target target, uint processId, State result, string name)
    {
        if (target.ProcessId != processId)
        {
            return;
        }
        target.State = result;
        if (result == State.Failed)
        {
            target.Failures++;
            // Back off: 10 s, 20 s, 40 s ... up to 5 minutes.
            double delay = Math.Min(300, 10 * Math.Pow(2, target.Failures - 1));
            target.NextAttempt = DateTime.UtcNow.AddSeconds(delay);
            Log.Warn($"Could not load DeskTap into the {name}; retrying in {delay:0} s");
        }
        else
        {
            Log.Info($"DeskTap {result} for the {name} (pid {processId})");
        }
    }

    private uint FindStartMenuProcess()
    {
        Process[] processes = Process.GetProcessesByName("StartMenuExperienceHost");
        try
        {
            foreach (var process in processes)
            {
                if (process.SessionId == _sessionId)
                {
                    return (uint)process.Id;
                }
            }
            return 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
