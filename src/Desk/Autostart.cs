namespace Desk;

/// <summary>Whether a source was found, is absent, or couldn't be checked.</summary>
internal enum Presence
{
    Missing,
    Present,
    Unknown,
}

/// <summary>The scheduled task as found: whether it's enabled and which program it starts.</summary>
internal sealed record TaskInfo(Presence Presence, bool Enabled = false, string? Command = null, string? Error = null)
{
    public static TaskInfo Missing { get; } = new(Presence.Missing);
}

/// <summary>The Run key value as found.</summary>
internal sealed record RunKeyInfo(Presence Presence, string? Command = null, string? Error = null)
{
    public static RunKeyInfo Missing { get; } = new(Presence.Missing);
}

/// <summary>The outcome of one system change, with the reason when it failed.</summary>
internal sealed record Outcome(bool Ok, string? Error = null)
{
    public static Outcome Success { get; } = new(true);

    public static Outcome Failure(string error) => new(false, error);
}

/// <summary>Where autostart comes from: the scheduled task, or the Run key fallback.</summary>
internal interface IAutostartBackend
{
    /// <summary>The Desk.exe autostart should start.</summary>
    string ExePath { get; }

    TaskInfo QueryTask();

    Outcome CreateTask();

    Outcome DeleteTask();

    RunKeyInfo QueryRunKey();

    Outcome WriteRunKey();

    Outcome DeleteRunKey();

    /// <summary>Whether a command (as found in a source) starts <see cref="ExePath"/>.</summary>
    bool StartsThisExe(string command);
}

internal enum AutostartState
{
    /// <summary>A working source starts this Desk.exe at sign-in.</summary>
    On,

    /// <summary>Nothing starts Desk at sign-in.</summary>
    Off,

    /// <summary>A source exists but doesn't work: disabled, or another Desk.exe.</summary>
    Inactive,

    /// <summary>It couldn't be checked.</summary>
    Unknown,
}

internal enum AutostartSource
{
    Task,
    RunKey,
}

/// <summary>What was actually found, with a reason for anything other than plainly on or off.</summary>
internal sealed record AutostartStatus(AutostartState State, AutostartSource? Source = null, string? Detail = null)
{
    public static AutostartStatus Unchecked { get; } = new(AutostartState.Unknown, Detail: "还没有检查");

    /// <summary>The task and the Run key, judged together.</summary>
    public static AutostartStatus From(TaskInfo task, RunKeyInfo runKey, Func<string, bool> startsThisExe)
    {
        bool taskWorks = task is { Presence: Presence.Present, Enabled: true, Command: { } taskCommand } &&
                         startsThisExe(taskCommand);
        if (taskWorks)
        {
            return new AutostartStatus(AutostartState.On, AutostartSource.Task);
        }
        bool runKeyWorks = runKey is { Presence: Presence.Present, Command: { } runCommand } &&
                           startsThisExe(runCommand);
        if (runKeyWorks)
        {
            return new AutostartStatus(AutostartState.On, AutostartSource.RunKey);
        }

        if (task.Presence == Presence.Unknown || runKey.Presence == Presence.Unknown)
        {
            var errors = new[] { task.Error, runKey.Error }.Where(e => e != null);
            return new AutostartStatus(AutostartState.Unknown, Detail: "无法确认：" + string.Join("；", errors));
        }
        if (task.Presence == Presence.Present)
        {
            return new AutostartStatus(AutostartState.Inactive, AutostartSource.Task,
                !task.Enabled ? "计划任务已被禁用" : "计划任务指向其他位置的 Desk.exe");
        }
        if (runKey.Presence == Presence.Present)
        {
            return new AutostartStatus(AutostartState.Inactive, AutostartSource.RunKey,
                "注册表启动项指向其他位置的 Desk.exe");
        }
        return new AutostartStatus(AutostartState.Off);
    }
}

/// <summary>The result of turning autostart on or off: the state found afterwards, and why it failed.</summary>
internal sealed record AutostartResult(bool Success, AutostartStatus Status, string? Error, long Ticket,
    bool Configured = false, string? Warning = null);

/// <summary>
/// Starts Desk when the user signs in. A scheduled task with a logon trigger
/// runs right away, while programs in the Run key are started by Explorer
/// only after a delay, so the task is preferred and the Run key is the
/// fallback when the task can't be created.
/// </summary>
/// <remarks>
/// Every change is checked by looking again: turning it on succeeds when a
/// source that starts this Desk.exe is found afterwards (the task, or else
/// the Run key); turning it off succeeds when neither source is left. A
/// query that fails is "unknown", never "off". Calls are slow (schtasks.exe)
/// and serialized; each result carries a ticket so the caller can drop one
/// that finished before a newer one.
/// </remarks>
internal sealed class Autostart
{
    public const string Argument = "--autostart";

    private readonly IAutostartBackend _backend;
    private readonly object _lock = new();
    private long _ticket;
    private bool _userChose;

    public Autostart(IAutostartBackend backend)
    {
        _backend = backend;
    }

    /// <summary>Looks at both sources without changing anything.</summary>
    public AutostartStatus Check() =>
        AutostartStatus.From(SafeQueryTask(), SafeQueryRunKey(), _backend.StartsThisExe);

    /// <summary>A check that is ordered with the changes (for the settings window).</summary>
    public AutostartResult Inspect()
    {
        lock (_lock)
        {
            return new AutostartResult(true, Check(), null, ++_ticket);
        }
    }

