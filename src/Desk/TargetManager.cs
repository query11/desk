namespace Desk;

/// <summary>What the target manager is doing for one target process.</summary>
internal enum InjectionPhase
{
    /// <summary>Nothing in progress; the next tick checks or loads the engine.</summary>
    Idle,

    /// <summary>An attempt runs in the background.</summary>
    Injecting,

    /// <summary>A loader thread was created in the target and hasn't run yet (Start menu suspended).</summary>
    Waiting,

    /// <summary>DeskTap.dll is loaded in the target.</summary>
    Loaded,

    /// <summary>The last attempt failed; retried at <see cref="TargetSnapshot.NextAttempt"/>.</summary>
    Failed,
}

/// <summary>An immutable view of one target for the status display.</summary>
internal sealed record TargetSnapshot(
    ProcessInstance Process,
    InjectionPhase Phase,
    InjectResult? LastFailure,
    int Failures,
    DateTime NextAttempt,
    bool CurrentLoaded,
    bool Outdated,
    DateTime PhaseSince);

/// <summary>
/// Finds explorer's taskbar and the Start menu host, and makes sure DeskTap is
/// loaded into them (again after they restart).
/// </summary>
/// <remarks>
/// Every decision to start an attempt reads the intent (enabled, crash
/// protection) at that moment, after the events of the same tick ran, never
/// a value captured earlier. Each attempt has a number; a result only counts
/// when it belongs to the current attempt for the same process instance, so
/// cancelled, superseded and late results can't overwrite newer state. A
/// loader thread that already exists in a target is tracked until it ends,
/// whatever the intent, so it is neither started twice nor its memory freed
/// while it may still read it. All members run on the UI thread.
/// </remarks>
internal sealed class TargetManager
{
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RemoteWait = TimeSpan.FromSeconds(5);

    private sealed class Target(string name)
    {
        public readonly string Name = name;
        public ProcessInstance Process;
        public InjectionPhase Phase = InjectionPhase.Idle;
        public DateTime PhaseSince;
        public int Attempt;
        public int BusyAttempt;
        public CancellationTokenSource? Cancel;
        public IRemoteLoad? RemoteLoad;
        public bool Exited;
        public DateTime NextAttempt = DateTime.MinValue;
        public int Failures;
        public InjectResult? LastFailure;
        public IReadOnlyList<string> Modules = [];
    }

    private readonly ITargetPlatform _platform;
    private readonly IWorkScheduler _scheduler;
    private readonly TimeProvider _time;
    private readonly string _currentDll;
    private readonly Func<bool> _explorerWanted;
    private readonly Func<bool> _startMenuWanted;
    private readonly Target _explorer = new("explorer");
    private readonly Target _startMenu = new("Start menu host");
    private bool _startMenuRestarted;
    private bool _ticking;
    private bool _tickAgain;

    /// <summary>Raised with the new process when the taskbar's explorer changed (not for the first one).</summary>
    public event Action<ProcessInstance>? ExplorerChanged;

    /// <summary>A target's process or phase changed.</summary>
    public event Action? Changed;

    /// <param name="currentDll">The deployed DeskTap.dll; a loaded copy with another name is outdated.</param>
    /// <param name="explorerWanted">Whether explorer should get the engine, read when an attempt would start.</param>
    /// <param name="startMenuWanted">The same for the Start menu host.</param>
    public TargetManager(ITargetPlatform platform, IWorkScheduler scheduler, TimeProvider time, string currentDll,
        Func<bool> explorerWanted, Func<bool> startMenuWanted)
    {
        _platform = platform;
        _scheduler = scheduler;
        _time = time;
        _currentDll = currentDll;
        _explorerWanted = explorerWanted;
        _startMenuWanted = startMenuWanted;
    }

    public TargetSnapshot Explorer => Snapshot(_explorer);

    public TargetSnapshot StartMenu => Snapshot(_startMenu);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Forgets failures and re-checks the loaded engines on the next tick
    /// ("re-apply"). Running attempts and pending loader threads are kept, so
    /// nothing is loaded twice.
    /// </summary>
    public void Reset()
    {
        foreach (var target in new[] { _explorer, _startMenu })
        {
            target.Failures = 0;
            target.NextAttempt = DateTime.MinValue;
            target.LastFailure = null;
            if (target.Phase is InjectionPhase.Failed or InjectionPhase.Loaded)
            {
                SetPhase(target, InjectionPhase.Idle);
            }
        }
    }

    /// <summary>
    /// Calls off attempts that haven't reached the target yet; for when
    /// styling was just turned off. The next tick does the same by itself.
    /// </summary>
    public void Cancel()
    {
        CancelAttempt(_explorer);
        CancelAttempt(_startMenu);
    }

