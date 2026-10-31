using System.Text;
using System.Text.Json.Nodes;

namespace Desk;

/// <summary>The island with live system figures on the left of the taskbar.</summary>
internal sealed record MonitorConfig(
    bool Enabled,
    bool Network,
    bool Cpu,
    bool Memory,
    string CpuLabel,
    string MemoryLabel,
    string TextColor,
    string SecondaryTextColor,
    string WarningColor);

/// <summary>
/// Writes runtime\&lt;process&gt;.json, which DeskTap.dll watches. Files are
/// replaced atomically and only rewritten when their content changes.
/// </summary>
internal sealed class RuntimeWriter
{
    public const string ExplorerFile = "explorer.exe";
    public const string StartMenuFile = "startmenuexperiencehost.exe";

    private readonly Dictionary<string, string> _lastContent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _writtenSerials = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;
    private long _lastSerial;
    private long _recover;

    public RuntimeWriter(string? directory = null)
    {
        _directory = directory ?? Paths.RuntimeDir;
    }

    /// <summary>The serial of the configuration last written for the process; 0 if none.</summary>
    public long LastSerial(string process) => _writtenSerials.GetValueOrDefault(process);

    /// <summary>
    /// Makes the next writes go out again with a new recover token: engines
    /// whose initialization failed retry it right away.
    /// </summary>
    public void RequestRecovery()
    {
        _recover++;
        Invalidate();
    }

    /// <param name="requestedTheme">"Light" or "Dark" to force the theme of the window content.</param>
    public void Write(string process, bool enabled, bool clickThrough, IReadOnlyList<RuleSpec> rules,
        MonitorConfig? monitor = null, string? requestedTheme = null)
    {
        var rulesArray = new JsonArray();
        foreach (var rule in rules)
        {
            var styles = new JsonArray();
            foreach (var style in rule.Styles)
            {
                styles.Add(style);
            }
            rulesArray.Add(new JsonObject { ["target"] = rule.Target, ["styles"] = styles });
        }

        var payload = new JsonObject
        {
            ["schema"] = 1,
            ["enabled"] = enabled,
            ["clickThrough"] = clickThrough,
            ["rules"] = rulesArray,
        };
        if (_recover != 0)
        {
            payload["recover"] = _recover;
        }
        if (monitor != null)
        {
            payload["monitor"] = new JsonObject
            {
                ["enabled"] = monitor.Enabled,
                ["network"] = monitor.Network,
                ["cpu"] = monitor.Cpu,
                ["memory"] = monitor.Memory,
                ["cpuLabel"] = monitor.CpuLabel,
                ["memoryLabel"] = monitor.MemoryLabel,
                ["text"] = monitor.TextColor,
                ["secondaryText"] = monitor.SecondaryTextColor,
                ["warning"] = monitor.WarningColor,
            };
        }
        if (requestedTheme != null)
        {
            payload["requestedTheme"] = requestedTheme;
        }

        string content = payload.ToJsonString();
        if (_lastContent.TryGetValue(process, out var last) && last == content)
        {
            return;
        }

        long serial = NextSerial();
        payload["serial"] = serial;

        try
        {
            Directory.CreateDirectory(_directory);
            string target = Path.Combine(_directory, process + ".json");
            string temp = target + ".tmp";
            File.WriteAllText(temp, payload.ToJsonString(), new UTF8Encoding(false));
            File.Move(temp, target, overwrite: true);
            _lastContent[process] = content;
            _writtenSerials[process] = serial;
            Log.Info($"Wrote {process}.json: enabled={enabled}, {rules.Count} rules");
        }
        catch (Exception e)
        {
            Log.Error($"Could not write the runtime file for {process}", e);
        }
    }

    /// <summary>Forces the next Write to rewrite the files.</summary>
    public void Invalidate() => _lastContent.Clear();

    private long NextSerial()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _lastSerial = Math.Max(now, _lastSerial + 1);
        return _lastSerial;
    }
}
