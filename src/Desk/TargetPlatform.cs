namespace Desk;

/// <summary>
/// One run of a process: the id together with its creation time, so that a
/// reused process id is a different instance.
/// </summary>
internal readonly record struct ProcessInstance(uint Id, long StartTime)
{
    public bool IsNone => Id == 0;

    public override string ToString() => Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The explorer that owns the taskbar, and the taskbar window's thread.</summary>
internal sealed record TaskbarTarget(ProcessInstance Process, uint ThreadId, IntPtr Window);

/// <summary>Where loading DeskTap.dll into a process stopped.</summary>
internal enum InjectStage
{
    None,
    QueryModules,
    OpenProcess,
    AllocateMemory,
    WriteMemory,
    CreateThread,
    LoadLibrary,
    Hook,
    Timeout,
    Exception,
}

internal enum InjectOutcome
{
    /// <summary>DeskTap.dll is loaded in the process.</summary>
    Loaded,

    /// <summary>The loader thread exists but hasn't finished (the process is suspended or busy).</summary>
    Waiting,

    /// <summary>Loading failed; worth retrying later.</summary>
    Failed,

    /// <summary>Called off before anything was done in the target.</summary>
    Cancelled,
}

/// <summary>The result of one attempt to load DeskTap.dll into a target.</summary>
internal sealed record InjectResult(InjectOutcome Outcome, InjectStage Stage = InjectStage.None, int Error = 0,
    IRemoteLoad? Pending = null)
{
    public static InjectResult Loaded { get; } = new(InjectOutcome.Loaded);

    public static InjectResult Cancelled { get; } = new(InjectOutcome.Cancelled);

    public static InjectResult Fail(InjectStage stage, int error = 0) => new(InjectOutcome.Failed, stage, error);

    public static InjectResult Wait(IRemoteLoad pending) => new(InjectOutcome.Waiting, Pending: pending);

    /// <summary>For logs and the settings window, e.g. "CreateThread (error 5)".</summary>
    public string Describe() => Error != 0 ? $"{Stage} (error {Error})" : Stage.ToString();
}

internal enum RemoteLoadState
{
    /// <summary>The loader thread hasn't finished yet.</summary>
    Running,

    /// <summary>LoadLibrary returned a module.</summary>
    Succeeded,

    /// <summary>LoadLibrary failed in the target.</summary>
    Failed,

    /// <summary>The target process is gone.</summary>
    ProcessExited,
}

/// <summary>
/// A LoadLibrary thread created in a target that hasn't finished. It owns the
/// handles and the path in the target's memory, which the thread may still
/// read; disposing releases what can be released safely.
/// </summary>
internal interface IRemoteLoad : IDisposable
{
    /// <summary>Checks the thread without waiting.</summary>
    RemoteLoadState Poll();
}

/// <summary>
/// What the target manager needs from Windows. The real implementation is
/// <see cref="WindowsTargetPlatform"/>; tests use a scripted one.
/// </summary>
internal interface ITargetPlatform
{
    /// <summary>The explorer with the taskbar, or null when there is no taskbar right now.</summary>
    TaskbarTarget? FindTaskbar();

    /// <summary>The Start menu host of this session, or none.</summary>
    ProcessInstance FindStartMenu();

    /// <summary>Paths of the DeskTap builds loaded in the process; null when the process can't be inspected.</summary>
    IReadOnlyList<string>? FindLoadedTaps(ProcessInstance process);

    /// <summary>Loads the DLL with a window hook (explorer). Unhooks early when cancelled.</summary>
    InjectResult InjectWithHook(TaskbarTarget target, TimeSpan timeout, CancellationToken cancel);

    /// <summary>
    /// Starts a remote LoadLibrary thread (Start menu host) and waits up to
    /// <paramref name="wait"/>. Cancellation is honored until the thread is
    /// created; after that the result carries the pending load.
    /// </summary>
    InjectResult StartRemoteLoad(ProcessInstance process, TimeSpan wait, CancellationToken cancel);

    /// <summary>Ends the process (used once to replace an outdated Start menu engine).</summary>
    bool Terminate(ProcessInstance process);
}

/// <summary>Runs injection work off the UI thread and brings results back to it.</summary>
internal interface IWorkScheduler
{
    void RunInBackground(Action work);

    void Post(Action callback);
}

/// <summary>Thread pool for the work, the UI thread's synchronization context for the results.</summary>
internal sealed class ThreadPoolScheduler(SynchronizationContext ui) : IWorkScheduler
{
    public void RunInBackground(Action work) => Task.Run(work);

    public void Post(Action callback) => ui.Post(_ => callback(), null);
}
