using System.Text.Json.Nodes;
using Xunit;

namespace Desk.Tests;

public class DisplayProfileTests
{
    private static Theme Theme() => Desk.Theme.Load(Path.Combine(AppContext.BaseDirectory, "themes", "desk-islands.json"));

    [Fact]
    public void LocalOverridesRespectModeAndNeverLeakToOtherThemes()
    {
        var profile = new DisplayPreferences { Enabled = true, ThemeOverrides = new()
            { ["desk-islands"] = new() { ["iconSize"] = "32", ["islandOpacity"] = "0.1" } } };
        var global = new Dictionary<string, string> { ["iconSize"] = "28", ["islandOpacity@light"] = "0.6" };
        var resolved = profile.Resolve("desk-islands", global);
        Assert.Equal("32", resolved["iconSize"]); Assert.False(resolved.ContainsKey("islandOpacity@light"));
        Assert.Equal("28", global["iconSize"]);
        Assert.Equal("28", profile.Resolve("desk-air", global)["iconSize"]);
        profile.Enabled = false; Assert.Equal("28", profile.Resolve("desk-islands", global)["iconSize"]);
    }

    [Fact]
    public void MaximizedWindowChangesOnlyItsOwnDisplay()
    {
        var settings = new Settings { MaximizeMode = MaximizeMode.Merge };
        var primary = DisplayRuntimePlanner.Build(Theme(), settings, null, "DISPLAY1", true, true, true);
        var secondary = DisplayRuntimePlanner.Build(Theme(), settings, null, "DISPLAY2", false, true, true);
        Assert.False(primary.Layout.Adaptive); Assert.True(secondary.Layout.Adaptive);
        Assert.Contains(secondary.Rules, r => r.Target == "Taskbar.TaskbarFrame" && r.Styles.Contains("HorizontalAlignment=Center"));
        Assert.DoesNotContain(primary.Rules, r => r.Target == "Taskbar.TaskbarFrame" && r.Styles.Contains("HorizontalAlignment=Center"));
    }

    [Fact]
    public void DisplayHasIndependentGeometryAndInformationIsland()
    {
        var settings = new Settings { MaximizeMode = MaximizeMode.Merge };
        var profile = new DisplayPreferences { Enabled = true, MaximizeMode = MaximizeMode.Keep, ShowMonitor = false,
            ThemeOverrides = new() { ["desk-islands"] = new() { ["iconSize"] = "32", ["buttonWidth"] = "54", ["islandOpacity@light"] = "0.15" } } };
        var isolated = DisplayRuntimePlanner.Build(Theme(), settings, profile, "DISPLAY2", true, true, true);
        var global = DisplayRuntimePlanner.Build(Theme(), settings, null, "DISPLAY1", false, true, true);
        Assert.True(isolated.Layout.Adaptive); Assert.False(isolated.Monitor.Enabled);
        Assert.Equal(32, isolated.Layout.IconSize); Assert.Equal(54, isolated.Layout.ButtonWidth);
        Assert.Equal(28, global.Layout.IconSize); Assert.True(global.Monitor.Enabled);
        Assert.All(isolated.Rules.Where(r => r.Target == "Taskbar.TaskbarFrame").SelectMany(r => r.Styles), s => Assert.False(s.StartsWith("MaxWidth=")));
    }

    [Fact]
    public void ProfilesRoundTripAndRuntimeContainsIndependentRules()
    {
        using var dir = new TempDir(); var settings = new Settings();
        settings.Displays["display-device"] = new DisplayPreferences { Enabled = true, MonitorCpu = false };
        settings.FavoriteThemes.Add("DESK-AIR");
        string file = dir.File("settings.json"); Assert.True(settings.Save(file));
        var loaded = Settings.Load(file);
        Assert.True(loaded.Displays.ContainsKey("DISPLAY-DEVICE")); Assert.Contains("desk-air", loaded.FavoriteThemes);
        var profiles = new[] {
            DisplayRuntimePlanner.Build(Theme(), settings, null, "DISPLAY1", true, true, true),
            DisplayRuntimePlanner.Build(Theme(), settings, loaded.Displays["display-device"], "DISPLAY2", false, true, true) };
        var writer = new RuntimeWriter(dir.File("runtime"));
        Assert.True(writer.Write("explorer.exe", true, false, [], displays: profiles));
        var root = JsonNode.Parse(File.ReadAllText(dir.File("runtime/explorer.exe.json")))!;
        Assert.Equal(2, root["displays"]!.AsArray().Count);
        Assert.False(root["displays"]![1]!["monitor"]!["cpu"]!.GetValue<bool>());
        Assert.True(root["displays"]![1]!["layout"]!["adaptive"]!.GetValue<bool>());
    }

    [Fact]
    public void AScreensInfoIslandFollowsTheGlobalSettingWhereItHasNoChoice()
    {
        var global = new DisplayDefaults(ShowMonitor: true, MonitorNetwork: true, MonitorCpu: false, MonitorMemory: true, MaximizeMode.Merge);
        var screen = new DisplayPreferences { MonitorNetwork = false, MonitorCpu = true };
        // Its own choices only count once its own settings are on.
        Assert.Equal(["network", "memory"], screen.MonitorItems(global));
        screen.Enabled = true;
        Assert.Equal(["cpu", "memory"], screen.MonitorItems(global));
        screen.ShowMonitor = false;
        Assert.Empty(screen.MonitorItems(global));
        Assert.Empty(new DisplayPreferences().MonitorItems(global with { ShowMonitor = false }));
    }
}
