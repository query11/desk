using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Desk.Ui;

/// <summary>A card with a slider for one theme parameter, and its number to type in.</summary>
internal sealed class ParameterCard : CardControl
{
    private readonly Slider _slider;
    private readonly System.Windows.Controls.TextBox _value;
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

        // The number can be typed: Enter or leaving the box takes it, Esc
        // goes back. It is in the slider's units, with them ("28 px", "35%").
        _value = new System.Windows.Controls.TextBox
        {
            Width = 72,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            MaxLength = 12,
        };
        // Equal-width digits: the number doesn't jitter while dragging.
        System.Windows.Documents.Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);
        AutomationProperties.SetAutomationId(_value, "Value." + info.Name);
        AutomationProperties.SetName(_value, info.Title + "的数值");
        AutomationProperties.SetHelpText(_value,
            $"可以输入 {info.Format(info.Minimum)} 到 {info.Format(info.Maximum)} 之间的数值，按回车确认。");
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
            ShowNumber(e.NewValue);
            if (!_updating)
            {
                ValueChanged?.Invoke(this, Info.Inverted ? 1 - e.NewValue : e.NewValue);
            }
        };
        _value.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); _value.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _value.Text = info.Format(_slider.Value); _value.SelectAll(); e.Handled = true; }
        };
        _value.LostKeyboardFocus += (_, _) => Commit();
        _value.GotKeyboardFocus += (_, _) => _value.SelectAll();

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
        AutomationProperties.SetAutomationId(_reset, "Reset." + info.Name);
        AutomationProperties.SetName(_reset, "恢复" + info.Title + "默认值");
        panel.Children.Add(_reset);
        Content = panel;
        // The box keeps its width; the slider gives way on a narrow page.
        SizeChanged += (_, _) => _slider.Width = Math.Clamp(ActualWidth * .27, 72, 190);
        AutomationProperties.SetName(_slider, info.Title);
        AutomationProperties.SetAutomationId(_slider, "Parameter." + info.Name);
    }

    public ParameterInfo Info { get; }

    /// <summary>Raised when the user moves the slider or types a number (not for <see cref="Show"/>).</summary>
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
            ShowNumber(shown);
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>The number next to the slider, unless the user is typing one.</summary>
    private void ShowNumber(double shown)
    {
        if (!_value.IsKeyboardFocused) _value.Text = Info.Format(shown);
    }

    /// <summary>
    /// Takes the typed number: kept within the range and on the slider's
    /// steps; anything that isn't a number puts the current value back.
    /// </summary>
    internal void Commit()
    {
        if (!ParameterValue.TryParse(_value.Text, Info.Unit, out double typed))
        {
            _value.Text = Info.Format(_slider.Value);
            return;
        }
        double shown = ParameterValue.Settle(typed, Info.Minimum, Info.Maximum, Info.Step);
        _value.Text = Info.Format(shown);
        if (Math.Abs(shown - _slider.Value) > Info.Step / 1000)
        {
            _slider.Value = shown;
        }
    }
}