    /// <summary>Called periodically on the UI thread.</summary>
    public void Tick()
    {
        // An event handler (crash protection) may change the settings and
        // tick again from inside a tick; run that afterwards instead.
        if (_ticking)
        {
            _tickAgain = true;
            return;
        }
        _ticking = true;
        try
        {
            do
            {
                _tickAgain = false;
                TickExplorer();
                TickStartMenu();
            } while (_tickAgain);
        }
        finally
        {
            _ticking = false;
        }
    }

    private void TickExplorer()
    {
        var taskbar = _platform.FindTaskbar();
        if (taskbar == null)
        {
            return;
        }

        if (taskbar.Process != _explorer.Process)
        {
            bool hadExplorer = !_explorer.Process.IsNone;
            SwitchProcess(_explorer, taskbar.Process);
            Log.Info($"Taskbar explorer is pid {taskbar.Process}");
            if (hadExplorer)
            {
                ExplorerChanged?.Invoke(taskbar.Process);
            }
        }

        // Read now: the handler above may just have turned styling off.
        if (!_explorerWanted())
        {
            CancelAttempt(_explorer);
            return;
        }
        if (!ShouldAttempt(_explorer) || !CheckLoaded(_explorer))
        {
            return;
        }

        StartAttempt(_explorer, cancel => _platform.InjectWithHook(taskbar, HookTimeout, cancel));
    }

    private void TickStartMenu()
    {
        var process = _platform.FindStartMenu();
        if (process.IsNone)
        {
            return;
        }
        if (process != _startMenu.Process)
        {
            SwitchProcess(_startMenu, process);
            Log.Info($"Start menu host is pid {process}");
        }

        // A loader thread that already exists is followed up whatever the
        // intent: it frees its memory when done and must not be doubled.
        if (_startMenu.RemoteLoad != null)
        {
            PollRemoteLoad(_startMenu);
        }

        if (!_startMenuWanted())
        {
            CancelAttempt(_startMenu);
            return;
        }
        if (!ShouldAttempt(_startMenu) || !CheckLoaded(_startMenu))
        {
            return;
        }

        StartAttempt(_startMenu, cancel => _platform.StartRemoteLoad(process, RemoteWait, cancel));
    }

    /// <summary>
    /// Looks for an engine already in the process. Returns true when an
    /// attempt should be made.
    /// </summary>
    private bool CheckLoaded(Target target)
    {
        var modules = _platform.FindLoadedTaps(target.Process);
        if (modules == null)
        {
            Fail(target, InjectResult.Fail(InjectStage.QueryModules));
            return false;
        }
        if (modules.Count == 0)
        {
            return true;
        }

        target.Modules = modules;
        bool current = modules.Any(IsCurrent);
        Log.Info($"DeskTap already loaded in the {target.Name}: {string.Join(", ", modules)}");
        if (!current && target == _startMenu && !_startMenuRestarted)
        {
            // The Start menu host is closed most of the time and Windows
            // starts it again right away; the next tick loads the current
            // engine into the new process. Only once per run, so that a
            // failure can't turn into a restart loop.
            _startMenuRestarted = true;
            Log.Info("Restarting the Start menu host to load the updated engine");
            if (_platform.Terminate(target.Process))
            {
                return false;
            }
            Log.Warn("Could not restart the Start menu host");
        }
        else if (!current)
        {
            Log.Warn($"The {target.Name} runs an older DeskTap; it is replaced when the process restarts");
        }
        SetPhase(target, InjectionPhase.Loaded);
        return false;
    }

    private bool IsCurrent(string loadedPath) =>
        string.Equals(Path.GetFileName(loadedPath), Path.GetFileName(_currentDll), StringComparison.OrdinalIgnoreCase);

    private bool ShouldAttempt(Target target) =>
        !target.Exited &&
        target.BusyAttempt == 0 &&
        target.RemoteLoad == null &&
        (target.Phase == InjectionPhase.Idle ||
         (target.Phase == InjectionPhase.Failed && Now >= target.NextAttempt));

    private void StartAttempt(Target target, Func<CancellationToken, InjectResult> work)
    {
        int attempt = ++target.Attempt;
        var process = target.Process;
        var cancel = new CancellationTokenSource();
        target.Cancel?.Dispose();
        target.Cancel = cancel;
        target.BusyAttempt = attempt;
        SetPhase(target, InjectionPhase.Injecting);

        var token = cancel.Token;
        _scheduler.RunInBackground(() =>
        {
            InjectResult result;
            if (token.IsCancellationRequested)
            {
                result = InjectResult.Cancelled;
            }
            else
            {
                try
                {
                    result = work(token);
                }
                catch (Exception e)
                {
                    Log.Error($"Loading DeskTap into the {target.Name} failed", e);
                    result = InjectResult.Fail(InjectStage.Exception, e.HResult);
                }
            }
            _scheduler.Post(() => Complete(target, process, attempt, result));
        });
    }

