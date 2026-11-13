using System.Windows.Controls;

namespace Desk.Ui.Pages;

public sealed class AppearancePage : Page
{
    // The page is built again on every visit; the gallery's filter, preview
    // choices and the way back to the previous theme live as long as Desk does.
    private static readonly GalleryState State = new() { UseWallpaper = true };

    public AppearancePage()
    {
        Title = "外观方案";
        var app = UiHost.App;
        var gallery = new ThemeGallery(app, app.SystemLightMode, State)
        {
            MaxWidth = 1000,
            MonitorItems = () => app.MonitorItems,
        };
        Content = gallery;
        void Refresh()
        {
            gallery.SystemLight = app.SystemLightMode;
            gallery.Refresh();
        }
        Loaded += (_, _) => { app.StateChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => app.StateChanged -= Refresh;
    }
}
