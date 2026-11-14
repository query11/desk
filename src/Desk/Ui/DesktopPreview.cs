using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Desk.Ui;

/// <summary>Which part of the desktop a preview shows.</summary>
internal enum PreviewFocus
{
    /// <summary>All of it: the Start menu open above the taskbar (overview, gallery).</summary>
    Desktop,

    /// <summary>The bottom of the screen, larger, with the Start menu closed (taskbar and display pages).</summary>
    Taskbar,

    /// <summary>The Start menu, larger (Start menu page).</summary>
    StartMenu,
}

/// <summary>
/// Vector illustration, not a screenshot: one picture for the overview, the
/// gallery and the parameter pages. Reads the same constants as the theme
/// compiler, from a theme with its saved changes or from the values being
/// edited.
/// </summary>
internal sealed class DesktopPreview : FrameworkElement
{
    /// <summary>A theme constant in a mode (true: light), as the picture should show it.</summary>
    private Func<string, bool, string?>? _constant;
    private bool _bar;
    private bool _light = true;
    private bool _menuLight = true;
    public ImageSource? Wallpaper { get; set; }

    /// <summary>
    /// What the info island on the left shows ("network", "cpu", "memory");
    /// empty or null when it is switched off.
    /// </summary>
    public IReadOnlyList<string>? MonitorItems { get; set; }

    public PreviewFocus Part { get; set; }

    /// <summary>
    /// For values that change while a slider is dragged: a blurred wallpaper
    /// not made yet is made a moment later, and the closest one is shown
    /// meanwhile, instead of one new picture for every step.
    /// </summary>
    public bool QuickBlur { get; set; }

    // One cache for every preview: the gallery rebuilds its cards while the
    // user types in the search box, and all cards share a size and a wallpaper.
    private static readonly Dictionary<(int Width, int Radius), ImageSource> BlurredWallpapers = [];
    private static ImageSource? _blurredFrom;

    /// <summary>Blur radii come in steps: a drag of the blur slider makes a few pictures, not sixty.</summary>
    private const int BlurStep = 4;

    private bool _blurPending;

