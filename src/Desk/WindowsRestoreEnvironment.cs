namespace Desk;

/// <summary>The emergency restore on Windows.</summary>
internal sealed class WindowsRestoreEnvironment : IRestoreEnvironment
{
    /// <summary>Set by Desk.exe --restore to ask the running Desk.exe to restore and quit.</summary>
    public const string RestoreEventName = @"Local\Desk.Host.Restore";

    public bool HostRunning()
    {
        if (!Mutex.TryOpenExisting(Program.HostMutexName, out var mutex))
        {
            return false;
        }
        mutex.Dispose();
        return true;
    }

    public bool AskHostToExit(TimeSpan timeout)
    {
        if (EventWaitHandle.TryOpenExisting(RestoreEventName, out var restore))
        {
            using (restore)
            {
                restore.Set();
            }
        }
        return WaitForHostToEnd(timeout);
    }

    public HostLease? FindHost() => HostSession.ReadHeartbeat();

    public bool TerminateHost(HostLease host, TimeSpan timeout)
    {
        try
        {
            if (host.ProcessStart != 0 && WindowsTargetPlatform.ProcessStartTime(host.ProcessId) != host.ProcessStart)
            {
                // Not that Desk.exe any more; it's gone.
                return WaitForHostToEnd(TimeSpan.Zero);
            }
            using var process = System.Diagnostics.Process.GetProcessById((int)host.ProcessId);
            if (!string.Equals(process.ProcessName, "Desk", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            process.Kill();
            process.WaitForExit((int)timeout.TotalMilliseconds);
            return WaitForHostToEnd(timeout);
        }
        catch (Exception e)
        {
            Log.Warn($"Restore: could not end Desk: {e.Message}");
            return false;
        }
    }

    public bool WriteDisabledConfigurations()
    {
        try
        {
            Directory.CreateDirectory(Paths.RuntimeDir);
            // No owner: engines apply it as it is, whatever happens to this
            // short-lived process. Older engines read it too.
            var writer = new RuntimeWriter();
            return writer.Write(RuntimeWriter.ExplorerFile, false, false, []) &&
                   writer.Write(RuntimeWriter.StartMenuFile, false, false, []);
        }
        catch (Exception e)
        {
            Log.Error("Restore: could not write the configurations", e);
            return false;
        }
    }

    public AutoHideRestore RestoreAutoHide() => new AutoHideController(new WindowsAppBarState()).Restore();

    public bool TurnStylingOff()
    {
        var settings = Settings.Load();
        settings.Enabled = false;
        return settings.Save();
    }

    private static bool WaitForHostToEnd(TimeSpan timeout)
    {
        if (!Mutex.TryOpenExisting(Program.HostMutexName, out var mutex))
        {
            return true;
        }
        using (mutex)
        {
            try
            {
                if (!mutex.WaitOne(timeout))
                {
                    return false;
                }
            }
            catch (AbandonedMutexException)
            {
                // It ended without releasing it: gone all the same.
            }
            mutex.ReleaseMutex();
            return true;
        }
    }
}
