using System.Text;
using System.Text.Json.Nodes;

namespace Desk;

/// <summary>
/// Writes runtime\&lt;process&gt;.json, which DeskTap.dll watches. Files are
/// replaced atomically and only rewritten when their content changes.
/// </summary>
internal sealed class RuntimeWriter
{
    public const string ExplorerFile = "explorer.exe";
    public const string StartMenuFile = "startmenuexperiencehost.exe";

    private readonly Dictionary<string, string> _lastContent = new(StringComparer.OrdinalIgnoreCase);
    private long _lastSerial;

    public void Write(string process, bool enabled, bool clickThrough, IReadOnlyList<RuleSpec> rules)
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

        string content = payload.ToJsonString();
        if (_lastContent.TryGetValue(process, out var last) && last == content)
        {
            return;
        }

        payload["serial"] = NextSerial();

        try
        {
            Directory.CreateDirectory(Paths.RuntimeDir);
            string target = Path.Combine(Paths.RuntimeDir, process + ".json");
            string temp = target + ".tmp";
            File.WriteAllText(temp, payload.ToJsonString(), new UTF8Encoding(false));
            File.Move(temp, target, overwrite: true);
            _lastContent[process] = content;
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
