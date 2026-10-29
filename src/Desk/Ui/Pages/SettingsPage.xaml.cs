using System.Windows.Controls;

namespace Desk.Ui.Pages;

public partial class SettingsPage : Page
{
    private const string ProjectUrl = "https://github.com/query11/desk";

    private readonly DeskApp _app = UiHost.App;
    private bool _updating;

    public SettingsPage()
    {
        InitializeComponent();

        VersionText.Text = $"Desk {DeskApp.Version}";
        AutostartSwitch.Checked += (_, _) => SetAutostart(true);
        AutostartSwitch.Unchecked += (_, _) => SetAutostart(false);
        ThemesFolderButton.Click += (_, _) => _app.OpenThemesFolder();
        LogsFolderButton.Click += (_, _) => _app.OpenLogsFolder();
        ProjectButton.Click += (_, _) => DeskApp.OpenUrl(ProjectUrl);
        ExitButton.Click += (_, _) => _app.Exit();

        Loaded += (_, _) =>
        {
            _app.StateChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => _app.StateChanged -= Refresh;
    }

    private async void SetAutostart(bool enabled)
    {
        if (_updating)
        {
            return;
        }
        AutostartSwitch.IsEnabled = false;
        try
        {
            await _app.SetAutostartAsync(enabled);
        }
        finally
        {
            AutostartSwitch.IsEnabled = true;
        }
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            AutostartSwitch.IsChecked = _app.AutostartEnabled;
        }
        finally
        {
            _updating = false;
        }
    }
}
