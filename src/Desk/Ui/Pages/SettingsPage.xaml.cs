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
        ExportDiagnosticsButton.Click += (_, _) => ExportDiagnostics();
        ProjectButton.Click += (_, _) => DeskApp.OpenUrl(ProjectUrl);
        ExitButton.Click += (_, _) => _app.Exit();

        Loaded += (_, _) =>
        {
            _app.StateChanged += Refresh;
            Refresh();
            // Shows what is really set up, also after changes made outside Desk.
            _app.CheckAutostart();
        };
        Unloaded += (_, _) => _app.StateChanged -= Refresh;
    }

    private async void ExportDiagnostics()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出诊断",
            FileName = $"Desk-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "Zip 文件|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
        };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) != true)
        {
            return;
        }

        ExportDiagnosticsButton.IsEnabled = false;
        ExportInfoBar.IsOpen = true;
        ExportInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Informational;
        ExportInfoBar.Title = "正在导出诊断…";
        ExportInfoBar.Message = "";
        try
        {
            var result = await _app.ExportDiagnosticsAsync(dialog.FileName);
            if (result.Success)
            {
                ExportInfoBar.Severity = result.Missing.Count == 0
                    ? Wpf.Ui.Controls.InfoBarSeverity.Success
                    : Wpf.Ui.Controls.InfoBarSeverity.Warning;
                ExportInfoBar.Title = "诊断已导出";
                ExportInfoBar.Message = result.Missing.Count == 0
                    ? result.Path
                    : $"{result.Path}\n以下内容没能读取，已在包里的 summary.json 中注明：{string.Join("；", result.Missing)}";
            }
            else
            {
                ExportInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
                ExportInfoBar.Title = "导出失败";
                ExportInfoBar.Message = result.Error ?? "";
            }
        }
        catch (Exception e)
        {
            Log.Error("Exporting diagnostics failed", e);
            ExportInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            ExportInfoBar.Title = "导出失败";
            ExportInfoBar.Message = e.Message;
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private async void SetAutostart(bool enabled)
    {
        if (_updating)
        {
            return;
        }
        AutostartSwitch.IsEnabled = false;
        AutostartStateText.Text = enabled ? "正在开启…" : "正在关闭…";
        try
        {
            await _app.SetAutostartAsync(enabled);
        }
        catch (Exception e)
        {
            Log.Error("Autostart change failed", e);
        }
        finally
        {
            AutostartSwitch.IsEnabled = true;
            // The switch shows what is set up, not what was asked for.
            Refresh();
        }
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            var status = _app.AutostartInfo;
            AutostartSwitch.IsChecked = status.State == AutostartState.On;
            AutostartStateText.Text = status.State switch
            {
                AutostartState.On => status.Source == AutostartSource.Task
                    ? "当前：已开启（计划任务）"
                    : "当前：已开启（注册表启动项，会比计划任务晚一些启动）",
                AutostartState.Off => "当前：已关闭",
                AutostartState.Inactive => $"当前：不会启动（{status.Detail}）",
                _ => $"当前：{status.Detail ?? "未知"}",
            };

            AutostartInfoBar.IsOpen = _app.AutostartMessage != null;
            AutostartInfoBar.Title = _app.AutostartMessageIsError ? "开机自启没有改成功" : "开机自启已改好，但有一点问题";
            AutostartInfoBar.Message = _app.AutostartMessage ?? "";
            AutostartInfoBar.Severity = _app.AutostartMessageIsError
                ? Wpf.Ui.Controls.InfoBarSeverity.Error
                : Wpf.Ui.Controls.InfoBarSeverity.Warning;
        }
        finally
        {
            _updating = false;
        }
    }
}
