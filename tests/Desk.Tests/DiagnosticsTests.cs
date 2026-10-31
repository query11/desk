using System.IO.Compression;
using System.Text.Json.Nodes;
using Xunit;

namespace Desk.Tests;

/// <summary>The diagnostics package: what it holds, what it leaves out, what it hides.</summary>
public class DiagnosticsTests
{
    private const string Profile = @"C:\Users\Zhang San";
    private const string LocalAppData = @"C:\Users\Zhang San\AppData\Local";

    private static Redactor TestRedactor() => new(
        [(LocalAppData, "%LOCALAPPDATA%"), (Profile, "%USERPROFILE%")], "Zhang San");

    private static Dictionary<string, string> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public void UserFoldersAndNameAreReplaced()
    {
        var redactor = TestRedactor();
        Assert.Equal(@"%LOCALAPPDATA%\Desk\bin\DeskTap-abc.dll",
            redactor.Apply(@"C:\Users\Zhang San\AppData\Local\Desk\bin\DeskTap-abc.dll"));
        Assert.Equal(@"%USERPROFILE%\Desktop", redactor.Apply(@"c:\users\zhang san\Desktop"));
        // JSON writes backslashes doubled.
        Assert.Equal(@"""module"":""%LOCALAPPDATA%\\Desk""",
            redactor.Apply(@"""module"":""C:\\Users\\Zhang San\\AppData\\Local\\Desk"""));
        // The name in other paths and in the task name, not elsewhere.
        Assert.Equal(@"D:\Users\<user>\x", redactor.Apply(@"D:\Users\Zhang San\x"));
        Assert.Equal("Desk Autostart (<user>)", redactor.Apply("Desk Autostart (Zhang San)"));
        Assert.Equal("Zhang Sanmen", redactor.Apply("Zhang Sanmen"));
    }

    [Fact]
    public void ThePackageHoldsTheSummaryAndDesksFiles()
    {
        using var dir = new TempDir();
        string data = dir.File("Desk");
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        Directory.CreateDirectory(Path.Combine(data, "runtime"));
        Directory.CreateDirectory(Path.Combine(data, "state"));
        File.WriteAllText(Path.Combine(data, "logs", "desk.log"), $"[..] INFO DeskTap deployed: {LocalAppData}\\Desk\\bin\\DeskTap-abc.dll\n");
        File.WriteAllText(Path.Combine(data, "logs", "desk.1.log"), "older\n");
        File.WriteAllText(Path.Combine(data, "logs", "tap-explorer.exe.log"), "engine\n");
        File.WriteAllText(Path.Combine(data, "runtime", "explorer.exe.json"), "{\"serial\":1}");
        File.WriteAllText(Path.Combine(data, "state", "explorer.exe.100.json"), EngineStatusTests.ReportJson());
        File.WriteAllText(Path.Combine(data, "unrelated.txt"), "not Desk's");
        string settings = dir.File("settings.json");
        File.WriteAllText(settings, "{\"Enabled\":true}");

        var files = DiagnosticsExport.CollectFiles(data, settings);
        var summary = new JsonObject { ["desk"] = "0.4.0", ["engineDll"] = $@"{LocalAppData}\Desk\bin\DeskTap-abc.dll" };
        string zipPath = dir.File("out.zip");
        var result = DiagnosticsExport.Write(zipPath, summary, files, TestRedactor());

        Assert.True(result.Success);
        Assert.Empty(result.Missing);
        var entries = ReadZip(zipPath);
        Assert.Equal(
            ["logs/desk.1.log", "logs/desk.log", "logs/tap-explorer.exe.log", "runtime/explorer.exe.json",
             "settings.json", "state/explorer.exe.100.json", "summary.json"],
            entries.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Contains(@"%LOCALAPPDATA%\Desk\bin", entries["logs/desk.log"]);
        Assert.DoesNotContain("Zhang San", string.Concat(entries.Values));
        var parsed = JsonNode.Parse(entries["summary.json"])!;
        Assert.Equal("0.4.0", (string)parsed["desk"]!);
        Assert.Empty(parsed["missing"]!.AsArray());
    }

    [Fact]
    public void UnreadablePartsAreListedNotFatal()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("desk.log"), "fine");
        Directory.CreateDirectory(dir.File("locked.log")); // Can't be read as a file.
        var files = new[]
        {
            new DiagnosticFile("logs/desk.log", dir.File("desk.log")),
            new DiagnosticFile("logs/gone.log", dir.File("gone.log")),
            new DiagnosticFile("logs/locked.log", dir.File("locked.log")),
        };
        var result = DiagnosticsExport.Write(dir.File("out.zip"), new JsonObject(), files, TestRedactor());

        Assert.True(result.Success);
        Assert.Equal(2, result.Missing.Count);
        Assert.Contains(result.Missing, m => m.StartsWith("logs/gone.log: 不存在"));
        Assert.Contains(result.Missing, m => m.StartsWith("logs/locked.log: 无法读取"));
        var entries = ReadZip(dir.File("out.zip"));
        Assert.Equal("fine", entries["logs/desk.log"]);
        Assert.Equal(2, JsonNode.Parse(entries["summary.json"])!["missing"]!.AsArray().Count);
    }

    [Fact]
    public void ALogBeingWrittenCanBeExported()
    {
        using var dir = new TempDir();
        using var log = new LogWriter(dir.Path, "desk");
        log.Write("INFO", "before the export", DateTime.Now);
        log.Flush(TimeSpan.FromSeconds(5));
        // The writer keeps its file open (shared) while the export reads it.
        var result = DiagnosticsExport.Write(dir.File("out.zip"), new JsonObject(),
            [new DiagnosticFile("logs/desk.log", log.CurrentPath)], TestRedactor());
        log.Write("INFO", "after the export", DateTime.Now);
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));

        Assert.True(result.Success);
        Assert.Contains("before the export", ReadZip(dir.File("out.zip"))["logs/desk.log"]);
        Assert.Contains("after the export", File.ReadAllText(log.CurrentPath));
    }

    [Fact]
    public void AnUnwritableTargetFailsCleanly()
    {
        using var dir = new TempDir();
        var result = DiagnosticsExport.Write(dir.File(Path.Combine("missing-folder", "out.zip")), new JsonObject(), [],
            TestRedactor());
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}
