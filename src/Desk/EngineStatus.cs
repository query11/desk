using System.Globalization;
using System.Text.Json;

namespace Desk;

/// <summary>How far styling got in a target process.</summary>
internal enum EngineStatus
{
    /// <summary>Styling is turned off for this target.</summary>
    Off,

    /// <summary>Looking for the process or loading the engine into it.</summary>
    Connecting,

    /// <summary>The loader thread runs once the process wakes up (the Start menu is opened).</summary>
    Waiting,

    /// <summary>Loading the engine failed; retried automatically.</summary>
    Failed,

    /// <summary>The engine is loaded and setting up.</summary>
    Initializing,

    /// <summary>The engine is applying the current configuration.</summary>
    Applying,

    /// <summary>The engine is ready, but the target's interface hasn't been created yet.</summary>
    WaitingForUi,

    /// <summary>Applied, but no rule matched anything.</summary>
    NoTargets,

    /// <summary>The current configuration is applied.</summary>
    Active,

    /// <summary>The engine couldn't initialize; retried automatically or on re-apply.</summary>
    EngineFailed,

    /// <summary>The engine is loaded but doesn't report anything (an older build, or stuck).</summary>
    NotResponding,

    /// <summary>An older engine runs in the process; it is replaced when the process restarts.</summary>
    Outdated,

    /// <summary>The engine can't run (missing DeskTap.dll, unsupported system).</summary>
    Unavailable,
}

/// <summary>A status with an explanation for the settings window, if there is one.</summary>
internal sealed record TargetStatus(EngineStatus Status, string? Detail = null);

/// <summary>What an engine wrote to state\&lt;process&gt;.&lt;pid&gt;.json.</summary>
internal sealed record EngineReport(
    int Protocol,
    string Version,
    uint ProcessId,
    long ProcessStart,
    string Module,
    string Instance,
    string State,
    int InitAttempts,
    string? ErrorStage,
    string? ErrorCode,
    long RetryAt,
    long ConfigSerial,
    bool Enabled,
    long AppliedSerial,
    string Apply,
    int UiThreads,
    int Matched,
    int Errors,
    string HostState,
    string? HostSession,
    string? HostReason,
    IReadOnlyDictionary<string, long> Counters)
{
    public static EngineReport Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
        var host = root.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.Object ? h : default;
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        if (root.TryGetProperty("counters", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in c.EnumerateObject())
            {
                if (property.Value.TryGetInt64(out long value))
                {
                    counters[property.Name] = value;
                }
            }
        }

        return new EngineReport(
            Int(root, "protocol"),
            Str(root, "version") ?? "",
            (uint)Long(root, "pid"),
            Long(root, "processStart"),
            Str(root, "module") ?? "",
            Str(root, "instance") ?? "",
            Str(root, "state") ?? "",
            Int(root, "initAttempts"),
            Str(error, "stage"),
            Str(error, "hr"),
            Long(root, "retryAt"),
            Long(root, "configSerial"),
            root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
            Long(root, "appliedSerial"),
            Str(root, "apply") ?? "",
            Int(root, "uiThreads"),
            Int(root, "matched"),
            Int(root, "errors"),
            Str(host, "state") ?? "none",
            Str(host, "session"),
            Str(host, "reason"),
            counters);
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
            ? result
            : 0;

    private static int Int(JsonElement element, string name) => (int)Math.Clamp(Long(element, name), int.MinValue, int.MaxValue);
}

/// <summary>
/// Reads the engines' status files. Only a report written by the current
/// instance of the target process counts: a file left by an earlier process
/// with the same id (creation time differs) is ignored.
/// </summary>
internal sealed class EngineStatusReader
{
    private readonly string _directory;
    private readonly Dictionary<string, (DateTime Written, long Length, EngineReport? Report)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public EngineStatusReader(string directory)
    {
        _directory = directory;
    }

    public static string FileName(string processName, uint processId) =>
        $"{processName.ToLowerInvariant()}.{processId.ToString(CultureInfo.InvariantCulture)}.json";

    /// <summary>The report for the process instance, or null when there is none (yet).</summary>
    public EngineReport? Read(string processName, ProcessInstance process)
    {
        if (process.IsNone)
        {
            return null;
        }
        string path = Path.Combine(_directory, FileName(processName, process.Id));
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                _cache.Remove(path);
                return null;
            }
            if (!_cache.TryGetValue(path, out var cached) ||
                cached.Written != info.LastWriteTimeUtc || cached.Length != info.Length)
            {
                EngineReport? report;
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    report = EngineReport.Parse(reader.ReadToEnd());
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
                {
                    // Being replaced right now, or damaged: keep the last good one.
                    return cached.Report is { } last && Matches(last, process) ? last : null;
                }
                cached = (info.LastWriteTimeUtc, info.Length, report);
                _cache[path] = cached;
            }
            return cached.Report is { } current && Matches(current, process) ? current : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Matches(EngineReport report, ProcessInstance process) =>
        report.ProcessId == process.Id &&
        (report.ProcessStart == 0 || process.StartTime == 0 || report.ProcessStart == process.StartTime);

