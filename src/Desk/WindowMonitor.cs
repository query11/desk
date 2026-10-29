using static Desk.NativeMethods;

namespace Desk;

/// <summary>
/// Tracks whether a window is maximized on the taskbar's monitor, using
/// WinEvent hooks (delivered on the UI thread) and a short debounce.
/// </summary>
internal sealed class WindowMonitor : IDisposable
{
    private const uint GA_ROOT = 2;

    private readonly WinEventDelegate _callback;
    private readonly List<IntPtr> _hooks = [];
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };

    public event Action? MaximizedChanged;

    public bool HasMaximizedWindow { get; private set; }

    public WindowMonitor()
    {
        _callback = OnWinEvent;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Evaluate();
        };

        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);

        HasMaximizedWindow = FindMaximizedWindow();
    }

    public void Dispose()
    {
        foreach (var hook in _hooks)
        {
            UnhookWinEvent(hook);
        }
        _hooks.Clear();
        _debounce.Dispose();
    }

    private void Hook(uint min, uint max)
    {
        IntPtr hook = SetWinEventHook(min, max, IntPtr.Zero, _callback, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook != IntPtr.Zero)
        {
            _hooks.Add(hook);
        }
        else
        {
            Log.Warn($"SetWinEventHook({min:X}) failed");
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime)
    {
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
        {
            return;
        }
        if (eventType == EVENT_OBJECT_LOCATIONCHANGE && GetAncestor(hwnd, GA_ROOT) != hwnd)
        {
            return;
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private void Evaluate()
    {
        bool maximized = FindMaximizedWindow();
        if (maximized != HasMaximizedWindow)
        {
            HasMaximizedWindow = maximized;
            MaximizedChanged?.Invoke();
        }
    }

    private static bool FindMaximizedWindow()
    {
        IntPtr taskbar = FindWindowW("Shell_TrayWnd", null);
        IntPtr monitor = taskbar != IntPtr.Zero ? MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
        bool found = false;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || !IsZoomed(hwnd) || IsCloaked(hwnd))
            {
                return true;
            }
            if ((GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }
            if (monitor != IntPtr.Zero && MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != monitor)
            {
                return true;
            }
            found = true;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
