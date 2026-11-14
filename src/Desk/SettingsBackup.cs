using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Desk;

/// <summary>A personal theme in a backup: its file name in the themes folder, and its contents.</summary>
internal sealed record BackupTheme(string FileName, byte[] Content);

/// <summary>What a backup holds, checked and ready to restore.</summary>
internal sealed record SettingsBackupContents(Settings Settings, IReadOnlyList<BackupTheme> Themes, DateTime? Created, string? DeskVersion);

/// <summary>
/// 备份设置 / 从备份恢复: settings.json and the personal themes in one zip.
/// A backup is read and checked completely before anything changes, and a
/// restore that can't be finished puts everything back as it was.
/// </summary>
internal static class SettingsBackup
{
    private const string Format = "desk-backup";
    private const int MaxEntryBytes = 2 * 1024 * 1024;
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private const int MaxThemes = 500;

    /// <summary>Writes the backup; a file that is there already is replaced only once the new one is complete.</summary>
    public static void Create(string zipPath, Settings settings, string themesDir, string deskVersion, DateTime now)
    {
        var themes = Directory.Exists(themesDir)
            ? Directory.GetFiles(themesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        string temp = zipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var manifest = new JsonObject
                {
                    ["format"] = Format,
                    ["version"] = 1,
                    ["desk"] = deskVersion,
                    ["created"] = now.ToUniversalTime().ToString("O"),
                    ["themes"] = themes.Count,
                };
                Add(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
                Add(zip, "settings.json", Encoding.UTF8.GetBytes(settings.ToJson()));
                foreach (string file in themes)
                {
                    Add(zip, "themes/" + Path.GetFileName(file), File.ReadAllBytes(file));
                }
            }
            File.Move(temp, zipPath, overwrite: true);
            Log.Info($"Settings backed up to {zipPath} ({themes.Count} personal themes)");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Reads and checks a backup. Nothing changes; the reason is in the exception (InvalidDataException).</summary>
    public static SettingsBackupContents Read(string zipPath)
    {
        if (new FileInfo(zipPath).Length > MaxFileBytes)
            throw new InvalidDataException("文件超过 64 MB，不是 Desk 的设置备份。");
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("这不是 zip 文件，或者文件已经损坏。");
        }
        using (zip)
        {
            Settings? settings = null;
            bool manifest = false;
            DateTime? created = null;
            string? desk = null;
            var themes = new List<BackupTheme>();
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                byte[] content = ReadEntry(entry);
                if (entry.FullName == "manifest.json")
                {
                    var root = Json(content, "manifest.json") as JsonObject;
                    manifest = root?["format"] is JsonValue format && format.TryGetValue(out string? kind) && kind == Format;
                    if (root?["created"] is JsonValue when && when.TryGetValue(out string? stamp) &&
                        DateTime.TryParse(stamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                        created = at;
                    desk = root?["desk"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
                }
                else if (entry.FullName == "settings.json")
                {
                    try
                    {
                        settings = Settings.Parse(Encoding.UTF8.GetString(content));
                    }
                    catch (JsonException e)
                    {
                        throw new InvalidDataException("备份里的设置无法读取：" + e.Message);
                    }
                }
                else if (entry.FullName.StartsWith("themes/", StringComparison.Ordinal))
                {
                    string name = entry.FullName["themes/".Length..];
                    CheckThemeName(name);
                    CheckTheme(name, content);
                    themes.Add(new BackupTheme(name, content));
                    if (themes.Count > MaxThemes) throw new InvalidDataException($"备份里的方案超过 {MaxThemes} 个。");
                }
                else
                {
                    throw new InvalidDataException($"备份里有不认识的文件「{entry.FullName}」，没有恢复。");
                }
            }
            if (!manifest || settings == null)
                throw new InvalidDataException("这不是 Desk 的设置备份（缺少 manifest.json 或 settings.json）。");
            if (themes.Select(t => t.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != themes.Count)
                throw new InvalidDataException("备份里有重名的方案文件。");
            return new SettingsBackupContents(settings, themes, created, desk);
        }
    }

    /// <summary>
    /// Puts the backup's personal themes into the themes folder (a different
    /// file of the same name goes to .trash first, as when deleting one) and
    /// takes its settings (see Settings.RestoreFrom), then saves. If that
    /// fails at any point, the files and the settings are put back.
    /// </summary>
    public static void Restore(SettingsBackupContents backup, Settings target, string themesDir, Func<bool> save)
    {
        string snapshot = target.ToJson();
        var placed = new List<(string Path, string? Trashed)>();
        bool settingsTaken = false;
        try
        {
            Directory.CreateDirectory(themesDir);
            foreach (var theme in backup.Themes)
            {
                string path = Path.Combine(themesDir, theme.FileName);
                if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(theme.Content)) continue;
                string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                string? trashed = null;
                try
                {
                    File.WriteAllBytes(staged, theme.Content);
                    if (File.Exists(path))
                    {
                        string trash = Path.Combine(themesDir, ".trash");
                        Directory.CreateDirectory(trash);
                        trashed = Path.Combine(trash, Path.GetFileNameWithoutExtension(theme.FileName) + "-" + Guid.NewGuid().ToString("N") + ".json");
                        File.Move(path, trashed);
                    }
                    File.Move(staged, path);
                }
                catch
                {
                    if (trashed != null && !File.Exists(path)) File.Move(trashed, path);
                    throw;
                }
                finally
                {
                    if (File.Exists(staged)) File.Delete(staged);
                }
                placed.Add((path, trashed));
            }
            target.RestoreFrom(backup.Settings);
            settingsTaken = true;
            if (!save()) throw new IOException("设置没有保存成功。");
            Log.Info($"Settings restored from a backup ({backup.Themes.Count} personal themes, {placed.Count} written)");
        }
        catch (Exception e)
        {
            if (settingsTaken)
            {
                var before = Settings.Parse(snapshot);
                target.RestoreFrom(before);
                // RestoreFrom leaves a trial out; this is the same moment, so it goes back too.
                target.Theme = before.Theme;
                target.PendingThemeTrial = before.PendingThemeTrial;
            }
            for (int i = placed.Count - 1; i >= 0; i--)
            {
                var (path, trashed) = placed[i];
                try
                {
                    File.Delete(path);
                    if (trashed != null) File.Move(trashed, path);
                }
                catch (Exception undo)
                {
                    Log.Warn($"Could not put {path} back after a failed restore: {undo.Message}");
                }
            }
            Log.Warn($"Restoring a backup failed: {e.Message}");
            throw new IOException("没有恢复成功，原来的设置和方案已保留：" + e.Message, e);
        }
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    /// <summary>An entry's bytes, no more than allowed whatever its header says.</summary>
    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxEntryBytes)
                throw new InvalidDataException($"备份里的「{entry.FullName}」超过 2 MB。");
        }
        return buffer.ToArray();
    }

    private static JsonNode? Json(byte[] content, string name)
    {
        try
        {
            return JsonNode.Parse(content);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"备份里的「{name}」无法读取：{e.Message}");
        }
    }

    /// <summary>A plain file name in the themes folder: nothing that could land elsewhere.</summary>
    private static void CheckThemeName(string name)
    {
        bool plain = name.Length is > 5 and <= 120 && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                     !name.StartsWith('.') && name.IndexOfAny(['/', '\\', ':']) < 0 &&
                     name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Path.GetFileName(name) == name;
        if (!plain) throw new InvalidDataException($"备份里的方案文件名「{name}」不能用。");
    }

    /// <summary>The theme must load, as when it is imported.</summary>
    private static void CheckTheme(string name, byte[] content)
    {
        string temp = Path.Combine(Path.GetTempPath(), "desk-backup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(temp, content);
            _ = Theme.Load(temp);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new InvalidDataException($"备份里的方案「{name}」无法读取：{e.Message}");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
