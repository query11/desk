using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Desk.Ui;

/// <summary>
/// The screens as Windows arranges them, one button each: two screens of the
/// same model can't be told apart by name and resolution alone. Marks the
/// main screen, the one being adjusted and those with their own settings.
/// The list above it stays the way to choose with the keyboard.
/// </summary>
internal sealed class DisplayMap : Panel
{
    private const double MapHeight = 132;
    private const double Gap = 3;

    /// <summary>A screen was clicked.</summary>
    public event Action<DesktopDisplay>? Chosen;

    /// <param name="selected">The screen being adjusted.</param>
    /// <param name="custom">Whether a screen uses its own settings.</param>
    public void Show(IReadOnlyList<DesktopDisplay> displays, string? selected, Func<string, bool> custom)
    {
        // The same screens: the buttons stay (with the focus on them) and only change their marks.
        bool same = Children.Count == displays.Count &&
                    Children.OfType<FrameworkElement>().Select(c => c.Tag).SequenceEqual(displays);
        if (!same)
        {
            Children.Clear();
            foreach (var display in displays)
            {
                var button = new Wpf.Ui.Controls.Button
                {
                    Tag = display, Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch, MinWidth = 0, MinHeight = 0,
                };
                AutomationProperties.SetAutomationId(button, "Displays.Screen." + display.Id);
                button.Click += (_, _) => Chosen?.Invoke(display);
                Children.Add(button);
            }
        }
        int number = 0;
        foreach (var button in Children.OfType<Wpf.Ui.Controls.Button>())
        {
            var display = (DesktopDisplay)button.Tag;
            number++;
            bool chosen = display.Id == selected, own = custom(display.Id);
            string marks = string.Join(" · ", new[] { display.Primary ? "主屏" : null, own ? "独立设置" : null }.OfType<string>());
            var lines = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(new TextBlock
            {
                Text = number.ToString(), FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            });
            lines.Children.Add(new TextBlock { Text = display.Name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
            if (marks.Length > 0)
                lines.Children.Add(new TextBlock { Text = marks, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
            // A small screen in the picture gets smaller text rather than cut-off text.
            button.Content = new Viewbox { Child = lines, StretchDirection = StretchDirection.DownOnly };
            button.Appearance = chosen ? ControlAppearance.Primary : ControlAppearance.Secondary;
            button.ToolTip = display.Label;
            AutomationProperties.SetName(button, $"屏幕 {number}：{display.Label}" + (own ? "，使用独立设置" : "") +
                (chosen ? "，正在调整" : ""));
        }
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size available)
    {
        var size = new Size(double.IsInfinity(available.Width) ? 480 : available.Width, MapHeight);
        foreach (var (child, rect) in Places(size)) child.Measure(rect.Size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (child, rect) in Places(finalSize)) child.Arrange(rect);
        return finalSize;
    }

    /// <summary>Each screen's button where the screen is, scaled to fit, the whole centered.</summary>
    private List<(UIElement Child, Rect Place)> Places(Size size)
    {
        var screens = Children.OfType<FrameworkElement>().Where(c => c.Tag is DesktopDisplay)
            .Select(c => (Child: (UIElement)c, Display: (DesktopDisplay)c.Tag)).ToList();
        var places = new List<(UIElement, Rect)>();
        if (screens.Count == 0) return places;
        double left = screens.Min(s => s.Display.Left), top = screens.Min(s => s.Display.Top);
        double width = Math.Max(1, screens.Max(s => s.Display.Left + s.Display.Width) - left);
        double height = Math.Max(1, screens.Max(s => s.Display.Top + s.Display.Height) - top);
        double scale = Math.Min(size.Width / width, size.Height / height);
        double offsetX = (size.Width - width * scale) / 2, offsetY = (size.Height - height * scale) / 2;
        foreach (var (child, display) in screens)
        {
            var place = new Rect(offsetX + (display.Left - left) * scale + Gap, offsetY + (display.Top - top) * scale + Gap,
                Math.Max(0, display.Width * scale - 2 * Gap), Math.Max(0, display.Height * scale - 2 * Gap));
            places.Add((child, place));
        }
        return places;
    }
}
