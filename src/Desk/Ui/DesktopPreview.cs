using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Desk.Ui;

/// <summary>Vector illustration, not a screenshot. Reads the same constants as the theme compiler.</summary>
internal sealed class DesktopPreview : FrameworkElement
{
    private ThemeOption? _option;
    private bool _light = true;
    private bool _menuLight = true;
    public ImageSource? Wallpaper { get; set; }

    /// <summary>
    /// What the info island on the left shows ("network", "cpu", "memory");
    /// empty or null when it is switched off.
    /// </summary>
    public IReadOnlyList<string>? MonitorItems { get; set; }

    // One cache for every preview: the gallery rebuilds its cards while the
    // user types in the search box, and all cards share a size and a wallpaper.
    private static readonly Dictionary<(int Width, int Radius), ImageSource> BlurredWallpapers = [];
    private static ImageSource? _blurredFrom;

    private ImageSource BlurredWallpaper(double width, double blur)
    {
        // Rounded up to steps of 64: resizing the window doesn't blur the
        // wallpaper again for every pixel of width.
        int pixels = Math.Clamp(((int)Math.Ceiling(width) + 63) / 64 * 64, 320, 1600);
        if (!ReferenceEquals(_blurredFrom, Wallpaper))
        {
            BlurredWallpapers.Clear();
            _blurredFrom = Wallpaper;
        }
        int radius = (int)Math.Clamp(Math.Round(blur), 0, 60);
        if (BlurredWallpapers.TryGetValue((pixels, radius), out var cached)) return cached;
        var visual = new DrawingVisual { Effect = new BlurEffect { Radius = radius, RenderingBias = RenderingBias.Performance } };
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new ImageBrush(Wallpaper) { Stretch = Stretch.UniformToFill }, null, new Rect(-20, -20, pixels + 40, 400));
        var bitmap = new RenderTargetBitmap(pixels, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        if (BlurredWallpapers.Count >= 24) BlurredWallpapers.Clear();
        BlurredWallpapers[(pixels, radius)] = bitmap;
        return bitmap;
    }

