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

        VersionHeader.Title = $"Desk {DeskApp.Version}";
        AutostartSwitch.Checked += (_, _) => SetAutostart(true);
        AutostartSwitch.Unchecked += (_, _) => SetAutostart(false);
        ThemesFolderButton.Click += (_, _) => _app.OpenThemesFolder();
        LogsFolderButton.Click += (_, _) => _app.OpenLogsFolder();
        ExportDiagnosticsButton.Click += (_, _) => ExportDiagnostics();
        BackupButton.Click += (_, _) => Backup();
        RestoreButton.Click += (_, _) => Restore();
        ProjectButton.Click += (_, _) => DeskApp.OpenUrl(ProjectUrl);
        ExitButton.Click += async (_, _) =>
        {
            if (await UiHost.ConfirmAsync("退出 Desk？",
                    "任务栏和开始菜单会恢复成系统原样。之后再次运行 Desk.exe 就能重新启用。", "退出并还原",
                    dangerous: true))
            {
                _app.Exit();
            }
        };

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

    private async void Backup()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "备份设置",
            FileName = $"Desk-设置备份-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "Zip 文件|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
        };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) != true)
        {
            return;
        }
        BackupButton.IsEnabled = false;
        BackupInfoBar.IsOpen = false;
        try
        {
            await _app.BackupSettingsAsync(dialog.FileName);
            UiHost.Toast("设置已备份", dialog.FileName);
        }
        catch (Exception e)
        {
            Log.Error("Backing up the settings failed", e);
            ShowBackupProblem("没有备份成功", e.Message);
        }
        finally
        {
            BackupButton.IsEnabled = true;
        }
    }

    private async void Restore()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "从备份恢复",
            Filter = "Desk 设置备份 (*.zip)|*.zip",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) != true)
        {
            return;
        }
        RestoreButton.IsEnabled = false;
        BackupInfoBar.IsOpen = false;
        try
        {
            // Checked completely before asking: a file that can't be used says why, and nothing changes.
            var backup = await _app.ReadBackupAsync(dialog.FileName);
            string made = backup.Created is { } created ? created.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "时间不明";
            string themes = backup.Themes.Count == 0 ? "，没有个人方案" : $"和 {backup.Themes.Count} 个个人方案";
            if (!await UiHost.ConfirmAsync("从备份恢复？",
                    $"这份备份做于 {made}，包含设置{themes}。\n\n现在的设置会被替换；同名但内容不同的个人方案会先移到主题目录的 .trash 文件夹。开机自启、窗口位置和美化开关保持现在的状态。",
                    "恢复", dangerous: true))
            {
                return;
            }
            _app.RestoreBackup(backup);
            UiHost.Toast("已从备份恢复", "设置和个人方案已换成备份里的，外观会在几秒内更新。");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowBackupProblem(e is InvalidDataException ? "这个文件不能用来恢复" : "没有恢复成功", e.Message);
        }
        finally
        {
            RestoreButton.IsEnabled = true;
        }
    }

    /// <summary>Errors stay on the page, to be read.</summary>
    private void ShowBackupProblem(string title, string message)
    {
        BackupInfoBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
        BackupInfoBar.Title = title;
        BackupInfoBar.Message = message;
        BackupInfoBar.IsOpen = true;
    }

    private async void SetAutostart(bool enabled)
    {
        if (_updating)
        {
            return;
        }
        AutostartSwitch.IsEnabled = false;
        AutostartRing.IsIndeterminate = true;
        AutostartRing.Visibility = System.Windows.Visibility.Visible;
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
            AutostartRing.IsIndeterminate = false;
            AutostartRing.Visibility = System.Windows.Visibility.Collapsed;
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
