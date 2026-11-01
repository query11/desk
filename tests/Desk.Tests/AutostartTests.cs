using Xunit;

namespace Desk.Tests;

/// <summary>A task scheduler and Run key that can fail on purpose.</summary>
internal sealed class FakeAutostartBackend : IAutostartBackend
{
    public const string Exe = @"C:\Tools\Desk\Desk.exe";

    public string ExePath => Exe;

    public TaskInfo Task { get; set; } = TaskInfo.Missing;

    public RunKeyInfo RunKey { get; set; } = RunKeyInfo.Missing;

    public string? CreateFails { get; set; }

    public string? DeleteTaskFails { get; set; }

    public string? WriteRunKeyFails { get; set; }

    public string? DeleteRunKeyFails { get; set; }

    public string? QueryTaskFails { get; set; }

    public string? QueryRunKeyFails { get; set; }

    /// <summary>schtasks says it created the task, but it comes out disabled (a policy, say).</summary>
    public bool CreatesDisabledTask { get; set; }

    public List<string> Calls { get; } = [];

    public TaskInfo QueryTask() =>
        QueryTaskFails != null ? new TaskInfo(Presence.Unknown, Error: QueryTaskFails) : Task;

    public Outcome CreateTask()
    {
        Calls.Add("create");
        if (CreateFails != null)
        {
            return Outcome.Failure(CreateFails);
        }
        Task = new TaskInfo(Presence.Present, !CreatesDisabledTask, $"\"{Exe}\"");
        return Outcome.Success;
    }

    public Outcome DeleteTask()
    {
        Calls.Add("delete");
        if (DeleteTaskFails != null)
        {
            return Outcome.Failure(DeleteTaskFails);
        }
        Task = TaskInfo.Missing;
        return Outcome.Success;
    }

    public RunKeyInfo QueryRunKey() =>
        QueryRunKeyFails != null ? new RunKeyInfo(Presence.Unknown, Error: QueryRunKeyFails) : RunKey;

    public Outcome WriteRunKey()
    {
        Calls.Add("write");
        if (WriteRunKeyFails != null)
        {
            return Outcome.Failure(WriteRunKeyFails);
        }
        RunKey = new RunKeyInfo(Presence.Present, $"\"{Exe}\" --autostart");
        return Outcome.Success;
    }

    public Outcome DeleteRunKey()
    {
        Calls.Add("unwrite");
        if (DeleteRunKeyFails != null)
        {
            return Outcome.Failure(DeleteRunKeyFails);
        }
        RunKey = RunKeyInfo.Missing;
        return Outcome.Success;
    }

