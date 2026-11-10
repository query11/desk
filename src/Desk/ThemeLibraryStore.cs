using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Desk;

internal sealed record ThemeAdjustment(string Key, string Name, string Before, string After, bool? Light);

/// <summary>Theme file operations stay inside the personal library; imports get fresh identities.</summary>
internal sealed class ThemeLibraryStore(string builtInDirectory, string userDirectory, Settings settings, Func<bool> save)
{
    public IEnumerable<string> Files()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in new[] { userDirectory, builtInDirectory })
            if (Directory.Exists(directory))
                foreach (string file in Directory.GetFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                    if (seen.Add(Path.GetFileNameWithoutExtension(file))) yield return file;
    }

    public IReadOnlyList<ThemeOption> List() => Files().Select(file => ThemeOption.Read(file, IsPersonal(file),
        new Dictionary<string, string>(settings.OverridesFor(Path.GetFileNameWithoutExtension(file)))))
        .Select(option => option with { Favorite = settings.FavoriteThemes.Contains(option.Id) })
        .OrderByDescending(t => t.Favorite).ThenBy(t => t.IsUserTheme).ThenBy(t => t.Theme?.Order ?? 100).ThenBy(t => t.Name).ToList();

    public string Find(string id) => Files().FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), id, StringComparison.OrdinalIgnoreCase))
        ?? throw new FileNotFoundException("该方案已被移走，请刷新列表。");

    private bool IsPersonal(string file) => string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFullPath(userDirectory), StringComparison.OrdinalIgnoreCase);

    public void Favorite(string id)
    {
        _ = Find(id);
        bool wasFavorite = settings.FavoriteThemes.Remove(id);
        if (!wasFavorite) settings.FavoriteThemes.Add(id);
        if (save()) return;
        if (wasFavorite) settings.FavoriteThemes.Add(id); else settings.FavoriteThemes.Remove(id);
        throw new IOException("收藏没有保存成功，请检查设置目录权限。");
    }

    public string Duplicate(string id, string name)
    {
        Directory.CreateDirectory(userDirectory);
        string target = NewPersonalPath();
        Theme.Load(Find(id)).SaveCopy(target, name, settings.OverridesFor(id));
        return Path.GetFileNameWithoutExtension(target);
    }

    public void Rename(string id, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 60) throw new ArgumentException("名称需要 1–60 个字符。");
        string file = Find(id);
        if (!IsPersonal(file)) throw new InvalidOperationException("内置方案可以先复制，再重命名。");
        var root = ReadRoot(file);
        root["name"] = name;
        AtomicWrite(file, root);
    }

    public string Delete(string id)
    {
        string file = Find(id);
        if (!IsPersonal(file)) throw new InvalidOperationException("不能删除内置方案。");
        string trash = Path.Combine(userDirectory, ".trash");
        Directory.CreateDirectory(trash);
        string destination = Path.Combine(trash, id + "-" + Guid.NewGuid().ToString("N") + ".json");
        File.Move(file, destination);
        return destination;
    }

    public string Import(string source)
    {
        if (new FileInfo(source).Length > 2 * 1024 * 1024) throw new InvalidDataException("主题文件超过 2 MB。");
        // Parse before writing; never copy over a built-in id or another personal theme.
        var theme = Theme.Load(source);
        if (theme.Compile(ThemeTarget.Taskbar, true, false).Count + theme.Compile(ThemeTarget.StartMenu, true, false).Count == 0)
            throw new InvalidDataException("文件中没有任务栏或开始菜单样式。");
        Directory.CreateDirectory(userDirectory);
        string target = NewPersonalPath();
        theme.SaveCopy(target, theme.Name, new Dictionary<string, string>());
        return Path.GetFileNameWithoutExtension(target);
    }

    public void Export(string id, string destination)
    {
        if (Files().Any(file => string.Equals(Path.GetFullPath(file), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("请选择主题库以外的位置导出，避免覆盖正在使用的方案。");
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var theme = Theme.Load(Find(id));
            theme.SaveCopy(temp, theme.Name, settings.OverridesFor(id));
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public IReadOnlyList<ThemeAdjustment> Adjustments(string id)
    {
        var theme = Theme.Load(Find(id));
        var result = new List<ThemeAdjustment>();
        foreach (var (key, after) in settings.OverridesFor(id))
        {
            string name = key.Split('@')[0];
            bool? mode = key.EndsWith("@light", StringComparison.Ordinal) ? true : key.EndsWith("@dark", StringComparison.Ordinal) ? false : null;
            string before = theme.GetConstant(name, mode ?? true) ?? "未定义";
            string dark = theme.GetConstant(name, false) ?? "未定义";
            bool equal = before == after || (double.TryParse(before, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && double.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && Math.Abs(a - b) < .000001);
            if (mode == null && before != dark) { equal = false; before += " / " + dark; }
            if (!equal) result.Add(new(key, name, before, after, mode));
        }
        return result;
    }

    private string NewPersonalPath() => Path.Combine(userDirectory, "my-look-" + Guid.NewGuid().ToString("N") + ".json");
    private static JsonObject ReadRoot(string path) => JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
    private static void AtomicWrite(string file, JsonObject root)
    {
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
