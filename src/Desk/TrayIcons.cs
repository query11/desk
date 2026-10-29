using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Desk;

/// <summary>Draws the tray icon: a wide island and a small one, like the taskbar.</summary>
internal static class TrayIcons
{
    public static Icon Create(bool enabled)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width * 2);
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            float unit = size / 32f;
            var color = enabled ? Color.FromArgb(255, 0, 120, 212) : Color.FromArgb(255, 128, 128, 128);
            using var brush = new SolidBrush(color);
            using var faint = new SolidBrush(Color.FromArgb(enabled ? 110 : 80, color));

            // A window above the taskbar.
            FillRounded(graphics, faint, new RectangleF(5 * unit, 4 * unit, 22 * unit, 13 * unit), 3 * unit);
            // The app island and the tray island.
            FillRounded(graphics, brush, new RectangleF(4 * unit, 20 * unit, 17 * unit, 8 * unit), 3.5f * unit);
            FillRounded(graphics, brush, new RectangleF(23 * unit, 20 * unit, 6 * unit, 8 * unit), 3 * unit);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void FillRounded(Graphics graphics, Brush brush, RectangleF rect, float radius)
    {
        float d = radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
