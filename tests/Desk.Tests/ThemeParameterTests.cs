using System.Text.RegularExpressions;
using Xunit;

namespace Desk.Tests;

/// <summary>
/// Slider changes belong to the theme they were made in; old settings with one
/// set of changes for all themes are moved once, to the theme they were made with.
/// </summary>
public class ThemeParameterTests
{
    private const string Islands = "desk-islands";
    private const string Clear = "desk-islands-clear";

    private static readonly string[] TaskbarPage =
        ["radius", "gap", "islandBlur", "islandOpacity", "islandLuminosity", "islandSaturation", "islandNoise"];

    private static readonly string[] StartMenuPage =
        ["menuRadius", "menuBlur", "menuOpacity", "menuLuminosity", "menuSaturation", "menuNoise"];

    private static Theme Load(string id) =>
        Theme.Load(Path.Combine(AppContext.BaseDirectory, "themes", id + ".json"));

    /// <summary>The TintOpacity the engine would get for the taskbar islands.</summary>
    private static string CompiledOpacity(Settings settings, string id, bool light)
    {
        var theme = Load(id);
        var rules = theme.Compile(ThemeTarget.Taskbar, light, false, settings.OverridesFor(theme.Id));
        string style = rules.SelectMany(r => r.Styles).First(s => s.Contains("TintOpacity"));
        return Regex.Match(style, "TintOpacity=\"([^\"]+)\"").Groups[1].Value;
    }

    [Fact]
    public void TheThemesHaveTheExpectedDefaults()
    {
        Assert.Equal("0.5", Load(Islands).GetConstant("islandOpacity", true));
        Assert.Equal("0.3", Load(Clear).GetConstant("islandOpacity", true));
    }

    [Fact]
    public void AChangeInOneThemeDoesNotCarryOverToAnother()
    {
        var settings = new Settings();
        // A: the regular islands at 0.8 (light).
        settings.SetOverride(Islands, "islandOpacity", "0.8", lightMode: true);
        Assert.Equal("0.8", CompiledOpacity(settings, Islands, light: true));

        // B: the clear theme without changes uses its own default.
        Assert.Equal("0.3", CompiledOpacity(settings, Clear, light: true));

        // B adjusted on its own; A keeps its value.
        settings.SetOverride(Clear, "islandOpacity", "0.2", lightMode: true);
        Assert.Equal("0.2", CompiledOpacity(settings, Clear, light: true));
        Assert.Equal("0.8", CompiledOpacity(settings, Islands, light: true));
    }

    [Fact]
    public void LightAndDarkAreSeparate()
    {
        var settings = new Settings();
        settings.SetOverride(Islands, "islandOpacity", "0.8", lightMode: true);
        Assert.Equal("0.45", CompiledOpacity(settings, Islands, light: false));
        settings.SetOverride(Islands, "islandOpacity", "0.6", lightMode: false);
        Assert.Equal("0.6", CompiledOpacity(settings, Islands, light: false));
        Assert.Equal("0.8", CompiledOpacity(settings, Islands, light: true));

        // A value for both modes replaces the per-mode ones.
        settings.SetOverride(Islands, "islandOpacity", "0.7", lightMode: null);
        Assert.Equal("0.7", CompiledOpacity(settings, Islands, light: true));
        Assert.Equal("0.7", CompiledOpacity(settings, Islands, light: false));
    }

    [Fact]
    public void ChangesSurviveARestart()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        var settings = new Settings { Theme = Clear };
        settings.SetOverride(Islands, "islandOpacity", "0.8", lightMode: true);
        settings.SetOverride(Clear, "islandBlur", "12", lightMode: null);
        Assert.True(settings.Save(path));

