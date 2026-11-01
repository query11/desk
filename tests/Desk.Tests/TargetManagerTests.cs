using Xunit;

namespace Desk.Tests;

/// <summary>
/// The injection state machine: intent read at the last moment (crash
/// protection, turning styling off), stale results, Start menu failures and
/// suspended loader threads.
/// </summary>
public class TargetManagerTests
{
    private readonly FakePlatform _platform = new();
    private readonly ManualScheduler _scheduler = new();
    private readonly ManualTime _time = new();
    private bool _explorerWanted = true;
    private bool _startMenuWanted;

    private TargetManager Create() => new(_platform, _scheduler, _time, FakePlatform.CurrentDll,
        () => _explorerWanted, () => _startMenuWanted);

    // ------------------------------------------------------------ explorer

    [Fact]
    public void CrashProtectionStopsInjectingInTheSameTickAndAfterwards()
    {
        var guard = new RestartGuard();
        var targets = Create();
        // What DeskApp does: crash protection turns styling off from inside
        // the tick that noticed the new explorer, then ticks again.
        targets.ExplorerChanged += _ =>
        {
            if (guard.OnRestart(_time.GetUtcNow().UtcDateTime) && _explorerWanted)
            {
                _explorerWanted = false;
                targets.Cancel();
                targets.Tick();
            }
        };

        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(1, _platform.HookCalls);

        // Two restarts are tolerated and get the engine again.
        for (uint pid = 101; pid <= 102; pid++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            _platform.Explorer = new ProcessInstance(pid, pid);
            targets.Tick();
            _scheduler.Drain();
        }
        Assert.Equal(3, _platform.HookCalls);

        // The third restart within 3 minutes trips the protection: no
        // attempt for the new explorer, not even one queued.
        _time.Advance(TimeSpan.FromSeconds(10));
        _platform.Explorer = new ProcessInstance(103, 103);
        targets.Tick();
        Assert.Empty(_scheduler.Background);
        _scheduler.Drain();
        Assert.Equal(3, _platform.HookCalls);
        Assert.False(_explorerWanted);

        // Later ticks and further restarts don't load it either.
        for (int i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            targets.Tick();
            _scheduler.Drain();
        }
        _platform.Explorer = new ProcessInstance(104, 104);
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(3, _platform.HookCalls);
        Assert.Equal(InjectionPhase.Idle, targets.Explorer.Phase);

        // Turning styling on again connects normally.
        _explorerWanted = true;
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(4, _platform.HookCalls);
        Assert.Equal(InjectionPhase.Loaded, targets.Explorer.Phase);
        Assert.Equal(104u, targets.Explorer.Process.Id);
    }

    [Fact]
    public void TurningOffWhileAnAttemptIsQueuedSkipsTheNativeCall()
    {
        var targets = Create();
        targets.Tick();
        Assert.Single(_scheduler.Background);

        _explorerWanted = false;
        targets.Cancel();
        _scheduler.Drain();

        Assert.Equal(0, _platform.HookCalls);
        Assert.Equal(InjectionPhase.Idle, targets.Explorer.Phase);

        _explorerWanted = true;
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(1, _platform.HookCalls);
        Assert.Equal(InjectionPhase.Loaded, targets.Explorer.Phase);
    }

    [Fact]
    public void ATickWithStylingOffCancelsAQueuedAttempt()
    {
        var targets = Create();
        targets.Tick();
        _explorerWanted = false;
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(0, _platform.HookCalls);
        Assert.Equal(InjectionPhase.Idle, targets.Explorer.Phase);
    }

