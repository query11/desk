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
        Loaded += (_, _) => Navigation.Navigate(typeof(HomePage));
        SizeChanged += (_, e) =>
        {
            bool wide = e.NewSize.Width >= CompactWidth;
            if (Navigation.IsPaneOpen != wide)
            {
                Navigation.IsPaneOpen = wide;
            }
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
}
