using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Desk;

/// <summary>A file to put into the diagnostics package, under a relative name.</summary>
internal sealed record DiagnosticFile(string EntryName, string Path);

internal sealed record DiagnosticsResult(bool Success, string Path, IReadOnlyList<string> Missing, string? Error);

/// <summary>
/// Replaces the user's folders and name in paths with placeholders, so the
/// package shows where things are relative to them without naming the user.
/// </summary>
internal sealed class Redactor
{
    private readonly List<(string Find, string Replace)> _folders;
    private readonly Regex? _userName;

    /// <param name="folders">Folder paths and their placeholder, e.g. %LOCALAPPDATA%.</param>
    /// <param name="userName">Replaced where it appears as a path segment or in the task name.</param>
    public Redactor(IEnumerable<(string Find, string Replace)> folders, string? userName)
    {
        // Longest first: %LOCALAPPDATA% before %USERPROFILE%.
        _folders = folders.Where(f => !string.IsNullOrEmpty(f.Find))
            .OrderByDescending(f => f.Find.Length)
            .ToList();
        if (!string.IsNullOrEmpty(userName))
        {
            string name = Regex.Escape(userName);
            _userName = new Regex($@"(?<=[\\/]Users[\\/]){name}(?=[\\/""]|$)|(?<=Desk Autostart \(){name}(?=\))",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }
    }

    public static Redactor ForCurrentUser() => new(
    [
        (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
        (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
        (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        (Path.GetTempPath().TrimEnd('\\', '/'), "%TEMP%"),
    ], Environment.UserName);

    public string Apply(string text)
    {
        foreach (var (find, replace) in _folders)
        {
            text = text.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
            // JSON files write backslashes doubled.
            string escaped = find.Replace("\\", "\\\\");
            if (escaped != find)
            {
                text = text.Replace(escaped, replace, StringComparison.OrdinalIgnoreCase);
            }
        }
        return _userName?.Replace(text, "<user>") ?? text;
    }
}

/// <summary>
/// The "export diagnostics" package: a summary (versions, system, displays,
/// theme, engine states, recent problems) and Desk's own logs, configurations
/// and engine reports, nothing else. Files that can't be read are listed
/// instead of failing the export.
/// </summary>
internal static class DiagnosticsExport
{
    /// <summary>Of a larger file, only the end goes in.</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>Desk's files: logs (with history), runtime configurations, engine reports, settings.</summary>
    public static List<DiagnosticFile> CollectFiles(string dataDir, string settingsFile)
    {
        var files = new List<DiagnosticFile>();
        void Add(string folder, string pattern, string entryFolder)
        {
            string path = Path.Combine(dataDir, folder);
            if (!Directory.Exists(path))
            {
                return;
            }
            foreach (string file in Directory.GetFiles(path, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(new DiagnosticFile($"{entryFolder}/{Path.GetFileName(file)}", file));
            }
        }
        Add("logs", "*.log", "logs");
        Add("runtime", "*.json", "runtime");
        Add("state", "*.json", "state");
        if (File.Exists(Path.Combine(dataDir, "autohide-restore.json")))
        {
            files.Add(new DiagnosticFile("state/autohide-restore.json", Path.Combine(dataDir, "autohide-restore.json")));
        }
        files.Add(new DiagnosticFile("settings.json", settingsFile));
        return files;
    }

    public static DiagnosticsResult Write(string zipPath, JsonObject summary, IEnumerable<DiagnosticFile> files,
        Redactor redactor)
    {
        var missing = new List<string>();
        string temp = zipPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    string? text = ReadShared(file.Path, out string? problem);
                    if (text == null)
                    {
                        missing.Add($"{file.EntryName}: {problem}");
                        continue;
                    }
                    AddText(zip, file.EntryName, redactor.Apply(text));
                }

                summary["missing"] = new JsonArray(missing.Select(m => (JsonNode)redactor.Apply(m)).ToArray());
                AddText(zip, "summary.json",
                    redactor.Apply(summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            }
            File.Move(temp, zipPath, overwrite: true);
            Log.Info($"Diagnostics exported to {zipPath} ({missing.Count} parts missing)");
            return new DiagnosticsResult(true, zipPath, missing, null);
        }
        catch (Exception e)
        {
            Log.Error("Exporting diagnostics failed", e);
            try
            {
                File.Delete(temp);
            }
            catch
            {
            }
            return new DiagnosticsResult(false, zipPath, missing, e.Message);
        }
    }

    /// <summary>Reads a file another process may be writing; null with the reason when it can't.</summary>
    private static string? ReadShared(string path, out string? problem)
    {
        problem = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - MaxFileBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string text = reader.ReadToEnd();
            return start > 0 ? $"(前 {start} 字节已省略)\n{text}" : text;
        }
        catch (FileNotFoundException)
        {
            problem = "不存在";
        }
        catch (DirectoryNotFoundException)
        {
            problem = "不存在";
        }
        catch (Exception e)
        {
            problem = $"无法读取（{e.GetType().Name}: {e.Message}）";
        }
        return null;
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
