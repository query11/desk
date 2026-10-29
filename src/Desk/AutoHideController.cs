using System.Runtime.InteropServices;
using static Desk.NativeMethods;

namespace Desk;

/// <summary>
/// Turns on taskbar auto-hide while a window is maximized (the "auto-hide"
/// maximize mode), and puts the user's own setting back afterwards.
/// </summary>
internal sealed class AutoHideController
{
    private bool _forced;
    private bool _userHadAutoHide;

    public void SetForced(bool autoHide)
    {
        if (autoHide == _forced)
        {
            return;
        }

        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = FindWindowW("Shell_TrayWnd", null),
        };
        int state = (int)SHAppBarMessage(ABM_GETSTATE, ref data).ToInt64();

        if (autoHide)
        {
            _userHadAutoHide = (state & ABS_AUTOHIDE) != 0;
            if (!_userHadAutoHide)
            {
                data.lParam = (IntPtr)(state | ABS_AUTOHIDE);
                SHAppBarMessage(ABM_SETSTATE, ref data);
            }
        }
        else if (!_userHadAutoHide)
        {
            data.lParam = (IntPtr)(state & ~ABS_AUTOHIDE);
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }

        _forced = autoHide;
    }
}
