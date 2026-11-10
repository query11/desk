using Xunit;

namespace Desk.Tests;

public class ThemeManagementTests
{
    private static string BuiltIns => Path.Combine(AppContext.BaseDirectory, "themes");

    [Fact]
    public void TrialExpiresWithoutAnyWindowAndRestoresOriginal()
    {
        using var dir = new TempDir();
        string file = dir.File("settings.json");
        var settings = new Settings { Theme = "desk-islands" };
        var clock = new ManualTime();
        var applied = new List<string>();
        var trial = new ThemeTrialController(settings, () => settings.Save(file), () => applied.Add(settings.Theme), clock);
        trial.Begin("desk-air");
        Assert.Equal(15, trial.SecondsLeft);
        Assert.Equal("desk-islands", Settings.Load(file).PendingThemeTrial!.OriginalTheme);
        clock.Advance(TimeSpan.FromSeconds(16));
        trial.Tick();
        Assert.Equal("desk-islands", settings.Theme);
        Assert.Null(Settings.Load(file).PendingThemeTrial);
        Assert.Equal(new[] { "desk-air", "desk-islands" }, applied);
    }

    [Fact]
    public void RestartRecoversUnconfirmedTrialEvenBeforeDeadline()
    {
        var settings = new Settings { Theme = "desk-air", PendingThemeTrial = new("desk-islands", "desk-air", DateTime.UtcNow.AddMinutes(1)) };
        Assert.True(ThemeTrialController.RecoverInterrupted(settings));
        Assert.Equal("desk-islands", settings.Theme);
        Assert.False(ThemeTrialController.RecoverInterrupted(settings));
    }

    [Fact]
    public void KeepCommitsButExpiredKeepRollsBack()
    {
        var settings = new Settings(); var clock = new ManualTime();
        var trial = new ThemeTrialController(settings, () => true, () => { }, clock);
        trial.Begin("desk-air"); trial.Keep();
        clock.Advance(TimeSpan.FromMinutes(1)); trial.Tick();
        Assert.Equal("desk-air", settings.Theme);
        Assert.Null(trial.Pending);
        trial.Begin("desk-dusk"); clock.Advance(TimeSpan.FromSeconds(15)); trial.Keep();
        Assert.Equal("desk-air", settings.Theme);
    }

    [Fact]
    public void RepeatedTrialKeepsFirstOriginalAndSelectionCancelsCountdown()
    {
        var settings = new Settings(); var clock = new ManualTime();
        var trial = new ThemeTrialController(settings, () => true, () => { }, clock);
        trial.Begin("desk-air"); trial.Begin("desk-dusk"); trial.Cancel();
        Assert.Equal("desk-islands", settings.Theme);
        trial.Begin("desk-air"); trial.Select("desk-focus");
        clock.Advance(TimeSpan.FromSeconds(20)); trial.Tick();
        Assert.Equal("desk-focus", settings.Theme); Assert.Null(trial.Pending);
    }

    [Fact]
    public void FailedJournalWriteDoesNotApplyTrial()
    {
        var settings = new Settings(); bool applied = false;
        var trial = new ThemeTrialController(settings, () => false, () => applied = true, new ManualTime());
        Assert.Throws<IOException>(() => trial.Begin("desk-air"));
        Assert.Equal("desk-islands", settings.Theme); Assert.Null(trial.Pending); Assert.False(applied);
    }

    [Fact]
    public void FailedRollbackCanRetryAfterStorageRecovers()
    {
        var settings = new Settings(); var clock = new ManualTime(); bool writable = true;
        var trial = new ThemeTrialController(settings, () => writable, () => { }, clock);
        trial.Begin("desk-air"); writable = false; clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Throws<IOException>(trial.Tick);
        Assert.NotNull(trial.Pending);
        writable = true; trial.Tick(); Assert.Equal("desk-islands", settings.Theme);
    }

    [Fact]
    public void PersonalThemeLifecyclePreservesAdjustmentsAndOriginalFiles()
    {
        using var dir = new TempDir(); var settings = new Settings();
        settings.SetOverride("desk-air", "iconSize", "30", null);
        var store = new ThemeLibraryStore(BuiltIns, dir.File("themes"), settings, () => true);
        string id = store.Duplicate("desk-air", "我的玻璃");
        Assert.Equal("30", Theme.Load(store.Find(id)).GetConstant("iconSize", true));
        store.Rename(id, "玻璃 \"A\"");
        Assert.Equal("玻璃 \"A\"", store.List().Single(t => t.Id == id).Name);
        store.Favorite(id); Assert.Equal(id, store.List()[0].Id);
        string export = dir.File("export.json");
        File.WriteAllText(export, "old content");
        store.Export(id, export);
        string imported = store.Import(export);
        Assert.NotEqual(id, imported);
        Assert.Equal("30", Theme.Load(store.Find(imported)).GetConstant("iconSize", false));
        string trash = store.Delete(id);
        Assert.True(File.Exists(trash)); Assert.DoesNotContain(store.List(), t => t.Id == id);
        Assert.True(File.Exists(Path.Combine(BuiltIns, "desk-air.json")));
    }

    [Fact]
    public void InvalidImportsAndLibraryOverwriteAreRejected()
    {
        using var dir = new TempDir(); var store = new ThemeLibraryStore(BuiltIns, dir.File("themes"), new Settings(), () => true);
        string broken = dir.File("bad.json"); File.WriteAllText(broken, "{bad}");
        Assert.ThrowsAny<Exception>(() => store.Import(broken));
        Assert.Throws<InvalidOperationException>(() => store.Delete("desk-air"));
        Assert.Throws<FileNotFoundException>(() => store.Delete("../desk-air"));
        Assert.Throws<InvalidOperationException>(() => store.Export("desk-air", Path.Combine(BuiltIns, "desk-air.json")));
        Assert.DoesNotContain(store.List(), t => t.IsUserTheme);
    }

    [Fact]
    public void FavoriteWriteFailureIsReversibleAndAdjustmentsExcludeDefaults()
    {
        using var dir = new TempDir(); var settings = new Settings();
        var store = new ThemeLibraryStore(BuiltIns, dir.File("themes"), settings, () => false);
        Assert.Throws<IOException>(() => store.Favorite("desk-air"));
        Assert.Empty(settings.FavoriteThemes);
        settings.SetOverride("desk-air", "iconSize", "28.0", null);
        settings.SetOverride("desk-air", "menuOpacity", "0.3", true);
        var change = Assert.Single(store.Adjustments("desk-air"));
        Assert.Equal("menuOpacity@light", change.Key); Assert.Equal("0.12", change.Before); Assert.Equal("0.3", change.After);
    }
}
