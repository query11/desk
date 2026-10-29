using System.Windows.Forms;
using static Desk.NativeMethods;

namespace Desk;

/// <summary>
/// A hidden top-level window for the global hotkey and the TaskbarCreated
/// broadcast (which message-only windows don't receive).
/// </summary>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private bool _hotkeyRegistered;

    public event Action? HotkeyPressed;

    public event Action? TaskbarCreated;

    public MessageWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "DeskHostWindow",
            ExStyle = (int)WS_EX_TOOLWINDOW,
        });
    }

    /// <summary>Ctrl + Alt + Shift + D.</summary>
    public bool RegisterHotkey()
    {
        _hotkeyRegistered = RegisterHotKey(Handle, HotkeyId,
            MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.D);
        return _hotkeyRegistered;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)WM_HOTKEY && m.WParam == HotkeyId)
        {
            HotkeyPressed?.Invoke();
        }
        else if (_taskbarCreated != 0 && m.Msg == (int)_taskbarCreated)
        {
            TaskbarCreated?.Invoke();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(Handle, HotkeyId);
        }
        DestroyHandle();
    }
}
