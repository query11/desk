using Xunit;

namespace Desk.Tests;

public class AppearanceTests
{
    [Theory]
    [InlineData("desk-islands")]
    [InlineData("desk-islands-clear")]
    [InlineData("desk-bar")]
    public void IconAndSpacingCompileIndependently(string id)
    {
        var theme = Theme.Load(Path.Combine(AppContext.BaseDirectory, "themes", id + ".json"));
        var overrides = new Dictionary<string, string> { ["iconSize"] = "28", ["buttonWidth"] = "56" };
        var rules = theme.Compile(ThemeTarget.Taskbar, true, false, overrides);
        Assert.Contains(rules, rule => rule.Target.Contains("Image#Icon") && rule.Styles.Contains("Width=28"));
        Assert.Contains(rules, rule => rule.Styles.Contains("MinWidth=56"));
        overrides["iconSize"] = "30";
        rules = theme.Compile(ThemeTarget.Taskbar, true, false, overrides);
        Assert.Contains(rules, rule => rule.Target.Contains("Image#Icon") && rule.Styles.Contains("Width=30"));
        Assert.Contains(rules, rule => rule.Styles.Contains("MinWidth=56"));
    }

    [Fact]
    public void RuntimeLayoutIsExplicitAndEmergencyRestoreRemovesIt()
    {
        using var dir = new TempDir();
        var writer = new RuntimeWriter(dir.File("runtime"));
        Assert.True(writer.Write(RuntimeWriter.ExplorerFile, true, false, [],
            layout: new TaskbarLayoutConfig(true, 52, 28)));
        string file = dir.File("runtime/explorer.exe.json");
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)))
        {
            var layout = doc.RootElement.GetProperty("layout");
            Assert.True(layout.GetProperty("adaptive").GetBoolean());
            Assert.Equal(52, layout.GetProperty("buttonWidth").GetDouble());
        }
        Assert.True(writer.WriteDisabledConfigurations());
        using var disabled = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        Assert.False(disabled.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(disabled.RootElement.TryGetProperty("layout", out _));
    }

    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static Dictionary<string, string> Snapshot(Settings settings, string theme = "desk-islands") =>
        new(settings.OverridesFor(theme));

    [Fact]
    public void FailedUndoSaveRetainsValuesAndHistoryForRetry()
    {
        var settings = new Settings();
        var history = new AppearanceHistory();
        var before = Snapshot(settings);
        settings.SetOverride(settings.Theme, "iconSize", "30", null);
        history.Record(settings.Theme, ParameterScope.Taskbar, before, Snapshot(settings), null, Now);
        Assert.Throws<IOException>(() => history.Undo(settings, settings.Theme, ParameterScope.Taskbar, () => false));
        Assert.Equal("30", Snapshot(settings)["iconSize"]);
        Assert.True(history.CanUndo(settings.Theme, ParameterScope.Taskbar));
        Assert.True(history.Undo(settings, settings.Theme, ParameterScope.Taskbar, () => true));
        Assert.Empty(Snapshot(settings));
        Assert.False(history.CanUndo(settings.Theme, ParameterScope.Taskbar));
    }

    [Fact]
    public void ContinuousSizeAdjustmentsUndoTogetherIncludingButtonWidth()
    {
        var settings = new Settings();
        var history = new AppearanceHistory();
        foreach (int size in new[] { 26, 28, 30 })
        {
            var before = Snapshot(settings);
            settings.SetOverride(settings.Theme, "iconSize", size.ToString(), null);
            settings.SetOverride(settings.Theme, "buttonWidth", (size + 20).ToString(), null);
            history.Record(settings.Theme, ParameterScope.Taskbar, before, Snapshot(settings), "iconSize", Now.AddMilliseconds(size * 10));
        }
        Assert.True(history.Undo(settings, settings.Theme, ParameterScope.Taskbar));
        Assert.Empty(Snapshot(settings));
        Assert.False(history.CanUndo(settings.Theme, ParameterScope.Taskbar));
    }

    [Fact]
    public void UndoDoesNotRevertAnotherPageOrTheme()
    {
        var settings = new Settings();
        var history = new AppearanceHistory();
        var before = Snapshot(settings);
        settings.SetOverride(settings.Theme, "menuBlur", "18", null);
        history.Record(settings.Theme, ParameterScope.StartMenu, before, Snapshot(settings), null, Now);
        settings.SetOverride(settings.Theme, "iconSize", "28", null);
        settings.SetOverride("desk-islands-clear", "menuBlur", "40", null);
        history.Undo(settings, settings.Theme, ParameterScope.StartMenu);
        Assert.Equal("28", Snapshot(settings)["iconSize"]);
        Assert.DoesNotContain("menuBlur", Snapshot(settings));
        Assert.Equal("40", settings.OverridesFor("desk-islands-clear")["menuBlur"]);
    }

    [Fact]
    public void UndoRestoresModeSpecificKeysRemovedByASharedValue()
    {
        var settings = new Settings();
        settings.SetOverride(settings.Theme, "menuBlur", "18", true);
        settings.SetOverride(settings.Theme, "menuBlur", "24", false);
        var before = Snapshot(settings);
        settings.SetOverride(settings.Theme, "menuBlur", "30", null);
        var history = new AppearanceHistory();
        history.Record(settings.Theme, ParameterScope.StartMenu, before, Snapshot(settings), null, Now);
        history.Undo(settings, settings.Theme, ParameterScope.StartMenu);
        Assert.Equal(before, Snapshot(settings));
    }

    [Fact]
    public void UndoSkipsKeysThatWereChangedOutsideItsHistory()
    {
        var settings = new Settings();
        var history = new AppearanceHistory();
        var before = Snapshot(settings);
        settings.SetOverride(settings.Theme, "menuBlur", "18", null);
        history.Record(settings.Theme, ParameterScope.StartMenu, before, Snapshot(settings), null, Now);
        settings.SetOverride(settings.Theme, "menuBlur", "40", null);
        history.Undo(settings, settings.Theme, ParameterScope.StartMenu);
        Assert.Equal("40", Snapshot(settings)["menuBlur"]);
    }

    [Fact]
    public void PresetsOnlyChangeMaterialAndTheSelectedColorMode()
    {
        var settings = new Settings();
        settings.SetOverride(settings.Theme, "iconSize", "28", null);
        settings.SetOverride(settings.Theme, "radius", "5", null);
        settings.SetOverride(settings.Theme, "islandOpacity", "0.6", false);
        foreach (var (key, value) in MaterialPresets.All[0].Values(ParameterScope.Taskbar, true))
        {
            var parts = key.Split('@');
            settings.SetOverride(settings.Theme, parts[0], value, parts.Length > 1 ? true : null);
        }
        var values = Snapshot(settings);
        Assert.Equal("28", values["iconSize"]);
        Assert.Equal("5", values["radius"]);
        Assert.Equal("0.6", values["islandOpacity@dark"]);
        Assert.Equal("0.12", values["islandOpacity@light"]);
        Assert.DoesNotContain(values.Keys, key => key.StartsWith("menu"));
    }

    [Fact]
    public void SavedThemeCompilesIdenticallyInBothModesAndLayoutsWithoutOverwriting()
    {
        var theme = Theme.Load(Path.Combine(AppContext.BaseDirectory, "themes", "desk-islands.json"));
        var settings = new Settings();
        settings.SetOverride(theme.Id, "iconSize", "28", null);
        settings.SetOverride(theme.Id, "menuOpacity", "0.12", true);
        settings.SetOverride(theme.Id, "menuOpacity", "0.25", false);
        using var dir = new TempDir();
        string path = dir.File("my-look.json");
        theme.SaveCopy(path, "我的玻璃", settings.OverridesFor(theme.Id));
        var saved = Theme.Load(path);
        Assert.Equal("我的玻璃", saved.Name);
        foreach (bool light in new[] { true, false })
        foreach (bool maximized in new[] { true, false })
        foreach (var target in new[] { ThemeTarget.Taskbar, ThemeTarget.StartMenu })
        {
            var expected = theme.Compile(target, light, maximized, settings.OverridesFor(theme.Id));
            var actual = saved.Compile(target, light, maximized);
            Assert.Equal(expected.Select(r => r.Target), actual.Select(r => r.Target));
            Assert.Equal(expected.SelectMany(r => r.Styles), actual.SelectMany(r => r.Styles));
        }
        string original = File.ReadAllText(path);
        Assert.Throws<IOException>(() => theme.SaveCopy(path, "另一个", settings.OverridesFor(theme.Id)));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void ResetCanBeUndoneWithoutTouchingTheOtherPage()
    {
        var settings = new Settings();
        settings.SetOverride(settings.Theme, "iconSize", "30", null);
        settings.SetOverride(settings.Theme, "islandOpacity", "0.2", true);
        settings.SetOverride(settings.Theme, "menuBlur", "18", null);
        var before = Snapshot(settings);
        settings.ResetOverrides(settings.Theme, ["iconSize", "islandOpacity"]);
        var history = new AppearanceHistory();
        history.Record(settings.Theme, ParameterScope.Taskbar, before, Snapshot(settings), null, Now);
        history.Undo(settings, settings.Theme, ParameterScope.Taskbar);
        Assert.Equal(before, Snapshot(settings));
    }

    // ---- editing the mode Windows isn't in

    [Fact]
    public void EditingTheOtherModeChangesOnlyItsValueAndUndoRestoresOnlyIt()
    {
        var theme = Theme.Load(Path.Combine(AppContext.BaseDirectory, "themes", "desk-islands.json"));
        var settings = new Settings();
        var history = new AppearanceHistory();
        // Windows shows light, and its tint was tuned first.
        OverrideEdits.Set(settings, history, theme.Id, ParameterScope.Taskbar, "islandOpacity", 0.3, perMode: true, light: true, Now);
        string? darkBefore = theme.GetConstant("islandOpacity", false, settings.OverridesFor(theme.Id));

        // Then the dark tint, without switching Windows.
        OverrideEdits.Set(settings, history, theme.Id, ParameterScope.Taskbar, "islandOpacity", 0.55, perMode: true, light: false, Now.AddSeconds(5));
        var values = Snapshot(settings, theme.Id);
        Assert.Equal("0.55", values["islandOpacity@dark"]);
        Assert.Equal("0.3", values["islandOpacity@light"]);
        Assert.DoesNotContain("islandOpacity", values.Keys);
        Assert.Equal("0.3", theme.GetConstant("islandOpacity", true, settings.OverridesFor(theme.Id)));

        // Values that aren't kept per mode stay one value for both.
        OverrideEdits.Set(settings, history, theme.Id, ParameterScope.Taskbar, "radius", 12, perMode: false, light: false, Now.AddSeconds(6));
        Assert.Equal("12", Snapshot(settings, theme.Id)["radius"]);

        // Undo while editing dark takes back the dark edits only; the light
        // one stays, and stays undoable where it was made.
        Assert.True(history.Undo(settings, theme.Id, ParameterScope.Taskbar, light: false));
        Assert.True(history.Undo(settings, theme.Id, ParameterScope.Taskbar, light: false));
        Assert.False(history.CanUndo(theme.Id, ParameterScope.Taskbar, light: false));
        Assert.Equal(darkBefore, theme.GetConstant("islandOpacity", false, settings.OverridesFor(theme.Id)));
        Assert.Equal("0.3", Snapshot(settings, theme.Id)["islandOpacity@light"]);
        Assert.DoesNotContain("radius", Snapshot(settings, theme.Id).Keys);
        Assert.True(history.CanUndo(theme.Id, ParameterScope.Taskbar, light: true));
        Assert.True(history.Undo(settings, theme.Id, ParameterScope.Taskbar, light: true));
        Assert.Empty(Snapshot(settings, theme.Id));
    }

    [Fact]
    public void ResettingOneModeKeepsTheOthersValue()
    {
        var settings = new Settings();
        var history = new AppearanceHistory();
        // One value for both modes, as older settings may have it.
        settings.SetOverride(settings.Theme, "menuOpacity", "0.4", null);
        Assert.True(settings.HasOverrides(settings.Theme, ["menuOpacity"], false));
        OverrideEdits.Reset(settings, history, settings.Theme, ParameterScope.StartMenu, ["menuOpacity"], light: false,
            onlyThisMode: true, Now);
        var values = Snapshot(settings);
        Assert.Equal("0.4", values["menuOpacity@light"]);
        Assert.DoesNotContain("menuOpacity", values.Keys);
        Assert.DoesNotContain("menuOpacity@dark", values.Keys);
        Assert.False(settings.HasOverrides(settings.Theme, ["menuOpacity"], false));
        Assert.True(settings.HasOverrides(settings.Theme, ["menuOpacity"], true));
        Assert.True(history.Undo(settings, settings.Theme, ParameterScope.StartMenu, light: false));
        Assert.Equal("0.4", Snapshot(settings)["menuOpacity"]);
    }

    [Fact]
    public void APresetForTheOtherModeOnlySetsThatModesTint()
    {
        var settings = new Settings();
        var history = new AppearanceHistory();
        settings.SetOverride(settings.Theme, "islandOpacity", "0.6", true);
        OverrideEdits.Preset(settings, history, settings.Theme, ParameterScope.Taskbar, MaterialPresets.All[0], light: false,
            _ => true, Now);
        var values = Snapshot(settings);
        Assert.Equal("0.6", values["islandOpacity@light"]);
        Assert.Equal("0.16", values["islandOpacity@dark"]);
        Assert.Equal("0.2", values["islandLuminosity@dark"]);
        Assert.DoesNotContain("islandLuminosity@light", values.Keys);
        Assert.Equal("18", values["islandBlur"]);
        Assert.False(history.CanUndo(settings.Theme, ParameterScope.Taskbar, light: true));
    }

    [Fact]
    public void AnEditThatCanNotBeSavedIsTakenBack()
    {
        var settings = new Settings();
        var history = new AppearanceHistory();
        settings.SetOverride(settings.Theme, "radius", "8", null);
        var before = Snapshot(settings);
        Assert.Throws<IOException>(() => OverrideEdits.Reset(settings, history, settings.Theme, ParameterScope.Taskbar,
            ["radius"], true, false, Now, () => false));
        Assert.Equal(before, Snapshot(settings));
        Assert.Throws<IOException>(() => OverrideEdits.Set(settings, history, settings.Theme, ParameterScope.Taskbar,
            "radius", 3, false, true, Now, () => false));
        Assert.Equal(before, Snapshot(settings));
        Assert.False(history.CanUndo(settings.Theme, ParameterScope.Taskbar, light: true));
    }
}
