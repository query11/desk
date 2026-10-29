using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Desk;

internal sealed record RuleSpec(string Target, IReadOnlyList<string> Styles);

internal enum ThemeTarget
{
    Taskbar,
    StartMenu,
}

/// <summary>
/// A theme file. Rules use the target/style syntax of the Windows 11 Taskbar
/// Styler mod for Windhawk; `$name` refers to a constant, and constants can
/// have separate light and dark values.
/// </summary>
internal sealed class Theme
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required string FilePath { get; init; }

    private readonly Dictionary<string, (string Light, string Dark)> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RuleSpec>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public static Theme Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new InvalidDataException("The theme must be a JSON object.");

        string id = System.IO.Path.GetFileNameWithoutExtension(path);
        var theme = new Theme
        {
            Id = id,
            Name = root["name"]?.GetValue<string>() ?? id,
            Description = root["description"]?.GetValue<string>() ?? "",
            FilePath = path,
        };

        if (root["constants"] is JsonObject constants)
        {
            foreach (var (name, value) in constants)
            {
                switch (value)
                {
                    case JsonObject variants:
                        string light = Scalar(variants["light"]) ?? Scalar(variants["dark"]) ?? "";
                        string dark = Scalar(variants["dark"]) ?? light;
                        theme._constants[name] = (light, dark);
                        break;
                    default:
                        string text = Scalar(value) ?? "";
                        theme._constants[name] = (text, text);
                        break;
                }
            }
        }

        foreach (var target in new[] { "taskbar", "startMenu" })
        {
            if (root[target] is not JsonObject sections)
            {
                continue;
            }
            foreach (var (sectionName, rules) in sections)
            {
                if (rules is JsonArray array)
                {
                    theme._sections[$"{target}.{sectionName}"] = ParseRules(array);
                }
            }
        }

        return theme;
    }

    /// <summary>Peeks at a theme file's name and description without fully loading it.</summary>
    public static (string Name, string Description) ReadInfo(string path)
    {
        string fallback = System.IO.Path.GetFileNameWithoutExtension(path);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = document.RootElement;
            string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? fallback
                : fallback;
            string description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : "";
            return (name, description);
        }
        catch
        {
            return (fallback, "");
        }
    }

    public bool HasConstant(string name) => _constants.ContainsKey(name);

    /// <summary>
    /// A constant's value in a Windows mode. Overrides (from the settings
    /// window) are named "name@light" / "name@dark" for one mode, or "name".
    /// </summary>
    public string? GetConstant(string name, bool lightMode, IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (!_constants.TryGetValue(name, out var value))
        {
            return null;
        }
        if (overrides != null)
        {
            if (overrides.TryGetValue($"{name}@{(lightMode ? "light" : "dark")}", out var modeValue))
            {
                return modeValue;
            }
            if (overrides.TryGetValue(name, out var anyValue))
            {
                return anyValue;
            }
        }
        return lightMode ? value.Light : value.Dark;
    }

    /// <summary>
    /// The rules to send to the engine: `common` plus `maximized` (when a
    /// window is maximized and the theme should merge) or `floating`.
    /// </summary>
    public List<RuleSpec> Compile(ThemeTarget target, bool lightMode, bool maximized,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        string prefix = target == ThemeTarget.Taskbar ? "taskbar" : "startMenu";
        var rules = new List<RuleSpec>();
        AddSection(rules, $"{prefix}.common");
        AddSection(rules, maximized ? $"{prefix}.maximized" : $"{prefix}.floating");

        var constants = _constants.Keys
            .Select(name => (Name: "$" + name, Value: GetConstant(name, lightMode, overrides) ?? ""))
            .OrderByDescending(c => c.Name.Length)
            .ToList();

        return rules
            .Select(r => new RuleSpec(Substitute(r.Target, constants),
                r.Styles.Select(s => Substitute(s, constants)).ToList()))
            .ToList();
    }

    private void AddSection(List<RuleSpec> rules, string key)
    {
        if (_sections.TryGetValue(key, out var section))
        {
            rules.AddRange(section);
        }
    }

    private static List<RuleSpec> ParseRules(JsonArray array)
    {
        var rules = new List<RuleSpec>();
        foreach (var item in array)
        {
            if (item is not JsonObject rule || rule["target"]?.GetValue<string>() is not { } target)
            {
                continue;
            }
            var styles = new List<string>();
            if (rule["styles"] is JsonArray styleArray)
            {
                foreach (var style in styleArray)
                {
                    if (Scalar(style) is { } text)
                    {
                        styles.Add(text);
                    }
                }
            }
            rules.Add(new RuleSpec(target, styles));
        }
        return rules;
    }

    private static string? Scalar(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue(out string? text) => text,
        JsonValue value => value.ToJsonString(),
        _ => null,
    };

    // Constants can refer to other constants, so substitute a few times.
    private static string Substitute(string text, List<(string Name, string Value)> constants)
    {
        for (int pass = 0; pass < 4 && text.Contains('$'); pass++)
        {
            var builder = new StringBuilder(text);
            foreach (var (name, value) in constants)
            {
                builder.Replace(name, value);
            }
            string next = builder.ToString();
            if (next == text)
            {
                break;
            }
            text = next;
        }
        return text;
    }
}
