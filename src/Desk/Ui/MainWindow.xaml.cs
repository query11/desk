using Desk.Ui.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Desk.Ui;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        // Follow light/dark changes of Windows while the window is open.
        SystemThemeWatcher.Watch(this);
        Loaded += (_, _) => Navigation.Navigate(typeof(HomePage));
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
}