    public bool StartsThisExe(string command) =>
        string.Equals(WindowsAutostartBackend.ExtractPath(command), Exe, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the settings page shows is what is really set up.</summary>
public class AutostartTests
{
    private readonly FakeAutostartBackend _backend = new();

    private Autostart Create() => new(_backend);

    [Fact]
    public void TurningOnUsesTheTask()
    {
        _backend.RunKey = new RunKeyInfo(Presence.Present, $"\"{FakeAutostartBackend.Exe}\" --autostart");
        var result = Create().Set(true);
        Assert.True(result.Success);
        Assert.Equal(new AutostartStatus(AutostartState.On, AutostartSource.Task), result.Status);
        Assert.Equal(Presence.Missing, _backend.RunKey.Presence); // The fallback is removed.
        Assert.Null(result.Warning);
    }

    [Fact]
    public void ATaskThatCantBeCreatedFallsBackToTheRunKey()
    {
        _backend.CreateFails = "schtasks /Create 返回 1：拒绝访问";
        var result = Create().Set(true);
        Assert.True(result.Success);
        Assert.Equal(AutostartSource.RunKey, result.Status.Source);
        Assert.Contains("拒绝访问", result.Warning);
    }

    [Fact]
    public void ACreatedButDisabledTaskIsNotTakenForOn()
    {
        _backend.CreatesDisabledTask = true;
        var result = Create().Set(true);
        Assert.True(result.Success);
        Assert.Equal(AutostartSource.RunKey, result.Status.Source);
        Assert.Contains("没有找到可用的任务", result.Warning);
    }

    [Fact]
    public void BothFailingIsReportedWithTheRealState()
    {
        _backend.CreateFails = "schtasks /Create 返回 1";
        _backend.WriteRunKeyFails = "Access to the registry key is denied.";
        var result = Create().Set(true);
        Assert.False(result.Success);
        Assert.Equal(AutostartState.Off, result.Status.State);
        Assert.Contains("schtasks /Create", result.Error);
        Assert.Contains("registry key is denied", result.Error);
    }

    [Fact]
    public void ATransientFailureCanBeRetried()
    {
        var autostart = Create();
        _backend.CreateFails = "busy";
        _backend.WriteRunKeyFails = "busy";
        Assert.False(autostart.Set(true).Success);

        _backend.CreateFails = null;
        _backend.WriteRunKeyFails = null;
        var result = autostart.Set(true);
        Assert.True(result.Success);
        Assert.Equal(AutostartSource.Task, result.Status.Source);
    }

    [Fact]
    public void TurningOffRemovesBoth()
    {
        _backend.Task = new TaskInfo(Presence.Present, true, $"\"{FakeAutostartBackend.Exe}\"");
        _backend.RunKey = new RunKeyInfo(Presence.Present, $"\"{FakeAutostartBackend.Exe}\" --autostart");
        var result = Create().Set(false);
        Assert.True(result.Success);
        Assert.Equal(AutostartState.Off, result.Status.State);
    }

    [Fact]
    public void ATaskThatCantBeDeletedIsAFailure()
    {
        _backend.Task = new TaskInfo(Presence.Present, true, $"\"{FakeAutostartBackend.Exe}\"");
        _backend.DeleteTaskFails = "schtasks /Delete 返回 1：拒绝访问";
        var result = Create().Set(false);
        Assert.False(result.Success);
        Assert.Equal(AutostartState.On, result.Status.State); // Still starts Desk.
        Assert.Contains("计划任务没能删除", result.Error);
    }

    [Fact]
    public void ARunKeyThatCantBeDeletedIsAFailure()
    {
        _backend.RunKey = new RunKeyInfo(Presence.Present, $"\"{FakeAutostartBackend.Exe}\" --autostart");
        _backend.DeleteRunKeyFails = "Access denied";
        var result = Create().Set(false);
        Assert.False(result.Success);
        Assert.Equal(AutostartState.On, result.Status.State);
        Assert.Contains("注册表启动项没能删除", result.Error);
    }

    [Fact]
    public void AFailedQueryIsUnknownNotOff()
    {
        _backend.QueryTaskFails = "schtasks /Query 返回 -1：超时";
        var status = Create().Check();
        Assert.Equal(AutostartState.Unknown, status.State);
        Assert.Contains("超时", status.Detail);

        var result = Create().Set(false);
        Assert.False(result.Success);
        Assert.Equal(AutostartState.Unknown, result.Status.State);
    }

    [Fact]
    public void TurningOffWhenNothingIsSetUpSucceeds()
    {
        var result = Create().Set(false);
        Assert.True(result.Success);
        Assert.DoesNotContain("delete", _backend.Calls);
    }

    [Fact]
    public void ADisabledTaskIsInactiveAndLeftAlone()
    {
        _backend.Task = new TaskInfo(Presence.Present, false, $"\"{FakeAutostartBackend.Exe}\"");
        var result = Create().Refresh(configured: true);
        Assert.Equal(AutostartState.Inactive, result.Status.State);
        Assert.Contains("已被禁用", result.Status.Detail);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public void ATaskForAnOldLocationIsUpdated()
    {
        _backend.Task = new TaskInfo(Presence.Present, true, @"""D:\Old\Desk.exe""");
        Assert.Equal(AutostartState.Inactive, Create().Check().State);
        var result = Create().Refresh(configured: true);
        Assert.True(result.Success);
        Assert.Equal(new AutostartStatus(AutostartState.On, AutostartSource.Task), result.Status);
        Assert.Contains("create", _backend.Calls);
    }

    [Fact]
    public void ARunKeyAloneIsMovedToTheTask()
    {
        _backend.RunKey = new RunKeyInfo(Presence.Present, $"\"{FakeAutostartBackend.Exe}\" --autostart");
        var result = Create().Refresh(configured: true);
        Assert.Equal(AutostartSource.Task, result.Status.Source);
        Assert.Equal(Presence.Missing, _backend.RunKey.Presence);
    }

    [Fact]
    public void WithBothSourcesTheRunKeyGoes()
    {
        _backend.Task = new TaskInfo(Presence.Present, true, $"\"{FakeAutostartBackend.Exe}\"");
        _backend.RunKey = new RunKeyInfo(Presence.Present, $"\"{FakeAutostartBackend.Exe}\" --autostart");
        var result = Create().Refresh(configured: true);
        Assert.Equal(new AutostartStatus(AutostartState.On, AutostartSource.Task), result.Status);
        Assert.Equal(Presence.Missing, _backend.RunKey.Presence);
    }

    [Fact]
    public void TheFirstRunTurnsItOn()
    {
        var result = Create().Refresh(configured: false);
        Assert.True(result.Configured);
        Assert.Equal(AutostartState.On, result.Status.State);
    }

    [Fact]
    public void AUserChoiceBeforeTheStartUpCheckWins()
    {
        var autostart = Create();
        var off = autostart.Set(false); // The user was quicker than the delayed check.
        var check = autostart.Refresh(configured: false);
        Assert.Equal(AutostartState.Off, check.Status.State);
        Assert.DoesNotContain("create", _backend.Calls);
        Assert.True(check.Ticket > off.Ticket);
    }

    [Fact]
    public void ResultsAreOrdered()
    {
        var autostart = Create();
        var first = autostart.Set(true);
        var second = autostart.Inspect();
        var third = autostart.Set(false);
        Assert.True(first.Ticket < second.Ticket && second.Ticket < third.Ticket);
    }

    [Fact]
    public void BackendExceptionsBecomeFailures()
    {
        var backend = new ThrowingBackend();
        var result = new Autostart(backend).Set(true);
        Assert.False(result.Success);
        Assert.Equal(AutostartState.Unknown, result.Status.State);
        Assert.Contains("boom", result.Error);
    }

    [Theory]
    [InlineData(@"""C:\Tools\Desk\Desk.exe"" --autostart", @"C:\Tools\Desk\Desk.exe")]
    [InlineData(@"""C:\Program Files\Desk\Desk.exe""", @"C:\Program Files\Desk\Desk.exe")]
    [InlineData(@"C:\Tools\Desk.exe --autostart", @"C:\Tools\Desk.exe")]
    [InlineData("   ", null)]
    public void CommandPaths(string command, string? path)
    {
        Assert.Equal(path, WindowsAutostartBackend.ExtractPath(command));
    }

    private sealed class ThrowingBackend : IAutostartBackend
    {
        public string ExePath => FakeAutostartBackend.Exe;

        public TaskInfo QueryTask() => throw new InvalidOperationException("boom");

        public Outcome CreateTask() => throw new InvalidOperationException("boom");

        public Outcome DeleteTask() => throw new InvalidOperationException("boom");

        public RunKeyInfo QueryRunKey() => throw new InvalidOperationException("boom");

        public Outcome WriteRunKey() => throw new InvalidOperationException("boom");

        public Outcome DeleteRunKey() => throw new InvalidOperationException("boom");

        public bool StartsThisExe(string command) => false;
    }
}
