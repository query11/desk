using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wpf.Ui.Controls;

namespace Desk.Ui;

/// <summary>Preview, presets, recoverable edits, and progressive parameter controls.</summary>
internal sealed class ParameterSection
{
    private readonly IAppearanceEditor _app;
    private readonly ParameterScope _scope;
    private readonly IReadOnlyList<ParameterInfo> _parameters;
    private readonly Panel _panel;
    private readonly FrameworkElement _emptyHint;
    private readonly ButtonBase _reset;
    private readonly List<ParameterCard> _cards = [];
    private readonly List<(MaterialPreset Preset, Wpf.Ui.Controls.Button Button)> _presets = [];
    private MaterialPreview? _preview;
    private Wpf.Ui.Controls.Button? _undo;
    private System.Windows.Controls.RadioButton? _lightMode;
    private System.Windows.Controls.RadioButton? _darkMode;
    private InfoBar? _modeHint;
    private string? _builtFor;
    private bool _updating;

    // Which sections the user left open, and which mode they edit, per page:
    // a page is built again on every visit and would forget otherwise.
    private static readonly HashSet<string> Expanded = [];
    private static readonly Dictionary<string, bool> EditedModes = [];
    private readonly string? _layoutKey;

    /// <summary>The mode chosen to edit; null follows the one the desktop shows.</summary>
    private bool? _mode;

    /// <param name="layoutKey">Names the page; with it the expanders and the edited mode come back the way they were left.</param>
    public ParameterSection(IAppearanceEditor app, ParameterScope scope, IReadOnlyList<ParameterInfo> parameters,
        Panel panel, FrameworkElement emptyHint, ButtonBase reset, string? layoutKey = null)
    {
        _layoutKey = layoutKey;
        _app = app;
        _scope = scope;
        _parameters = parameters;
        _panel = panel;
        _emptyHint = emptyHint;
        _reset = reset;
        if (layoutKey != null && EditedModes.TryGetValue(layoutKey, out bool mode)) _mode = mode;
        _reset.Click += (_, _) =>
        {
            if (TryEdit(() => _app.ResetParameters(_scope, _parameters.Select(p => p.Name), EditingLight)))
                UiHost.Toast("已恢复默认", "可以点「撤销调整」找回刚才的效果。");
        };
    }

    /// <summary>The mode the desktop shows this part in now.</summary>
    private bool ShownLight => _app.EffectiveLight(_scope);

    /// <summary>The mode whose values the page shows and changes.</summary>
    private bool EditingLight => _mode ?? ShownLight;

    public void Refresh()
    {
        string key = _app.ThemeId + "|" + string.Join(",", _parameters.Select(p => _app.HasParameter(p.Name)));
        if (key != _builtFor)
        {
            _builtFor = key;
            Build();
        }
        bool light = EditingLight;
        foreach (var card in _cards)
        {
            if (_app.GetParameter(_scope, card.Info.Name, light) is { } value) card.Show(value);
            card.ShowReset(_app.HasChangedParameters([card.Info.Name], card.Info.PerMode ? light : null));
        }
        _emptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _reset.IsEnabled = _app.HasChangedParameters(_parameters.Select(p => p.Name));
        RefreshMode();
        RefreshPreview();
    }