    public DesktopPreview()
    {
        Height = 190;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    public void Update(ThemeOption? option, bool light, bool? menuLight = null)
    {
        _option = option;
        _light = light;
        _menuLight = menuLight ?? light;
        InvalidateVisual();
    }

    /// <summary>The theme constants the picture is drawn from.</summary>
    private static readonly string[] Drawn =
    [
        "islandOpacity", "islandLuminosity", "islandBlur", "islandTint", "radius", "gap", "buttonWidth", "iconSize",
        "menuOpacity", "menuLuminosity", "menuBlur", "menuTint", "menuRadius",
    ];

    /// <summary>Equal for two options that give the same picture in this mode.</summary>
    public static string Key(ThemeOption option, bool light) =>
        option.IsBar + "|" + string.Join("|", Drawn.Select(name => option.Theme?.GetConstant(name, light, option.Overrides)));

    private double Number(string key, bool light, double fallback) =>
        double.TryParse(_option?.Theme?.GetConstant(key, light, _option.Overrides), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;

    private Color Tint(string key, bool light)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(_option?.Theme?.GetConstant(key, light, _option.Overrides)
                ?? (light ? "#F3F3F3" : "#202020"));
        }
        catch (Exception e) when (e is FormatException or NotSupportedException or ArgumentException)
        {
            return light ? Colors.WhiteSmoke : Color.FromRgb(32, 32, 32);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double scale = ActualHeight / 360;
        double sceneWidth = ActualWidth / scale;
        double side = (sceneWidth - 640) / 2;
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, sceneWidth, 360), 16, 16));
        dc.PushTransform(new ScaleTransform(sceneWidth / 640, 1));
        // Identical wallpaper across options makes material differences comparable.
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(135, 164, 181), Color.FromRgb(47, 78, 106), 40), null,
            new Rect(0, 0, 640, 360));
        dc.DrawGeometry(new LinearGradientBrush(Color.FromRgb(199, 215, 216), Color.FromRgb(88, 128, 153), 80), null,
            Geometry.Parse("M0,0 L640,0 L640,100 C440,-30 320,270 0,220 Z"));
        dc.DrawGeometry(new LinearGradientBrush(Color.FromRgb(132, 172, 187), Color.FromRgb(33, 67, 99), 75), null,
            Geometry.Parse("M0,360 L640,360 L640,145 C400,60 370,320 0,270 Z"));
        dc.Pop();
        if (Wallpaper != null)
            dc.DrawRectangle(new ImageBrush(Wallpaper) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, sceneWidth, 360));
        // Keep app icons and menu proportions stable even in a wide overview banner.
        dc.PushTransform(new TranslateTransform(side, 0));

        void Glass(Rect rect, string prefix, bool light, double radius)
        {
            double opacity = Math.Clamp(Number(prefix + "Opacity", light, .25), 0, 1);
            double luminosity = Math.Clamp(Number(prefix + "Luminosity", light, .4), 0, 1);
            double blur = Math.Clamp(Number(prefix + "Blur", light, 20) / 60, 0, 1);
            var tint = Tint(prefix + "Tint", light);
            // Approximation of composited glass; the real desktop uses DeskBlur.
            tint.A = (byte)(255 * Math.Clamp(.12 + .62 * opacity + .18 * luminosity + .08 * blur, .12, .96));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(16, 0, 0, 0)), null,
                new Rect(rect.X, rect.Y + 4, rect.Width, rect.Height), radius, radius);
            if (Wallpaper != null)
            {
                dc.PushClip(new RectangleGeometry(rect, radius, radius));
                dc.DrawImage(BlurredWallpaper(sceneWidth, Number(prefix + "Blur", light, 20)), new Rect(-side, 0, sceneWidth, 360));
                dc.Pop();
            }
            dc.DrawRoundedRectangle(new SolidColorBrush(tint), new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1), rect, radius, radius);
        }
        var ink = new SolidColorBrush(_menuLight ? Color.FromArgb(150, 24, 39, 54) : Color.FromArgb(190, 242, 246, 250));
        double menuRadius = Math.Clamp(Number("menuRadius", _menuLight, 20), 0, 40);
        Glass(new Rect(182, 32, 276, 252), "menu", _menuLight, menuRadius);
        dc.DrawRoundedRectangle(new SolidColorBrush(_menuLight ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(70, 0, 0, 0)),
            null, new Rect(201, 50, 238, 25), 8, 8);
        dc.DrawEllipse(null, new Pen(ink, 1.5), new Point(215, 61), 4, 4);
        dc.DrawLine(new Pen(ink, 1.5), new Point(218, 64), new Point(221, 67));
        dc.DrawRoundedRectangle(ink, null, new Rect(230, 59, 100, 3), 1.5, 1.5);
        dc.DrawRoundedRectangle(ink, null, new Rect(205, 91, 47, 4), 2, 2);
        for (int i = 0; i < 12; i++)
        {
            double x = 213 + (i % 6) * 39, y = 110 + (i / 6) * 46;
            Icon(dc, i, x, y, 19);
            dc.DrawRoundedRectangle(ink, null, new Rect(x - 3, y + 25, 25, 2), 1, 1);
        }
        dc.DrawRoundedRectangle(ink, null, new Rect(205, 210, 47, 4), 2, 2);
        for (int i = 0; i < 2; i++)
        {
            dc.DrawRoundedRectangle(ink, null, new Rect(205 + i * 122, 231, 12, 15), 3, 3);
            dc.DrawRoundedRectangle(ink, null, new Rect(224 + i * 122, 233, 60, 3), 1, 1);
            dc.DrawRoundedRectangle(ink, null, new Rect(224 + i * 122, 242, 43, 2), 1, 1);
        }
        double radius = Math.Clamp(Number("radius", _light, 10), 0, 30);
        bool bar = _option?.IsBar == true;
        double gap = bar ? 0 : Math.Clamp(Number("gap", _light, 4), 0, 8);
        double spacing = Math.Clamp(Number("buttonWidth", _light, 48), 40, 64) * .75;
        double size = Math.Clamp(Number("iconSize", _light, 28), 16, 40) * .75;
        double width = spacing * 6 + 24;
        Glass(bar ? new Rect(-side, 307, sceneWidth, 53) : new Rect(320 - width / 2, 307 + gap, width, 48 - gap * 2), "island", _light, bar ? 0 : radius);
        if (!bar) Glass(new Rect(546 + side, 307 + gap, 78, 48 - gap * 2), "island", _light, radius);
        for (int i = 0; i < 6; i++) Icon(dc, i, 320 - spacing * 3 + spacing * i + (spacing - size) / 2, 321, size);
        var tray = new SolidColorBrush(_light ? Color.FromArgb(190, 30, 45, 60) : Color.FromArgb(220, 240, 245, 255));
        dc.DrawRoundedRectangle(tray, null, new Rect(591 + side, 323, 22, 3), 1, 1);
        dc.DrawRoundedRectangle(tray, null, new Rect(586 + side, 333, 27, 2), 1, 1);
        dc.DrawEllipse(tray, null, new Point(561 + side, 328), 2, 2);
        dc.DrawEllipse(tray, null, new Point(571 + side, 328), 2, 2);
        if (MonitorItems is { Count: > 0 } items)
        {
            // The info island: a label and a value per item, as on the desktop.
            // When it would run into the apps it shows less, like the real one.
            double left = 16 - side, cell = 30;
            int count = items.Count;
            while (count > 0 && left + count * cell + 14 > 320 - width / 2 - 8) count--;
            if (count > 0 && !bar) Glass(new Rect(left, 307 + gap, count * cell + 14, 48 - gap * 2), "island", _light, radius);
            var label = new SolidColorBrush(_light ? Color.FromArgb(120, 30, 45, 60) : Color.FromArgb(150, 240, 245, 255));
            for (int i = 0; i < count; i++)
            {
                double x = left + 10 + i * cell;
                dc.DrawRoundedRectangle(label, null, new Rect(x, 323, items[i] == "network" ? 16 : 11, 2.5), 1, 1);
                dc.DrawRoundedRectangle(tray, null, new Rect(x, 332, items[i] == "network" ? 21 : 15, 3.5), 1.5, 1.5);
            }
        }
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static void Icon(DrawingContext dc, int index, double x, double y, double size)
    {
        string[] colors = ["#2588DC", "#E5AD45", "#35A7A4", "#7184C8", "#E68471", "#617F98"];
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(colors[index % colors.Length])!;
        if (index == 0)
        {
            double half = (size - 2) / 2;
            for (int i = 0; i < 4; i++) dc.DrawRectangle(brush, null, new Rect(x + (i % 2) * (half + 2), y + (i / 2) * (half + 2), half, half));
        }
        else
        {
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y, size, size), size * .24, size * .24);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(185, 255, 255, 255)), null,
                new Rect(x + size * .25, y + size * .32, size * .5, size * .36), 2, 2);
        }
    }
}