        var loaded = Settings.Load(path);
        Assert.Equal("0.8", CompiledOpacity(loaded, Islands, light: true));
        Assert.Equal("0.3", CompiledOpacity(loaded, Clear, light: true));
        Assert.Equal("12", Load(Clear).GetConstant("islandBlur", true, loaded.OverridesFor(Clear)));
        // Theme ids are file names: any case.
        Assert.Equal("0.8", loaded.OverridesFor("DESK-ISLANDS")["islandOpacity@light"]);
        Assert.DoesNotContain("\"Overrides\"", File.ReadAllText(path));
    }

    [Fact]
    public void ResetOnlyAffectsThisThemeAndThisPage()
    {
        var settings = new Settings();
        settings.SetOverride(Islands, "islandOpacity", "0.8", lightMode: true);
        settings.SetOverride(Islands, "islandOpacity", "0.6", lightMode: false);
        settings.SetOverride(Islands, "radius", "4", lightMode: null);
        settings.SetOverride(Islands, "menuOpacity", "0.4", lightMode: true);
        settings.SetOverride(Clear, "islandOpacity", "0.2", lightMode: true);

        Assert.True(settings.HasOverrides(Islands, TaskbarPage));
        settings.ResetOverrides(Islands, TaskbarPage);

        Assert.False(settings.HasOverrides(Islands, TaskbarPage));
        Assert.Equal("0.5", CompiledOpacity(settings, Islands, light: true));
        Assert.Equal("0.45", CompiledOpacity(settings, Islands, light: false));
        Assert.True(settings.HasOverrides(Islands, StartMenuPage));
        Assert.Equal("0.2", CompiledOpacity(settings, Clear, light: true));
    }

    [Fact]
    public void OldSettingsAreMovedToTheirThemeOnce()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        File.WriteAllText(path, """
            {
              "Enabled": true,
              "Theme": "desk-islands",
              "Overrides": { "islandOpacity@light": "0.8", "radius": "6" }
            }
            """);

        var first = Settings.Load(path);
        Assert.Equal("0.8", first.OverridesFor(Islands)["islandOpacity@light"]);
        Assert.Empty(first.OverridesFor(Clear));
        Assert.Equal("0.3", CompiledOpacity(first, Clear, light: true));
        Assert.Null(first.LegacyOverrides);
        Assert.Equal(Islands, first.MigratedOverrides!.Theme);
        Assert.Equal("6", first.MigratedOverrides.Values["radius"]);
        string saved = File.ReadAllText(path);
        Assert.DoesNotContain("\"Overrides\"", saved);

        // Loading again (and migrating again) changes nothing.
        var second = Settings.Load(path);
        Assert.False(second.MigrateLegacyOverrides());
        Assert.Equal(saved, File.ReadAllText(path));
        Assert.Equal(first.OverridesFor(Islands), second.OverridesFor(Islands));
    }

    [Fact]
    public void AnInterruptedMigrationDoesNotOverwriteNewerValues()
    {
        // Old values are still there, and the theme already has its own.
        var settings = new Settings
        {
            Theme = Islands,
            LegacyOverrides = new() { ["islandOpacity@light"] = "0.8", ["gap"] = "2" },
        };
        settings.SetOverride(Islands, "islandOpacity", "0.9", lightMode: true);
        Assert.True(settings.MigrateLegacyOverrides());
        Assert.Equal("0.9", settings.OverridesFor(Islands)["islandOpacity@light"]);
        Assert.Equal("2", settings.OverridesFor(Islands)["gap"]);
        Assert.False(settings.MigrateLegacyOverrides());
    }

    [Fact]
    public void AMissingUserThemeStillGetsItsOldValues()
    {
        // The selected user theme isn't loadable right now; Desk shows the
        // default theme meanwhile. The values go to the selected theme.
        var settings = new Settings
        {
            Theme = "my-theme",
            LegacyOverrides = new() { ["islandOpacity@light"] = "0.8" },
        };
        settings.MigrateLegacyOverrides();
        Assert.Equal("0.8", settings.OverridesFor("my-theme")["islandOpacity@light"]);
        Assert.Empty(settings.OverridesFor(Islands));
    }

    [Fact]
    public void WithoutASelectedThemeTheOldValuesAreKept()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        File.WriteAllText(path, """{ "Theme": "", "Overrides": { "radius": "6" } }""");
        var settings = Settings.Load(path);
        Assert.Empty(settings.ThemeOverrides);
        Assert.Equal("6", settings.LegacyOverrides!["radius"]);

        settings.Save(path);
        Assert.Equal("6", Settings.Load(path).LegacyOverrides!["radius"]);
    }
}
