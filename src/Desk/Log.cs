using System.Collections.Concurrent;
using System.Text;

namespace Desk;

/// <summary>
/// Writes one log file with rotation: desk.log, then desk.1.log ... the
/// oldest history file is dropped. Lines are queued and written by a
/// background thread, so callers (the UI thread) never wait for the disk.
/// </summary>
/// <remarks>
/// - Size: a file is rotated before it would exceed <see cref="MaxFileSize"/>,
///   during the run and at start-up; nothing is deleted at start-up, so the
///   previous run's log stays (as desk.1.log once the new one fills up).
///   At most <see cref="History"/> old files are kept.
/// - The queue holds at most <see cref="Capacity"/> lines; what doesn't fit
///   is dropped and the number is written once there is room. Errors aren't
///   treated differently: they also go to <see cref="RecentProblems"/>, in
///   memory, for the diagnostics export.
/// - The same message repeated within 10 s is written once, followed by how
///   often it was repeated.
/// - When rotating fails (a file is held open), writing continues in the
///   current file up to twice the size, then lines are dropped and counted.
///   Disk errors never reach the caller.
/// - <see cref="Flush"/> waits (bounded) until everything queued is written;
///   Desk calls it on exit and after unhandled exceptions.
/// </remarks>
internal sealed class LogWriter : IDisposable
{
    public const long DefaultMaxFileSize = 1024 * 1024;
    public const int DefaultHistory = 4;
    public const int DefaultCapacity = 4096;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

    private readonly string _directory;
    private readonly string _name;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _repeatLock = new();
    private readonly ConcurrentQueue<string> _problems = new();
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _written = new(false);
    private int _queued;
    private long _enqueuedCount;
    private long _writtenCount;
    private long _dropped;
    private long _droppedReported;
    private long _failures;
    private bool _disposed;
    private FileStream? _stream;
    private long _rotateRetryAt;
    private string? _lastMessage;
    private DateTime _lastMessageAt;
    private int _repeats;