    /// <summary>Blurred wallpapers the last rendering would have liked to use (QuickBlur).</summary>
    private readonly HashSet<(int Width, int Radius)> _wanted = [];

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
        int radius = (int)Math.Round(Math.Clamp(blur, 0, 60) / BlurStep) * BlurStep;
        if (BlurredWallpapers.TryGetValue((pixels, radius), out var cached)) return cached;
        if (QuickBlur && BlurredWallpapers.Where(pair => pair.Key.Width == pixels)
                .OrderBy(pair => Math.Abs(pair.Key.Radius - radius)).Select(pair => pair.Value).FirstOrDefault() is { } closest)
        {
            _wanted.Add((pixels, radius));
            if (!_blurPending)
            {
                _blurPending = true;
                Dispatcher.BeginInvoke(MakeWanted, DispatcherPriority.Background);
            }
            return closest;
        }
        return MakeBlurred(pixels, radius);
    }

    /// <summary>Once the slider rests a moment: the pictures the latest values need.</summary>
    private void MakeWanted()
    {
        _blurPending = false;
        if (!ReferenceEquals(_blurredFrom, Wallpaper)) return;
        bool made = false;
        foreach (var (pixels, radius) in _wanted.ToList())
        {
            if (BlurredWallpapers.ContainsKey((pixels, radius))) continue;
            MakeBlurred(pixels, radius);
            made = true;
        }
        if (made) InvalidateVisual();
    }

    private ImageSource MakeBlurred(int pixels, int radius)
    {
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

    /// <summary>A theme with its saved changes.</summary>
    public void Update(ThemeOption? option, bool light, bool? menuLight = null)
    {
        _constant = option?.Theme is { } theme ? (name, mode) => theme.GetConstant(name, mode, option.Overrides) : null;
        _bar = option?.IsBar == true;
        _light = light;
        _menuLight = menuLight ?? light;
        InvalidateVisual();
    }

    /// <summary>Values by name, such as the ones being edited on a parameter page.</summary>
    /// <param name="bar">The taskbar is the full-width bar (a theme without islands).</param>
    public void Update(Func<string, bool, string?> constant, bool bar, bool light, bool menuLight)
    {
        _constant = constant;
        _bar = bar;
        _light = light;
        _menuLight = menuLight;
        InvalidateVisual();
    }

    /// <summary>A constant as the picture reads it: the taskbar's in the taskbar's mode, the menu's in its own.</summary>
    internal string? Shown(string name) =>
        _constant?.Invoke(name, name.StartsWith("menu", StringComparison.Ordinal) ? _menuLight : _light);

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
        double.TryParse(_constant?.Invoke(key, light), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;

    private Color Tint(string key, bool light)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(_constant?.Invoke(key, light)
                ?? (light ? "#F3F3F3" : "#202020"));
        }
        catch (Exception e) when (e is FormatException or NotSupportedException or ArgumentException)
        {
            return light ? Colors.WhiteSmoke : Color.FromRgb(32, 32, 32);
        }
    }

    /// <summary>
    /// The rows of the 640 × 360 scene to show. The taskbar and the menu are
    /// shown larger, but never so large that the scene gets narrower than
    /// 640: then more rows show (above the taskbar, around the menu).
    /// </summary>
    private (double Scale, double Top) Viewport()
    {
        if (Part == PreviewFocus.Desktop) return (ActualHeight / 360, 0);
        var (top, rows) = Part == PreviewFocus.Taskbar ? (196.0, 164.0) : (14.0, 290.0);
        double scale = Math.Min(ActualHeight / rows, ActualWidth / 640);
        double shown = ActualHeight / scale;
        double start = Part == PreviewFocus.Taskbar ? 360 - shown : top + rows / 2 - shown / 2;
        return (scale, Math.Clamp(start, 0, Math.Max(0, 360 - shown)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        _wanted.Clear();
        var (scale, top) = Viewport();
        double sceneWidth = ActualWidth / scale;
        double side = (sceneWidth - 640) / 2;
        // 16 scene units on the whole desktop, as before; 12 px on the larger views.
        double corner = Part == PreviewFocus.Desktop ? 16 : 12 / scale;
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.PushTransform(new TranslateTransform(0, -top));
        dc.PushClip(new RectangleGeometry(new Rect(0, top, sceneWidth, ActualHeight / scale), corner, corner));
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
        // On the taskbar page the menu is closed.
        if (Part != PreviewFocus.Taskbar)
        {
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
        }
        double radius = Math.Clamp(Number("radius", _light, 10), 0, 30);
        bool bar = _bar;
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

/// <summary>
/// The preview of a parameter page: the desktop picture, larger where the
/// page applies, labelled as an illustration and with the mode it shows.
/// </summary>
internal sealed class ParameterPreview : Grid
{
    private readonly TextBlock _mode;

    public ParameterPreview(PreviewFocus focus)
    {
        Picture = new DesktopPreview
        {
            Part = focus, QuickBlur = true, Height = focus == PreviewFocus.StartMenu ? 220 : 150,
        };
        Margin = new Thickness(0, 0, 0, 12);
        // The picture itself ignores the mouse; this keeps the tooltip.
        Background = Brushes.Transparent;
        Children.Add(Picture);
        Children.Add(Pill(Label("效果示意"), HorizontalAlignment.Left));
        Children.Add(Pill(_mode = Label(""), HorizontalAlignment.Right));
        ToolTip = "布局与材质示意，按当前编辑的数值绘制；实际效果以桌面为准。";
        AutomationProperties.SetName(this, "效果示意预览");
        Loaded += async (_, _) =>
        {
            if (Picture.Wallpaper != null) return;
            var wallpaper = await WallpaperPreview.LoadAsync();
            await Dispatcher.InvokeAsync(() => { Picture.Wallpaper = wallpaper.Image; Picture.InvalidateVisual(); });
        };
    }

    public DesktopPreview Picture { get; }

    public void ShowMode(bool light) => _mode.Text = light ? "浅色" : "深色";

    // Readable on any wallpaper: light text on a dark, translucent label.
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 11, Foreground = Brushes.White };

    private static Border Pill(TextBlock text, HorizontalAlignment side) => new()
    {
        Child = text, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2),
        Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), Margin = new Thickness(10, 8, 10, 0),
        HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
    };
}
