using System.Windows.Controls;

namespace Desk.Ui.Pages;

public partial class StartMenuPage : Page
{
    private readonly DeskApp _app = UiHost.App;
    private readonly ParameterSection _parameters;
    private bool _updating;

    public StartMenuPage()
    {
        InitializeComponent();
        _parameters = new ParameterSection(_app, ParameterScope.StartMenu, Parameters.StartMenu,
            ParametersPanel, EmptyHint, ResetButton);

        StyleSwitch.Checked += (_, _) => SetStyled(true);
        StyleSwitch.Unchecked += (_, _) => SetStyled(false);
        SchemeBox.SelectionChanged += (_, _) =>
        {
            if (!_updating && SchemeBox.SelectedIndex >= 0)
            {
                _app.StartMenuScheme = (StartMenuScheme)SchemeBox.SelectedIndex;
            }
        };

        Loaded += (_, _) =>
        {
            _app.StateChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => _app.StateChanged -= Refresh;
    }

    private void SetStyled(bool styled)
    {
        if (!_updating)
        {
            _app.StyleStartMenu = styled;
        }
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            StyleSwitch.IsChecked = _app.StyleStartMenu;
            SchemeBox.SelectedIndex = (int)_app.StartMenuScheme;
            ModeText.Text = _app.StartMenuLightMode
                ? "正在调整浅色玻璃的数值（着色浓度和明暗统一在浅色、深色下分别保存）。"
                : "正在调整深色玻璃的数值（着色浓度和明暗统一在浅色、深色下分别保存）。";
            _parameters.Refresh();
        }
        finally
        {
            _updating = false;
        }
    }
}