    public LogWriter(string directory, string name, long maxFileSize = DefaultMaxFileSize,
        int history = DefaultHistory, int capacity = DefaultCapacity, bool startThread = true)
    {
        _directory = directory;
        _name = name;
        MaxFileSize = maxFileSize;
        History = history;
        Capacity = capacity;
        if (startThread)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Desk log writer" };
            _thread.Start();
        }
    }

    public long MaxFileSize { get; }

    public int History { get; }

    public int Capacity { get; }

    public string CurrentPath => Path.Combine(_directory, _name + ".log");

    /// <summary>Lines dropped because the queue was full or the disk refused them.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Failed disk operations (opening, writing, rotating).</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>The latest warnings and errors, newest last.</summary>
    public IReadOnlyList<string> RecentProblems => _problems.ToArray();

    public string PathFor(int index) => index == 0 ? CurrentPath : Path.Combine(_directory, $"{_name}.{index}.log");

    public void Write(string level, string message, DateTime now)
    {
        if (_disposed)
        {
            return;
        }

        string? repeatedNote = null;
        lock (_repeatLock)
        {
            if (message == _lastMessage && now - _lastMessageAt < RepeatWindow)
            {
                _repeats++;
                return;
            }
            if (_repeats > 0)
            {
                repeatedNote = Format(now, "INFO", $"(上一条消息又重复了 {_repeats} 次)");
            }
            _lastMessage = message;
            _lastMessageAt = now;
            _repeats = 0;
        }

        if (repeatedNote != null)
        {
            Enqueue(repeatedNote);
        }
        string line = Format(now, level, message);
        if (level is "WARN" or "ERROR")
        {
            _problems.Enqueue(line.TrimEnd());
            while (_problems.Count > 100 && _problems.TryDequeue(out _))
            {
            }
        }
        Enqueue(line);
    }

    /// <summary>Waits until what was queued so far is written, or the timeout passed.</summary>
    public bool Flush(TimeSpan timeout)
    {
        string? note = null;
        lock (_repeatLock)
        {
            if (_repeats > 0)
            {
                note = Format(DateTime.Now, "INFO", $"(上一条消息又重复了 {_repeats} 次)");
                _repeats = 0;
                _lastMessage = null;
            }
        }
        if (note != null)
        {
            Enqueue(note);
        }

        long target = Interlocked.Read(ref _enqueuedCount);
        if (_thread == null)
        {
            WritePending();
            return true;
        }
        var deadline = DateTime.UtcNow + timeout;
        while (Interlocked.Read(ref _writtenCount) < target)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return false;
            }
            _written.Reset();
            _signal.Release();
            _written.Wait(left < TimeSpan.FromMilliseconds(50) ? left : TimeSpan.FromMilliseconds(50));
        }
        return true;
    }

    /// <summary>Writes what is queued on the calling thread (tests, and writers without a thread).</summary>
    public void WritePending()
    {
        var batch = new StringBuilder();
        long count = 0;
        while (_queue.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _queued);
            batch.Append(line);
            count++;
        }
        long dropped = Interlocked.Read(ref _dropped);
        if (dropped > _droppedReported)
        {
            batch.Append(Format(DateTime.Now, "WARN", $"{dropped - _droppedReported} 条日志没能写入（队列已满或磁盘错误）"));
            _droppedReported = dropped;
        }
        if (batch.Length > 0)
        {
            WriteToFile(Encoding.UTF8.GetBytes(batch.ToString()), count);
        }
        Interlocked.Add(ref _writtenCount, count);
        _written.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Flush(TimeSpan.FromSeconds(2));
        _disposed = true;
        _signal.Release();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _stream?.Dispose();
        _stream = null;
    }

    private static string Format(DateTime time, string level, string message) =>
        $"[{time:yyyy-MM-dd HH:mm:ss.fff}] {level} {message}{Environment.NewLine}";

    private void Enqueue(string line)
    {
        if (Interlocked.Increment(ref _queued) > Capacity)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _queue.Enqueue(line);
        Interlocked.Increment(ref _enqueuedCount);
        if (_thread != null)
        {
            _signal.Release();
        }
    }

    private void Run()
    {
        while (!_disposed)
        {
            _signal.Wait(TimeSpan.FromSeconds(1));
            // Collect a burst into one write.
            while (_signal.CurrentCount > 0 && _signal.Wait(0))
            {
            }
            try
            {
                WritePending();
            }
            catch
            {
                // Logging must never take Desk down.
            }
        }
    }

    private void WriteToFile(byte[] bytes, long lines)
    {
        try
        {
            var stream = Open();
            if (stream == null)
            {
                Interlocked.Add(ref _dropped, lines);
                return;
            }
            if (stream.Length > 0 && stream.Length + bytes.Length > MaxFileSize && stream.Length >= _rotateRetryAt)
            {
                Rotate();
                stream = Open();
                if (stream == null)
                {
                    Interlocked.Add(ref _dropped, lines);
                    return;
                }
            }
            if (stream.Length + bytes.Length > MaxFileSize * 2)
            {
                // Rotating keeps failing: protect the disk.
                Interlocked.Add(ref _dropped, lines);
                return;
            }
            stream.Write(bytes);
            stream.Flush();
        }
        catch
        {
            Interlocked.Increment(ref _failures);
            Interlocked.Add(ref _dropped, lines);
            _stream?.Dispose();
            _stream = null;
        }
    }

    private FileStream? Open()
    {
        if (_stream != null)
        {
            return _stream;
        }
        try
        {
            Directory.CreateDirectory(_directory);
            // Shared, so that a viewer or the diagnostics export can read it
            // and the file can be renamed by a rotation elsewhere.
            _stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            return _stream;
        }
        catch
        {
            Interlocked.Increment(ref _failures);
            return null;
        }
    }

    private void Rotate()
    {
        _stream?.Dispose();
        _stream = null;
        try
        {
            File.Delete(PathFor(History));
            for (int i = History - 1; i >= 0; i--)
            {
                if (File.Exists(PathFor(i)))
                {
                    File.Move(PathFor(i), PathFor(i + 1), overwrite: true);
                }
            }
            _rotateRetryAt = 0;
        }
        catch
        {
            // Keep writing to the current file and try again a bit later.
            Interlocked.Increment(ref _failures);
            try
            {
                _rotateRetryAt = new FileInfo(CurrentPath).Length + MaxFileSize / 4;
            }
            catch
            {
                _rotateRetryAt = MaxFileSize;
            }
        }
    }
}

/// <summary>The log of this process (desk.log for the tray app, restore.log for --restore).</summary>
internal static class Log
{
    private static LogWriter? _writer;

    public static LogWriter? Writer => _writer;

    /// <param name="name">desk for the tray app, restore for Desk.exe --restore.</param>
    public static void Init(string name = "desk")
    {
        try
        {
            _writer = new LogWriter(Paths.LogsDir, name);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }
        catch
        {
            _writer = null;
        }
    }

    /// <summary>Writes out what is queued (waits up to 2 seconds).</summary>
    public static void Flush() => _writer?.Flush(TimeSpan.FromSeconds(2));

    /// <summary>The latest warnings and errors of this run.</summary>
    public static IReadOnlyList<string> RecentProblems => _writer?.RecentProblems ?? [];

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception == null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        System.Diagnostics.Debug.WriteLine($"{level} {message}");
        try
        {
            _writer?.Write(level, message, DateTime.Now);
        }
        catch
        {
            // Logging must never take Desk down.
        }
    }
}
