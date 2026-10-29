using System.Windows.Controls;

namespace Desk.Ui.Pages;

public partial class TaskbarPage : Page
{
    private readonly DeskApp _app = UiHost.App;
    private readonly ParameterSection _parameters;
    private bool _updating;

    public TaskbarPage()
    {
        InitializeComponent();
        _parameters = new ParameterSection(_app, ParameterScope.Taskbar, Parameters.Taskbar,
            ParametersPanel, EmptyHint, ResetButton);

        MaximizeBox.SelectionChanged += (_, _) =>
        {
            if (!_updating && MaximizeBox.SelectedIndex >= 0)
            {
                _app.MaximizeMode = (MaximizeMode)MaximizeBox.SelectedIndex;
            }
        };
        ClickThroughSwitch.Checked += (_, _) => SetClickThrough(true);
        ClickThroughSwitch.Unchecked += (_, _) => SetClickThrough(false);
        Wire(MonitorSwitch, value => _app.ShowMonitor = value);
        Wire(MonitorNetworkBox, value => _app.MonitorNetwork = value);
        Wire(MonitorCpuBox, value => _app.MonitorCpu = value);
        Wire(MonitorMemoryBox, value => _app.MonitorMemory = value);

        Loaded += (_, _) =>
        {
            _app.StateChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => _app.StateChanged -= Refresh;
    }

    private void Wire(System.Windows.Controls.Primitives.ToggleButton toggle, Action<bool> set)
    {
        toggle.Checked += (_, _) =>
        {
            if (!_updating)
            {
                set(true);
            }
        };
        toggle.Unchecked += (_, _) =>
        {
            if (!_updating)
            {
                set(false);
            }
        };
    }

    private void SetClickThrough(bool enabled)
    {
        if (!_updating)
        {
            _app.ClickThrough = enabled;
        }
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            _parameters.Refresh();
            MaximizeBox.SelectedIndex = (int)_app.MaximizeMode;
            ClickThroughSwitch.IsChecked = _app.ClickThrough;
            MonitorSwitch.IsChecked = _app.ShowMonitor;
            MonitorNetworkBox.IsChecked = _app.MonitorNetwork;
            MonitorCpuBox.IsChecked = _app.MonitorCpu;
            MonitorMemoryBox.IsChecked = _app.MonitorMemory;
            MonitorNetworkBox.IsEnabled = MonitorCpuBox.IsEnabled = MonitorMemoryBox.IsEnabled = _app.ShowMonitor;
        }
        finally
        {
            _updating = false;
        }
    }
}
