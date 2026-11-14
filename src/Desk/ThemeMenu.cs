namespace Desk;

/// <summary>A theme in the tray's 主题 menu.</summary>
internal sealed record ThemeMenuItem(string Id, string Text, bool Enabled, bool Checked);

/// <summary>The tray's 主题 menu, kept apart from Windows Forms so it can be tested.</summary>
internal static class ThemeMenu
{
    /// <summary>
    /// The themes in the gallery's order (GetAppearanceOptions), in its
    /// groups: favorites, recommended, the other built-in themes, the user's
    /// own. A null between two groups is a separator. Themes whose file
    /// can't be read are listed greyed out; the one on trial says so.
    /// </summary>
    public static IReadOnlyList<ThemeMenuItem?> Build(IReadOnlyList<ThemeOption> options, string current, ThemeTrial? trial)
    {
        var items = new List<ThemeMenuItem?>();
        // GroupBy keeps the order within a group, and the groups are put in theirs.
        foreach (var group in options.GroupBy(Group).OrderBy(group => group.Key))
        {
            if (items.Count > 0) items.Add(null);
            foreach (var option in group)
            {
                bool usable = option.Error == null;
                bool trying = trial != null && Same(trial.TrialTheme, option.Id);
                string note = !usable ? "（无法读取）" : trying ? "（试用中）" : "";
                items.Add(new ThemeMenuItem(option.Id, option.Name + note, usable, Same(option.Id, current)));
            }
        }
        return items;
    }

    private static int Group(ThemeOption option) =>
        option.Favorite ? 0 : option.Featured ? 1 : !option.IsUserTheme ? 2 : 3;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
