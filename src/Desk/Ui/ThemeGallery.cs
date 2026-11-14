using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Desk.Ui;

/// <summary>
/// What the gallery remembers while the settings window is used: the page is
/// built again every time it is opened, and a look at the overview must not
/// lose the filter or the way back to the previous theme.
/// </summary>
internal sealed class GalleryState
{
    public string Filter { get; set; } = "推荐";
    public string Search { get; set; } = "";

    /// <summary>The preview's colors; null follows Windows.</summary>
    public bool? Light { get; set; }

    public bool UseWallpaper { get; set; }

    /// <summary>The theme "撤销切换" goes back to.</summary>
    public string? Previous { get; set; }
}

/// <summary>Local-only, keyboard accessible theme gallery. No shell access in the view.</summary>
internal sealed class ThemeGallery : StackPanel
{
    private static readonly string[] Filters = ["推荐", "全部", "收藏", "我的方案"];

    private readonly IThemeLibrary _library;
    private readonly GalleryState _state;
    private readonly UniformGrid _cards = new() { Columns = 2, Margin = new Thickness(-6, 0, -6, 0) };
    private readonly Wpf.Ui.Controls.TextBox _search = new()
    {
        PlaceholderText = "搜索方案名称或风格…", Margin = new Thickness(0, 0, 0, 12),
        Icon = new SymbolIcon { Symbol = SymbolRegular.Search24 },
    };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(20, 40, 20, 40), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _count = new() { Margin = new Thickness(2, 4, 0, 12) };
    private readonly InfoBar _feedback = new() { IsClosable = true, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Grid _bar = new();
    private readonly WrapPanel _tools = new();
    private readonly Button _undo = new() { Content = "撤销切换", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 8), Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowUndo24 } };
    private readonly Button _mode = new() { Margin = new Thickness(0, 0, 8, 8), ToolTip = "切换预览的配色。只影响这个页面，不会更改 Windows 的模式。" };
    private readonly ToggleSwitch _wallpaperSwitch = new()
    {
        Content = "我的壁纸", Margin = new Thickness(4, 0, 12, 8), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "用当前的桌面壁纸预览；关闭后使用示例壁纸。",
    };
    private readonly List<Button> _filters = [];
    private IReadOnlyList<ThemeOption> _options = [];
    private bool _systemLight;
    private bool _busy;
    private bool _loadingWallpaper;
    private bool _updating;
    private System.Windows.Media.ImageSource? _wallpaper;

    // The cards of the last build by theme, with what each showed. A card
    // whose theme and state didn't change is kept as it is (with the focus
    // on its buttons); a settings change that doesn't concern the gallery
    // changes nothing. Previews of the same wallpaper and colors only.
    private readonly Dictionary<string, (string Key, Border Card)> _built = new(StringComparer.OrdinalIgnoreCase);
    private string? _builtFor;
    private System.Windows.Media.ImageSource? _builtWallpaper;

    /// <summary>Focus to put back once applying is done (its button was disabled meanwhile).</summary>
    private (string Id, int Card)? _pendingFocus;

    public ThemeGallery(IThemeLibrary library, bool light, GalleryState? state = null)
    {
        _library = library;
        _systemLight = light;
        _state = state ?? new GalleryState();
        Children.Add(new TextBlock { Text = "给桌面换一种气质", Style = (Style)FindResource("Desk.PageTitle") });
        Children.Add(new TextBlock
        {
            Text = "一套方案，同时搭配任务栏与开始菜单。选择之后，还可以单独微调。",
            Style = (Style)FindResource("Desk.Lead"),
        });

        _search.Text = _state.Search;
        Children.Add(_search);

        // Filters on the left, view and library actions on the right; stacked when narrow.
        _bar.ColumnDefinitions.Add(new ColumnDefinition());
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var filters = new WrapPanel();
        foreach (string filter in Filters)
        {
            var button = new Button { Tag = filter, Margin = new Thickness(0, 0, 8, 8), MinWidth = 72 };
            button.Click += (_, _) => { _state.Filter = filter; BuildCards(); };
            AutomationProperties.SetAutomationId(button, "Gallery.Filter." + filter);
            _filters.Add(button);
            filters.Children.Add(button);
        }
        _bar.Children.Add(filters);
        _tools.Children.Add(_undo);
        _tools.Children.Add(_mode);
        _tools.Children.Add(_wallpaperSwitch);
        var import = new Button { Content = "导入", ToolTip = "导入 Desk 主题文件（JSON）", Margin = new Thickness(0, 0, 8, 8), Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowDownload24 } };
        import.Click += (_, _) => RunAction(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入 Desk 主题", Filter = "Desk 主题 (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            _library.ImportTheme(dialog.FileName); _state.Filter = "我的方案"; _search.Text = "";
            Done("已导入", "导入的方案在「我的方案」中，可以先试用再应用。");
        });
        AutomationProperties.SetAutomationId(import, "Gallery.Import");
        _tools.Children.Add(import);
        var refresh = new Button { ToolTip = "重新读取主题文件夹", Width = 36, Height = 32, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 8), Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowSync24 } };
        refresh.Click += (_, _) => Refresh();
        AutomationProperties.SetName(refresh, "刷新列表");
        AutomationProperties.SetAutomationId(refresh, "Gallery.Refresh");
        _tools.Children.Add(refresh);
        _bar.Children.Add(_tools);
        Children.Add(_bar);

        _count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _count.FontSize = 12;
        _count.TextWrapping = TextWrapping.Wrap;
        Children.Add(_count);
        Children.Add(_feedback);
        Children.Add(_cards);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Children.Add(_empty);
        AutomationProperties.SetAutomationId(_search, "Gallery.Search");
        AutomationProperties.SetName(_search, "搜索外观方案");
        AutomationProperties.SetAutomationId(_undo, "Gallery.Undo");
        AutomationProperties.SetAutomationId(_mode, "Gallery.Mode");
        AutomationProperties.SetAutomationId(_wallpaperSwitch, "Gallery.Wallpaper");
        AutomationProperties.SetName(_wallpaperSwitch, "用我的壁纸预览");
        _search.TextChanged += (_, _) => { _state.Search = _search.Text; BuildCards(); };
        _mode.Click += (_, _) =>
        {
            bool light = !Light;
            // Back on the Windows mode: follow it again when it changes.
            _state.Light = light == _systemLight ? null : light;
            BuildCards();
        };
        _wallpaperSwitch.Checked += (_, _) => { if (!_updating) LoadWallpaper(announce: true); };
        _wallpaperSwitch.Unchecked += (_, _) =>
        {
            if (_updating) return;
            _state.UseWallpaper = false; _wallpaper = null; BuildCards();
        };
        _undo.Click += async (_, _) => { if (_state.Previous is { } id) await ApplyAsync(id); };
        SizeChanged += (_, _) => Reflow(ActualWidth);
        Reflow(double.NaN);
        if (_state.UseWallpaper) LoadWallpaper(announce: false);
        Refresh();
    }

    /// <summary>Reads what the info island shows, so the previews match the desktop.</summary>
    public Func<IReadOnlyList<string>>? MonitorItems { get; set; }

    /// <summary>The Windows mode; the previews follow it until the user picks one.</summary>
    public bool SystemLight
    {
        get => _systemLight;
        set => _systemLight = value;
    }

    private bool Light => _state.Light ?? _systemLight;

    public void Refresh()
    {
        try { _options = _library.GetAppearanceOptions(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _options = [];
            Feedback("无法读取方案列表", e.Message, InfoBarSeverity.Error);
        }
        BuildCards();
    }

    private void Reflow(double width)
    {
        _cards.Columns = width < 620 ? 1 : 2;
        bool wide = width >= 840;
        Grid.SetRow(_tools, wide ? 0 : 1);
        Grid.SetColumn(_tools, wide ? 1 : 0);
        Grid.SetColumnSpan(_tools, wide ? 1 : 2);
        _tools.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    private async void LoadWallpaper(bool announce)
    {
        if (_loadingWallpaper) return;
        _loadingWallpaper = true;
        _state.UseWallpaper = true;
        BuildCards();
        var result = await WallpaperPreview.LoadAsync();
        // The read finishes on another thread when no WPF context was current.
        await Dispatcher.InvokeAsync(() =>
        {
            _loadingWallpaper = false;
            _wallpaper = result.Image;
            _state.UseWallpaper = _wallpaper != null;
            if (result.Error != null && announce) Feedback("壁纸预览", result.Error, InfoBarSeverity.Informational);
            BuildCards();
        });
    }

    private bool Matches(ThemeOption option, string filter) => filter switch
    {
        "推荐" => option.Featured,
        "收藏" => option.Favorite,
        "我的方案" => option.IsUserTheme,
        _ => true,
    };

    private void BuildCards()
    {
        // While a theme is applied the cards stay as they are, greyed out;
        // ApplyAsync builds them once it's done.
        if (_busy) return;
        var focus = FocusedCardControl() ?? _pendingFocus;
        _pendingFocus = null;
        bool light = Light;
        _mode.Content = light ? "浅色预览" : "深色预览";
        _mode.Icon = new SymbolIcon { Symbol = light ? SymbolRegular.WeatherSunny24 : SymbolRegular.WeatherMoon24 };
        _updating = true;
        _wallpaperSwitch.IsEnabled = !_loadingWallpaper;
        _wallpaperSwitch.IsChecked = _state.UseWallpaper;
        _updating = false;
        bool canUndo = _state.Previous != null && !Same(_state.Previous, _library.ThemeId) && _library.PendingTrial == null;
        _undo.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        _undo.IsEnabled = !_busy && canUndo;
        if (canUndo && _options.FirstOrDefault(t => Same(t.Id, _state.Previous)) is { } previous)
            _undo.ToolTip = "换回「" + previous.Name + "」";
        foreach (var filter in _filters)
        {
            string name = (string)filter.Tag;
            int count = _options.Count(t => Matches(t, name));
            filter.Content = count > 0 ? name + "  " + count : name;
            filter.Appearance = name == _state.Filter ? ControlAppearance.Primary : ControlAppearance.Secondary;
            AutomationProperties.SetName(filter, name + "，" + count + " 个方案");
        }
        string query = _search.Text.Trim();
        var visible = _options.Where(t => Matches(t, _state.Filter)
            && (t.Name + " " + t.Description + " " + t.Tag).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _count.Text = $"{visible.Count} 个方案 · 预览是效果示意，实际外观还取决于壁纸、Windows 配色和已保存的微调。";
        _count.Visibility = visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = query.Length > 0 ? "没有匹配「" + query + "」的方案。\n试试「通透」或「横栏」，或者换一个分类。"
            : _state.Filter == "我的方案" ? "还没有个人方案。\n在任务栏或开始菜单页调整后点「保存为我的方案」，也可以从上方导入。"
            : _state.Filter == "收藏" ? "还没有收藏。\n点方案名称旁边的星标，把常用的方案放在这里。"
            : "这里还没有方案。可以切换到「全部」，或在设置中打开主题文件夹。";
        var trial = _library.PendingTrial;
        var monitor = MonitorItems?.Invoke();

        // What every card shows the same way: when it changes, all are built again.
        string common = light + "|" + string.Join(",", monitor ?? []);
        if (common != _builtFor || !ReferenceEquals(_wallpaper, _builtWallpaper))
        {
            _built.Clear();
            _builtFor = common;
            _builtWallpaper = _wallpaper;
        }
        var cards = new List<UIElement>(visible.Count);
        foreach (var option in visible)
        {
            string key = CardKey(option, trial, light);
            if (!_built.TryGetValue(option.Id, out var built) || built.Key != key)
            {
                _built[option.Id] = built = (key, BuildCard(option, trial, light, monitor));
            }
            cards.Add(built.Card);
        }
        foreach (string gone in _built.Keys.Where(id => !_options.Any(o => Same(o.Id, id))).ToList())
        {
            _built.Remove(gone);
        }
        // Only when the list changed: a card that stays keeps its focus.
        if (!_cards.Children.Cast<UIElement>().SequenceEqual(cards))
        {
            _cards.Children.Clear();
            foreach (var card in cards) _cards.Children.Add(card);
        }
        RestoreFocus(focus);
    }

    /// <summary>Everything a card shows and does; equal keys build equal cards.</summary>
    private string CardKey(ThemeOption option, ThemeTrial? trial, bool light)
    {
        var (selected, trying, original) = CardState(option, trial);
        return string.Join("\u001F", option.Id, option.Name, option.Description, option.Tag, option.Error,
            option.IsUserTheme, option.Favorite, option.Overrides.Count > 0, selected, trying, original,
            DesktopPreview.Key(option, light));
    }

    private (bool Selected, bool Trying, bool Original) CardState(ThemeOption option, ThemeTrial? trial)
    {
        bool selected = Same(option.Id, _library.ThemeId);
        bool trying = trial != null && Same(trial.TrialTheme, option.Id);
        bool original = trial != null && Same(trial.OriginalTheme, option.Id) && !trying;
        return (selected, trying, original);
    }

    /// <summary>The card control that has keyboard focus, and the place of its card.</summary>
    private (string Id, int Card)? FocusedCardControl()
    {
        for (int i = 0; i < _cards.Children.Count; i++)
        {
            var card = _cards.Children[i];
            if (!card.IsKeyboardFocusWithin) continue;
            for (var element = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
                 element != null && !ReferenceEquals(element, card); element = ParentOf(element))
            {
                if (AutomationProperties.GetAutomationId(element) is { Length: > 0 } id) return (id, i);
            }
            return null;
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    /// <summary>
    /// Puts the focus back where it was when its control was replaced: the
    /// same button of the same theme's card, or the next one of that card that
    /// can take it ("当前方案" can't). When the card is gone (no longer a
    /// favorite, say), the card that took its place.
    /// </summary>
    private void RestoreFocus((string Id, int Card)? focus)
    {
        if (focus is not { } was || _cards.Children.Count == 0) return;
        if (FocusedCardControl() is { } now && now.Id == was.Id) return;
        string[] parts = was.Id.Split('.', 3);
        if (parts.Length < 3 || parts[0] != "Gallery") return;
        var card = _built.TryGetValue(parts[2], out var built) && _cards.Children.Contains(built.Card)
            ? built.Card
            : (Border)_cards.Children[Math.Min(was.Card, _cards.Children.Count - 1)];
        string theme = (string)card.Tag;
        foreach (string kind in new[] { parts[1], "More", "Favorite" })
        {
            if (Find(card, "Gallery." + kind + "." + theme) is not { IsEnabled: true } target) continue;
            // A new card isn't visible to the keyboard before its first layout.
            if (!target.Focus())
                Dispatcher.BeginInvoke(() => target.Focus(), DispatcherPriority.Loaded);
            return;
        }
    }

    private static Button? Find(DependencyObject parent, string id)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && AutomationProperties.GetAutomationId(button) == id) return button;
            if (Find(child, id) is { } found) return found;
        }
        return null;
    }

    private Border BuildCard(ThemeOption option, ThemeTrial? trial, bool light, IReadOnlyList<string>? monitor)
    {
        var (selected, trying, original) = CardState(option, trial);
        bool usable = option.Error == null;

        var content = new StackPanel();
        // Inset: the picture's corners follow the card's instead of poking through them.
        var preview = new DesktopPreview { Wallpaper = _wallpaper, MonitorItems = monitor, Margin = new Thickness(8, 8, 8, 0) };
        preview.Update(option, light);
        content.Children.Add(preview);

        var body = new StackPanel { Margin = new Thickness(18, 12, 18, 16) };
        string tagText = option.Tag + (trying ? " · 试用中" : usable && option.Overrides.Count > 0 ? " · 已微调" : "");
        var tags = new TextBlock { Text = tagText, FontSize = 12, Margin = new Thickness(0, 0, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        tags.SetResourceReference(TextBlock.ForegroundProperty, trying ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        body.Children.Add(tags);

        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock
        {
            Text = option.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = option.Name,
        });
        var star = new SymbolIcon { Symbol = SymbolRegular.Star24, Filled = option.Favorite, FontSize = 18 };
        if (option.Favorite) star.SetResourceReference(IconElement.ForegroundProperty, "SystemFillColorCautionBrush");
        var favorite = new Button
        {
            Icon = star, ToolTip = option.Favorite ? "取消收藏" : "收藏方案", Appearance = ControlAppearance.Transparent,
            Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(8, 0, -6, 0), BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(favorite, (option.Favorite ? "取消收藏：" : "收藏：") + option.Name);
        AutomationProperties.SetAutomationId(favorite, "Gallery.Favorite." + option.Id);
        favorite.Click += (_, _) => RunAction(() => _library.ToggleFavorite(option.Id));
        Grid.SetColumn(favorite, 1);
        title.Children.Add(favorite);
        body.Children.Add(title);

        var description = new TextBlock
        {
            Text = option.Description, Style = (Style)FindResource("Desk.Caption"), Height = 40,
            Margin = new Thickness(0, 2, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = option.Error ?? option.Description,
        };
        if (!usable) description.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        body.Children.Add(description);

        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var apply = new Button { HorizontalAlignment = HorizontalAlignment.Stretch };
        if (!usable)
        {
            apply.Content = "文件需要修复"; apply.IsEnabled = false; apply.ToolTip = option.Error;
        }
        else if (trying)
        {
            apply.Content = "保留这套"; apply.Appearance = ControlAppearance.Primary; apply.IsEnabled = !_busy;
            apply.ToolTip = "结束试用并换成这套方案。";
        }
        else if (original)
        {
            apply.Content = "还原到这套"; apply.IsEnabled = !_busy;
            apply.ToolTip = "结束试用，回到试用前的方案。";
        }
        else if (selected)
        {
            apply.Content = "当前方案"; apply.IsEnabled = false;
            apply.Icon = new SymbolIcon { Symbol = SymbolRegular.Checkmark24 };
        }
        else
        {
            apply.Content = "应用方案"; apply.IsEnabled = !_busy;
            apply.ToolTip = "切换整套外观；各方案之前的微调都会保留。";
        }
        AutomationProperties.SetAutomationId(apply, "Gallery.Apply." + option.Id);
        AutomationProperties.SetName(apply, (trying ? "保留这套：" : original ? "还原到这套：" : selected ? "当前方案：" : "应用方案：") + option.Name);
        apply.Click += async (_, _) => await ApplyAsync(option.Id);
        actions.Children.Add(apply);

        var trialButton = new Button
        {
            // The theme a trial returns to has nothing to try.
            Content = trying ? "试用中…" : "试用 15 秒", IsEnabled = usable && !_busy && !selected && !original,
            Margin = new Thickness(8, 0, 0, 0), ToolTip = "先换上看看，15 秒后自动换回；窗口顶部可以保留或立即还原。",
        };
        AutomationProperties.SetAutomationId(trialButton, "Gallery.Trial." + option.Id);
        AutomationProperties.SetName(trialButton, "试用 15 秒：" + option.Name);
        trialButton.Click += (_, _) => RunAction(() => _library.StartThemeTrial(option.Id));
        Grid.SetColumn(trialButton, 1);
        actions.Children.Add(trialButton);

        var menu = BuildMenu(option);
        var more = new Button
        {
            Icon = new SymbolIcon { Symbol = SymbolRegular.MoreHorizontal24 }, ToolTip = "更多操作",
            Width = 36, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch, IsEnabled = menu.Items.Count > 0, ContextMenu = menu,
        };
        AutomationProperties.SetName(more, "更多操作：" + option.Name);
        AutomationProperties.SetAutomationId(more, "Gallery.More." + option.Id);
        more.Click += (_, _) => { menu.PlacementTarget = more; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
        Grid.SetColumn(more, 2);
        actions.Children.Add(more);
        body.Children.Add(actions);
        content.Children.Add(body);

        bool marked = selected || trying;
        var card = new Border { Child = content, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(marked ? 2 : 1), Margin = new Thickness(6, 0, 6, 12), Tag = option.Id };
        // The thicker outline of the current theme must not move its content.
        content.Margin = new Thickness(marked ? 0 : 1);
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, marked ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
        return card;
    }

    /// <summary>Only what can be done with this theme; nothing greyed out to wonder about.</summary>
    private ContextMenu BuildMenu(ThemeOption option)
    {
        var menu = new ContextMenu();
        void Add(string text, SymbolRegular icon, RoutedEventHandler click)
        {
            var item = new MenuItem { Header = text, Icon = new SymbolIcon { Symbol = icon } };
            item.Click += click;
            menu.Items.Add(item);
        }
        bool usable = option.Error == null;
        if (usable && option.Overrides.Count > 0)
        {
            Add("查看微调记录…", SymbolRegular.History24, async (_, _) =>
            {
                try { await UiHost.ShowContentAsync(option.Name + " · 微调记录", new AdjustmentList(_library, option.Id)); }
                catch (Exception e) { Feedback("无法查看微调记录", e.Message, InfoBarSeverity.Error); }
                Refresh();
            });
        }
        if (usable)
        {
            Add("复制为个人方案…", SymbolRegular.Copy24, async (_, _) =>
            {
                string? name = await UiHost.PromptNameAsync("复制方案", "会包含这套方案已保存的微调。", option.Name + " 副本");
                if (name != null) RunAction(() => { _library.DuplicateTheme(option.Id, name); _state.Filter = "我的方案"; });
            });
        }
        if (usable && option.IsUserTheme)
        {
            Add("重命名…", SymbolRegular.Rename24, async (_, _) =>
            {
                string? name = await UiHost.PromptNameAsync("重命名方案", "名称最多 60 个字符。", option.Name);
                if (name != null) RunAction(() => _library.RenameTheme(option.Id, name));
            });
        }
        if (usable)
        {
            Add("导出方案…", SymbolRegular.ArrowUpload24, (_, _) => RunAction(() =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出 Desk 主题", Filter = "Desk 主题 (*.json)|*.json", FileName = option.Id + ".json", DefaultExt = ".json" };
                if (dialog.ShowDialog() == true) { _library.ExportTheme(option.Id, dialog.FileName); Done("已导出", "包含这套方案已保存的微调。"); }
            }));
        }
        if (option.IsUserTheme)
        {
            Add("删除个人方案…", SymbolRegular.Delete24, async (_, _) =>
            {
                if (await UiHost.ConfirmAsync("删除「" + option.Name + "」？", "正在使用这套方案时会切回默认方案。文件会保留在主题目录的 .trash 文件夹。", "删除", true))
                    RunAction(() => _library.DeleteTheme(option.Id));
            });
        }
        return menu;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void RunAction(Action action)
    {
        try { action(); }
        catch (Exception e) { Feedback("操作未完成", e.Message, InfoBarSeverity.Error); }
        Refresh();
    }

    private Task ApplyAsync(string id)
    {
        if (_busy) return Task.CompletedTask;
        // Taken before the buttons are disabled, which takes the focus away.
        var focus = FocusedCardControl();
        _busy = true;
        // Greyed out where they are: building every card again for a moment
        // made the gallery flash.
        var paused = new List<Button>();
        foreach (var card in _cards.Children.OfType<Border>())
        {
            foreach (string kind in new[] { "Apply", "Trial" })
            {
                if (Find(card, "Gallery." + kind + "." + card.Tag) is { IsEnabled: true } button)
                {
                    button.IsEnabled = false;
                    paused.Add(button);
                }
            }
        }
        _undo.IsEnabled = false;
        Feedback("正在应用…", "正在更新任务栏和开始菜单。", InfoBarSeverity.Informational);
        // Paint feedback before the synchronous local configuration write.
        return Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var trial = _library.PendingTrial;
                // During a trial the theme to go back to is the one from before it.
                string previous = trial?.OriginalTheme ?? _library.ThemeId;
                _library.ThemeId = id;
                if (!Same(previous, id)) _state.Previous = previous;
                if (trial != null && Same(trial.OriginalTheme, id))
                    Done("已还原", "试用结束，外观回到试用前的方案。");
                else if (trial != null && Same(trial.TrialTheme, id))
                    Done("已保留这套方案", "之后可以在任务栏和开始菜单页继续微调，也可以撤销切换。");
                else
                    Done("方案已切换", "外观会在几秒内更新，可以撤销切换；是否生效见概览里的状态。");
            }
            catch (Exception e)
            {
                Feedback("没有切换成功", e.Message, InfoBarSeverity.Error);
            }
            finally
            {
                _busy = false;
                foreach (var button in paused) button.IsEnabled = true;
                _pendingFocus = focus;
                Refresh();
            }
        }, DispatcherPriority.Background).Task;
    }

    /// <summary>
    /// Success, at the bottom of the window, going away by itself like on the
    /// other pages; what is in progress and errors stay on the page.
    /// </summary>
    private void Done(string title, string message)
    {
        // "正在应用…" is over.
        _feedback.IsOpen = false;
        UiHost.Toast(title, message);
    }

    private void Feedback(string title, string message, InfoBarSeverity severity)
    {
        _feedback.Title = title;
        _feedback.Message = message;
        _feedback.Severity = severity;
        _feedback.IsOpen = true;
    }
}
