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

/// <summary>Local-only, keyboard accessible theme gallery. No shell access in the view.</summary>
internal sealed class ThemeGallery : StackPanel
{
    private readonly IThemeLibrary _library;
    private readonly UniformGrid _cards = new() { Columns = 2, Margin = new Thickness(-6, 0, -6, 0) };
    private readonly Wpf.Ui.Controls.TextBox _search = new() { PlaceholderText = "搜索方案名称或风格…", Margin = new Thickness(0, 0, 0, 14) };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 36, 20, 36), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
    private readonly InfoBar _feedback = new() { IsClosable = true, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Button _undo = new() { Content = "撤销切换", IsEnabled = false, Margin = new Thickness(0, 0, 8, 8), Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowUndo24 } };
    private readonly Button _mode = new() { Content = "预览：浅色", Margin = new Thickness(0, 0, 8, 8) };
    private readonly List<Button> _filters = [];
    private IReadOnlyList<ThemeOption> _options = [];
    private string _filter = "推荐";
    private string? _previous;
    private bool _light;
    private bool _busy;
    private System.Windows.Media.ImageSource? _wallpaper;
    private readonly Button _wallpaperButton = new() { Content = "使用当前壁纸", Margin = new Thickness(0, 0, 8, 8) };

    public ThemeGallery(IThemeLibrary library, bool light)
    {
        _library = library;
        _light = light;
        Children.Add(new TextBlock { Text = "给桌面换一种气质", FontSize = 28, FontWeight = FontWeights.SemiBold });
        var intro = new TextBlock
        {
            Text = "一套方案，同时搭配任务栏与开始菜单。选择之后，还可以单独微调。",
            Margin = new Thickness(0, 8, 0, 20), Style = (Style)FindResource("Desk.Caption"),
        };
        Children.Add(intro);
        var actions = new WrapPanel();
        actions.Children.Add(_mode);
        actions.Children.Add(_wallpaperButton);
        _wallpaperButton.Click += async (_, _) =>
        {
            if (_wallpaper != null) { _wallpaper = null; _wallpaperButton.Content = "使用当前壁纸"; BuildCards(); return; }
            _wallpaperButton.IsEnabled = false;
            _wallpaperButton.Content = "读取壁纸…";
            var result = await WallpaperPreview.LoadAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                _wallpaper = result.Image;
                _wallpaperButton.IsEnabled = true;
                _wallpaperButton.Content = _wallpaper == null ? "重试读取壁纸" : "使用示例壁纸";
                if (result.Error != null) Feedback("壁纸预览", result.Error, InfoBarSeverity.Informational);
                BuildCards();
            });
        };
        actions.Children.Add(_undo);
        var refresh = new Button { Content = "刷新列表", Margin = new Thickness(0, 0, 0, 8), Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowSync24 } };
        refresh.Click += (_, _) => Refresh();
        actions.Children.Add(refresh);
        var import = new Button { Content = "导入方案", Margin = new Thickness(8, 0, 0, 8) };
        import.Click += (_, _) => RunAction(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入 Desk 主题", Filter = "Desk 主题 (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            _library.ImportTheme(dialog.FileName); _filter = "我的方案"; _search.Text = "";
            Feedback("已导入", "导入的方案在“我的方案”中，点击试用即可查看。", InfoBarSeverity.Success);
        });
        actions.Children.Add(import);
        Children.Add(actions);
        Children.Add(new TextBlock
        {
            Text = "效果示意 · 预览配色仅影响此页面；实际外观取决于壁纸、Windows 配色与已保存的微调。",
            Style = (Style)FindResource("Desk.Caption"), Margin = new Thickness(0, 4, 0, 18),
        });
        Children.Add(_search);
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (string filter in new[] { "推荐", "全部", "收藏", "我的方案" })
        {
            var button = new Button { Content = filter, Tag = filter, Margin = new Thickness(0, 0, 8, 8), MinWidth = 76 };
            button.Click += (_, _) => { _filter = filter; BuildCards(); };
            AutomationProperties.SetAutomationId(button, "Gallery.Filter." + filter);
            _filters.Add(button);
            filters.Children.Add(button);
        }
        Children.Add(filters);
        _count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _count.FontSize = 12;
        Children.Add(_count);
        Children.Add(_feedback);
        Children.Add(_cards);
        Children.Add(_empty);
        AutomationProperties.SetAutomationId(_search, "Gallery.Search");
        AutomationProperties.SetName(_search, "搜索外观方案");
        AutomationProperties.SetAutomationId(_undo, "Gallery.Undo");
        AutomationProperties.SetAutomationId(_mode, "Gallery.Mode");
        _search.TextChanged += (_, _) => BuildCards();
        _mode.Click += (_, _) => { _light = !_light; BuildCards(); };
        _undo.Click += async (_, _) => { if (_previous is { } id) await ApplyAsync(id); };
        SizeChanged += (_, _) => _cards.Columns = ActualWidth < 620 ? 1 : 2;
        Refresh();
    }

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

    private void BuildCards()
    {
        _mode.Content = _light ? "预览：浅色" : "预览：深色";
        _undo.IsEnabled = !_busy && _previous != null && _previous != _library.ThemeId;
        foreach (var filter in _filters) filter.Appearance = (string)filter.Tag == _filter ? ControlAppearance.Primary : ControlAppearance.Secondary;
        string query = _search.Text.Trim();
        var visible = _options.Where(t => (_filter != "我的方案" || t.IsUserTheme) && (_filter != "推荐" || t.Featured) && (_filter != "收藏" || t.Favorite)
            && (t.Name + " " + t.Description + " " + t.Tag).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _cards.Children.Clear();
        _count.Text = $"{visible.Count} 个方案 · 点击应用后生效";
        _empty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = query.Length > 0 ? "没有匹配的方案，试试“通透”或“横栏”。"
            : _filter == "我的方案" ? "还没有个人方案。调整任务栏或开始菜单后，点击“保存为我的方案”。"
            : _filter == "收藏" ? "点击方案右下方的星标，将常用方案放在这里。"
            : "这里还没有方案。可切换到“全部”，或在设置中打开主题文件夹。";
        foreach (var option in visible)
        {
            bool selected = string.Equals(option.Id, _library.ThemeId, StringComparison.OrdinalIgnoreCase);
            var content = new StackPanel();
            var preview = new DesktopPreview { Wallpaper = _wallpaper };
            preview.Update(option, _light);
            content.Children.Add(preview);
            var body = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
            var tags = new TextBlock { Text = option.Tag, FontSize = 11, Margin = new Thickness(0, 0, 0, 6) };
            tags.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            body.Children.Add(tags);
            body.Children.Add(new TextBlock { Text = option.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = option.Name });
            body.Children.Add(new TextBlock
            {
                Text = option.Description, Style = (Style)FindResource("Desk.Caption"), Height = 42,
                Margin = new Thickness(0, 6, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = option.Description,
            });
            var apply = new Button
            {
                Content = option.Error != null ? "文件需要修复" : selected ? "✓  当前方案" : "应用方案",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Appearance = selected ? ControlAppearance.Primary : ControlAppearance.Secondary,
                IsEnabled = !_busy && !selected && option.Error == null,
                ToolTip = option.Error ?? "切换整套外观；保留各方案之前的微调。",
            };
            AutomationProperties.SetAutomationId(apply, "Gallery.Apply." + option.Id);
            AutomationProperties.SetName(apply, (selected ? "当前方案：" : "应用方案：") + option.Name);
            apply.Click += async (_, _) => await ApplyAsync(option.Id);
            body.Children.Add(apply);
            var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var trial = new Button { Content = "试用 15 秒", IsEnabled = !selected && option.Error == null, Margin = new Thickness(0, 0, 6, 4) };
            AutomationProperties.SetAutomationId(trial, "Gallery.Trial." + option.Id);
            trial.Click += (_, _) => RunAction(() => _library.StartThemeTrial(option.Id));
            tools.Children.Add(trial);
            var favorite = new Button { Content = option.Favorite ? "★" : "☆", ToolTip = option.Favorite ? "取消收藏" : "收藏方案", Margin = new Thickness(0, 0, 6, 4) };
            AutomationProperties.SetName(favorite, (option.Favorite ? "取消收藏：" : "收藏：") + option.Name);
            AutomationProperties.SetAutomationId(favorite, "Gallery.Favorite." + option.Id);
            favorite.Click += (_, _) => RunAction(() => _library.ToggleFavorite(option.Id));
            tools.Children.Add(favorite);
            var more = new Button { Content = "更多", Margin = new Thickness(0, 0, 6, 4) };
            var menu = new ContextMenu();
            void AddAction(string text, Action action, bool enabled = true)
            {
                var item = new MenuItem { Header = text, IsEnabled = enabled };
                item.Click += (_, _) => RunAction(action); menu.Items.Add(item);
            }
            var copyItem = new MenuItem { Header = "复制为个人方案…", IsEnabled = option.Error == null };
            copyItem.Click += async (_, _) =>
            {
                string? name = await UiHost.PromptNameAsync("复制方案", "会包含这套方案已保存的微调。", option.Name + " 副本");
                if (name != null) RunAction(() => { _library.DuplicateTheme(option.Id, name); _filter = "我的方案"; });
            };
            menu.Items.Add(copyItem);
            var rename = new MenuItem { Header = "重命名…", IsEnabled = option.IsUserTheme && option.Error == null };
            rename.Click += async (_, _) =>
            {
                string? name = await UiHost.PromptNameAsync("重命名方案", "名称最多 60 个字符。", option.Name);
                if (name != null) RunAction(() => _library.RenameTheme(option.Id, name));
            };
            menu.Items.Add(rename);
            AddAction("导出方案…", () =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出 Desk 主题", Filter = "Desk 主题 (*.json)|*.json", FileName = option.Id + ".json", DefaultExt = ".json" };
                if (dialog.ShowDialog() == true) { _library.ExportTheme(option.Id, dialog.FileName); Feedback("已导出", "包含这套方案已保存的微调。", InfoBarSeverity.Success); }
            }, option.Error == null);
            var delete = new MenuItem { Header = "删除个人方案…", IsEnabled = option.IsUserTheme };
            delete.Click += async (_, _) =>
            {
                if (await UiHost.ConfirmAsync("删除“" + option.Name + "”？", "当前使用这套方案时会切回默认方案。文件会保留在主题目录的 .trash 文件夹。", "删除", true))
                    RunAction(() => _library.DeleteTheme(option.Id));
            };
            menu.Items.Add(delete);
            more.ContextMenu = menu;
            more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
            tools.Children.Add(more);
            body.Children.Add(tools);
            if (option.Overrides.Count > 0 && option.Error == null)
            {
                var changes = new Button { Content = "查看微调记录 →", Appearance = ControlAppearance.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 5, 0, 0) };
                changes.Click += async (_, _) =>
                {
                    try { await UiHost.ShowContentAsync(option.Name + " · 修改记录", new AdjustmentList(_library, option.Id)); }
                    catch (Exception e) { Feedback("无法查看修改", e.Message, InfoBarSeverity.Error); }
                };
                body.Children.Add(changes);
            }
            content.Children.Add(body);
            var card = new Border { Child = content, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(selected ? 2 : 1), Margin = new Thickness(6, 0, 6, 12) };
            card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            card.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
            _cards.Children.Add(card);
        }
    }

    private void RunAction(Action action)
    {
        try { action(); }
        catch (Exception e) { Feedback("操作未完成", e.Message, InfoBarSeverity.Error); }
        Refresh();
    }

    private Task ApplyAsync(string id)
    {
        if (_busy) return Task.CompletedTask;
        _busy = true;
        BuildCards();
        Feedback("正在应用…", "正在更新任务栏和开始菜单。", InfoBarSeverity.Informational);
        // Paint feedback before the synchronous local configuration write.
        return Dispatcher.InvokeAsync(() =>
        {
            try
            {
                string previous = _library.ThemeId;
                _library.ThemeId = id;
                _previous = previous;
                Feedback("方案已切换", "外观将在几秒内更新；可以撤销切换，生效状态请查看概览。", InfoBarSeverity.Success);
            }
            catch (Exception e)
            {
                Feedback("没有切换成功", e.Message, InfoBarSeverity.Error);
            }
            finally
            {
                _busy = false;
                Refresh();
            }
        }, DispatcherPriority.Background).Task;
    }

    private void Feedback(string title, string message, InfoBarSeverity severity)
    {
        _feedback.Title = title;
        _feedback.Message = message;
        _feedback.Severity = severity;
        _feedback.IsOpen = true;
    }
}
