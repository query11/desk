using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Desk.Ui;

/// <summary>Fills a panel with slider cards for theme parameters and keeps them in sync.</summary>
internal sealed class ParameterSection
{
    private readonly DeskApp _app;
    private readonly ParameterScope _scope;
    private readonly IReadOnlyList<ParameterInfo> _parameters;
    private readonly Panel _panel;
    private readonly FrameworkElement _emptyHint;
    private readonly ButtonBase _reset;
    private readonly List<ParameterCard> _cards = [];
    private string? _builtFor;

    public ParameterSection(DeskApp app, ParameterScope scope, IReadOnlyList<ParameterInfo> parameters,
        Panel panel, FrameworkElement emptyHint, ButtonBase reset)
    {
        _app = app;
        _scope = scope;
        _parameters = parameters;
        _panel = panel;
        _emptyHint = emptyHint;
        _reset = reset;
        _reset.Click += (_, _) => _app.ResetParameters(_parameters.Select(p => p.Name));
    }

    public void Refresh()
    {
        // The cards depend on which constants the theme has.
        string key = _app.ThemeId + "|" + string.Join(",", _parameters.Select(p => _app.HasParameter(p.Name)));
        if (key != _builtFor)
        {
            _builtFor = key;
            _cards.Clear();
            _panel.Children.Clear();
            foreach (var parameter in _parameters.Where(p => _app.HasParameter(p.Name)))
            {
                var card = new ParameterCard(parameter);
                card.ValueChanged += (c, value) =>
                {
                    _app.SetParameter(_scope, c.Info.Name, value, c.Info.PerMode);
                    _reset.IsEnabled = true;
                };
                _cards.Add(card);
                _panel.Children.Add(card);
            }
        }

        foreach (var card in _cards)
        {
            if (_app.GetParameter(_scope, card.Info.Name) is { } value)
            {
                card.Show(value);
            }
        }

        _emptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _reset.IsEnabled = _app.HasChangedParameters(_parameters.Select(p => p.Name));
    }
}
