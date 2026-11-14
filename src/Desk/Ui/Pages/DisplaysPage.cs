using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Desk.Ui.Pages;

/// <summary>
/// Per-display settings, in the same cards as the other pages. Every choice
/// that can follow the global setting says so in words instead of a
/// half-checked box.
/// </summary>
public sealed class DisplaysPage : Page
{
    private readonly IDisplaySettings _app;
    private readonly ComboBox _screens = new() { DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly DisplayMap _map = new() { Margin = new Thickness(0, 0, 0, 12) };

    // With one screen there is nothing to choose: its name instead of a list that can't open.
    private readonly TextBlock _single = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _screensCaption = new() { Margin = new Thickness(2, 6, 0, 16) };
    private readonly ToggleSwitch _enabled = new();
    private readonly ComboBox _maximize = new() { MinWidth = 200 };
    private readonly StackPanel _parameters = new();
    private readonly StackPanel _options = new();
    private readonly InfoBar _empty = new()
    {
        Title = "当前主题没有可以调整的任务栏参数", Message = "内置主题都可以在这里按屏幕微调。",
        IsClosable = false, IsOpen = true, Severity = InfoBarSeverity.Informational, Margin = new Thickness(0, 0, 0, 4),
    };
    private readonly Wpf.Ui.Controls.Button _reset = new()
    {
        Content = "清除本屏微调", VerticalAlignment = VerticalAlignment.Center,
        Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowReset24 },
    };
    private readonly List<(ComboBox Box, string Key)> _monitor = [];
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
        page.Children.Add(new TextBlock { Text = "为某一块屏幕单独调整任务栏的尺寸、材质和信息岛。没有单独设置的屏幕使用全局外观。", Style = (Style)FindResource("Desk.Lead") });

        page.Children.Add(_map);
        page.Children.Add(_screens);
        page.Children.Add(_single);
        _screensCaption.SetResourceReference(StyleProperty, "Desk.Caption");
        page.Children.Add(_screensCaption);
        page.Children.Add(Card(SymbolRegular.Options24, "为这块屏幕使用独立设置",
            "关闭后这块屏幕跟随全局外观；已经保存的微调会保留，再次打开即可恢复。", _enabled));
        page.Children.Add(_options);

        _options.Children.Add(new TextBlock { Text = "行为", Style = (Style)FindResource("Desk.SectionHeader") });
        // "跟随全局" says what that is now: see RefreshValues.
        foreach (string choice in new[] { "跟随全局", "恢复成整条任务栏", "保持浮岛" }) _maximize.Items.Add(new ComboBoxItem { Content = choice });
        _options.Children.Add(Card(SymbolRegular.Maximize24, "窗口最大化时", "这块屏幕上有窗口铺满时，任务栏怎么显示。", _maximize));

        _options.Children.Add(new TextBlock { Text = "左侧信息岛", Style = (Style)FindResource("Desk.SectionHeader") });
        foreach (var (title, description, icon, key) in new[]
        {
            ("信息岛", "在这块屏幕上显示网速和资源占用。", SymbolRegular.DataUsage24, "show"),
            ("网速", "上传和下载速度。", SymbolRegular.ArrowSort24, "network"),
            ("CPU", "处理器占用。", SymbolRegular.DeveloperBoard24, "cpu"),
            ("内存", "内存占用。", SymbolRegular.Ram20, "memory"),
        })
        {
            var box = new ComboBox { MinWidth = 160 };
            foreach (string choice in new[] { "跟随全局", "显示", "隐藏" }) box.Items.Add(new ComboBoxItem { Content = choice });
            box.SelectionChanged += (_, _) =>
            {
                bool? value = box.SelectedIndex switch { 1 => true, 2 => false, _ => null };
                Change(preferences =>
                {
                    switch (key)
                    {
                        case "show": preferences.ShowMonitor = value; break;
                        case "network": preferences.MonitorNetwork = value; break;
                        case "cpu": preferences.MonitorCpu = value; break;
                        default: preferences.MonitorMemory = value; break;
                    }
                });
            };
            AutomationProperties.SetAutomationId(box, "Displays." + key);
            AutomationProperties.SetName(box, "本屏" + title);
            _monitor.Add((box, key));
            _options.Children.Add(Card(icon, title, description, box));
        }

