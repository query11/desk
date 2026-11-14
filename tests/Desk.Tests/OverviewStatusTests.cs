using Xunit;

namespace Desk.Tests;

public class OverviewStatusTests
{
    [Fact]
    public void WithoutTheEngineTheLineDoesNotSayItRuns()
    {
        // What the smoke test sees: DeskTap.dll missing, styling switched on.
        var state = OverviewStatus.Of(engineError: true, enabled: true, PartHealth.Problem, PartHealth.Problem);
        Assert.Equal(OverallState.Unavailable, state);
        Assert.Equal("无法运行", OverviewStatus.Label(state));
        Assert.DoesNotContain("已启用", OverviewStatus.Summary(state));
        Assert.DoesNotContain("正在运行", OverviewStatus.Summary(state));
        // Also when the user switched it off: the engine can't run either way.
        Assert.Equal(OverallState.Unavailable, OverviewStatus.Of(true, false, PartHealth.Fine, PartHealth.Fine));
    }

    [Fact]
    public void TheWorstPartDecides()
    {
        Assert.Equal(OverallState.Off, OverviewStatus.Of(false, false, PartHealth.Problem, PartHealth.Busy));
        Assert.Equal(OverallState.Problem, OverviewStatus.Of(false, true, PartHealth.Fine, PartHealth.Problem));
        Assert.Equal(OverallState.Busy, OverviewStatus.Of(false, true, PartHealth.Busy, PartHealth.Fine));
        Assert.Equal(OverallState.Running, OverviewStatus.Of(false, true, PartHealth.Fine, PartHealth.Fine));
    }

    [Fact]
    public void PartsAreSortedByHowTheyDo()
    {
        Assert.Equal(PartHealth.Fine, OverviewStatus.HealthOf(EngineStatus.Active));
        Assert.Equal(PartHealth.Fine, OverviewStatus.HealthOf(EngineStatus.Waiting));
        Assert.Equal(PartHealth.Fine, OverviewStatus.HealthOf(EngineStatus.Outdated));
        Assert.Equal(PartHealth.Fine, OverviewStatus.HealthOf(EngineStatus.Off));
        Assert.Equal(PartHealth.Busy, OverviewStatus.HealthOf(EngineStatus.Connecting));
        Assert.Equal(PartHealth.Busy, OverviewStatus.HealthOf(EngineStatus.Applying));
        Assert.Equal(PartHealth.Problem, OverviewStatus.HealthOf(EngineStatus.NotResponding));
        Assert.Equal(PartHealth.Problem, OverviewStatus.HealthOf(EngineStatus.Unavailable));
    }

    [Fact]
    public void EveryStateSaysWhatToDoNext()
    {
        var states = Enum.GetValues<OverallState>();
        Assert.Equal(states.Length, states.Select(OverviewStatus.Summary).Distinct().Count());
        Assert.Contains("「维护与恢复」", OverviewStatus.Summary(OverallState.Problem));
        Assert.Contains("开关", OverviewStatus.Summary(OverallState.Off));
        Assert.All(states, state => Assert.DoesNotContain("！", OverviewStatus.Summary(state)));
    }
}
