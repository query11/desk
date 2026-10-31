using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Desk.Ui.Pages;

public partial class HomePage : Page
{
    private readonly DeskApp _app = UiHost.App;
    private bool _updating;

    public HomePage()
    {
        InitializeComponent();

        EnabledSwitch.Checked += (_, _) => SetEnabled(true);
        EnabledSwitch.Unchecked += (_, _) => SetEnabled(false);
        ThemeBox.SelectionChanged += (_, _) =>
        {
            if (!_updating && ThemeBox.SelectedItem is ThemeInfo theme)
            {
                _app.ThemeId = theme.Id;
            }
        };
        ReapplyButton.Click += (_, _) => _app.Reapply();
        RestartExplorerButton.Click += (_, _) => _app.RestartExplorer();

        Loaded += (_, _) =>
        {
            _app.StateChanged += Refresh;
            _app.StatusChanged += RefreshStatus;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _app.StateChanged -= Refresh;
            _app.StatusChanged -= RefreshStatus;
        };
    }

    private void SetEnabled(bool enabled)
    {
        if (!_updating)
        {
            _app.Enabled = enabled;
        }
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            EnabledSwitch.IsChecked = _app.Enabled;

            var themes = _app.GetThemes();
            ThemeBox.ItemsSource = themes;
            ThemeBox.SelectedItem = themes.FirstOrDefault(t =>
                string.Equals(t.Id, _app.ThemeId, StringComparison.OrdinalIgnoreCase));
            ThemeDescriptionText.Text = _app.ThemeDescription;

            EngineErrorInfo.Message = _app.EngineError ?? "";
            EngineErrorInfo.IsOpen = _app.EngineError != null;

            ConflictInfo.Message = _app.Conflicts.Count == 0
                ? ""
                : $"检测到 {string.Join("、", _app.Conflicts)} 正在运行。它们也会修改任务栏，可能和 Desk 冲突，建议先关闭它们的任务栏相关功能。";
            ConflictInfo.IsOpen = _app.Conflicts.Count > 0;
        }
        finally
        {
            _updating = false;
        }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        SummaryText.Text = _app.Enabled
            ? "任务栏和开始菜单正在使用 Desk 的样式。关闭开关会立即恢复系统原样。"
            : "美化已停用，任务栏和开始菜单是系统原样。";

        ShowStatus(TaskbarStatusText, TaskbarStatusIcon, _app.TaskbarStatus, "已生效",
            "正在连接任务栏…");
        ShowStatus(StartMenuStatusText, StartMenuStatusIcon, _app.StartMenuStatus, "已生效",
            "正在连接开始菜单…");

        OutdatedInfo.IsOpen = _app.ExplorerOutdated;
    }

    private static void ShowStatus(System.Windows.Controls.TextBlock text, SymbolIcon icon, EngineStatus status,
        string active, string connecting)
    {
        (text.Text, icon.Symbol, string brush) = status switch
        {
            EngineStatus.Active => (active, SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccessBrush"),
            EngineStatus.Connecting => (connecting, SymbolRegular.ArrowSync24, "TextFillColorSecondaryBrush"),
            EngineStatus.Waiting => ("下次打开开始菜单时生效", SymbolRegular.Clock24, "TextFillColorSecondaryBrush"),
            EngineStatus.Failed => ("连接失败，稍后自动重试", SymbolRegular.Warning24, "SystemFillColorCautionBrush"),
            EngineStatus.Off => ("已关闭", SymbolRegular.Circle24, "TextFillColorTertiaryBrush"),
            _ => ("无法使用", SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush"),
        };
        icon.SetResourceReference(IconElement.ForegroundProperty, brush);
    }
}
