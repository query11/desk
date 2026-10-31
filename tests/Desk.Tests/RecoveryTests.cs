using System.Text.Json.Nodes;
using Xunit;

namespace Desk.Tests;

/// <summary>A taskbar whose auto-hide state the test controls.</summary>
internal sealed class FakeAppBar : IAppBarState
{
    public const int AutoHide = 1;

    public int? State { get; set; } = 2; // Always on top, not auto-hidden.

    public List<int> Sets { get; } = [];

    public Action? BeforeSet { get; set; }

    public bool AutoHidden => (State & AutoHide) != 0;

    public int? Get() => State;

    public bool Set(int state)
    {
        if (State == null)
        {
            return false;
        }
        BeforeSet?.Invoke();
        Sets.Add(state);
        State = state;
        return true;
    }
}

/// <summary>Desk's own changes to auto-hide survive a crash and never override the user's choice.</summary>
public class AutoHideTests
{
    [Fact]
    public void TurnsItOnAndBackOff()
    {
        using var dir = new TempDir();
        var bar = new FakeAppBar();
        var controller = new AutoHideController(bar, dir.File("autohide.json"));

        controller.SetForced(true);
        Assert.True(bar.AutoHidden);
        Assert.True(File.Exists(dir.File("autohide.json")));

        controller.SetForced(false);
        Assert.False(bar.AutoHidden);
        Assert.False(File.Exists(dir.File("autohide.json")));
    }

    [Fact]
    public void TheRecordIsWrittenBeforeTheChange()
    {
        using var dir = new TempDir();
        var bar = new FakeAppBar();
        bar.BeforeSet = () => Assert.True(File.Exists(dir.File("autohide.json")));
        new AutoHideController(bar, dir.File("autohide.json")).SetForced(true);
        Assert.Single(bar.Sets);
    }

    [Fact]
    public void AUserWhoHadItOnKeepsIt()
    {
        using var dir = new TempDir();
        var bar = new FakeAppBar { State = 3 };
        var controller = new AutoHideController(bar, dir.File("autohide.json"));
        controller.SetForced(true);
        controller.SetForced(false);
        Assert.Empty(bar.Sets);
        Assert.True(bar.AutoHidden);
        Assert.Equal(AutoHideRestore.NothingToDo, controller.Restore());
    }

    [Fact]
    public void ACrashedRunIsUndoneByTheNextStart()
    {
        using var dir = new TempDir();
        var bar = new FakeAppBar();
        new AutoHideController(bar, dir.File("autohide.json")).SetForced(true);
        // Desk.exe dies here; the in-memory state is gone.

        var next = new AutoHideController(bar, dir.File("autohide.json"));
        Assert.Equal(AutoHideRestore.Restored, next.Restore());
        Assert.False(bar.AutoHidden);
        // Twice is harmless.
        Assert.Equal(AutoHideRestore.NothingToDo, next.Restore());
        Assert.Equal(2, bar.Sets.Count);
    }

    [Fact]
    public void AChangeTheUserMadeMeanwhileIsLeftAlone()
    {
        using var dir = new TempDir();
        var bar = new FakeAppBar();
        var controller = new AutoHideController(bar, dir.File("autohide.json"));
        controller.SetForced(true);
        bar.State = 2; // The user turned auto-hide off in the system settings.

        controller.SetForced(false);
        Assert.Single(bar.Sets);
        Assert.False(File.Exists(dir.File("autohide.json")));
    }

    [Fact]
    public void ARestoreWithoutTaskbarIsTriedAgainLater()
    {
        using var dir = new TempDir();
        var bar = new FakeAppBar();
        new AutoHideController(bar, dir.File("autohide.json")).SetForced(true);

        bar.State = null; // Explorer is restarting.
        var next = new AutoHideController(bar, dir.File("autohide.json"));
        Assert.Equal(AutoHideRestore.Failed, next.Restore());
        Assert.True(File.Exists(dir.File("autohide.json")));

        bar.State = 3;
        Assert.Equal(AutoHideRestore.Restored, next.Restore());
        Assert.False(bar.AutoHidden);
    }

    [Fact]
    public void WithoutARecordNothingIsChanged()
    {
        using var dir = new TempDir();
        // The record can't be written (a file where the folder should be).
        File.WriteAllText(dir.File("blocked"), "");
        var bar = new FakeAppBar();
        var controller = new AutoHideController(bar, Path.Combine(dir.File("blocked"), "autohide.json"));
        controller.SetForced(true);
        Assert.Empty(bar.Sets);
        Assert.False(bar.AutoHidden);
    }
}

internal sealed class FakeRestoreEnvironment : IRestoreEnvironment
{
    public bool Running { get; set; }

    public bool Responds { get; set; } = true;

    public HostLease? Host { get; set; }

    public bool Writable { get; set; } = true;

    public AutoHideRestore AutoHide { get; set; } = AutoHideRestore.NothingToDo;

    public List<string> Calls { get; } = [];

    public bool HostRunning() => Running;

    public bool AskHostToExit(TimeSpan timeout)
    {
        Calls.Add("ask");
        if (Responds)
        {
            Running = false;
        }
        return Responds;
    }

    public HostLease? FindHost() => Host;

