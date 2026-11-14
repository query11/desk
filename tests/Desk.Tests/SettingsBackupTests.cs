using System.IO.Compression;
using System.Reflection;
using System.Text;
using Xunit;

namespace Desk.Tests;

public class SettingsBackupTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 6, 0, 0, DateTimeKind.Utc);
    private static string BuiltIn(string id) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "themes", id + ".json"));

    private static Settings Tuned()
    {
        var settings = new Settings
        {
            Theme = "my-look-1", MaximizeMode = MaximizeMode.Keep, StyleStartMenu = false, StartMenuScheme = StartMenuScheme.Dark,
            ShowMonitor = false, MonitorCpu = false, ClickThrough = true,
        };
        settings.FavoriteThemes.Add("desk-dusk");
        settings.SetOverride("my-look-1", "radius", "12", null);
        settings.SetOverride("my-look-1", "islandOpacity", "0.3", false);
        settings.Displays["MONITOR\\A"] = new DisplayPreferences { Enabled = true, ShowMonitor = false };
        return settings;
    }

    private static (TempDir Dir, string Themes, string Zip) Backup(Settings settings)
    {
        var dir = new TempDir();
        string themes = dir.File("themes");
        Directory.CreateDirectory(themes);
        File.WriteAllText(Path.Combine(themes, "my-look-1.json"), BuiltIn("desk-air"));
        File.WriteAllText(Path.Combine(themes, "my-look-2.json"), BuiltIn("desk-dusk"));
        Directory.CreateDirectory(Path.Combine(themes, ".trash"));
        File.WriteAllText(Path.Combine(themes, ".trash", "old.json"), BuiltIn("desk-bar"));
        string zip = dir.File("backup.zip");
        SettingsBackup.Create(zip, settings, themes, "0.6.0", Now);
        return (dir, themes, zip);
    }

    [Fact]
    public void ARestoreBringsBackTheSettingsAndThePersonalThemes()
    {
        var (dir, themes, zip) = Backup(Tuned());
        using var _ = dir;
        // Since then: other settings, one theme changed, one removed, one new.
        var current = new Settings { Theme = "desk-frost", Enabled = false, AutostartConfigured = true, AutostartPath = @"C:\Desk\Desk.exe",
            WelcomeShown = true, Window = new WindowPlacement { X = 10, Y = 10, Width = 900, Height = 700 } };
        File.WriteAllText(Path.Combine(themes, "my-look-1.json"), BuiltIn("desk-minimal"));
        File.Delete(Path.Combine(themes, "my-look-2.json"));
        File.WriteAllText(Path.Combine(themes, "my-look-3.json"), BuiltIn("desk-capsule"));

        var backup = SettingsBackup.Read(zip);
        Assert.Equal(Now, backup.Created?.ToUniversalTime());
        Assert.Equal("0.6.0", backup.DeskVersion);
        Assert.Equal(["my-look-1.json", "my-look-2.json"], backup.Themes.Select(t => t.FileName));
        SettingsBackup.Restore(backup, current, themes, () => true);

        Assert.Equal(Tuned().ToJson(), WithMachine(current, Tuned()).ToJson());
        Assert.Equal(BuiltIn("desk-air"), File.ReadAllText(Path.Combine(themes, "my-look-1.json")));
        Assert.Equal(BuiltIn("desk-dusk"), File.ReadAllText(Path.Combine(themes, "my-look-2.json")));
        // Not in the backup, so left alone; the replaced version went to .trash.
        Assert.True(File.Exists(Path.Combine(themes, "my-look-3.json")));
        Assert.Contains(Directory.GetFiles(Path.Combine(themes, ".trash")), f => File.ReadAllText(f) == BuiltIn("desk-minimal"));
        // What belongs to this computer and this moment stays.
        Assert.False(current.Enabled);
        Assert.True(current.AutostartConfigured);
        Assert.Equal(@"C:\Desk\Desk.exe", current.AutostartPath);
        Assert.True(current.WelcomeShown);
        Assert.Equal(10, current.Window!.X);
    }

    /// <summary>The settings with the values a restore keeps from the computer copied over, to compare the rest.</summary>
    private static Settings WithMachine(Settings restored, Settings backup)
    {
        var copy = Settings.Parse(restored.ToJson());
        copy.Enabled = backup.Enabled;
        copy.AutostartConfigured = backup.AutostartConfigured;
        copy.AutostartPath = backup.AutostartPath;
        copy.WelcomeShown = backup.WelcomeShown;
        copy.CloseTipShown = backup.CloseTipShown;
        copy.Window = backup.Window;
        return copy;
    }

    [Fact]
    public void EverySettingIsEitherRestoredOrKept()
    {
        // A new setting has to be decided on here: does it come back with a backup?
        string[] kept = ["Enabled", "AutostartConfigured", "AutostartPath", "WelcomeShown", "CloseTipShown", "Window", "PendingThemeTrial"];
        string[] restored = ["Theme", "MaximizeMode", "StyleStartMenu", "StartMenuScheme", "ShowMonitor", "MonitorNetwork", "MonitorCpu",
            "MonitorMemory", "ClickThrough", "FavoriteThemes", "Displays", "ThemeOverrides", "LegacyOverrides", "MigratedOverrides"];
        var properties = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order();
        Assert.Equal(kept.Concat(restored).Order(), properties);
    }

    [Fact]
    public void ATrialRunningInTheBackupDoesNotComeBack()
    {
        var settings = Tuned();
        settings.Theme = "desk-frost";
        settings.PendingThemeTrial = new ThemeTrial("desk-dusk", "desk-frost", Now.AddSeconds(10));
        var (dir, themes, zip) = Backup(settings);
        using var _ = dir;
        var current = new Settings();
        SettingsBackup.Restore(SettingsBackup.Read(zip), current, themes, () => true);
        Assert.Equal("desk-dusk", current.Theme);
        Assert.Null(current.PendingThemeTrial);
    }

    [Fact]
    public void ARestoreThatCanNotBeSavedChangesNothing()
    {
        var (dir, themes, zip) = Backup(Tuned());
        using var _ = dir;
        var current = new Settings { Theme = "desk-frost" };
        current.SetOverride("desk-frost", "radius", "3", null);
        File.WriteAllText(Path.Combine(themes, "my-look-1.json"), BuiltIn("desk-minimal"));
        File.Delete(Path.Combine(themes, "my-look-2.json"));
        string before = current.ToJson();
        var files = Snapshot(themes);

        var error = Assert.Throws<IOException>(() => SettingsBackup.Restore(SettingsBackup.Read(zip), current, themes, () => false));
        Assert.Contains("原来的设置和方案已保留", error.Message);
        Assert.Equal(before, current.ToJson());
        Assert.Equal(files, Snapshot(themes));
    }

    private static Dictionary<string, string> Snapshot(string themes) =>
        Directory.GetFiles(themes, "*.json").ToDictionary(Path.GetFileName, File.ReadAllText)!;

    private static string Zip(TempDir dir, params (string Name, string Text)[] entries)
    {
        string path = dir.File(Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return path;
    }

    [Fact]
    public void FilesThatAreNotABackupAreRefusedBeforeAnythingChanges()
    {
        using var dir = new TempDir();
        const string manifest = "{ \"format\": \"desk-backup\", \"version\": 1 }";
        string settings = new Settings().ToJson();
        File.WriteAllText(dir.File("plain.zip"), "not a zip");
        string[] refused =
        [
            dir.File("plain.zip"),
            Zip(dir, ("settings.json", settings)),
            Zip(dir, ("manifest.json", manifest)),
            Zip(dir, ("manifest.json", manifest), ("settings.json", "{ broken")),
            Zip(dir, ("manifest.json", manifest), ("settings.json", settings), ("themes/mine.json", "{ broken")),
            Zip(dir, ("manifest.json", manifest), ("settings.json", settings), ("themes/../../evil.json", BuiltIn("desk-air"))),
            Zip(dir, ("manifest.json", manifest), ("settings.json", settings), ("themes/.hidden.json", BuiltIn("desk-air"))),
            Zip(dir, ("manifest.json", manifest), ("settings.json", settings), ("readme.txt", "hello")),
        ];
        foreach (string file in refused)
        {
            var error = Assert.Throws<InvalidDataException>(() => SettingsBackup.Read(file));
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
        }
        // A backup without themes is fine.
        Assert.Empty(SettingsBackup.Read(Zip(dir, ("manifest.json", manifest), ("settings.json", settings))).Themes);
    }
}
