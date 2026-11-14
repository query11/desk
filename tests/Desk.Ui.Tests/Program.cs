using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Desk;
using Desk.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

// Runs real WPF controls without starting Desk, injecting the shell, or saving user settings.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "build/ui-appearance");
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary());
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Desk;component/Ui/Styles.xaml"),
        });
        if (!args.Contains("--shell-only"))
        foreach (bool light in new[] { true, false })
        foreach (var scope in new[] { ParameterScope.Taskbar, ParameterScope.StartMenu })
        foreach (int width in new[] { 760, 460 })
        {
            ApplicationThemeManager.Apply(light ? ApplicationTheme.Light : ApplicationTheme.Dark);
            var editor = new MemoryEditor(light);
            var panel = new StackPanel();
            var reset = new Wpf.Ui.Controls.Button { Content = "恢复默认", HorizontalAlignment = HorizontalAlignment.Right };
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = scope == ParameterScope.Taskbar ? "任务栏 · 浮岛外观" : "开始菜单 · 材质与外观",
                FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12),
            });
            content.Children.Add(reset);
            content.Children.Add(panel);
            var root = new Border { Width = width, Padding = new Thickness(24), Child = content };
            root.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
            root.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
            TextElement.SetFontFamily(root, new FontFamily("Segoe UI, Microsoft YaHei UI"));
            TextElement.SetFontSize(root, 14);
            var section = new ParameterSection(editor, scope,
                scope == ParameterScope.Taskbar ? Parameters.Taskbar : Parameters.StartMenu,
                panel, new TextBlock(), reset);
            editor.Changed = section.Refresh;
            section.Refresh();
            Layout(root);
            var advanced = Find<Expander>(root, "Advanced." + scope);
            var preview = Find<Expander>(root, "Preview." + scope);
            Check(!preview.IsExpanded, "Preview should start collapsed");
            Check(!advanced.IsExpanded, "Advanced controls should start collapsed");
            string prefix = scope == ParameterScope.Taskbar ? "island" : "menu";
            double before = editor.GetParameter(scope, prefix + "Opacity")!.Value;
            var clear = Find<Wpf.Ui.Controls.Button>(root, "Preset." + scope + ".clear");
            clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(clear.Appearance == Wpf.Ui.Controls.ControlAppearance.Primary, "Selected preset highlight");
            var opacity = Find<Slider>(root, "Parameter." + prefix + "Opacity");
            double expected = light ? .12 : .16;
            Check(Math.Abs(opacity.Value - (1 - expected)) < .00001, "Transparency must invert tint opacity");
            var undo = Find<Wpf.Ui.Controls.Button>(root, "Undo." + scope);
            Check(undo.IsEnabled, "Preset enables undo");
            undo.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(Math.Abs(editor.GetParameter(scope, prefix + "Opacity")!.Value - before) < .00001, "Undo restores original appearance");
            opacity.Value = .8;
            Check(Math.Abs(editor.GetParameter(scope, prefix + "Opacity")!.Value - .2) < .00001, "Slider input maps to tint correctly");
            if (scope == ParameterScope.Taskbar)
            {
                var icon = Find<Slider>(root, "Parameter.iconSize");
                var spacing = Find<Slider>(root, "Parameter.buttonWidth");
                spacing.Value = 56;
                icon.Value = 30;
                Find<Wpf.Ui.Controls.Button>(root, "Reset.iconSize").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(icon.Value == 28 && spacing.Value == 56, "Reset icon without resetting spacing");
                Check(Math.Abs(opacity.Value - .8) < .00001, "Individual reset preserves material");
                undo.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(icon.Value == 30 && spacing.Value == 56, "Individual reset is undoable");
            }
            reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!reset.IsEnabled, "Reset disables itself");
            undo.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(Math.Abs(opacity.Value - .8) < .00001, "Reset is undoable");
            clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Layout(root);
            // Interactive controls must stay inside the available column at both widths.
            foreach (var control in Descendants(root).OfType<FrameworkElement>().Where(e => e is Slider or ButtonBase))
            {
                if (control.ActualWidth <= 0 || !VisibleInTree(control)) continue;
                var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
                Check(bounds.Left >= 0 && bounds.Right <= width + 1, $"Control overflows: {control.GetType().Name} {bounds}");
            }
            Render(root, Path.Combine(output, $"{scope}-{(light ? "light" : "dark")}-{width}.png"));
            advanced.IsExpanded = true;
            preview.IsExpanded = true;
            // Let WPF UI's expand animation finish before inspecting the expanded content.
            var frame = new DispatcherFrame();
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            settle.Tick += (_, _) => { settle.Stop(); frame.Continue = false; };
            settle.Start();
            Dispatcher.PushFrame(frame);
            Layout(root);
            Render(root, Path.Combine(output, $"{scope}-{(light ? "light" : "dark")}-{width}-advanced.png"));
            CheckIcons(root, scope.ToString());
            Console.WriteLine($"PASS {scope}, {(light ? "light" : "dark")}, {width}px: preset, slider, undo, reset, layout, icons");
        }
        if (!args.Contains("--shell-only"))
        foreach (bool light in new[] { true, false })
        foreach (int width in new[] { 860, 460 })
        {
            ApplicationThemeManager.Apply(light ? ApplicationTheme.Light : ApplicationTheme.Dark);
            var library = new MemoryLibrary();
            var gallery = new ThemeGallery(library, light);
            var root = new Border { Width = width, Padding = new Thickness(24), Child = gallery };
            root.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
            root.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
            TextElement.SetFontFamily(root, new FontFamily("Segoe UI, Microsoft YaHei UI"));
            TextElement.SetFontSize(root, 14);
            Layout(root);
            Check(Descendants(root).OfType<DesktopPreview>().Count() == 7, "Seven recommended paired presets");
            Check(Descendants(root).OfType<UniformGrid>().Single().Columns == (width < 620 ? 1 : 2), "Responsive gallery columns");
            Check((string)Find<Wpf.Ui.Controls.Button>(root, "Gallery.Filter.推荐").Content == "推荐  7", "Filters show how many themes they hold");
            Check(Find<Wpf.Ui.Controls.Button>(root, "Gallery.Undo").Visibility == Visibility.Collapsed, "Nothing to undo before a switch");
            Render(root, Path.Combine(output, $"Gallery-{(light ? "light" : "dark")}-{width}.png"));
            CheckIcons(root, "gallery");
            // The "⋯" menus aren't in the visual tree until they open.
            foreach (var more in Descendants(root).OfType<Wpf.Ui.Controls.Button>().Where(b => b.ContextMenu != null))
                foreach (var item in more.ContextMenu.Items.OfType<MenuItem>())
                    Check(item.Icon is Wpf.Ui.Controls.SymbolIcon { Symbol: var symbol } && (int)symbol <= 0xFFFF, $"gallery menu: icon of '{item.Header}' can't be shown");
            void Click(string id)
            {
                Find<Wpf.Ui.Controls.Button>(root, id).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Layout(root);
            }
            Click("Gallery.Apply.desk-air");
            Check(library.ThemeId == "desk-air", "Apply selected preset");
            Check(!Find<Wpf.Ui.Controls.Button>(root, "Gallery.Apply.desk-air").IsEnabled, "Current preset cannot be reapplied accidentally");
            Check(Find<Wpf.Ui.Controls.Button>(root, "Gallery.Undo").Visibility == Visibility.Visible, "A switch offers its undo");
            Click("Gallery.Undo");
            Check(library.ThemeId == "desk-islands", "Switch undo restores previous theme");
            Click("Gallery.Mode");
            Check(library.ThemeId == "desk-islands", "Preview mode never changes real theme");
            var search = Find<Wpf.Ui.Controls.TextBox>(root, "Gallery.Search");
            search.Text = "专注";
            Layout(root);
            Check(Descendants(root).OfType<DesktopPreview>().Count() == 1, "Search by name");
            search.Text = "no-such-theme";
            Layout(root);
            Check(!Descendants(root).OfType<DesktopPreview>().Any(), "Search empty state");
            search.Text = "";
            Click("Gallery.Filter.我的方案");
            Check(!Descendants(root).OfType<DesktopPreview>().Any(), "Personal themes empty state");
            Click("Gallery.Filter.全部");
            Check(Descendants(root).OfType<DesktopPreview>().Count() == 10, "Original themes still available");
            Click("Gallery.Favorite.desk-dusk");
            Click("Gallery.Filter.收藏");
            Check(Descendants(root).OfType<DesktopPreview>().Count() == 1, "Favorite filter");
            Click("Gallery.Trial.desk-dusk");
            Check(library.ThemeId == "desk-dusk", "Trial action is wired");
            var keep = Find<Wpf.Ui.Controls.Button>(root, "Gallery.Apply.desk-dusk");
            Check(keep.IsEnabled && (string)keep.Content == "保留这套", "A running trial can be kept from its card");
            Check(Find<Wpf.Ui.Controls.Button>(root, "Gallery.Undo").Visibility == Visibility.Collapsed, "No undo while a trial runs");
            Click("Gallery.Filter.全部");
            var back = Find<Wpf.Ui.Controls.Button>(root, "Gallery.Apply.desk-islands");
            Check(back.IsEnabled && (string)back.Content == "还原到这套", "A running trial can be ended from the original theme's card");
            Click("Gallery.Apply.desk-islands");
            Check(library.ThemeId == "desk-islands" && library.PendingTrial == null, "Returning to the original theme ends the trial");
            Click("Gallery.Filter.收藏");
            Click("Gallery.Favorite.desk-dusk");
            Check(!Descendants(root).OfType<DesktopPreview>().Any(), "Remove favorite updates filter");
            Click("Gallery.Filter.全部");
            library.FailSelection = true;
            Click("Gallery.Apply.desk-frost");
            Check(library.ThemeId == "desk-islands", "Failed selection preserves theme");
            Check(Descendants(root).OfType<Wpf.Ui.Controls.InfoBar>().Any(b => b.IsOpen && b.Severity == Wpf.Ui.Controls.InfoBarSeverity.Error), "Failure is visible");
            foreach (var control in Descendants(root).OfType<ButtonBase>().Where(VisibleInTree))
            {
                var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
                Check(bounds.Left >= -1 && bounds.Right <= width + 1, "Gallery action fits available width");
            }
            // The page is built again on every visit: what it remembers lives outside it.
            var state = new GalleryState();
            var visit = new ThemeGallery(new MemoryLibrary(), light, state);
            Find<Wpf.Ui.Controls.Button>(visit, "Gallery.Filter.全部").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Find<Wpf.Ui.Controls.Button>(visit, "Gallery.Mode").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var revisit = new ThemeGallery(new MemoryLibrary(), light, state);
            Check(Descendants(revisit).OfType<DesktopPreview>().Count() == 10 && state.Light == !light, "A second visit keeps the filter and the preview mode");
            Console.WriteLine($"PASS gallery, {(light ? "light" : "dark")}, {width}px: apply, undo, trial, search, empty, error, preview isolation, revisit, layout");
        }
        foreach (bool light in new[] { true, false })
        foreach (int width in new[] { 1160, 720 })
        {
            ApplicationThemeManager.Apply(light ? ApplicationTheme.Light : ApplicationTheme.Dark);
            var shell = new MainWindow();
            ApplicationThemeManager.Apply(light ? ApplicationTheme.Light : ApplicationTheme.Dark);
            var content = (FrameworkElement)shell.Content;
            // Render the real shell without showing a window or starting Desk's host.
            shell.Content = null;
            shell.UpdateResponsiveLayout(width);
            shell.Navigation.Transition = Wpf.Ui.Animations.Transition.None;
            shell.Navigation.ApplyTemplate();
            shell.Navigation.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            shell.Navigation.ReplaceContent(new Page { Title = "外观方案", Content = new ThemeGallery(new MemoryLibrary(), light) }, null);
            content.Width = width;
            content.Height = 840;
            content.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
            content.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
            TextElement.SetFontFamily(content, shell.FontFamily);
            content.Measure(new Size(width, 840));
            content.Arrange(new Rect(0, 0, width, 840));
            content.UpdateLayout();
            content.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var shellFrame = new DispatcherFrame();
            var shellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            shellTimer.Tick += (_, _) => { shellTimer.Stop(); shellFrame.Continue = false; };
            shellTimer.Start();
            Dispatcher.PushFrame(shellFrame);
            content.UpdateLayout();
            Render(content, Path.Combine(output, $"Window-{(light ? "light" : "dark")}-{width}.png"));
            shell.TrialBanner.Visibility = Visibility.Visible;
            shell.TrialText.Text = "正在试用「圆润胶囊」 · 15 秒后自动还原";
            content.UpdateLayout();
            Render(content, Path.Combine(output, $"Window-trial-{(light ? "light" : "dark")}-{width}.png"));
            foreach (var action in new[] { shell.KeepTrialButton, shell.CancelTrialButton })
            {
                var bounds = action.TransformToAncestor(content).TransformBounds(new Rect(action.RenderSize));
                Check(bounds.Left >= 0 && bounds.Right <= width, "Trial actions fit the shell");
            }
            Check(Descendants(content).OfType<ThemeGallery>().Any(), "Gallery is mounted in real navigation shell");
            CheckIcons(content, "shell");
            Check(shell.Navigation.PaneDisplayMode == (width >= 960 ? Wpf.Ui.Controls.NavigationViewPaneDisplayMode.Left : Wpf.Ui.Controls.NavigationViewPaneDisplayMode.LeftMinimal), "Shell collapses at narrow width");
            shell.Close();
            Console.WriteLine($"PASS navigation shell, {(light ? "light" : "dark")}, {width}px");
        }
        if (!args.Contains("--shell-only"))
        foreach (bool light in new[] { true, false })
        foreach (int width in new[] { 860, 460 })
        {
            ApplicationThemeManager.Apply(light ? ApplicationTheme.Light : ApplicationTheme.Dark);
            var service = new MemoryDisplays(light);
            var page = new Desk.Ui.Pages.DisplaysPage(service);
            var content = (FrameworkElement)page.Content;
            page.Content = null;
            var root = new Border { Width = width, Padding = new Thickness(24), Child = content };
            root.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
            root.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
            TextElement.SetFontFamily(root, new FontFamily("Segoe UI, Microsoft YaHei UI"));
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Layout(root);
            var screens = Find<ComboBox>(root, "Displays.Select");
            var originalScreens = screens.ItemsSource;
            var enabled = Find<Wpf.Ui.Controls.ToggleSwitch>(root, "Displays.Enabled");
            enabled.IsChecked = true;
            Find<ComboBox>(root, "Displays.Maximize").SelectedIndex = 2;
            Find<ComboBox>(root, "Displays.show").SelectedIndex = 2;
            Layout(root);
            Check(service.GetDisplayPreferences("A").Enabled && service.GetDisplayPreferences("A").ShowMonitor == false, "Primary display settings stored");
            Check(ReferenceEquals(originalScreens, screens.ItemsSource), "Editing a profile preserves the display selector");
            screens.SelectedIndex = 1;
            Layout(root);
            Check(enabled.IsChecked == false && service.GetDisplayPreferences("B").ShowMonitor == null, "Secondary display stays independent");
            enabled.IsChecked = true;
            Find<ComboBox>(root, "Displays.Maximize").SelectedIndex = 1;
            screens.SelectedIndex = 0;
            Layout(root);
            Check(Find<ComboBox>(root, "Displays.Maximize").SelectedIndex == 2, "Return restores primary behavior");
            Check(Find<ComboBox>(root, "Displays.show").SelectedIndex == 2 && Find<ComboBox>(root, "Displays.cpu").SelectedIndex == 0, "Info island choices return, untouched ones follow the global setting");
            Check(service.GetDisplayPreferences("B").MaximizeMode == MaximizeMode.Merge, "Secondary behavior retained");
            var displayFrame = new DispatcherFrame();
            var displayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            displayTimer.Tick += (_, _) => { displayTimer.Stop(); displayFrame.Continue = false; };
            displayTimer.Start(); Dispatcher.PushFrame(displayFrame); Layout(root);
            Render(root, Path.Combine(output, $"Displays-{(light ? "light" : "dark")}-{width}.png"));
            CheckIcons(root, "displays");
            foreach (var control in Descendants(root).OfType<FrameworkElement>().Where(e => e is ButtonBase or ComboBox or Slider).Where(VisibleInTree))
            {
                if (control.ActualWidth == 0) continue;
                var bounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
                Check(bounds.Left >= -1 && bounds.Right <= width + 1, "Display control fits available width");
            }
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Console.WriteLine($"PASS displays, {(light ? "light" : "dark")}, {width}px: per-screen selection, independent toggles, behavior, layout");
        }
        if (!args.Contains("--shell-only"))
        {
            ApplicationThemeManager.Apply(ApplicationTheme.Light);
            var wallpaper = WallpaperPreview.LoadAsync().GetAwaiter().GetResult();
            var preview = new DesktopPreview { Width = 800, Height = 360, Wallpaper = wallpaper.Image };
            preview.Update(ThemeOption.Read(Path.Combine(AppContext.BaseDirectory, "themes", "desk-air.json"), false), true);
            Layout(preview);
            Render(preview, Path.Combine(output, "Current-wallpaper-preview.png"));
            Check(wallpaper.Image != null || wallpaper.Error != null, "Wallpaper read returns an image or a visible fallback reason");
            Console.WriteLine("PASS wallpaper: local read, image/fallback, material render");
        }
        app.Shutdown();
    }

    private static void Layout(FrameworkElement root)
    {
        for (int i = 0; i < 3; i++)
        {
            root.Measure(new Size(root.Width, double.PositiveInfinity));
            root.Arrange(new Rect(new Point(), root.DesiredSize));
            root.UpdateLayout();
            root.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }
    }

    private static void Render(FrameworkElement root, string file)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        png.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static bool VisibleInTree(DependencyObject element) =>
        (element is not UIElement ui || ui.Visibility == Visibility.Visible) &&
        (VisualTreeHelper.GetParent(element) is not { } parent || VisibleInTree(parent));

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants(root).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // WPF UI turns a symbol into text 16 bits at a time: a symbol past U+FFFF
    // compiles, but shows as a stray letter ("ԧ") instead of the icon.
    private static void CheckIcons(DependencyObject root, string where)
    {
        foreach (var icon in Descendants(root).OfType<Wpf.Ui.Controls.SymbolIcon>())
            Check((int)icon.Symbol <= 0xFFFF, $"{where}: icon {icon.Symbol} can't be shown by WPF UI");
    }

    private sealed class MemoryLibrary : IThemeLibrary
    {
        private readonly HashSet<string> _favorites = [];
        private string _id = "desk-islands";
        public bool FailSelection;
        public ThemeTrial? PendingTrial { get; private set; }
        public string ThemeId
        {
            get => _id;
            set { if (FailSelection) throw new IOException("测试：主题文件已被移走"); _id = value; PendingTrial = null; }
        }
        public IReadOnlyList<ThemeOption> GetAppearanceOptions() => Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "themes"), "*.json")
            .Select(f => ThemeOption.Read(f, false)).Select(t => t with { Favorite = _favorites.Contains(t.Id) }).OrderByDescending(t => t.Favorite).ThenBy(t => t.Theme?.Order ?? 100).ToList();
        public void StartThemeTrial(string id)
        {
            string original = PendingTrial?.OriginalTheme ?? _id;
            ThemeId = id;
            PendingTrial = new ThemeTrial(original, id, DateTime.UtcNow.AddSeconds(ThemeTrialController.DurationSeconds));
        }
        public void ToggleFavorite(string id) { if (!_favorites.Remove(id)) _favorites.Add(id); }
        public string DuplicateTheme(string id, string name) => throw new NotSupportedException();
        public void RenameTheme(string id, string name) => throw new NotSupportedException();
        public void DeleteTheme(string id) => throw new NotSupportedException();
        public string ImportTheme(string path) => throw new NotSupportedException();
        public void ExportTheme(string id, string path) => throw new NotSupportedException();
        public IReadOnlyList<ThemeAdjustment> GetThemeAdjustments(string id) => [];
        public void ResetThemeAdjustment(string id, string key) => throw new NotSupportedException();
    }

    private sealed class MemoryDisplays(bool light) : IDisplaySettings
    {
        private readonly Dictionary<string, DisplayPreferences> _preferences = new() { ["A"] = new(), ["B"] = new() };
        public event Action? StateChanged;
        public IReadOnlyList<DesktopDisplay> GetDisplays() => [new("A", "DISPLAY1", "主显示器", 3120, 2080, true), new("B", "DISPLAY2", "副显示器", 1920, 1080, false)];
        public DisplayPreferences GetDisplayPreferences(string id) => _preferences[id];
        public void UpdateDisplay(string id, Action<DisplayPreferences> change) { change(_preferences[id]); StateChanged?.Invoke(); }
        public IAppearanceEditor GetDisplayEditor(string id) => new MemoryEditor(light);
    }

    private sealed class MemoryEditor(bool light) : IAppearanceEditor
    {
        private readonly Theme _theme = Theme.Load(Path.Combine(AppContext.BaseDirectory, "themes", "desk-islands.json"));
        private readonly Settings _settings = new();
        private readonly AppearanceHistory _history = new();
        public Action? Changed;
        public string ThemeId => _theme.Id;
        public bool SystemLightMode => light;
        public bool StartMenuLightMode => light;
        public bool HasParameter(string name) => _theme.HasConstant(name);
        public double? GetParameter(ParameterScope scope, string name) =>
            double.TryParse(_theme.GetConstant(name, light, _settings.OverridesFor(ThemeId)), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double value) ? value : null;
        private void Edit(ParameterScope scope, Action edit)
        {
            var before = new Dictionary<string, string>(_settings.OverridesFor(ThemeId));
            edit();
            _history.Record(ThemeId, scope, before, _settings.OverridesFor(ThemeId), null, DateTime.UtcNow);
            Changed?.Invoke();
        }
        public void SetParameter(ParameterScope scope, string name, double value, bool perMode) =>
            Edit(scope, () => _settings.SetOverride(ThemeId, name, value.ToString(CultureInfo.InvariantCulture), perMode ? light : null));
        public bool HasChangedParameters(IEnumerable<string> names) => _settings.HasOverrides(ThemeId, names);
        public void ResetParameters(ParameterScope scope, IEnumerable<string> names) => Edit(scope, () => _settings.ResetOverrides(ThemeId, names));
        public bool CanUndoAppearance(ParameterScope scope) => _history.CanUndo(ThemeId, scope);
        public void UndoAppearance(ParameterScope scope) { _history.Undo(_settings, ThemeId, scope); Changed?.Invoke(); }
        public void ApplyMaterialPreset(ParameterScope scope, MaterialPreset preset) => Edit(scope, () =>
        {
            foreach (var (key, value) in preset.Values(scope, light))
            {
                var parts = key.Split('@');
                _settings.SetOverride(ThemeId, parts[0], value, parts.Length > 1 ? light : null);
            }
        });
        public string SaveAppearance(string name) => throw new NotSupportedException("UI smoke test never saves user themes.");
    }
}
