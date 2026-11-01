using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static Desk.NativeMethods;

namespace Desk;

/// <summary>The taskbar's auto-hide state (ABM_GETSTATE / ABM_SETSTATE).</summary>
internal interface IAppBarState
{
    /// <summary>The state bits, or null when they can't be read.</summary>
    int? Get();

    bool Set(int state);
}

internal sealed class WindowsAppBarState : IAppBarState
{
    public int? Get()
    {
        var data = Data();
        if (data.hWnd == IntPtr.Zero)
        {
            return null;
        }
        return (int)SHAppBarMessage(ABM_GETSTATE, ref data).ToInt64();
    }

    public bool Set(int state)
    {
        var data = Data();
        if (data.hWnd == IntPtr.Zero)
        {
            return false;
        }
        data.lParam = (IntPtr)state;
        SHAppBarMessage(ABM_SETSTATE, ref data);
        return true;
    }

    private static APPBARDATA Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = FindWindowW("Shell_TrayWnd", null),
    };
}

/// <summary>What Desk changed in the system, written down before the change.</summary>
internal sealed record AutoHideJournal(bool DeskTurnedOn, DateTime At, uint ProcessId);

internal enum AutoHideRestore
{
    /// <summary>Desk hadn't changed anything.</summary>
    NothingToDo,

    /// <summary>Auto-hide was turned off again.</summary>
    Restored,

    /// <summary>The user turned it off in the meantime; left alone.</summary>
    AlreadyRestored,

    /// <summary>The taskbar couldn't be reached; the record is kept for next time.</summary>
    Failed,
}

/// <summary>
/// Turns on taskbar auto-hide while a window is maximized (the "auto-hide"
/// maximize mode), and puts the user's setting back afterwards.
/// </summary>
/// <remarks>
/// Before Desk turns auto-hide on, it writes down that it did so; the record
/// is removed once it is undone. Undoing it follows one rule, whether Desk
/// exits normally, starts again after a crash or runs --restore: only when the
/// record says Desk turned it on, and only when it is still on (if the user
/// turned it off in the meantime, nothing is touched). A user who had
/// auto-hide on already gets no record, so their setting is never changed.
/// </remarks>
internal sealed class AutoHideController
{
    private readonly IAppBarState _appBar;
    private readonly string _journalPath;
    private bool _forced;

    public AutoHideController(IAppBarState appBar, string? journalPath = null)
    {
        _appBar = appBar;
        _journalPath = journalPath ?? Path.Combine(Paths.DataDir, "autohide-restore.json");
    }

    public void SetForced(bool autoHide)
    {
        if (autoHide == _forced)
        {
            return;
        }

        if (autoHide)
        {
            int? state = _appBar.Get();
            if (state == null)
            {
                return; // No taskbar right now; tried again on the next change.
            }
            if ((state.Value & ABS_AUTOHIDE) == 0)
            {
                // Written first: if Desk dies right after the change, the
                // next start (or --restore) knows to undo it.
                if (!WriteJournal(new AutoHideJournal(true, DateTime.UtcNow, (uint)Environment.ProcessId)))
                {
                    Log.Warn("Not turning on auto-hide: its restore record couldn't be saved");
                    return;
                }
                _appBar.Set(state.Value | ABS_AUTOHIDE);
                Log.Info("Auto-hide turned on while a window is maximized");
            }
            _forced = true;
        }
        else
        {
            Restore();
            _forced = false;
        }
    }

    /// <summary>Undoes what the record says Desk changed (also left over from a previous run).</summary>
    public AutoHideRestore Restore()
    {
        var journal = ReadJournal();
        if (journal is not { DeskTurnedOn: true })
        {
            DeleteJournal();
            return AutoHideRestore.NothingToDo;
        }

        int? state = _appBar.Get();
        if (state == null)
        {
            Log.Warn("Auto-hide not restored: no taskbar");
            return AutoHideRestore.Failed;
        }

        AutoHideRestore result;
        if ((state.Value & ABS_AUTOHIDE) != 0)
        {
            if (!_appBar.Set(state.Value & ~ABS_AUTOHIDE))
            {
                return AutoHideRestore.Failed;
            }
            result = AutoHideRestore.Restored;
            Log.Info("Auto-hide turned off again");
        }
        else
        {
            result = AutoHideRestore.AlreadyRestored;
            Log.Info("Auto-hide was turned off by the user meanwhile; left as it is");
        }
        DeleteJournal();
        return result;
    }

    private AutoHideJournal? ReadJournal()
    {
        try
        {
            return File.Exists(_journalPath)
                ? JsonSerializer.Deserialize<AutoHideJournal>(File.ReadAllText(_journalPath))
                : null;
        }
        catch (Exception e)
        {
            // A damaged record can't say Desk changed anything.
            Log.Warn($"Auto-hide record unreadable: {e.Message}");
            return null;
        }
    }

    private bool WriteJournal(AutoHideJournal journal)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            string temp = _journalPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(journal), new UTF8Encoding(false));
            File.Move(temp, _journalPath, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not save the auto-hide record: {e.Message}");
            return false;
        }
    }

    private void DeleteJournal()
    {
        try
        {
            File.Delete(_journalPath);
        }
        catch (Exception e)
        {
            Log.Warn($"Could not remove the auto-hide record: {e.Message}");
        }
    }
}
