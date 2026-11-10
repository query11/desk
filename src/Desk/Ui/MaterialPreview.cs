using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Wpf.Ui.Controls;

namespace Desk.Ui;

/// <summary>A deliberately labelled material/layout sample, not a shell health check.</summary>
internal sealed class MaterialPreview : Border
{
    private readonly Grid _scene = new() { ClipToBounds = true };
    private readonly Border _glass = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _blurred = new();
    private readonly Border _tint = new();
    private readonly Border _grain = new();
    private readonly StackPanel _icons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly BlurEffect _blur = new() { RenderingBias = RenderingBias.Performance };
    private readonly System.Windows.Controls.TextBlock _mode = new() { FontSize = 11, Margin = new Thickness(14, 10, 14, 0), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ParameterScope _scope;
    private System.Windows.Controls.TextBlock? _menuCaption;

    public MaterialPreview(ParameterScope scope)
    {
        _scope = scope;
        Height = scope == ParameterScope.Taskbar ? 128 : 160;
        Margin = new Thickness(0, 0, 0, 12);
        CornerRadius = new CornerRadius(12);
        Child = _scene;
        _blurred.Effect = _blur;
        _grain.Background = CreateNoise();
        var layers = new Grid { ClipToBounds = true };
        layers.Children.Add(_blurred);
        layers.Children.Add(_tint);
        layers.Children.Add(_grain);
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (scope == ParameterScope.StartMenu)
        {
            _menuCaption = new System.Windows.Controls.TextBlock
            {
                Text = "搜索应用、设置和文档", FontSize = 11, Margin = new Thickness(18, 0, 18, 18),
                Foreground = Brushes.DimGray,
            };
            content.Children.Add(_menuCaption);
        }
        content.Children.Add(_icons);
        layers.Children.Add(content);
        _glass.Child = layers;
        _scene.Children.Add(_glass);
        _scene.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "效果示意", FontSize = 11, Margin = new Thickness(14, 10, 14, 0), Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Top,
        });
        _scene.Children.Add(_mode);
        _glass.BorderThickness = new Thickness(1);
        _glass.BorderBrush = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255));
        _glass.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = .15 };
        SizeChanged += (_, _) =>
        {
            _glass.Width = Math.Min(scope == ParameterScope.Taskbar ? 390 : 300, Math.Max(120, ActualWidth - 60));
            _scene.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
        };
        _glass.SizeChanged += (_, _) => _glass.Clip = new RectangleGeometry(
            new Rect(0, 0, _glass.ActualWidth, _glass.ActualHeight), _glass.CornerRadius.TopLeft, _glass.CornerRadius.TopLeft);
        _glass.Height = scope == ParameterScope.Taskbar ? 66 : 120;
        ToolTip = "布局与材质示意；实际效果以桌面为准。";
        System.Windows.Automation.AutomationProperties.SetName(this, "材质效果示意预览");
    }

    public void Update(Func<string, double?> value, bool light)
    {
        string prefix = _scope == ParameterScope.Taskbar ? "island" : "menu";
        double opacity = value(prefix + "Opacity") ?? .3;
        double luminosity = value(prefix + "Luminosity") ?? .4;
        double saturation = value(prefix + "Saturation") ?? 1;
        double radius = value(_scope == ParameterScope.Taskbar ? "radius" : "menuRadius") ?? 10;
        double size = _scope == ParameterScope.Taskbar ? value("iconSize") ?? 24 : 24;
        double spacing = _scope == ParameterScope.Taskbar ? Math.Max(size + 8, value("buttonWidth") ?? 48) : 38;
        if (_scope == ParameterScope.Taskbar) _glass.Height = 64 - 2 * Math.Clamp(value("gap") ?? 4, 0, 8);
        _blur.Radius = Math.Clamp((value(prefix + "Blur") ?? 20) / 2, 0, 30);
        _glass.CornerRadius = _blurred.CornerRadius = _tint.CornerRadius = _grain.CornerRadius = new CornerRadius(radius);
        _glass.Clip = new RectangleGeometry(new Rect(0, 0, double.IsNaN(_glass.Width) ? 300 : _glass.Width, _glass.Height), radius, radius);
        var wallpaper = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new(Saturate(Color.FromRgb(47, 104, 177), saturation), 0),
                new(Saturate(Color.FromRgb(109, 173, 198), saturation), .42),
                new(Saturate(Color.FromRgb(221, 177, 159), saturation), .68),
                new(Saturate(Color.FromRgb(80, 96, 151), saturation), 1),
            },
        };
        _scene.Background = wallpaper;
        // A repeated accent makes the effect of blur visible on a small sample.
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(wallpaper, null, new Rect(0, 0, 400, 140));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(140, 241, 210, 187)), null, new Point(275, 70), 70, 55);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(160, 40, 75, 133)), null, new Rect(80, 0, 24, 140));
        }
        _blurred.Background = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        double coverage = 1 - (1 - opacity) * (1 - luminosity * .65);
        byte tint = light ? (byte)243 : (byte)28;
        _tint.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(coverage, 0, 1) * 255), tint, tint, tint));
        _grain.Opacity = Math.Clamp((value(prefix + "Noise") ?? .02) * 2, 0, .2);
        _mode.Text = light ? "浅色" : "深色";
        _mode.Foreground = Brushes.White;
        if (_menuCaption != null) _menuCaption.Foreground = light ? Brushes.DimGray : Brushes.WhiteSmoke;
        _icons.Children.Clear();
        var symbols = new[] { SymbolRegular.Grid24, SymbolRegular.Folder24, SymbolRegular.Globe24, SymbolRegular.Chat24, SymbolRegular.Settings24 };
        var colors = new[] { "#1687DC", "#E7AB36", "#248BA8", "#38A972", "#7576C4" };
        for (int i = 0; i < symbols.Length; i++)
        {
            _icons.Children.Add(new Border
            {
                Width = size, Height = size, Margin = new Thickness((spacing - size) / 2, 0, (spacing - size) / 2, 0), CornerRadius = new CornerRadius(size * .22),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])),
                Child = new SymbolIcon { Symbol = symbols[i], FontSize = size * .7, Foreground = Brushes.White },
            });
        }
    }

    private static Brush CreateNoise()
    {
        // A fixed random tile avoids a distracting regular grid and stays stable while dragging.
        var random = new Random(27);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 48, 48));
            for (int i = 0; i < 360; i++)
                dc.DrawRectangle(i % 2 == 0 ? Brushes.White : Brushes.Black, null,
                    new Rect(random.Next(48), random.Next(48), 1, 1));
        }
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 48, 48),
        };
        brush.Freeze();
        return brush;
    }

    private static Color Saturate(Color color, double amount)
    {
        double gray = color.R * .213 + color.G * .715 + color.B * .072;
        byte Channel(byte channel) => (byte)Math.Clamp(gray + (channel - gray) * amount, 0, 255);
        return Color.FromRgb(Channel(color.R), Channel(color.G), Channel(color.B));
    }
}
