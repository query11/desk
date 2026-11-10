using System.Windows.Controls;

namespace Desk.Ui.Pages;

public sealed class AppearancePage : Page
{
    public AppearancePage()
    {
        Title = "外观方案";
        var app = UiHost.App;
        var gallery = new ThemeGallery(app, app.SystemLightMode) { MaxWidth = 1000 };
        Content = gallery;
        Loaded += (_, _) => { app.StateChanged += gallery.Refresh; gallery.Refresh(); };
        Unloaded += (_, _) => app.StateChanged -= gallery.Refresh;
    }
}
