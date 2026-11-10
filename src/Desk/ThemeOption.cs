namespace Desk;

/// <summary>A local theme and its saved adjustments. Broken files remain visible with an error.</summary>
internal sealed record ThemeOption(string Id, string Name, string Description, bool IsUserTheme,
    Theme? Theme, IReadOnlyDictionary<string, string> Overrides, string? Error = null)
{
    public bool Favorite { get; init; }
    public bool Featured => !IsUserTheme && Theme?.Featured == true;
    public string Tag => IsUserTheme ? "我的方案" : Theme?.Tag ?? "无法读取";
    public bool IsBar => Theme != null && !Theme.HasConstant("radius");

    public static ThemeOption Read(string file, bool user, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var values = overrides ?? new Dictionary<string, string>();
        try
        {
            var theme = Theme.Load(file);
            return new(theme.Id, theme.Name, theme.Description, user, theme, values);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or InvalidDataException or FormatException)
        {
            return new(Path.GetFileNameWithoutExtension(file), Path.GetFileNameWithoutExtension(file),
                "主题文件无法读取，请检查 JSON 格式或文件权限。", user, null, values, e.Message);
        }
    }
}

internal interface IThemeLibrary
{
    string ThemeId { get; set; }
    IReadOnlyList<ThemeOption> GetAppearanceOptions();
    void StartThemeTrial(string id);
    void ToggleFavorite(string id);
    string DuplicateTheme(string id, string name);
    void RenameTheme(string id, string name);
    void DeleteTheme(string id);
    string ImportTheme(string path);
    void ExportTheme(string id, string path);
    IReadOnlyList<ThemeAdjustment> GetThemeAdjustments(string id);
    void ResetThemeAdjustment(string id, string key);
}