        // The same bar as on the taskbar page: a header with its reset action on the right.
        var bar = new Grid { Style = (Style)FindResource("Desk.SectionBar") };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = "外观与尺寸", Style = (Style)FindResource("Desk.SectionHeader"), Margin = new Thickness(0) });
        heading.Children.Add(new TextBlock { Text = "只改动这块屏幕；没有改动的项目继续跟随全局。", Style = (Style)FindResource("Desk.Caption"), Margin = new Thickness(0, 2, 0, 0) });
        bar.Children.Add(heading);
        Grid.SetColumn(_reset, 1);
        bar.Children.Add(_reset);
        _options.Children.Add(bar);
        _options.Children.Add(_empty);
        _options.Children.Add(_parameters);

        page.Children.Add(new TextBlock { Text = "开始菜单使用全局方案，Windows 的「自动隐藏」也是全局开关。拔掉显示器不会删除它的配置，再次接上时自动恢复。", Style = (Style)FindResource("Desk.Footnote") });

        AutomationProperties.SetAutomationId(_screens, "Displays.Select");
        AutomationProperties.SetName(_screens, "选择屏幕");
        AutomationProperties.SetAutomationId(_map, "Displays.Map");
        AutomationProperties.SetAutomationId(_single, "Displays.Single");
        AutomationProperties.SetAutomationId(_enabled, "Displays.Enabled");
        AutomationProperties.SetName(_enabled, "为这块屏幕使用独立设置");
        AutomationProperties.SetAutomationId(_maximize, "Displays.Maximize");
        AutomationProperties.SetName(_maximize, "本屏窗口最大化时");
        AutomationProperties.SetAutomationId(_reset, "Displays.Reset");

        _screens.SelectionChanged += (_, _) => BuildEditor();
        _map.Chosen += display => _screens.SelectedItem = display;
        _reset.Click += (_, _) => { if (_reset.Tag is Wpf.Ui.Controls.Button proxy) proxy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); };
        _enabled.Checked += (_, _) => Change(p => p.Enabled = true);
        _enabled.Unchecked += (_, _) => Change(p => p.Enabled = false);
        _maximize.SelectionChanged += (_, _) => Change(p => p.MaximizeMode = _maximize.SelectedIndex switch { 1 => MaximizeMode.Merge, 2 => MaximizeMode.Keep, _ => null });
        Loaded += (_, _) => { _app.StateChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => _app.StateChanged -= Refresh;
    }

    private static CardControl Card(SymbolRegular icon, string title, string description, object content) => new()
    {
        Icon = new SymbolIcon { Symbol = icon },
        Header = new CardHeader { Title = title, Description = description },
        Content = content,
    };

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
        string device = _device;
        _section = new ParameterSection(_app.GetDisplayEditor(device), ParameterScope.Taskbar, Parameters.Taskbar, _parameters, _empty, resetProxy, "displays")
        {
            // The preview shows this screen's info island.
            MonitorItems = () => _app.GetDisplayPreferences(device).MonitorItems(_app.DisplayDefaults),
        };
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
        // Several screens: the picture to click and the list for the keyboard; one: just its name.
        bool several = displays.Count > 1;
        _screens.Visibility = _map.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        _single.Visibility = displays.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        _single.Text = displays.Count == 1 ? displays[0].Label : "";
        _enabled.IsEnabled = displays.Count > 0;
        int custom = displays.Count(d => _app.GetDisplayPreferences(d.Id).Enabled);
        _screensCaption.Text = displays.Count switch
        {
            0 => "没有找到屏幕。",
            1 => custom == 1 ? "已连接 1 块屏幕 · 正在使用独立设置" : "已连接 1 块屏幕 · 跟随全局外观",
            _ => $"已连接 {displays.Count} 块屏幕 · {(custom == 0 ? "都跟随全局外观" : custom + " 块使用独立设置")}",
        };
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
            var global = _app.DisplayDefaults;
            _enabled.IsChecked = p.Enabled; _options.IsEnabled = p.Enabled;
            _maximize.SelectedIndex = p.MaximizeMode switch { MaximizeMode.Merge => 1, MaximizeMode.Keep => 2, _ => 0 };
            ((ComboBoxItem)_maximize.Items[0]).Content = "跟随全局（" + global.MaximizeMode switch
            {
                MaximizeMode.Merge => "恢复成整条任务栏",
                MaximizeMode.AutoHide => "自动隐藏任务栏",
                _ => "保持浮岛",
            } + "）";
            foreach (var (box, key) in _monitor)
            {
                bool? value = key switch { "show" => p.ShowMonitor, "network" => p.MonitorNetwork, "cpu" => p.MonitorCpu, _ => p.MonitorMemory };
                bool globalValue = key switch { "show" => global.ShowMonitor, "network" => global.MonitorNetwork, "cpu" => global.MonitorCpu, _ => global.MonitorMemory };
                ((ComboBoxItem)box.Items[0]).Content = globalValue ? "跟随全局（显示）" : "跟随全局（隐藏）";
                box.SelectedIndex = value switch { true => 1, false => 2, _ => 0 };
            }
            _map.Show(_app.GetDisplays(), _device, id => _app.GetDisplayPreferences(id).Enabled);
            _section?.Refresh();
            if (_reset.Tag is Wpf.Ui.Controls.Button proxy) _reset.IsEnabled = proxy.IsEnabled;
        }
        finally { _updating = false; }
    }
}