    /// <summary>
    /// The check at start-up: on by default the first time; kept pointing at
    /// this Desk.exe and using the task when it is on. A task the user
    /// disabled is their choice and left alone. Does nothing that a user's
    /// choice made since start-up would contradict.
    /// </summary>
    public AutostartResult Refresh(bool configured)
    {
        lock (_lock)
        {
            long ticket = ++_ticket;
            if (_userChose)
            {
                return new AutostartResult(true, Check(), null, ticket);
            }
            if (!configured)
            {
                Log.Info("Turning on autostart (first run)");
                return EnableCore(ticket) with { Configured = true };
            }

            var task = SafeQueryTask();
            var runKey = SafeQueryRunKey();
            var status = AutostartStatus.From(task, runKey, _backend.StartsThisExe);
            bool moved = (task.Presence == Presence.Present && task.Enabled && !Starts(task.Command)) ||
                         (task.Presence == Presence.Missing && runKey.Presence == Presence.Present &&
                          !Starts(runKey.Command));
            bool fallbackOnly = status is { State: AutostartState.On, Source: AutostartSource.RunKey } &&
                                task.Presence == Presence.Missing;
            bool both = status is { State: AutostartState.On, Source: AutostartSource.Task } &&
                        runKey.Presence == Presence.Present;
            if (moved || fallbackOnly || both)
            {
                // Desk.exe moved, autostart still uses the Run key, or both
                // would start it.
                Log.Info("Updating autostart");
                return EnableCore(ticket);
            }
            return new AutostartResult(true, status, null, ticket);
        }
    }

    /// <summary>Turns autostart on or off as the user asked, and checks the result.</summary>
    public AutostartResult Set(bool enabled)
    {
        lock (_lock)
        {
            _userChose = true;
            long ticket = ++_ticket;
            var result = enabled ? EnableCore(ticket) : DisableCore(ticket);
            return result with { Configured = true };
        }
    }

    private AutostartResult EnableCore(long ticket)
    {
        var created = Safe(_backend.CreateTask);
        var task = SafeQueryTask();
        if (created.Ok && task.Presence == Presence.Present && task.Enabled && Starts(task.Command))
        {
            var removed = Safe(_backend.DeleteRunKey);
            var status = AutostartStatus.From(task, SafeQueryRunKey(), _backend.StartsThisExe);
            Log.Info("Autostart on: scheduled task");
            return new AutostartResult(true, status, null, ticket,
                Warning: removed.Ok ? null : $"旧的注册表启动项没能删除（{removed.Error}）");
        }

        string taskError = created.Ok
            ? task.Presence == Presence.Unknown ? $"计划任务无法确认（{task.Error}）" : "计划任务创建后没有找到可用的任务"
            : $"计划任务没能创建（{created.Error}）";
        Log.Warn($"Autostart: {taskError}; using the Run key");

        var written = Safe(_backend.WriteRunKey);
        var runKey = SafeQueryRunKey();
        var final = AutostartStatus.From(task, runKey, _backend.StartsThisExe);
        if (written.Ok && final.State == AutostartState.On)
        {
            Log.Info($"Autostart on: {final.Source}");
            return new AutostartResult(true, final, null, ticket, Warning: taskError);
        }

        string error = written.Ok
            ? $"{taskError}；注册表启动项写入后没有找到"
            : $"{taskError}；注册表启动项也没能写入（{written.Error}）";
        Log.Warn($"Autostart could not be turned on: {error}");
        return new AutostartResult(false, final, error, ticket);
    }

    private AutostartResult DisableCore(long ticket)
    {
        var errors = new List<string>();
        var task = SafeQueryTask();
        if (task.Presence != Presence.Missing)
        {
            var deleted = Safe(_backend.DeleteTask);
            if (!deleted.Ok)
            {
                errors.Add($"计划任务没能删除（{deleted.Error}）");
            }
        }
        var removed = Safe(_backend.DeleteRunKey);
        if (!removed.Ok)
        {
            errors.Add($"注册表启动项没能删除（{removed.Error}）");
        }

        task = SafeQueryTask();
        var runKey = SafeQueryRunKey();
        var status = AutostartStatus.From(task, runKey, _backend.StartsThisExe);
        bool gone = task.Presence == Presence.Missing && runKey.Presence == Presence.Missing;
        if (gone)
        {
            Log.Info("Autostart off");
            return new AutostartResult(true, status, null, ticket);
        }

        if (task.Presence != Presence.Missing && errors.Count == 0)
        {
            errors.Add(task.Presence == Presence.Unknown ? $"计划任务无法确认（{task.Error}）" : "计划任务仍然存在");
        }
        if (runKey.Presence != Presence.Missing && !errors.Any(e => e.StartsWith("注册表")))
        {
            errors.Add(runKey.Presence == Presence.Unknown ? $"注册表启动项无法确认（{runKey.Error}）" : "注册表启动项仍然存在");
        }
        string error = string.Join("；", errors);
        Log.Warn($"Autostart could not be turned off: {error}");
        return new AutostartResult(false, status, error, ticket);
    }

    private bool Starts(string? command) => command != null && _backend.StartsThisExe(command);

    private TaskInfo SafeQueryTask()
    {
        try
        {
            return _backend.QueryTask();
        }
        catch (Exception e)
        {
            return new TaskInfo(Presence.Unknown, Error: e.Message);
        }
    }

    private RunKeyInfo SafeQueryRunKey()
    {
        try
        {
            return _backend.QueryRunKey();
        }
        catch (Exception e)
        {
            return new RunKeyInfo(Presence.Unknown, Error: e.Message);
        }
    }

    private static Outcome Safe(Func<Outcome> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception e)
        {
            return Outcome.Failure(e.Message);
        }
    }
}