    [Fact]
    public void AResultArrivingAfterTurningOffDoesNotCountButIsNotRepeated()
    {
        var targets = Create();
        // Styling is turned off while the hook is installed; the DLL got in
        // anyway (the native call can't be taken back).
        _platform.DuringCall = () =>
        {
            _explorerWanted = false;
            _scheduler.Post(targets.Cancel);
        };
        _platform.HookResults.Enqueue(_ => InjectResult.Loaded);
        targets.Tick();
        _scheduler.RunBackground();
        _scheduler.RunUi(); // Cancel first, then the (stale) result.
        _platform.DuringCall = null;

        Assert.Equal(InjectionPhase.Idle, targets.Explorer.Phase);
        for (int i = 0; i < 3; i++)
        {
            targets.Tick();
            _scheduler.Drain();
        }
        Assert.Equal(1, _platform.HookCalls);

        // On again: the engine that is already there is found, not loaded twice.
        _explorerWanted = true;
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(1, _platform.HookCalls);
        Assert.Equal(InjectionPhase.Loaded, targets.Explorer.Phase);
        Assert.True(targets.Explorer.CurrentLoaded);
    }

    [Fact]
    public void AResultForAnOldExplorerIsIgnored()
    {
        var targets = Create();
        // Explorer restarts while the hook for the old one is installed; the
        // old attempt then times out.
        _platform.DuringCall = () =>
        {
            _platform.DuringCall = null;
            _platform.Explorer = new ProcessInstance(101, 2);
            _scheduler.Post(targets.Tick);
        };
        _platform.HookResults.Enqueue(_ => InjectResult.Fail(InjectStage.Timeout));
        targets.Tick();
        _scheduler.Drain();

        // pid 100's attempt failed, pid 101's succeeded; only 101 counts.
        Assert.Equal(2, _platform.HookCalls);
        Assert.Equal(101u, targets.Explorer.Process.Id);
        Assert.Equal(InjectionPhase.Loaded, targets.Explorer.Phase);
        Assert.Null(targets.Explorer.LastFailure);
    }

    [Fact]
    public void AQueuedAttemptForAnOldExplorerIsDropped()
    {
        var targets = Create();
        targets.Tick(); // Attempt for pid 100 queued, not started.
        _platform.Explorer = new ProcessInstance(101, 2);
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(1, _platform.HookCalls);
        Assert.Equal(InjectionPhase.Loaded, targets.Explorer.Phase);
        Assert.Equal(101u, targets.Explorer.Process.Id);
    }

    [Fact]
    public void AReusedProcessIdIsANewProcess()
    {
        var targets = Create();
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(1, _platform.HookCalls);

        // Same id, different start time: a new explorer without the engine.
        _platform.Loaded.Clear();
        _platform.Explorer = new ProcessInstance(100, 99);
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(2, _platform.HookCalls);
    }

    [Fact]
    public void ReapplyDuringAnAttemptDoesNotStartASecondOne()
    {
        var targets = Create();
        targets.Tick();
        targets.Reset();
        targets.Tick();
        targets.Tick();
        Assert.Single(_scheduler.Background);
        _scheduler.Drain();
        Assert.Equal(1, _platform.HookCalls);
    }

    [Fact]
    public void ACancelledAttemptStillRunningBlocksANewOneUntilItReturns()
    {
        var targets = Create();
        targets.Tick();
        // The worker is running when styling goes off and on again.
        _platform.DuringCall = () =>
        {
            _explorerWanted = false;
            targets.Tick();
            _explorerWanted = true;
            targets.Tick();
            Assert.Empty(_scheduler.Background);
        };
        _scheduler.RunBackground();
        _platform.DuringCall = null;
        _scheduler.Drain();
        Assert.Equal(1, _platform.HookCalls);

        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(1, _platform.HookCalls);
        Assert.Equal(InjectionPhase.Loaded, targets.Explorer.Phase);
    }

    [Fact]
    public void AnOutdatedEngineInExplorerIsReported()
    {
        _platform.MarkLoaded(100, FakePlatform.OldDll);
        var targets = Create();
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(0, _platform.HookCalls);
        Assert.True(targets.Explorer.Outdated);
        Assert.False(targets.Explorer.CurrentLoaded);
        Assert.Equal(0, _platform.Terminated);
    }

    // ---------------------------------------------------------- Start menu

