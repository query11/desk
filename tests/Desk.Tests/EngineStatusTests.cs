using Xunit;

namespace Desk.Tests;

/// <summary>A scratch directory removed after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "desk-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}

/// <summary>
/// What the overview page may claim: "active" only on the engine's word that
/// the configuration Desk.exe wrote last is applied in the current process.
/// </summary>
public class EngineStatusTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ProcessInstance Explorer = new(100, 133_000_000_000_000_000);

    private static TargetSnapshot Loaded(DateTime? since = null, bool outdated = false) =>
        new(Explorer, InjectionPhase.Loaded, null, 0, DateTime.MinValue, !outdated, outdated, since ?? Now);

    /// <summary>A report in the format status.cpp writes.</summary>
    internal static string ReportJson(string state = "ready", long configSerial = 500, long appliedSerial = 500,
        string apply = "applied", int matched = 12, uint pid = 100, long start = 133_000_000_000_000_000,
        string host = "attached", int errors = 0, string? errorStage = null, long retryAt = 0) =>
        $$$"""
        {"protocol":1,"version":"0.4.0","process":"explorer.exe","pid":{{{pid}}},"processStart":{{{start}}},
         "module":"C:\\Users\\u\\AppData\\Local\\Desk\\bin\\DeskTap-abc.dll","instance":"3f2a","state":"{{{state}}}",
         "initAttempts":2{{{(errorStage != null ? $",\"error\":{{\"stage\":\"{errorStage}\",\"hr\":\"80070490\"}},\"retryAt\":{retryAt}" : "")}}},
         "configSerial":{{{configSerial}}},"enabled":true,"appliedSerial":{{{appliedSerial}}},"apply":"{{{apply}}}",
         "uiThreads":2,"matched":{{{matched}}},"errors":{{{errors}}},"host":{"state":"{{{host}}}","session":"s1"},
         "counters":{"uiThreadsSeen":2,"effectFactories":3}}
        """;

    private static EngineReport Report(string state = "ready", long configSerial = 500, long appliedSerial = 500,
        string apply = "applied", int matched = 12, string host = "attached", int errors = 0,
        string? errorStage = null, long retryAt = 0) =>
        EngineReport.Parse(ReportJson(state, configSerial, appliedSerial, apply, matched, host: host, errors: errors,
            errorStage: errorStage, retryAt: retryAt));

    private static TargetStatus Evaluate(EngineReport? report, long expected = 500, TargetSnapshot? injection = null,
        bool wanted = true) =>
        EngineStatusEvaluator.Evaluate(wanted, injection ?? Loaded(), report, expected, Now);

    [Fact]
    public void ParsesTheEngineReport()
    {
        var report = Report(errorStage: "SetSite", state: "failed", retryAt: 133_100_000_000_000_000);
        Assert.Equal(1, report.Protocol);
        Assert.Equal(100u, report.ProcessId);
        Assert.Equal(133_000_000_000_000_000, report.ProcessStart);
        Assert.Equal("failed", report.State);
        Assert.Equal("SetSite", report.ErrorStage);
        Assert.Equal("80070490", report.ErrorCode);
        Assert.Equal(133_100_000_000_000_000, report.RetryAt);
        Assert.Equal(12, report.Matched);
        Assert.Equal("attached", report.HostState);
        Assert.Equal(3, report.Counters["effectFactories"]);
        Assert.EndsWith("DeskTap-abc.dll", report.Module);
    }

    [Fact]
    public void ActiveOnlyWithTheEnginesWordForTheLatestConfiguration()
    {
        Assert.Equal(EngineStatus.Active, Evaluate(Report()).Status);
        // Loaded is not enough.
        Assert.Equal(EngineStatus.Initializing, Evaluate(null).Status);
        // Desk.exe wrote a newer configuration the engine hasn't seen yet.
        Assert.Equal(EngineStatus.Applying, Evaluate(Report(), expected: 501).Status);
        // Seen but still being applied on some UI thread.
        Assert.Equal(EngineStatus.Applying,
            Evaluate(Report(configSerial: 501, appliedSerial: 0, apply: "pending"), expected: 501).Status);
        // Nothing written yet.
        Assert.Equal(EngineStatus.Applying, Evaluate(Report(), expected: 0).Status);
    }

    [Fact]
    public void QuickSuccessiveChangesStayApplyingUntilTheLastOneIsDone()
    {
        // Three writes in a row; the engine applied the first, read the second.
        var report = Report(configSerial: 502, appliedSerial: 501);
        Assert.Equal(EngineStatus.Applying, Evaluate(report, expected: 503).Status);
        report = Report(configSerial: 503, appliedSerial: 503);
        Assert.Equal(EngineStatus.Active, Evaluate(report, expected: 503).Status);
    }

    [Fact]
    public void NoMatchAndNoUiAreNotSuccess()
    {
        Assert.Equal(EngineStatus.NoTargets, Evaluate(Report(apply: "noMatch", matched: 0)).Status);
        Assert.Equal(EngineStatus.WaitingForUi,
            Evaluate(Report(apply: "noUi", appliedSerial: 0, matched: 0)).Status);
    }

    [Fact]
    public void InitializationFailureIsShownWithItsCauseAndRecovers()
    {
        var retryAt = Now.AddSeconds(30).ToFileTimeUtc();
        var failed = Evaluate(Report(state: "failed", errorStage: "InitializeXamlDiagnosticsEx", retryAt: retryAt,
            appliedSerial: 0, apply: "pending"));
        Assert.Equal(EngineStatus.EngineFailed, failed.Status);
        Assert.Contains("InitializeXamlDiagnosticsEx", failed.Detail);
        Assert.Contains("30 秒后自动重试", failed.Detail);

        // Re-apply asked it to retry; it came up and applied the configuration.
        Assert.Equal(EngineStatus.Active, Evaluate(Report()).Status);
    }

    [Fact]
    public void AnEngineThatNeverReportsIsNotResponding()
    {
        var injection = Loaded(since: Now - EngineStatusEvaluator.FirstReportGrace - TimeSpan.FromSeconds(1));
        Assert.Equal(EngineStatus.NotResponding, Evaluate(null, injection: injection).Status);
    }

    [Fact]
    public void AnOlderEngineIsReportedAsOutdated()
    {
        Assert.Equal(EngineStatus.Outdated, Evaluate(null, injection: Loaded(outdated: true)).Status);
        // The new engine is there, but an old one owns the process.
        Assert.Equal(EngineStatus.Outdated, Evaluate(Report(state: "blocked")).Status);
    }

    [Fact]
    public void InjectionStatesComeFirst()
    {
        Assert.Equal(EngineStatus.Off, Evaluate(Report(), wanted: false).Status);
        var failed = new TargetSnapshot(Explorer, InjectionPhase.Failed, InjectResult.Fail(InjectStage.CreateThread, 5),
            1, Now.AddSeconds(10), false, false, Now);
        var status = Evaluate(null, injection: failed);
        Assert.Equal(EngineStatus.Failed, status.Status);
        Assert.Contains("错误 5", status.Detail);
        Assert.Contains("10 秒后自动重试", status.Detail);
        var waiting = failed with { Phase = InjectionPhase.Waiting, LastFailure = null };
        Assert.Equal(EngineStatus.Waiting, Evaluate(null, injection: waiting).Status);
    }

    [Fact]
    public void AReportOfAnEarlierProcessWithTheSameIdIsIgnored()
    {
        using var dir = new TempDir();
        var reader = new EngineStatusReader(dir.Path);
        File.WriteAllText(dir.File("explorer.exe.100.json"), ReportJson(start: 111));

        Assert.Null(reader.Read("explorer.exe", Explorer));
        Assert.NotNull(reader.Read("explorer.exe", Explorer with { StartTime = 111 }));
        // No report for the new process id at all.
        Assert.Null(reader.Read("explorer.exe", new ProcessInstance(101, 5)));
    }

    [Fact]
    public void AFileBeingReplacedKeepsTheLastGoodReport()
    {
        using var dir = new TempDir();
        var reader = new EngineStatusReader(dir.Path);
        string path = dir.File("explorer.exe.100.json");
        File.WriteAllText(path, ReportJson());
        Assert.Equal("ready", reader.Read("explorer.exe", Explorer)!.State);

        File.WriteAllText(path, "{\"protocol\":1,\"pid\":");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal("ready", reader.Read("explorer.exe", Explorer)!.State);

        File.Delete(path);
        Assert.Null(reader.Read("explorer.exe", Explorer));
    }

    [Fact]
    public void CleanUpRemovesReportsOfEndedProcesses()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("explorer.exe.100.json"), ReportJson());
        File.WriteAllText(dir.File("explorer.exe.90.json"), ReportJson(pid: 90, start: 5));
        File.WriteAllText(dir.File("startmenuexperiencehost.exe.200.json"), "not json");
        new EngineStatusReader(dir.Path).CleanUp((pid, start) => pid == 100 && start == Explorer.StartTime);

        Assert.True(File.Exists(dir.File("explorer.exe.100.json")));
        Assert.False(File.Exists(dir.File("explorer.exe.90.json")));
        Assert.False(File.Exists(dir.File("startmenuexperiencehost.exe.200.json")));
    }

    [Fact]
    public void RuntimeWriterTracksSerialsAndRecoveryRequests()
    {
        using var dir = new TempDir();
        var writer = new RuntimeWriter(dir.Path);
        writer.Write(RuntimeWriter.ExplorerFile, true, false, [new RuleSpec("Grid", ["Opacity=1"])]);
        long first = writer.LastSerial(RuntimeWriter.ExplorerFile);
        Assert.True(first > 0);

        // Unchanged content isn't rewritten and keeps its serial.
        writer.Write(RuntimeWriter.ExplorerFile, true, false, [new RuleSpec("Grid", ["Opacity=1"])]);
        Assert.Equal(first, writer.LastSerial(RuntimeWriter.ExplorerFile));

        // Re-apply: the same content goes out again with a recover token.
        writer.RequestRecovery();
        writer.Write(RuntimeWriter.ExplorerFile, true, false, [new RuleSpec("Grid", ["Opacity=1"])]);
        long second = writer.LastSerial(RuntimeWriter.ExplorerFile);
        Assert.True(second > first);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(dir.File("explorer.exe.json")))!;
        Assert.Equal(1, (long)json["recover"]!);
        Assert.Equal(second, (long)json["serial"]!);
    }
}
