using Xunit;

namespace Desk.Tests;

public class ThemeMenuTests
{
    private static List<List<string>> Groups(IReadOnlyList<ThemeMenuItem?> items)
    {
        var groups = new List<List<string>> { new() };
        foreach (var item in items)
        {
            if (item == null) groups.Add([]);
            else groups[^1].Add(item.Id);
        }
        return groups;
    }

    [Fact]
    public void TheTrayListsThemesLikeTheGallery()
    {
        using var dir = new TempDir();
        string user = Path.Combine(dir.Path, "user");
        Directory.CreateDirectory(user);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "themes", "desk-air.json"), Path.Combine(user, "my-look-1.json"));
        File.WriteAllText(Path.Combine(user, "broken.json"), "{ not json");
        var settings = new Settings { Theme = "desk-frost" };
        settings.FavoriteThemes.Add("desk-bar");
        var store = new ThemeLibraryStore(Path.Combine(AppContext.BaseDirectory, "themes"), user, settings, () => true);
        var options = store.List();
        var trial = new ThemeTrial("desk-air", "desk-frost", new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc));

        var items = ThemeMenu.Build(options, "desk-frost", trial);

        // The gallery's themes in the gallery's order...
        Assert.Equal(options.Select(o => o.Id), items.OfType<ThemeMenuItem>().Select(i => i.Id));
        // ...in its groups: favorites, recommended, the other built-in ones, mine.
        var groups = Groups(items);
        Assert.Equal(4, groups.Count);
        Assert.Equal(["desk-bar"], groups[0]);
        Assert.Equal(7, groups[1].Count);
        Assert.Equal("desk-air", groups[1][0]);
        Assert.Equal(["desk-islands", "desk-islands-clear"], groups[2].Order());
        Assert.Equal(["broken", "my-look-1"], groups[3].Order());

        var broken = items.OfType<ThemeMenuItem>().Single(i => i.Id == "broken");
        Assert.False(broken.Enabled);
        Assert.EndsWith("（无法读取）", broken.Text);
        var trying = Assert.Single(items.OfType<ThemeMenuItem>(), i => i.Checked);
        Assert.Equal("desk-frost", trying.Id);
        Assert.EndsWith("（试用中）", trying.Text);
    }

    [Fact]
    public void EmptyGroupsLeaveNoSeparators()
    {
        var store = new ThemeLibraryStore(Path.Combine(AppContext.BaseDirectory, "themes"), Path.Combine(Path.GetTempPath(), "desk-none-" + Guid.NewGuid().ToString("N")), new Settings(), () => true);
        var items = ThemeMenu.Build(store.List(), "desk-islands", null);
        // No favorites and no themes of the user's: recommended | the others.
        Assert.Equal(2, Groups(items).Count);
        Assert.NotNull(items[0]);
        Assert.NotNull(items[^1]);
        Assert.DoesNotContain(items, item => item?.Text.Contains('（') == true);
        Assert.Empty(ThemeMenu.Build([], "desk-islands", null));
    }
}
