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

        ShowStatus(TaskbarStatusText, TaskbarStatusIcon, _app.Taskbar, "任务栏", "资源管理器");
        ShowStatus(StartMenuStatusText, StartMenuStatusIcon, _app.StartMenu, "开始菜单", "开始菜单");

        OutdatedInfo.IsOpen = _app.ExplorerOutdated;
    }

    /// <param name="what">The part being styled, for the texts.</param>
    /// <param name="process">The process that has to restart to replace an old engine.</param>
    private static void ShowStatus(System.Windows.Controls.TextBlock text, SymbolIcon icon, TargetStatus status,
        string what, string process)
    {
        const string secondary = "TextFillColorSecondaryBrush";
        const string caution = "SystemFillColorCautionBrush";
        (string message, icon.Symbol, string brush) = status.Status switch
        {
            EngineStatus.Active => ("已生效", SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccessBrush"),
            EngineStatus.Connecting => ($"正在连接{what}…", SymbolRegular.ArrowSync24, secondary),
            EngineStatus.Initializing => ("引擎已加载，正在初始化…", SymbolRegular.ArrowSync24, secondary),
            EngineStatus.Applying => ("正在应用当前设置…", SymbolRegular.ArrowSync24, secondary),
            EngineStatus.Waiting => ("下次打开开始菜单时生效", SymbolRegular.Clock24, secondary),
            EngineStatus.WaitingForUi => ($"引擎已就绪，{what}出现后生效", SymbolRegular.Clock24, secondary),
            EngineStatus.NoTargets => ($"引擎已就绪，但主题规则没有匹配到{what}的界面元素", SymbolRegular.Warning24, caution),
            EngineStatus.Failed => ("连接失败", SymbolRegular.Warning24, caution),
            EngineStatus.EngineFailed => ("引擎初始化失败", SymbolRegular.Warning24, caution),
            EngineStatus.NotResponding => ("引擎没有回应。可以点「重新应用」，仍不行请重启" + process,
                SymbolRegular.Warning24, caution),
            EngineStatus.Outdated => ($"正在运行旧版引擎，重启{process}后更新", SymbolRegular.Info24, secondary),
            EngineStatus.Off => ("已关闭", SymbolRegular.Circle24, "TextFillColorTertiaryBrush"),
            _ => ("无法使用", SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush"),
        };
        text.Text = status.Detail is { } detail ? $"{message}：{detail}" : message;
        icon.SetResourceReference(IconElement.ForegroundProperty, brush);
    }
}