    private void Complete(Target target, ProcessInstance process, int attempt, InjectResult result)
    {
        if (target.Process != process)
        {
            // The process is gone or replaced; a loader thread in it doesn't matter.
            result.Pending?.Dispose();
            return;
        }
        if (target.BusyAttempt == attempt)
        {
            target.BusyAttempt = 0;
        }

        if (attempt != target.Attempt)
        {
            // Cancelled or superseded. The outcome is ignored, but a loader
            // thread it created is real and stays tracked.
            if (result.Pending != null)
            {
                Adopt(target, result.Pending);
            }
            Log.Info($"Ignoring a stale result for the {target.Name}: {result.Outcome}");
            return;
        }

        switch (result.Outcome)
        {
            case InjectOutcome.Loaded:
                Succeed(target);
                break;
            case InjectOutcome.Waiting when result.Pending != null:
                Adopt(target, result.Pending);
                Log.Info($"The {target.Name} (pid {process}) hasn't run the loader yet; waiting for it");
                break;
            case InjectOutcome.Cancelled:
                SetPhase(target, InjectionPhase.Idle);
                break;
            default:
                Fail(target, result);
                break;
        }
    }

    private void Adopt(Target target, IRemoteLoad pending)
    {
        if (target.RemoteLoad != null && !ReferenceEquals(target.RemoteLoad, pending))
        {
            target.RemoteLoad.Dispose();
        }
        target.RemoteLoad = pending;
        SetPhase(target, InjectionPhase.Waiting);
    }

    private void PollRemoteLoad(Target target)
    {
        var load = target.RemoteLoad!;
        RemoteLoadState state;
        try
        {
            state = load.Poll();
        }
        catch (Exception e)
        {
            Log.Error($"Checking the loader thread in the {target.Name} failed", e);
            state = RemoteLoadState.Failed;
        }

        switch (state)
        {
            case RemoteLoadState.Running:
                return;
            case RemoteLoadState.Succeeded:
                target.RemoteLoad = null;
                load.Dispose();
                Succeed(target);
                return;
            case RemoteLoadState.Failed:
                target.RemoteLoad = null;
                load.Dispose();
                Fail(target, InjectResult.Fail(InjectStage.LoadLibrary));
                return;
            default:
                // Gone; the next process instance gets a fresh start.
                target.RemoteLoad = null;
                load.Dispose();
                target.Exited = true;
                SetPhase(target, InjectionPhase.Idle);
                return;
        }
    }

    private void Succeed(Target target)
    {
        target.Failures = 0;
        target.LastFailure = null;
        // Which build got loaded is checked again on the next tick.
        target.Modules = _platform.FindLoadedTaps(target.Process) ?? [];
        SetPhase(target, InjectionPhase.Loaded);
        Log.Info($"DeskTap loaded into the {target.Name} (pid {target.Process})");
    }

    private void Fail(Target target, InjectResult result)
    {
        target.Failures++;
        target.LastFailure = result;
        // Back off: 10 s, 20 s, 40 s ... up to 5 minutes.
        double delay = Math.Min(300, 10 * Math.Pow(2, Math.Min(target.Failures, 10) - 1));
        target.NextAttempt = Now.AddSeconds(delay);
        SetPhase(target, InjectionPhase.Failed);
        Log.Warn($"Could not load DeskTap into the {target.Name} (pid {target.Process}): {result.Describe()}; " +
                 $"retrying in {delay:0} s");
    }

    private void CancelAttempt(Target target)
    {
        if (target.Phase != InjectionPhase.Injecting)
        {
            return;
        }
        // The worker unhooks or stops before creating a thread; whatever it
        // reports for this attempt number is ignored from now on.
        target.Cancel?.Cancel();
        target.Attempt++;
        SetPhase(target, InjectionPhase.Idle);
        Log.Info($"Cancelled loading DeskTap into the {target.Name}");
    }

    private void SwitchProcess(Target target, ProcessInstance process)
    {
        target.Cancel?.Cancel();
        target.Attempt++;
        target.BusyAttempt = 0;
        // The thread belongs to the old process; its memory goes with it.
        target.RemoteLoad?.Dispose();
        target.RemoteLoad = null;
        target.Process = process;
        target.Exited = false;
        target.Failures = 0;
        target.LastFailure = null;
        target.NextAttempt = DateTime.MinValue;
        target.Modules = [];
        SetPhase(target, InjectionPhase.Idle);
    }

    private void SetPhase(Target target, InjectionPhase phase)
    {
        if (target.Phase == phase)
        {
            return;
        }
        target.Phase = phase;
        target.PhaseSince = Now;
        Changed?.Invoke();
    }

    private TargetSnapshot Snapshot(Target target) => new(
        target.Process,
        target.Phase,
        target.LastFailure,
        target.Failures,
        target.NextAttempt,
        target.Modules.Any(IsCurrent),
        target.Modules.Count > 0 && !target.Modules.Any(IsCurrent),
        target.PhaseSince);
}