    /// <summary>Deletes reports of processes that no longer run.</summary>
    public void CleanUp(Func<uint, long, bool> isRunning)
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }
            foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                // <process>.<pid>.json
                string name = Path.GetFileNameWithoutExtension(file);
                int dot = name.LastIndexOf('.');
                if (dot < 0 || !uint.TryParse(name[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture,
                        out uint pid))
                {
                    continue;
                }
                long start = 0;
                try
                {
                    start = EngineReport.Parse(File.ReadAllText(file)).ProcessStart;
                }
                catch
                {
                    // Unreadable: decided by the process id alone.
                }
                if (!isRunning(pid, start))
                {
                    File.Delete(file);
                    _cache.Remove(file);
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Could not clean up engine status files: {e.Message}");
        }
    }
}

/// <summary>
/// Turns the injection state and the engine's report into what the settings
/// window shows. "Active" needs the engine's word that it applied the
/// configuration Desk.exe wrote last, to the current process instance.
/// </summary>
internal static class EngineStatusEvaluator
{
    /// <summary>How long a freshly loaded engine may take to write its first report.</summary>
    public static readonly TimeSpan FirstReportGrace = TimeSpan.FromSeconds(20);

    public static TargetStatus Evaluate(bool wanted, TargetSnapshot injection, EngineReport? report,
        long expectedSerial, DateTime now)
    {
        if (!wanted)
        {
            return new TargetStatus(EngineStatus.Off);
        }

        switch (injection.Phase)
        {
            case InjectionPhase.Waiting:
                return new TargetStatus(EngineStatus.Waiting);
            case InjectionPhase.Failed:
                return new TargetStatus(EngineStatus.Failed,
                    $"{Describe(injection.LastFailure)}，{RetryText(injection.NextAttempt, now)}");
            case InjectionPhase.Loaded:
                break;
            default:
                return new TargetStatus(EngineStatus.Connecting);
        }

        if (injection.Outdated)
        {
            return new TargetStatus(EngineStatus.Outdated);
        }
        if (report == null)
        {
            return now - injection.PhaseSince < FirstReportGrace
                ? new TargetStatus(EngineStatus.Initializing)
                : new TargetStatus(EngineStatus.NotResponding);
        }

        switch (report.State)
        {
            case "blocked":
                return new TargetStatus(EngineStatus.Outdated);
            case "failed":
                string retry = report.RetryAt > 0
                    ? RetryText(DateTime.FromFileTimeUtc(report.RetryAt), now)
                    : "可以点「重新应用」重试";
                return new TargetStatus(EngineStatus.EngineFailed,
                    $"初始化失败（{report.ErrorStage ?? "?"}，0x{report.ErrorCode ?? "?"}），{retry}");
            case "ready":
                break;
            default:
                return new TargetStatus(EngineStatus.Initializing);
        }

        if (report.HostState == "lost")
        {
            return new TargetStatus(EngineStatus.Applying, "引擎认为 Desk 已退出，等待重新连接");
        }
        if (expectedSerial <= 0 || report.ConfigSerial < expectedSerial)
        {
            return new TargetStatus(EngineStatus.Applying);
        }
        if (report.Apply == "noUi")
        {
            return new TargetStatus(EngineStatus.WaitingForUi);
        }
        if (report.AppliedSerial != expectedSerial)
        {
            return new TargetStatus(EngineStatus.Applying);
        }
        return report.Apply switch
        {
            "applied" => new TargetStatus(EngineStatus.Active,
                report.Errors > 0 ? $"{report.Errors} 条样式没能应用，详见日志" : null),
            "noMatch" => new TargetStatus(EngineStatus.NoTargets),
            _ => new TargetStatus(EngineStatus.Applying),
        };
    }

    private static string Describe(InjectResult? failure) => failure == null
        ? "连接失败"
        : failure.Stage switch
        {
            InjectStage.OpenProcess => $"无法打开进程（错误 {failure.Error}）",
            InjectStage.AllocateMemory or InjectStage.WriteMemory => $"无法写入进程（错误 {failure.Error}）",
            InjectStage.CreateThread => $"无法创建加载线程（错误 {failure.Error}）",
            InjectStage.LoadLibrary => "引擎文件没能加载（可能被安全软件拦截或没有读取权限）",
            InjectStage.Hook => $"无法安装加载钩子（错误 {failure.Error}）",
            InjectStage.Timeout => "等待加载超时",
            InjectStage.QueryModules => "无法检查进程里的模块",
            _ => $"连接失败（{failure.Describe()}）",
        };

    private static string RetryText(DateTime at, DateTime now)
    {
        double seconds = Math.Ceiling((at - now).TotalSeconds);
        return seconds <= 0 ? "正在重试" : $"{seconds:0} 秒后自动重试";
    }
}
