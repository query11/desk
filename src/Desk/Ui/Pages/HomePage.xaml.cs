using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
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
        TaskbarTile.Click += (_, _) => UiHost.Navigate(typeof(TaskbarPage));
        StartMenuTile.Click += (_, _) => UiHost.Navigate(typeof(StartMenuPage));
        ReapplyButton.Click += (_, _) =>
        {
            _app.Reapply();
            UiHost.Toast("已重新应用", "正在重新连接任务栏和开始菜单，几秒内生效。");
        };
        RestartExplorerButton.Click += async (_, _) =>
        {
            if (await UiHost.ConfirmAsync("重启资源管理器？",
                    "任务栏会消失几秒再出现，打开的文件夹窗口会被关闭。", "重启"))
            {
                _app.RestartExplorer();
            }
        };

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
            ThemeBox.IsEnabled = themes.Count > 0;
            ThemeBox.SelectedItem = themes.FirstOrDefault(t =>
                string.Equals(t.Id, _app.ThemeId, StringComparison.OrdinalIgnoreCase));
            ThemeHeader.Description = themes.Count == 0
                ? "没有找到主题文件。请确认 Desk.exe 旁边有 themes 文件夹，或者在「设置 → 主题文件夹」里放一个主题。"
                : _app.ThemeDescription;

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

        var taskbar = ShowStatus(TaskbarStatusText, TaskbarStatusIcon, TaskbarStatusRing, _app.Taskbar,
            "任务栏", "资源管理器");
        var startMenu = ShowStatus(StartMenuStatusText, StartMenuStatusIcon, StartMenuStatusRing, _app.StartMenu,
            "开始菜单", "开始菜单");
        ShowState(taskbar, startMenu);

        OutdatedInfo.IsOpen = _app.ExplorerOutdated;
    }

    /// <summary>The label next to the title: the worst of the two parts decides.</summary>
    private void ShowState(Health taskbar, Health startMenu)
    {
        (string text, string foreground, string background) = (_app.EngineError, _app.Enabled) switch
        {
            ({ }, _) => ("无法运行", "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush"),
            (_, false) => ("已停用", "TextFillColorSecondaryBrush", "SubtleFillColorSecondaryBrush"),
            _ => (Health)Math.Max((int)taskbar, (int)startMenu) switch
            {
                Health.Problem => ("需要注意", "SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush"),
                Health.Busy => ("正在连接", "TextFillColorSecondaryBrush", "SubtleFillColorSecondaryBrush"),
                _ => ("运行中", "SystemFillColorSuccessBrush", "SystemFillColorSuccessBackgroundBrush"),
            },
        };
        StateText.Text = text;
        StateText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, foreground);
        StateDot.SetResourceReference(Shape.FillProperty, foreground);
        StatePill.SetResourceReference(Border.BackgroundProperty, background);
    }

    /// <summary>How a part is doing, from good to bad; the order is used to pick the worst.</summary>
    private enum Health
    {
        Fine,
        Busy,
        Problem,
    }

    /// <param name="what">The part being styled, for the texts.</param>
    /// <param name="process">The process that has to restart to replace an old engine.</param>
    private static Health ShowStatus(System.Windows.Controls.TextBlock text, SymbolIcon icon, ProgressRing ring,
        TargetStatus status, string what, string process)
    {
        const string secondary = "TextFillColorSecondaryBrush";
        const string caution = "SystemFillColorCautionBrush";
        (string message, icon.Symbol, string brush, Health health) = status.Status switch
        {
            EngineStatus.Active => ("已生效", SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccessBrush", Health.Fine),
            EngineStatus.Connecting => ($"正在连接{what}…", SymbolRegular.ArrowSync24, secondary, Health.Busy),
            EngineStatus.Initializing => ("引擎已加载，正在初始化…", SymbolRegular.ArrowSync24, secondary, Health.Busy),
            EngineStatus.Applying => ("正在应用当前设置…", SymbolRegular.ArrowSync24, secondary, Health.Busy),
            EngineStatus.Waiting => ("下次打开开始菜单时生效", SymbolRegular.Clock24, secondary, Health.Fine),
            EngineStatus.WaitingForUi => ($"引擎已就绪，{what}出现后生效", SymbolRegular.Clock24, secondary, Health.Fine),
            EngineStatus.NoTargets => ($"引擎已就绪，但主题规则没有匹配到{what}的界面元素", SymbolRegular.Warning24, caution,
                Health.Problem),
            EngineStatus.Failed => ("连接失败", SymbolRegular.Warning24, caution, Health.Problem),
            EngineStatus.EngineFailed => ("引擎初始化失败", SymbolRegular.Warning24, caution, Health.Problem),
            EngineStatus.NotResponding => ("引擎没有回应。可以点「重新应用」，仍不行请重启" + process,
                SymbolRegular.Warning24, caution, Health.Problem),
            EngineStatus.Outdated => ($"正在运行旧版引擎，重启{process}后更新", SymbolRegular.Info24, secondary, Health.Fine),
            EngineStatus.Off => ("已关闭", SymbolRegular.Circle24, "TextFillColorTertiaryBrush", Health.Fine),
            _ => ("无法使用", SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush", Health.Problem),
        };
        text.Text = status.Detail is { } detail ? $"{message}：{detail}" : message;
        icon.SetResourceReference(IconElement.ForegroundProperty, brush);

        // Work in progress spins instead of showing a still icon.
        bool busy = health == Health.Busy;
        ring.IsIndeterminate = busy;
        ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        icon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        return health;
    }
}
