using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Desk.Ui;

internal sealed record WallpaperResult(ImageSource? Image, string? Error);

internal static class WallpaperPreview
{
    private static string? _cachedKey;
    private static ImageSource? _cachedImage;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(uint action, uint length, StringBuilder value, uint flags);

    public static Task<WallpaperResult> LoadAsync() => Task.Run(() =>
    {
        try
        {
            var path = new StringBuilder(32768);
            SystemParametersInfoW(0x0073, (uint)path.Capacity, path, 0);
            string file = path.ToString();
            if (!File.Exists(file)) file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
            if (!File.Exists(file)) return new WallpaperResult(null, "当前没有可读取的图片壁纸，已使用示例背景。");
            string key = file + File.GetLastWriteTimeUtc(file).Ticks;
            lock (typeof(WallpaperPreview))
            {
                if (_cachedKey == key && _cachedImage != null) return new WallpaperResult(_cachedImage, null);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 1600; image.StreamSource = stream; image.EndInit(); image.Freeze();
                _cachedImage = image; _cachedKey = key;
                return new WallpaperResult(image, null);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return new WallpaperResult(null, "壁纸暂时无法读取，已使用示例背景。");
        }
    });
}