    private TargetManager CreateStartMenu()
    {
        _explorerWanted = false;
        _startMenuWanted = true;
        _platform.StartMenu = new ProcessInstance(200, 5);
        return Create();
    }

    [Theory]
    [InlineData(nameof(InjectStage.OpenProcess), 5)]
    [InlineData(nameof(InjectStage.AllocateMemory), 8)]
    [InlineData(nameof(InjectStage.WriteMemory), 299)]
    [InlineData(nameof(InjectStage.CreateThread), 5)]
    public void AFailureIsRetriedWithBackoffAndRecovers(string stageName, int error)
    {
        var stage = Enum.Parse<InjectStage>(stageName);
        var targets = CreateStartMenu();
        _platform.RemoteResults.Enqueue(_ => InjectResult.Fail(stage, error));
        targets.Tick();
        _scheduler.Drain();

        var snapshot = targets.StartMenu;
        Assert.Equal(InjectionPhase.Failed, snapshot.Phase);
        Assert.Equal(stage, snapshot.LastFailure!.Stage);
        Assert.Equal(error, snapshot.LastFailure.Error);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddSeconds(10), snapshot.NextAttempt);

        // Not before the backoff is over.
        _time.Advance(TimeSpan.FromSeconds(9));
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(1, _platform.RemoteCalls);

