namespace Desk;

/// <summary>
/// Crash protection: explorer restarting again and again while DeskTap is
/// loaded most likely means the engine crashes it.
/// </summary>
internal sealed class RestartGuard
{
    private readonly int _threshold;
    private readonly TimeSpan _window;
    private readonly TimeSpan _requestedGrace;
    private readonly List<DateTime> _restarts = [];
    private DateTime _requestedAt = DateTime.MinValue;

    public RestartGuard(int threshold = 3, TimeSpan? window = null, TimeSpan? requestedGrace = null)
    {
        _threshold = threshold;
        _window = window ?? TimeSpan.FromMinutes(3);
        _requestedGrace = requestedGrace ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>Restarts in the current window.</summary>
    public int RecentRestarts => _restarts.Count;

    /// <summary>The user asked for a restart; the one that follows doesn't count.</summary>
    public void RestartRequested(DateTime now) => _requestedAt = now;

    /// <summary>Records a restart. Returns true when styling should be turned off.</summary>
    public bool OnRestart(DateTime now)
    {
        if (now - _requestedAt < _requestedGrace)
        {
            return false;
        }
        _restarts.Add(now);
        _restarts.RemoveAll(t => now - t > _window);
        Log.Warn($"Explorer restarted ({_restarts.Count} times in {_window.TotalMinutes:0} minutes)");
        if (_restarts.Count < _threshold)
        {
            return false;
        }
        _restarts.Clear();
        return true;
    }
}