    public bool TerminateHost(HostLease host, TimeSpan timeout)
    {
        Calls.Add($"terminate {host.ProcessId}");
        Running = false;
        return true;
    }

    public bool WriteDisabledConfigurations()
    {
        Calls.Add("configs");
        return Writable;
    }

    public AutoHideRestore RestoreAutoHide()
    {
        Calls.Add("autohide");
        var result = AutoHide;
        if (result != AutoHideRestore.Failed)
        {
            AutoHide = AutoHideRestore.NothingToDo;
        }
        return result;
    }

    public bool TurnStylingOff()
    {
        Calls.Add("setting");
        return true;
    }
}

/// <summary>Desk.exe --restore with a running, a hung and no Desk.exe.</summary>
public class EmergencyRestoreTests
{
    [Fact]
    public void WithoutDeskItRestoresDirectly()
    {
        var env = new FakeRestoreEnvironment { AutoHide = AutoHideRestore.Restored };
        var report = EmergencyRestore.Run(env);
        Assert.True(report.Succeeded);
        Assert.Equal(["configs", "autohide", "setting"], env.Calls);
        Assert.False(report.HostWasRunning);
    }

    [Fact]
    public void ARunningDeskIsAskedToQuitFirst()
    {
        var env = new FakeRestoreEnvironment { Running = true };
        var report = EmergencyRestore.Run(env);
        Assert.True(report.Succeeded);
        Assert.True(report.HostExited);
        Assert.Equal(["ask", "configs", "autohide", "setting"], env.Calls);
    }

    [Fact]
    public void AHungDeskIsEnded()
    {
        var env = new FakeRestoreEnvironment
        {
            Running = true,
            Responds = false,
            Host = new HostLease("s", 4242, 99),
        };
        var report = EmergencyRestore.Run(env);
        Assert.True(report.Succeeded);
        Assert.True(report.HostTerminated);
        Assert.Equal(["ask", "terminate 4242", "configs", "autohide", "setting"], env.Calls);
    }

    [Fact]
    public void AHungDeskThatCantBeFoundStillGetsTheConfigurationsWritten()
    {
        var env = new FakeRestoreEnvironment { Running = true, Responds = false };
        var report = EmergencyRestore.Run(env);
        Assert.False(report.Succeeded);
        Assert.True(report.ConfigurationsWritten);
        Assert.Contains("无法结束", report.Describe());
    }

    [Fact]
    public void RunningItTwiceIsHarmless()
    {
        var env = new FakeRestoreEnvironment { Running = true, AutoHide = AutoHideRestore.Restored };
        Assert.True(EmergencyRestore.Run(env).Succeeded);
        var second = EmergencyRestore.Run(env);
        Assert.True(second.Succeeded);
        Assert.False(second.HostWasRunning);
        Assert.Equal(AutoHideRestore.NothingToDo, second.AutoHide);
    }

    [Fact]
    public void AnUnwritableDataFolderIsReported()
    {
        var env = new FakeRestoreEnvironment { Writable = false };
        var report = EmergencyRestore.Run(env);
        Assert.False(report.Succeeded);
        Assert.Contains("无法写入还原配置", report.Describe());
    }
}

/// <summary>The owner named in the configurations, and the heartbeat.</summary>
public class HostSessionTests
{
    [Fact]
    public void ConfigurationsNameTheirOwner()
    {
        using var dir = new TempDir();
        var writer = new RuntimeWriter(dir.Path) { Owner = new HostLease("abc", 1234, 133_456_789_012_345_678) };
        writer.Write(RuntimeWriter.ExplorerFile, true, false, []);
        var json = JsonNode.Parse(File.ReadAllText(dir.File("explorer.exe.json")))!;
        Assert.Equal("abc", (string)json["host"]!["session"]!);
        Assert.Equal(1234, (int)json["host"]!["pid"]!);
        // As a string: a double would round the FILETIME.
        Assert.Equal("133456789012345678", (string)json["host"]!["start"]!);
    }

    [Fact]
    public void TheRestoreWritesConfigurationsWithoutOwner()
    {
        using var dir = new TempDir();
        var writer = new RuntimeWriter(dir.Path);
        writer.Write(RuntimeWriter.StartMenuFile, false, false, []);
        var json = JsonNode.Parse(File.ReadAllText(dir.File("startmenuexperiencehost.exe.json")))!;
        Assert.Null(json["host"]);
        Assert.False((bool)json["enabled"]!);
    }

    [Fact]
    public void TheHeartbeatAdvancesUntilExit()
    {
        using var dir = new TempDir();
        var session = new HostSession(new HostLease("abc", 1234, 77), dir.Path);
        session.Beat();
        session.Beat();
        var json = JsonNode.Parse(File.ReadAllText(dir.File("host.json")))!;
        Assert.Equal(2, (long)json["seq"]!);
        Assert.Equal("running", (string)json["state"]!);

        session.MarkExiting();
        session.Beat();
        json = JsonNode.Parse(File.ReadAllText(dir.File("host.json")))!;
        Assert.Equal(3, (long)json["seq"]!);
        Assert.Equal("exiting", (string)json["state"]!);

        Assert.Equal(new HostLease("abc", 1234, 77), HostSession.ReadHeartbeat(dir.Path));
    }
}
