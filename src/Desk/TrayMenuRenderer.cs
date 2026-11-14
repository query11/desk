using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Desk;

/// <summary>
/// The tray menu in the colors of the Windows mode (like the taskbar it sits
/// on), close to the menus of Windows 11: the default Windows Forms menu is
/// light even in dark mode.
/// </summary>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly Palette _palette;

    private TrayMenuRenderer(Palette palette) : base(new Colors(palette))
    {
        _palette = palette;
        RoundedEdges = false;
    }

    public bool Light => _palette == Palette.LightMode;

    public static TrayMenuRenderer For(bool light) => new(light ? Palette.LightMode : Palette.DarkMode);

    private sealed record Palette(Color Background, Color Hover, Color Text, Color Disabled, Color Separator, Color Border)
    {
        public static readonly Palette LightMode = new(Color.FromArgb(249, 249, 249), Color.FromArgb(234, 234, 234),
            Color.FromArgb(26, 26, 26), Color.FromArgb(150, 150, 150), Color.FromArgb(222, 222, 222), Color.FromArgb(214, 214, 214));

        public static readonly Palette DarkMode = new(Color.FromArgb(44, 44, 44), Color.FromArgb(61, 61, 61),
            Color.FromArgb(255, 255, 255), Color.FromArgb(128, 128, 128), Color.FromArgb(70, 70, 70), Color.FromArgb(78, 78, 78));
    }

    private sealed class Colors(Palette palette) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => palette.Background;
        public override Color ImageMarginGradientBegin => palette.Background;
        public override Color ImageMarginGradientMiddle => palette.Background;
        public override Color ImageMarginGradientEnd => palette.Background;
        public override Color MenuBorder => palette.Border;
        public override Color MenuItemBorder => palette.Hover;
        public override Color MenuItemSelected => palette.Hover;
        public override Color MenuItemSelectedGradientBegin => palette.Hover;
        public override Color MenuItemSelectedGradientEnd => palette.Hover;
        public override Color MenuItemPressedGradientBegin => palette.Hover;
        public override Color MenuItemPressedGradientEnd => palette.Hover;
        public override Color SeparatorDark => palette.Separator;
        public override Color SeparatorLight => palette.Separator;
        public override Color CheckBackground => palette.Background;
        public override Color CheckSelectedBackground => palette.Hover;
        public override Color CheckPressedBackground => palette.Hover;
        public override Color ButtonSelectedBorder => palette.Hover;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _palette.Text : _palette.Disabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled != false ? _palette.Text : _palette.Disabled;
        base.OnRenderArrow(e);
    }

    /// <summary>A check mark in the text's color: the default one is dark on any background.</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var box = e.ImageRectangle;
        if (box.Width <= 0 || box.Height <= 0) return;
        var smoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(e.Item.Enabled ? _palette.Text : _palette.Disabled, Math.Max(1.5f, box.Height / 10f))
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
        };
        e.Graphics.DrawLines(pen,
        [
            new PointF(box.Left + box.Width * .22f, box.Top + box.Height * .52f),
            new PointF(box.Left + box.Width * .42f, box.Top + box.Height * .72f),
            new PointF(box.Left + box.Width * .78f, box.Top + box.Height * .30f),
        ]);
        e.Graphics.SmoothingMode = smoothing;
    }

    /// <summary>Small rounded corners on Windows 11, like its own menus; nothing changes elsewhere.</summary>
    public static void RoundCorners(ToolStripDropDown menu)
    {
        try
        {
            int preference = 3; // DWMWCP_ROUNDSMALL
            DwmSetWindowAttribute(menu.Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
