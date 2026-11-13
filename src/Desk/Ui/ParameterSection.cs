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
    private string? _builtFor;

    // Which sections the user left open, per page: a page is built again on
    // every visit and would fold them otherwise.
    private static readonly HashSet<string> Expanded = [];
    private readonly string? _layoutKey;

    /// <param name="layoutKey">Names the page; with it the expanders reopen the way they were left.</param>
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
        _reset.Click += (_, _) =>
        {
            if (TryEdit(() => _app.ResetParameters(_scope, _parameters.Select(p => p.Name))))
                UiHost.Toast("已恢复默认", "可以点「撤销调整」找回刚才的效果。");
        };
    }

    public void Refresh()
    {
        string key = _app.ThemeId + "|" + string.Join(",", _parameters.Select(p => _app.HasParameter(p.Name)));
        if (key != _builtFor)
        {
            _builtFor = key;
            Build();
        }
        foreach (var card in _cards)
        {
            if (_app.GetParameter(_scope, card.Info.Name) is { } value) card.Show(value);
            card.ShowReset(_app.HasChangedParameters([card.Info.Name]));
        }
        _emptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _reset.IsEnabled = _app.HasChangedParameters(_parameters.Select(p => p.Name));
        RefreshPreview();
    }

    private void Build()
    {
        _cards.Clear();
        _presets.Clear();
        _panel.Children.Clear();
        _preview = null;
        _undo = null;
        var available = _parameters.Where(p => _app.HasParameter(p.Name)).ToList();
        if (available.Count == 0) return;

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
            button.Click += (_, _) => TryEdit(() => _app.ApplyMaterialPreset(_scope, preset));
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
        _undo.Click += (_, _) => TryEdit(() => _app.UndoAppearance(_scope));
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
                if (!TryEdit(() => _app.SetParameter(_scope, c.Info.Name, value, c.Info.PerMode))) return;
                _reset.IsEnabled = true;
                c.ShowReset(_app.HasChangedParameters([c.Info.Name]));
                RefreshPreview();
            };
            card.ResetRequested += c => TryEdit(() => _app.ResetParameters(_scope, [c.Info.Name]));
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
        bool light = _scope == ParameterScope.StartMenu ? _app.StartMenuLightMode : _app.SystemLightMode;
        _preview?.Update(name => _app.GetParameter(_scope, name), light);
        if (_undo != null) _undo.IsEnabled = _app.CanUndoAppearance(_scope);
        foreach (var (preset, button) in _presets)
        {
            bool selected = preset.Values(_scope, light).All(pair =>
                _app.GetParameter(_scope, pair.Key.Split('@')[0]) is { } current &&
                Math.Abs(current - double.Parse(pair.Value, CultureInfo.InvariantCulture)) < .0005);
            button.Appearance = selected ? ControlAppearance.Primary : ControlAppearance.Secondary;
        }
    }
}
