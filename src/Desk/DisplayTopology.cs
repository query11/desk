using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Desk;

internal sealed record DesktopDisplay(string Id, string Device, string Name, int Width, int Height, bool Primary)
{
    public string Label => $"{Name} · {Width} × {Height}" + (Primary ? " · 主屏" : "");
}

internal interface IDisplaySettings
{
    event Action? StateChanged;
    IReadOnlyList<DesktopDisplay> GetDisplays();
    DisplayPreferences GetDisplayPreferences(string id);
    void UpdateDisplay(string id, Action<DisplayPreferences> change);
    IAppearanceEditor GetDisplayEditor(string id);
}

internal static class DisplayTopology
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeMethods.RECT Monitor;
        public NativeMethods.RECT Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string device, uint index, ref DisplayDevice info, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    public static IReadOnlyList<DesktopDisplay> Read() => Screen.AllScreens.OrderByDescending(s => s.Primary).Select((screen, index) =>
    {
        var info = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        bool found = EnumDisplayDevicesW(screen.DeviceName, 0, ref info, 1);
        string id = found && !string.IsNullOrEmpty(info.DeviceId) ? info.DeviceId : screen.DeviceName;
        string name = found && !string.IsNullOrWhiteSpace(info.DeviceString) ? info.DeviceString : "显示器 " + (index + 1);
        return new DesktopDisplay(id, screen.DeviceName, name, screen.Bounds.Width, screen.Bounds.Height, screen.Primary);
    }).ToList();

    public static string DeviceForWindow(IntPtr window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfoW(NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info) ? info.Device : "";
    }
}
