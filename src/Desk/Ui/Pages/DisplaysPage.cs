using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Desk.Ui.Pages;

public sealed class DisplaysPage : Page
{
    private readonly IDisplaySettings _app;
    private readonly ComboBox _screens = new() { DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
    private readonly ToggleSwitch _enabled = new() { Content = "为这块屏幕使用独立设置", Margin = new Thickness(0, 0, 0, 16) };
    private readonly ComboBox _maximize = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly StackPanel _parameters = new();
    private readonly StackPanel _options = new();
    private readonly TextBlock _empty = new() { Text = "当前主题没有可调整的任务栏参数。", TextWrapping = TextWrapping.Wrap };
    private readonly Wpf.Ui.Controls.Button _reset = new() { Content = "清除本屏参数微调", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
    private readonly List<(CheckBox Box, string Key)> _checks = [];
    private ParameterSection? _section;
    private string? _device;
    private bool _updating;

    public DisplaysPage() : this(UiHost.App) { }

    internal DisplaysPage(IDisplaySettings app)
    {
        _app = app;
        Title = "显示器";
        var page = new StackPanel { Style = (Style)FindResource("Desk.Page") };
        Content = page;
        page.Children.Add(new TextBlock { Text = "每一块屏幕，都合适", Style = (Style)FindResource("Desk.PageTitle") });
        page.Children.Add(new TextBlock { Text = "分别调整图标、透明度和信息岛。关闭独立设置后跟随全局，已保存的微调仍会保留。", Style = (Style)FindResource("Desk.Lead") });
        page.Children.Add(_screens); page.Children.Add(_enabled); page.Children.Add(_options);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_screens, "Displays.Select");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_enabled, "Displays.Enabled");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_maximize, "Displays.Maximize");
        _options.Children.Add(new TextBlock { Text = "窗口最大化时", Style = (Style)FindResource("Desk.FirstSectionHeader") });
        _maximize.ItemsSource = new[] { "跟随全局行为", "只将本屏恢复为整条任务栏", "本屏始终保持浮岛" };
        _options.Children.Add(_maximize);
        _options.Children.Add(new TextBlock { Text = "信息岛 · 半选表示跟随全局", Style = (Style)FindResource("Desk.Caption"), Margin = new Thickness(0, 0, 0, 8) });
        var flags = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        foreach (var (title, key) in new[] { ("显示信息岛", "show"), ("网速", "network"), ("CPU", "cpu"), ("内存", "memory") })
        {
            var box = new CheckBox { Content = title, IsThreeState = true, Margin = new Thickness(0, 0, 16, 8) };
            void Changed(object sender, RoutedEventArgs e) => Change(preferences =>
            {
                switch (key) { case "show": preferences.ShowMonitor = box.IsChecked; break; case "network": preferences.MonitorNetwork = box.IsChecked; break;
                    case "cpu": preferences.MonitorCpu = box.IsChecked; break; case "memory": preferences.MonitorMemory = box.IsChecked; break; }
            });
            box.Checked += Changed; box.Unchecked += Changed; box.Indeterminate += Changed;
            _checks.Add((box, key)); flags.Children.Add(box);
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, "Displays." + key);
        }
        _options.Children.Add(flags); _options.Children.Add(_reset); _options.Children.Add(_empty); _options.Children.Add(_parameters);
        page.Children.Add(new TextBlock { Text = "开始菜单使用全局方案。Windows 的自动隐藏开关仍是全局行为；本屏可独立选择整条或浮岛。拔掉显示器不会删除它的配置。", Style = (Style)FindResource("Desk.Footnote") });
        _screens.SelectionChanged += (_, _) => BuildEditor();
        _reset.Click += (_, _) => { if (_reset.Tag is Wpf.Ui.Controls.Button proxy) proxy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); };
        _enabled.Checked += (_, _) => Change(p => p.Enabled = true);
        _enabled.Unchecked += (_, _) => Change(p => p.Enabled = false);
        _maximize.SelectionChanged += (_, _) => Change(p => p.MaximizeMode = _maximize.SelectedIndex switch { 1 => MaximizeMode.Merge, 2 => MaximizeMode.Keep, _ => null });
        Loaded += (_, _) => { _app.StateChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => _app.StateChanged -= Refresh;
    }

    private void Change(Action<DisplayPreferences> change)
    {
        if (_updating || _device == null) return;
        try { _app.UpdateDisplay(_device, change); }
        catch (Exception error) { UiHost.Toast("设置没有保存成功", error.Message, SymbolRegular.Warning24); Refresh(); }
    }

    private void BuildEditor()
    {
        if (_screens.SelectedItem is not DesktopDisplay display) return;
        if (_device == display.Id) return;
        _device = display.Id;
        // Each section owns its reset subscription, so use one permanent forwarder instead.
        var resetProxy = new Wpf.Ui.Controls.Button();
        _section = new ParameterSection(_app.GetDisplayEditor(_device), ParameterScope.Taskbar, Parameters.Taskbar, _parameters, _empty, resetProxy);
        _reset.Tag = resetProxy;
        RefreshValues();
    }

    private void Refresh()
    {
        var displays = _app.GetDisplays();
        var selected = _device;
        if (_screens.ItemsSource is not IEnumerable<DesktopDisplay> current || !current.SequenceEqual(displays))
        {
            _screens.ItemsSource = displays;
            _screens.SelectedItem = displays.FirstOrDefault(d => d.Id == selected) ?? displays.FirstOrDefault();
        }
        _screens.IsEnabled = displays.Count > 0;
        _enabled.IsEnabled = displays.Count > 0;
        if (displays.Count == 0) { _device = null; _options.IsEnabled = false; return; }
        BuildEditor(); RefreshValues();
    }

    private void RefreshValues()
    {
        if (_device == null) return;
        _updating = true;
        try
        {
            var p = _app.GetDisplayPreferences(_device);
            _enabled.IsChecked = p.Enabled; _options.IsEnabled = p.Enabled;
            _maximize.SelectedIndex = p.MaximizeMode switch { MaximizeMode.Merge => 1, MaximizeMode.Keep => 2, _ => 0 };
            foreach (var (box, key) in _checks) box.IsChecked = key switch { "show" => p.ShowMonitor, "network" => p.MonitorNetwork, "cpu" => p.MonitorCpu, _ => p.MonitorMemory };
            _section?.Refresh();
            if (_reset.Tag is Wpf.Ui.Controls.Button proxy) _reset.IsEnabled = proxy.IsEnabled;
        }
        finally { _updating = false; }
    }
}
