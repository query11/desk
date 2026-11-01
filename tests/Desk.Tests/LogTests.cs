using Xunit;

namespace Desk.Tests;

/// <summary>The log keeps a bounded history across runs and never takes Desk down.</summary>
public class LogTests
{
    private static readonly DateTime T0 = new(2026, 10, 31, 12, 0, 0);

    private static string Message(int i) => $"message {i:D6} " + new string('x', 80);

    private static long TotalSize(TempDir dir) =>
        Directory.GetFiles(dir.Path, "*.log").Sum(f => new FileInfo(f).Length);

    // The writer keeps desk.log open for writing (shared, as for a log
    // viewer); Windows refuses File.ReadAllText's read-only sharing then.
    private static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] ReadLines(string path) =>
        Read(path).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void RotatesWhileRunningAndKeepsABoundedHistory()
    {
        using var dir = new TempDir();
        var log = new LogWriter(dir.Path, "desk", maxFileSize: 8 * 1024, history: 3, startThread: false);
        for (int i = 0; i < 2000; i++)
        {
            log.Write("INFO", Message(i), T0.AddMilliseconds(i));
            if (i % 50 == 0)
            {
                log.WritePending();
            }
        }
        log.WritePending();

        var files = Directory.GetFiles(dir.Path).Select(Path.GetFileName).OrderBy(f => f).ToList();
        Assert.Equal(["desk.1.log", "desk.2.log", "desk.3.log", "desk.log"], files);
        Assert.True(TotalSize(dir) <= 4 * 8 * 1024, $"{TotalSize(dir)} bytes");
        foreach (var file in Directory.GetFiles(dir.Path))
        {
            Assert.True(new FileInfo(file).Length <= 8 * 1024);
        }
        // The newest lines are in desk.log, older ones in the history, in order.
        Assert.Contains(Message(1999), Read(dir.File("desk.log")));
        string history = Read(dir.File("desk.1.log"));
        Assert.DoesNotContain(Message(1999), history);
        Assert.Contains("message 0", history);
        Assert.Equal(0, log.Dropped);
    }

    [Fact]
    public void ARestartKeepsThePreviousRunsLog()
    {
        using var dir = new TempDir();
        var first = new LogWriter(dir.Path, "desk", maxFileSize: 4096, history: 2, startThread: false);
        first.Write("ERROR", "crash evidence from the previous run", T0);
        for (int i = 0; i < 40; i++)
        {
            first.Write("INFO", Message(i), T0.AddSeconds(i));
        }
        first.WritePending();
        first.Dispose();
        long before = new FileInfo(dir.File("desk.log")).Length;
        Assert.True(before > 3000);

        // The next start writes a line that doesn't fit: the old file is
        // moved to the history, not deleted.
        var second = new LogWriter(dir.Path, "desk", maxFileSize: 4096, history: 2, startThread: false);
        second.Write("INFO", "started again", T0.AddHours(1));
        second.Write("INFO", new string('y', 1500), T0.AddHours(1));
        second.WritePending();

        string all = string.Concat(Directory.GetFiles(dir.Path, "*.log").Select(Read));
        Assert.Contains("crash evidence from the previous run", all);
        Assert.Contains("started again", Read(dir.File("desk.log")));
    }

    [Fact]
    public void ConcurrentWritersLoseNothingThatFits()
    {
        using var dir = new TempDir();
        using var log = new LogWriter(dir.Path, "desk", maxFileSize: 1024 * 1024, history: 2);
        Parallel.For(0, 8, thread =>
        {
            for (int i = 0; i < 250; i++)
            {
                log.Write("INFO", $"thread {thread} line {i}", DateTime.Now);
            }
        });
        Assert.True(log.Flush(TimeSpan.FromSeconds(10)));

        var lines = ReadLines(dir.File("desk.log"));
        Assert.Equal(2000 - log.Dropped, lines.Length);
        Assert.Equal(0, log.Dropped);
        Assert.All(lines, line => Assert.Matches(@"^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}\] INFO thread \d line \d+$", line));
    }

    [Fact]
    public void AnUnwritableFolderIsHarmless()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("not-a-folder"), "");
        var log = new LogWriter(dir.File("not-a-folder"), "desk", startThread: false);
        log.Write("ERROR", "nowhere to go", T0);
        log.WritePending();
        Assert.True(log.Failures > 0);
        Assert.Equal(1, log.Dropped);
        Assert.Contains(log.RecentProblems, p => p.Contains("nowhere to go"));
    }

    [Fact]
    public void AFailingRotationKeepsWritingThenStopsAtTheLimit()
    {
        using var dir = new TempDir();
        // A folder where the history file should go: rotating fails.
        Directory.CreateDirectory(dir.File("desk.1.log"));
        Directory.CreateDirectory(dir.File("desk.2.log"));
        var log = new LogWriter(dir.Path, "desk", maxFileSize: 4096, history: 2, startThread: false);
        for (int i = 0; i < 300; i++)
        {
            log.Write("INFO", Message(i), T0.AddSeconds(i));
            log.WritePending();
        }
        long size = new FileInfo(dir.File("desk.log")).Length;
        Assert.True(size > 4096, "kept writing after the failed rotation");
        Assert.True(size <= 2 * 4096, $"{size} bytes");
        Assert.True(log.Dropped > 0);
        Assert.True(log.Failures > 0);
    }

    [Fact]
    public void TheQueueIsBoundedAndDropsAreReported()
    {
        using var dir = new TempDir();
        var log = new LogWriter(dir.Path, "desk", capacity: 10, startThread: false);
        for (int i = 0; i < 25; i++)
        {
            log.Write("INFO", $"line {i}", T0.AddSeconds(i));
        }
        Assert.Equal(15, log.Dropped);
        log.WritePending();
        var lines = ReadLines(dir.File("desk.log"));
        Assert.Equal(11, lines.Length);
        Assert.Contains("line 9", lines[9]);
        Assert.Contains("15 条日志没能写入", lines[10]);
    }

    [Fact]
    public void RepeatedMessagesAreWrittenOnce()
    {
        using var dir = new TempDir();
        var log = new LogWriter(dir.Path, "desk", startThread: false);
        for (int i = 0; i < 100; i++)
        {
            log.Write("WARN", "SetValue failed", T0.AddMilliseconds(i * 10));
        }
        log.Write("INFO", "something else", T0.AddSeconds(2));
        // The same message after the window counts again.
        log.Write("WARN", "SetValue failed", T0.AddSeconds(30));
        log.WritePending();
        var lines = ReadLines(dir.File("desk.log"));
        Assert.Equal(4, lines.Length);
        Assert.Contains("SetValue failed", lines[0]);
        Assert.Contains("又重复了 99 次", lines[1]);
        Assert.Contains("something else", lines[2]);
        Assert.Contains("SetValue failed", lines[3]);
    }

    [Fact]
    public void FlushWritesARepeatCountThatIsStillPending()
    {
        using var dir = new TempDir();
        var log = new LogWriter(dir.Path, "desk", startThread: false);
        log.Write("WARN", "again", T0);
        log.Write("WARN", "again", T0.AddSeconds(1));
        log.Flush(TimeSpan.FromSeconds(1));
        var lines = ReadLines(dir.File("desk.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("又重复了 1 次", lines[1]);
    }
}
