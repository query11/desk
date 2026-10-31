using System.Text;

namespace Desk;

internal static class Log
{
    private const long MaxSize = 2 * 1024 * 1024;
    private static readonly object Lock = new();
    private static string? _path;

    /// <param name="name">desk for the tray app, restore for Desk.exe --restore.</param>
    public static void Init(string name = "desk")
    {
        try
        {
            Directory.CreateDirectory(Paths.LogsDir);
            _path = Path.Combine(Paths.LogsDir, name + ".log");
            var info = new FileInfo(_path);
            if (info.Exists && info.Length > MaxSize)
            {
                info.Delete();
            }
        }
        catch
        {
            _path = null;
        }
    }

    /// <summary>Writes out what is queued.</summary>
    public static void Flush()
    {
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception == null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {message}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        if (_path == null)
        {
            return;
        }

        lock (Lock)
        {
            try
            {
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take Desk down.
            }
        }
    }
}
