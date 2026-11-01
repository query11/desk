using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Desk;

/// <summary>The owner block a configuration carries, and what the heartbeat names.</summary>
internal sealed record HostLease(string Session, uint ProcessId, long ProcessStart);

/// <summary>
/// This run of Desk.exe as the owner of the engines' configuration. Every
/// configuration names it (session id, process id, creation time), so an
/// engine can tell when its owner is gone and restore by itself; and it
/// writes runtime\host.json every few seconds for engines that aren't allowed
/// to open this process (the Start menu's app container).
/// </summary>
internal sealed class HostSession : IDisposable
{
    public const string HeartbeatFile = "host.json";

    private readonly string _directory;
    private readonly object _lock = new();
    private Timer? _timer;
    private long _seq;
    private bool _exiting;

    public HostSession(HostLease lease, string? directory = null)
    {
        Lease = lease;
        _directory = directory ?? Paths.RuntimeDir;
    }

    public HostLease Lease { get; }

    public static HostSession ForCurrentProcess()
    {
        uint pid = (uint)Environment.ProcessId;
        return new HostSession(new HostLease(Guid.NewGuid().ToString("N"), pid,
            NativeMethods.ProcessStartTime(pid)));
    }

    public void StartHeartbeat(TimeSpan interval)
    {
        Beat();
        // On the thread pool: the heartbeat says the process is alive. A busy
        // or hung settings window doesn't mean the styling should go.
        _timer = new Timer(_ => Beat(), null, interval, interval);
    }

    public void Beat()
    {
        lock (_lock)
        {
            if (_exiting)
            {
                return;
            }
            Write("running");
        }
    }

    /// <summary>The last heartbeat says this Desk.exe is leaving (it restored everything itself).</summary>
    public void MarkExiting()
    {
        lock (_lock)
        {
            _exiting = true;
            _timer?.Dispose();
            _timer = null;
            Write("exiting");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>The lease in the heartbeat file; used by the emergency restore to find a hung Desk.exe.</summary>
    public static HostLease? ReadHeartbeat(string? directory = null)
    {
        try
        {
            string path = Path.Combine(directory ?? Paths.RuntimeDir, HeartbeatFile);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string? session = root.GetProperty("session").GetString();
            uint pid = root.GetProperty("pid").GetUInt32();
            long start = long.Parse(root.GetProperty("start").GetString() ?? "0", CultureInfo.InvariantCulture);
            return session == null ? null : new HostLease(session, pid, start);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The owner block of a configuration.</summary>
    public static JsonObject ToJson(HostLease lease) => new()
    {
        ["session"] = lease.Session,
        ["pid"] = lease.ProcessId,
        // A FILETIME doesn't fit a JSON number (a double) exactly.
        ["start"] = lease.ProcessStart.ToString(CultureInfo.InvariantCulture),
    };

    private void Write(string state)
    {
        try
        {
            var json = ToJson(Lease);
            json["seq"] = ++_seq;
            json["state"] = state;
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, HeartbeatFile);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json.ToJsonString(), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            // The engines only rely on it after it worked once, and then
            // only after a long silence; a failed beat is not fatal.
            Log.Warn($"Could not write the heartbeat: {e.Message}");
        }
    }
}
