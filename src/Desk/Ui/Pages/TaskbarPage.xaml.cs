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

        Loaded += (_, _) =>
        {
            _app.StateChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => _app.StateChanged -= Refresh;
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
        }
        finally
        {
            _updating = false;
        }
    }
}
