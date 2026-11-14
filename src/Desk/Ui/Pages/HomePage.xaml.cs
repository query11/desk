using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Wpf.Ui.Controls;

namespace Desk.Ui.Pages;

public partial class HomePage : Page
{
    private readonly DeskApp _app = UiHost.App;
    private bool _updating;
    private bool _hadProblem;
    private readonly DesktopPreview _preview = new() { Height = 235 };

    public HomePage()
    {
        InitializeComponent();
        DesktopPreviewHost.Content = _preview;

        EnabledSwitch.Checked += (_, _) => SetEnabled(true);
        EnabledSwitch.Unchecked += (_, _) => SetEnabled(false);
        BrowseThemesButton.Click += (_, _) => UiHost.Navigate(typeof(AppearancePage));
        TaskbarTile.Click += (_, _) => UiHost.Navigate(typeof(TaskbarPage));
        StartMenuTile.Click += (_, _) => UiHost.Navigate(typeof(StartMenuPage));
        ReapplyButton.Click += (_, _) =>
        {
            _app.Reapply();
            UiHost.Toast("已重新应用", "正在重新连接任务栏和开始菜单，几秒内生效。");
        };
        RestartExplorerButton.Click += (_, _) => RestartExplorer();
        OutdatedRestartButton.Click += (_, _) => RestartExplorer();

        Loaded += async (_, _) =>
        {
            _app.StateChanged += Refresh;
            _app.StatusChanged += RefreshStatus;
            ConflictOpen.AddValueChanged(ConflictInfo, OnConflictToggled);
            Refresh();
            var wallpaper = await WallpaperPreview.LoadAsync();
            await Dispatcher.InvokeAsync(() => { _preview.Wallpaper = wallpaper.Image; _preview.InvalidateVisual(); });
        };
        Unloaded += (_, _) =>
        {
            _app.StateChanged -= Refresh;
            _app.StatusChanged -= RefreshStatus;
            ConflictOpen.RemoveValueChanged(ConflictInfo, OnConflictToggled);
        };
    }

    private static readonly System.ComponentModel.DependencyPropertyDescriptor ConflictOpen =
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar));

    /// <summary>The programs the conflict notice is about, in a fixed order.</summary>
    private string ConflictKey => string.Join("、", _app.Conflicts.Order(StringComparer.Ordinal));

    /// <summary>
    /// Closed by the user: it stays closed for the same programs while the
    /// window is open, also when the page is built again.
    /// </summary>
    private void OnConflictToggled(object? sender, EventArgs e)
    {
        if (!_updating && !ConflictInfo.IsOpen && _app.Conflicts.Count > 0)
        {
            UiHost.DismissedConflicts = ConflictKey;
        }
    }

    private async void RestartExplorer()
    {
        if (await UiHost.ConfirmAsync("重启资源管理器？",
                "任务栏会消失几秒再出现，打开的文件夹窗口会被关闭。", "重启"))
        {
            _app.RestartExplorer();
        }
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

            CurrentThemeName.Text = string.IsNullOrWhiteSpace(_app.ThemeName) ? "尚未选择方案" : _app.ThemeName;
            CurrentThemeCaption.Text = _app.PendingTrial != null
                ? "试用中，到时自动换回 · 效果示意，以桌面实际显示为准"
                : "当前方案 · 效果示意，以桌面实际显示为准";
            // The picture shows what the switch above turns on: dimmed while it is off.
            _preview.Opacity = _app.Enabled ? 1 : .5;
            _preview.MonitorItems = _app.MonitorItems;
            _preview.Update(_app.CurrentAppearance, _app.SystemLightMode, _app.StartMenuLightMode);

            EngineErrorInfo.Message = _app.EngineError ?? "";
            EngineErrorInfo.IsOpen = _app.EngineError != null;

            ConflictInfo.Message = _app.Conflicts.Count == 0
                ? ""
                : $"检测到 {string.Join("、", _app.Conflicts)} 正在运行。它们也会修改任务栏，可能和 Desk 冲突，建议先关闭它们的任务栏相关功能。";
            // Once the programs are gone, the notice is new again if they return.
            if (_app.Conflicts.Count == 0) UiHost.DismissedConflicts = null;
            ConflictInfo.IsOpen = _app.Conflicts.Count > 0 && ConflictKey != UiHost.DismissedConflicts;
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
            ? "已启用 · 各部分的实际生效情况见下方状态。"
            : "已停用 · 打开开关即可恢复这套外观。";

        var taskbar = ShowStatus(TaskbarStatusText, TaskbarStatusIcon, TaskbarStatusRing, _app.Taskbar,
            "任务栏", "资源管理器");
        var startMenu = ShowStatus(StartMenuStatusText, StartMenuStatusIcon, StartMenuStatusRing, _app.StartMenu,
            "开始菜单", "开始菜单");
        ShowState(taskbar, startMenu);

        OutdatedBanner.Visibility = _app.ExplorerOutdated ? Visibility.Visible : Visibility.Collapsed;

        // The way out of a problem is in the maintenance section: open it
        // when one appears, once, and leave it to the user after that.
        bool problem = _app.Enabled && (taskbar == Health.Problem || startMenu == Health.Problem);
        if (problem && !_hadProblem)
        {
            MaintenanceExpander.IsExpanded = true;
        }
        _hadProblem = problem;
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
            EngineStatus.Degraded => ("部分生效", SymbolRegular.Warning24, caution, Health.Problem),
            EngineStatus.EffectsReduced => ("已生效 · 纯色背景", SymbolRegular.Info24, secondary, Health.Fine),
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
