using System.Windows;
using Desk.Ui.Pages;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Desk.Ui;

public partial class MainWindow : FluentWindow
{
    /// <summary>Below this width the navigation pane folds to icons, leaving room for the cards.</summary>
    private const double CompactWidth = 960;

    private readonly SnackbarService _snackbars = new() { DefaultTimeOut = TimeSpan.FromSeconds(3) };
    private readonly ContentDialogService _dialogs = new();

    public MainWindow()
    {
        InitializeComponent();
        _snackbars.SetSnackbarPresenter(Snackbars);
        _dialogs.SetDialogHost(Dialogs);

        // Follow light/dark changes of Windows while the window is open.
        SystemThemeWatcher.Watch(this);
        Loaded += (_, _) =>
        {
            Navigation.Navigate(typeof(HomePage));
            UiHost.App.TrialChanged += RefreshTrial;
            RefreshTrial();
        };
        Closed += (_, _) => { if (UiHost.App != null) UiHost.App.TrialChanged -= RefreshTrial; };
        KeepTrialButton.Click += (_, _) => TrialAction(() => UiHost.App.KeepThemeTrial());
        CancelTrialButton.Click += (_, _) => TrialAction(() => UiHost.App.CancelThemeTrial());
        SizeChanged += (_, e) =>
        {
            UpdateResponsiveLayout(e.NewSize.Width);
        };
        Closing += (_, _) =>
        {
            try
            {
                SystemThemeWatcher.UnWatch(this);
            }
            catch (InvalidOperationException)
            {
                // Not loaded yet; nothing to undo.
            }
        };
    }

    private void RefreshTrial()
    {
        TrialBanner.Visibility = UiHost.App.PendingTrial == null ? Visibility.Collapsed : Visibility.Visible;
        TrialText.Text = $"正在试用「{UiHost.App.ThemeName}」 · {UiHost.App.TrialSecondsLeft} 秒后自动还原";
    }

    private void TrialAction(Action action)
    {
        try { action(); }
        catch (Exception e) { UiHost.Toast("操作未完成", e.Message, SymbolRegular.Warning24); }
    }

    internal async Task ShowContentAsync(string title, object content)
    {
        var dialog = new ContentDialog { Title = title, Content = content, CloseButtonText = "关闭", DialogMaxWidth = 640 };
        await _dialogs.ShowAsync(dialog, CancellationToken.None);
    }

    internal void UpdateResponsiveLayout(double width)
    {
        bool wide = width >= CompactWidth;
        Navigation.PaneDisplayMode = wide ? NavigationViewPaneDisplayMode.Left : NavigationViewPaneDisplayMode.LeftMinimal;
        Navigation.IsPaneOpen = wide;
    }

    internal void Toast(string title, string message, SymbolRegular icon, ControlAppearance appearance) =>
        _snackbars.Show(title, message, appearance, new SymbolIcon { Symbol = icon }, TimeSpan.Zero);

    internal async Task<bool> ConfirmAsync(string title, string message, string confirm, bool dangerous)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirm,
            PrimaryButtonAppearance = dangerous ? ControlAppearance.Danger : ControlAppearance.Primary,
            CloseButtonText = "取消",
            // Enter must not do the irreversible thing by accident.
            DefaultButton = ContentDialogButton.Close,
            DialogMaxWidth = 460,
        };
        return await _dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }

    internal async Task<string?> PromptNameAsync(string title, string message, string initial)
    {
        var input = new Wpf.Ui.Controls.TextBox { Text = initial, MaxLength = 60, Margin = new Thickness(0, 12, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(input, "AppearanceName");
        var content = new System.Windows.Controls.StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input);
        var dialog = new ContentDialog
        {
            Title = title, Content = content, PrimaryButtonText = "保存", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary, DialogMaxWidth = 460,
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return await _dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }
}