    private void Build()
    {
        _cards.Clear();
        _presets.Clear();
        _panel.Children.Clear();
        _preview = null;
        _undo = null;
        _lightMode = _darkMode = null;
        _modeHint = null;
        var available = _parameters.Where(p => _app.HasParameter(p.Name)).ToList();
        if (available.Count == 0) return;

        // Only some values are kept per mode; without them there is nothing to choose.
        if (available.Any(p => p.PerMode)) BuildModeChoice(available.Where(p => p.PerMode).Select(p => p.Title).Distinct());

        _preview = new MaterialPreview(_scope);
        var previewSection = new System.Windows.Controls.Expander
        {
            Header = "效果预览", Content = _preview, IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Remember(previewSection, "preview");
        AutomationProperties.SetAutomationId(previewSection, "Preview." + _scope);
        _panel.Children.Add(previewSection);
        var presetRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var preset in MaterialPresets.All)
        {
            var button = new Wpf.Ui.Controls.Button
            {
                Content = preset.Name, ToolTip = preset.Description,
                MinWidth = 72, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6),
            };
            button.Click += (_, _) => TryEdit(() => _app.ApplyMaterialPreset(_scope, preset, EditingLight));
            AutomationProperties.SetName(button, preset.Name + "材质预设");
            AutomationProperties.SetAutomationId(button, "Preset." + _scope + "." + preset.Id);
            _presets.Add((preset, button));
            presetRow.Children.Add(button);
        }
        _panel.Children.Add(presetRow);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _undo = new Wpf.Ui.Controls.Button
        {
            Content = "撤销调整", Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowUndo24 }, Margin = new Thickness(0, 0, 8, 4),
        };
        _undo.Click += (_, _) => TryEdit(() => _app.UndoAppearance(_scope, EditingLight));
        AutomationProperties.SetAutomationId(_undo, "Undo." + _scope);
        actions.Children.Add(_undo);
        var save = new Wpf.Ui.Controls.Button
        {
            Content = "保存为我的方案", Icon = new SymbolIcon { Symbol = SymbolRegular.Save24 }, Margin = new Thickness(0, 0, 0, 4),
        };
        AutomationProperties.SetAutomationId(save, "SaveAppearance." + _scope);
        save.Click += async (_, _) =>
        {
            string? name = await UiHost.PromptNameAsync("保存个人方案", "保存任务栏和开始菜单当前外观，可在外观方案中切换。", "我的桌面");
            if (name == null) return;
            try
            {
                _app.SaveAppearance(name);
                UiHost.Toast("方案已保存", "在「外观方案 → 我的方案」中选择「" + name.Trim() + "」。");
            }
            catch (Exception error)
            {
                UiHost.Toast("没有保存成功", error.Message, SymbolRegular.Warning24);
            }
        };
        actions.Children.Add(save);
        _panel.Children.Add(actions);

        var advancedPanel = new StackPanel();
        foreach (var parameter in available)
        {
            var card = new ParameterCard(parameter);
            card.ValueChanged += (c, value) =>
            {
                bool light = EditingLight;
                if (!TryEdit(() => _app.SetParameter(_scope, c.Info.Name, value, c.Info.PerMode, light))) return;
                _reset.IsEnabled = true;
                c.ShowReset(_app.HasChangedParameters([c.Info.Name], c.Info.PerMode ? light : null));
                RefreshPreview();
            };
            card.ResetRequested += c => TryEdit(() => _app.ResetParameters(_scope, [c.Info.Name], EditingLight, c.Info.PerMode));
            _cards.Add(card);
            (parameter.Advanced ? advancedPanel : _panel).Children.Add(card);
        }
        if (advancedPanel.Children.Count > 0)
        {
            var advanced = new System.Windows.Controls.Expander
            {
                Header = "高级设置 · 明暗、色彩与颗粒", Content = advancedPanel,
                Margin = new Thickness(0, 12, 0, 4), IsExpanded = false,
            };
            Remember(advanced, "advanced");
            AutomationProperties.SetAutomationId(advanced, "Advanced." + _scope);
            _panel.Children.Add(advanced);
        }
    }

    /// <summary>
    /// "正在编辑：浅色 | 深色": the values kept per mode can be changed for the
    /// mode the desktop isn't in, without switching Windows.
    /// </summary>
    private void BuildModeChoice(IEnumerable<string> perMode)
    {
        var choice = new StackPanel { Orientation = Orientation.Horizontal };
        _lightMode = ModeButton("浅色", true);
        _darkMode = ModeButton("深色", false);
        choice.Children.Add(_lightMode);
        choice.Children.Add(_darkMode);
        var card = new CardControl
        {
            Icon = new SymbolIcon { Symbol = SymbolRegular.DarkTheme24 },
            Header = new CardHeader
            {
                Title = "正在编辑",
                Description = string.Join("和", perMode) + "在浅色、深色下分别保存；其余数值两种模式共用。",
            },
            Content = choice,
        };
        AutomationProperties.SetName(choice, "正在编辑的模式");
        _panel.Children.Add(card);
        _modeHint = new InfoBar
        {
            Title = "当前桌面是另一种模式", IsClosable = false, Severity = InfoBarSeverity.Informational,
            Margin = new Thickness(0, 0, 0, 8),
        };
        AutomationProperties.SetAutomationId(_modeHint, "ModeHint." + _scope);
        _panel.Children.Add(_modeHint);
    }

    private System.Windows.Controls.RadioButton ModeButton(string text, bool light)
    {
        var button = new System.Windows.Controls.RadioButton
        {
            Content = text, MinWidth = 0, Margin = new Thickness(light ? 0 : 16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(button, "Mode." + _scope + "." + (light ? "light" : "dark"));
        AutomationProperties.SetName(button, "编辑" + text + "的数值");
        button.Checked += (_, _) =>
        {
            if (_updating) return;
            // Back on the mode the desktop shows: follow it again when it changes.
            _mode = light == ShownLight ? null : light;
            if (_layoutKey != null)
            {
                if (_mode is { } chosen) EditedModes[_layoutKey] = chosen;
                else EditedModes.Remove(_layoutKey);
            }
            Refresh();
        };
        return button;
    }

    private void RefreshMode()
    {
        if (_lightMode == null || _darkMode == null || _modeHint == null) return;
        bool light = EditingLight, shown = ShownLight;
        _updating = true;
        try
        {
            _lightMode.IsChecked = light;
            _darkMode.IsChecked = !light;
        }
        finally
        {
            _updating = false;
        }
        string now = shown ? "浅色" : "深色", edited = light ? "浅色" : "深色";
        _modeHint.Message = _scope == ParameterScope.StartMenu
            ? $"开始菜单现在是{now}玻璃。这里修改的是{edited}玻璃的数值，换成{edited}后才看得到。"
            : $"任务栏现在是{now}模式。这里修改的是{edited}模式的数值，Windows 切换到{edited}模式后才看得到。";
        _modeHint.IsOpen = light != shown;
    }

    private void Remember(System.Windows.Controls.Expander expander, string name)
    {
        if (_layoutKey == null) return;
        string key = _layoutKey + "." + name;
        expander.IsExpanded = Expanded.Contains(key);
        expander.Expanded += (_, _) => Expanded.Add(key);
        expander.Collapsed += (_, _) => Expanded.Remove(key);
    }

    private bool TryEdit(Action action)
    {
        try { action(); return true; }
        catch (Exception error)
        {
            Refresh();
            UiHost.Toast("调整没有保存成功", error.Message, SymbolRegular.Warning24);
            return false;
        }
    }

    private void RefreshPreview()
    {
        bool light = EditingLight;
        _preview?.Update(name => _app.GetParameter(_scope, name, light), light);
        if (_undo != null) _undo.IsEnabled = _app.CanUndoAppearance(_scope, light);
        foreach (var (preset, button) in _presets)
        {
            bool selected = preset.Values(_scope, light).All(pair =>
                _app.GetParameter(_scope, pair.Key.Split('@')[0], light) is { } current &&
                Math.Abs(current - double.Parse(pair.Value, CultureInfo.InvariantCulture)) < .0005);
            button.Appearance = selected ? ControlAppearance.Primary : ControlAppearance.Secondary;
        }
    }
}
