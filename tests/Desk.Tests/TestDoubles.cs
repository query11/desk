namespace Desk.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// Queues background work and UI callbacks so a test decides when each runs,
/// e.g. to disable styling while an attempt is queued or running.
/// </summary>
internal sealed class ManualScheduler : IWorkScheduler
{
    public Queue<Action> Background { get; } = new();

    public Queue<Action> Ui { get; } = new();

    public void RunInBackground(Action work) => Background.Enqueue(work);

    public void Post(Action callback) => Ui.Enqueue(callback);

    public void RunBackground()
    {
        while (Background.TryDequeue(out var work))
        {
            work();
        }
    }

    public void RunUi()
    {
        while (Ui.TryDequeue(out var callback))
        {
            callback();
        }
    }

    /// <summary>Runs everything queued, background first.</summary>
    public void Drain()
    {
        while (Background.Count > 0 || Ui.Count > 0)
        {
            RunBackground();
            RunUi();
        }
    }
}

internal sealed class FakeRemoteLoad : IRemoteLoad
{
    public RemoteLoadState State { get; set; } = RemoteLoadState.Running;

    public bool Disposed { get; private set; }

    public int Polls { get; private set; }

    public RemoteLoadState Poll()
    {
        Polls++;
        return State;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Scripted processes and injection results.</summary>
internal sealed class FakePlatform : ITargetPlatform
{
    public const string CurrentDll = @"C:\Users\u\AppData\Local\Desk\bin\DeskTap-current.dll";
    public const string OldDll = @"C:\Users\u\AppData\Local\Desk\bin\DeskTap-old.dll";

    public ProcessInstance Explorer { get; set; } = new(100, 1);

    public ProcessInstance StartMenu { get; set; }

    /// <summary>Modules per process id.</summary>
    public Dictionary<uint, List<string>> Loaded { get; } = [];

    public int HookCalls { get; private set; }

    public int RemoteCalls { get; private set; }

    public int Terminated { get; private set; }

    /// <summary>What the next hook injections return; Loaded when empty.</summary>
    public Queue<Func<CancellationToken, InjectResult>> HookResults { get; } = new();

    /// <summary>What the next remote loads return; Loaded when empty.</summary>
    public Queue<Func<CancellationToken, InjectResult>> RemoteResults { get; } = new();

    /// <summary>Runs inside the native call, e.g. to change the intent while it is in progress.</summary>
    public Action? DuringCall { get; set; }

    public TaskbarTarget? FindTaskbar() =>
        Explorer.IsNone ? null : new TaskbarTarget(Explorer, 7, new IntPtr(42));

    public ProcessInstance FindStartMenu() => StartMenu;

    public IReadOnlyList<string>? FindLoadedTaps(ProcessInstance process) =>
        Loaded.TryGetValue(process.Id, out var modules) ? modules.ToList() : [];

    public InjectResult InjectWithHook(TaskbarTarget target, TimeSpan timeout, CancellationToken cancel)
    {
        HookCalls++;
        DuringCall?.Invoke();
        var result = HookResults.Count > 0 ? HookResults.Dequeue()(cancel) : InjectResult.Loaded;
        if (result.Outcome == InjectOutcome.Loaded)
        {
            MarkLoaded(target.Process.Id);
        }
        return result;
    }

    public InjectResult StartRemoteLoad(ProcessInstance process, TimeSpan wait, CancellationToken cancel)
    {
        RemoteCalls++;
        DuringCall?.Invoke();
        var result = RemoteResults.Count > 0 ? RemoteResults.Dequeue()(cancel) : InjectResult.Loaded;
        if (result.Outcome == InjectOutcome.Loaded)
        {
            MarkLoaded(process.Id);
        }
        return result;
    }

    public bool Terminate(ProcessInstance process)
    {
        Terminated++;
        return true;
    }

    public void MarkLoaded(uint processId, string dll = CurrentDll)
    {
        if (!Loaded.TryGetValue(processId, out var modules))
        {
            Loaded[processId] = modules = [];
        }
        if (!modules.Contains(dll))
        {
            modules.Add(dll);
        }
    }
}