        // The cause went away: the next attempt loads it.
        _time.Advance(TimeSpan.FromSeconds(1));
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(2, _platform.RemoteCalls);
        Assert.Equal(InjectionPhase.Loaded, targets.StartMenu.Phase);
        Assert.Null(targets.StartMenu.LastFailure);
    }

    [Fact]
    public void BackoffGrowsAndIsCapped()
    {
        var targets = CreateStartMenu();
        var delays = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            _platform.RemoteResults.Enqueue(_ => InjectResult.Fail(InjectStage.OpenProcess, 5));
            targets.Tick();
            _scheduler.Drain();
            var next = targets.StartMenu.NextAttempt;
            delays.Add((next - _time.GetUtcNow().UtcDateTime).TotalSeconds);
            _time.Now = next;
        }
        Assert.Equal(new double[] { 10, 20, 40, 80, 160, 300, 300, 300 }, delays);
    }

    [Fact]
    public void ASuspendedHostIsWaitedForWithoutASecondThread()
    {
        var targets = CreateStartMenu();
        var pending = new FakeRemoteLoad();
        _platform.RemoteResults.Enqueue(_ => InjectResult.Wait(pending));
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(InjectionPhase.Waiting, targets.StartMenu.Phase);

        for (int i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            targets.Tick();
            _scheduler.Drain();
        }
        Assert.Equal(1, _platform.RemoteCalls);
        Assert.False(pending.Disposed);

        // The Start menu is opened: the thread runs and loads the DLL.
        pending.State = RemoteLoadState.Succeeded;
        _platform.MarkLoaded(200);
        targets.Tick();
        Assert.Equal(InjectionPhase.Loaded, targets.StartMenu.Phase);
        Assert.True(pending.Disposed);
        Assert.Equal(1, _platform.RemoteCalls);
    }

    [Fact]
    public void ALoadThatFailsInTheTargetIsRetried()
    {
        var targets = CreateStartMenu();
        var pending = new FakeRemoteLoad();
        _platform.RemoteResults.Enqueue(_ => InjectResult.Wait(pending));
        targets.Tick();
        _scheduler.Drain();

        pending.State = RemoteLoadState.Failed;
        targets.Tick();
        Assert.Equal(InjectionPhase.Failed, targets.StartMenu.Phase);
        Assert.Equal(InjectStage.LoadLibrary, targets.StartMenu.LastFailure!.Stage);
        Assert.True(pending.Disposed);

        _time.Advance(TimeSpan.FromSeconds(10));
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(2, _platform.RemoteCalls);
        Assert.Equal(InjectionPhase.Loaded, targets.StartMenu.Phase);
    }

    [Fact]
    public void WhenTheHostExitsWhileWaitingTheNewOneGetsTheEngine()
    {
        var targets = CreateStartMenu();
        var pending = new FakeRemoteLoad();
        _platform.RemoteResults.Enqueue(_ => InjectResult.Wait(pending));
        targets.Tick();
        _scheduler.Drain();

        _platform.StartMenu = new ProcessInstance(201, 6);
        targets.Tick();
        Assert.True(pending.Disposed);
        _scheduler.Drain();
        Assert.Equal(2, _platform.RemoteCalls);
        Assert.Equal(InjectionPhase.Loaded, targets.StartMenu.Phase);
        Assert.Equal(201u, targets.StartMenu.Process.Id);
    }

    [Fact]
    public void AnExitedHostReportedByTheLoadIsLetGo()
    {
        var targets = CreateStartMenu();
        var pending = new FakeRemoteLoad();
        _platform.RemoteResults.Enqueue(_ => InjectResult.Wait(pending));
        targets.Tick();
        _scheduler.Drain();

        // The process is gone before a new one is found.
        pending.State = RemoteLoadState.ProcessExited;
        targets.Tick();
        Assert.True(pending.Disposed);
        Assert.Equal(InjectionPhase.Idle, targets.StartMenu.Phase);

        _platform.StartMenu = new ProcessInstance(202, 7);
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(InjectionPhase.Loaded, targets.StartMenu.Phase);
        Assert.Equal(202u, targets.StartMenu.Process.Id);
    }

    [Fact]
    public void TurningOffAndOnWhileWaitingKeepsTheSameThread()
    {
        var targets = CreateStartMenu();
        var pending = new FakeRemoteLoad();
        _platform.RemoteResults.Enqueue(_ => InjectResult.Wait(pending));
        targets.Tick();
        _scheduler.Drain();

        _startMenuWanted = false;
        targets.Cancel();
        targets.Tick();
        Assert.Equal(InjectionPhase.Waiting, targets.StartMenu.Phase);
        Assert.False(pending.Disposed);

        _startMenuWanted = true;
        targets.Reset();
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(1, _platform.RemoteCalls);
        Assert.Equal(InjectionPhase.Waiting, targets.StartMenu.Phase);
    }

    [Fact]
    public void AThreadCreatedByACancelledAttemptIsStillTracked()
    {
        var targets = CreateStartMenu();
        var pending = new FakeRemoteLoad();
        // Turned off after CreateRemoteThread: the attempt is stale, the
        // thread is real.
        _platform.DuringCall = () =>
        {
            _startMenuWanted = false;
            _scheduler.Post(targets.Cancel);
        };
        _platform.RemoteResults.Enqueue(_ => InjectResult.Wait(pending));
        targets.Tick();
        _scheduler.RunBackground();
        _scheduler.RunUi();
        _platform.DuringCall = null;
        Assert.Equal(InjectionPhase.Waiting, targets.StartMenu.Phase);

        _startMenuWanted = true;
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(1, _platform.RemoteCalls);

        pending.State = RemoteLoadState.Succeeded;
        _platform.MarkLoaded(200);
        targets.Tick();
        Assert.Equal(InjectionPhase.Loaded, targets.StartMenu.Phase);
    }

    [Fact]
    public void AnOutdatedStartMenuEngineIsReplacedOnlyOnce()
    {
        var targets = CreateStartMenu();
        _platform.MarkLoaded(200, FakePlatform.OldDll);
        targets.Tick();
        Assert.Equal(1, _platform.Terminated);

        // The new host still gets an old engine (say it was cached): no loop.
        _platform.StartMenu = new ProcessInstance(203, 8);
        _platform.MarkLoaded(203, FakePlatform.OldDll);
        targets.Tick();
        _scheduler.Drain();
        Assert.Equal(1, _platform.Terminated);
        Assert.True(targets.StartMenu.Outdated);
        Assert.Equal(0, _platform.RemoteCalls);
    }
}
