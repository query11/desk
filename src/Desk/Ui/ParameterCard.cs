using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Desk.Ui;

/// <summary>A card with a slider for one theme parameter.</summary>
internal sealed class ParameterCard : CardControl
{
    private readonly Slider _slider;
    private readonly System.Windows.Controls.TextBlock _value;
    private bool _updating;

    public ParameterCard(ParameterInfo info)
    {
        Info = info;
        Margin = new Thickness(0, 0, 0, 4);
        Icon = new SymbolIcon { Symbol = info.Icon };
        Header = UiText.Header(info.Title, info.Description);

        _value = new System.Windows.Controls.TextBlock
        {
            MinWidth = 56,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };
        _slider = new Slider
        {
            Width = 220,
            Minimum = info.Minimum,
            Maximum = info.Maximum,
            SmallChange = info.Step,
            LargeChange = info.Step * 5,
            TickFrequency = info.Step,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _slider.ValueChanged += (_, e) =>
        {
            _value.Text = info.Format(e.NewValue);
            if (!_updating)
            {
                ValueChanged?.Invoke(this, e.NewValue);
            }
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_value);
        panel.Children.Add(_slider);
        Content = panel;
    }

    public ParameterInfo Info { get; }

    /// <summary>Raised when the user moves the slider (not for <see cref="Show"/>).</summary>
    public event Action<ParameterCard, double>? ValueChanged;

    /// <summary>Shows a value without reporting it back.</summary>
    public void Show(double value)
    {
        _updating = true;
        try
        {
            _slider.Value = Math.Clamp(value, Info.Minimum, Info.Maximum);
            _value.Text = Info.Format(value);
        }
        finally
        {
            _updating = false;
        }
    }
}
