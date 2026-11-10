using Xunit;

namespace Desk.Tests;

public class GalleryPresetTests
{
    [Theory]
    [InlineData("desk-air")]
    [InlineData("desk-frost")]
    [InlineData("desk-dusk")]
    [InlineData("desk-focus")]
    [InlineData("desk-compact")]
    [InlineData("desk-capsule")]
    [InlineData("desk-minimal")]
    public void PairedPresetsCompileAndPreserveShellLayout(string id)
    {
        var option = ThemeOption.Read(Path.Combine(AppContext.BaseDirectory, "themes", id + ".json"), false);
        Assert.Null(option.Error);
        Assert.True(option.Featured);
        var theme = option.Theme!;
        foreach (bool light in new[] { true, false })
        foreach (bool maximized in new[] { true, false })
        {
            Assert.Equal(id == "desk-compact" ? "26" : "28", theme.GetConstant("iconSize", light));
            var taskbar = theme.Compile(ThemeTarget.Taskbar, light, maximized);
            var menu = theme.Compile(ThemeTarget.StartMenu, light, maximized);
            Assert.NotEmpty(taskbar);
            Assert.NotEmpty(menu);
            Assert.All(taskbar.Concat(menu).SelectMany(r => r.Styles), style => Assert.DoesNotContain('$', style));
            Assert.All(menu.SelectMany(r => r.Styles).Where(s => s.Contains("<DeskBlur")),
                style => Assert.Contains("BackdropSource=\"Backdrop\"", style));
            // No preset is allowed to reintroduce outer width constraints that caused overflow.
            Assert.All(taskbar.Where(r => r.Target == "Taskbar.TaskbarFrame").SelectMany(r => r.Styles),
                style => Assert.False(style.StartsWith("MaxWidth=")));
        }
    }

    [Fact]
    public void BrokenUserFileIsListedAsUnavailable()
    {
        using var dir = new TempDir();
        string file = dir.File("broken.json");
        File.WriteAllText(file, "{ broken json }");
        var option = ThemeOption.Read(file, true);
        Assert.NotNull(option.Error);
        Assert.Null(option.Theme);
        Assert.True(option.IsUserTheme);
    }

    [Fact]
    public void PersonalCopyRetainsBothMaterialsAndNeverBecomesRecommended()
    {
        using var dir = new TempDir();
        var theme = Theme.Load(Path.Combine(AppContext.BaseDirectory, "themes", "desk-air.json"));
        string file = dir.File("personal.json");
        theme.SaveCopy(file, "我的玻璃", new Dictionary<string, string> { ["menuOpacity@light"] = "0.3", ["iconSize"] = "30" });
        var option = ThemeOption.Read(file, true);
        Assert.False(option.Featured);
        Assert.Equal("0.3", option.Theme!.GetConstant("menuOpacity", true));
        Assert.Equal("0.16", option.Theme.GetConstant("menuOpacity", false));
        Assert.Equal("30", option.Theme.GetConstant("iconSize", true));
    }
}
