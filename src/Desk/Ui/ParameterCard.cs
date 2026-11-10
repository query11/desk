using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Desk.Ui;

/// <summary>A card with a slider for one theme parameter.</summary>
internal sealed class ParameterCard : CardControl
{
    private readonly Slider _slider;
    private readonly System.Windows.Controls.TextBlock _value;
    private readonly Wpf.Ui.Controls.Button _reset;
    private bool _updating;

    public ParameterCard(ParameterInfo info)
    {
        // Implicit styles only apply to the exact type, so a subclass has to
        // ask for the card style explicitly; without it there is no template
        // and only the slider shows.
        SetResourceReference(StyleProperty, typeof(CardControl));
        Info = info;
        Icon = new SymbolIcon { Symbol = info.Icon };
        Header = new System.Windows.Controls.TextBlock { Text = info.Title, VerticalAlignment = VerticalAlignment.Center };
        ToolTip = info.Description;
        MinHeight = 56;

        _value = new System.Windows.Controls.TextBlock
        {
            MinWidth = 48,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };
        // Equal-width digits: the number doesn't jitter while dragging.
        System.Windows.Documents.Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);
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
                ValueChanged?.Invoke(this, Info.Inverted ? 1 - e.NewValue : e.NewValue);
            }
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_value);
        panel.Children.Add(_slider);
        _reset = new Wpf.Ui.Controls.Button
        {
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowReset20 },
            ToolTip = "恢复此项默认值", Width = 30, Height = 30, MinWidth = 0,
            Padding = new Thickness(4), Margin = new Thickness(8, 0, 0, 0),
            Appearance = ControlAppearance.Transparent,
        };
        _reset.Click += (_, _) => ResetRequested?.Invoke(this);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_reset, "Reset." + info.Name);
        System.Windows.Automation.AutomationProperties.SetName(_reset, "恢复" + info.Title + "默认值");
        panel.Children.Add(_reset);
        Content = panel;
        SizeChanged += (_, _) => _slider.Width = Math.Clamp(ActualWidth * .27, 88, 190);
        System.Windows.Automation.AutomationProperties.SetName(_slider, info.Title);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_slider, "Parameter." + info.Name);
    }

    public ParameterInfo Info { get; }

    /// <summary>Raised when the user moves the slider (not for <see cref="Show"/>).</summary>
    public event Action<ParameterCard, double>? ValueChanged;
    public event Action<ParameterCard>? ResetRequested;
    public void ShowReset(bool changed) => _reset.IsEnabled = changed;

    /// <summary>Shows a value without reporting it back.</summary>
    public void Show(double value)
    {
        _updating = true;
        try
        {
            double shown = Info.Inverted ? 1 - value : value;
            _slider.Value = Math.Clamp(shown, Info.Minimum, Info.Maximum);
            _value.Text = Info.Format(shown);
        }
        finally
        {
            _updating = false;
        }
    }
}
