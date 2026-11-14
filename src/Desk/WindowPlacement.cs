namespace Desk;

/// <summary>Where the settings window was when it was closed, to open it there again.</summary>
internal sealed class WindowPlacement
{
    /// <summary>
    /// The top-left corner when not maximized, in screen pixels; null when
    /// the window was never shown un-maximized.
    /// </summary>
    public int? X { get; set; }

    public int? Y { get; set; }

    /// <summary>The size when not maximized, in device-independent units (1/96 inch): the same at any scaling.</summary>
    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }

    /// <summary>The page that was open, by its class ("TaskbarPage"); an unknown one opens the overview.</summary>
    public string? Page { get; set; }
}

/// <summary>A screen's work area in pixels, and its scaling (1 is 96 DPI, 1.5 is 150%).</summary>
internal readonly record struct WorkArea(int Left, int Top, int Width, int Height, double Scale)
{
    public bool Contains(int x, int y) => x >= Left && x < Left + Width && y >= Top && y < Top + Height;
}

/// <summary>Where a window opens again, given the screens there are now.</summary>
internal readonly record struct WindowFit(int X, int Y, double Width, double Height, double Scale);

internal static class WindowPlacementRules
{
    /// <summary>
    /// The place to open the window again: on the work area its title bar
    /// was on, moved and shrunk to fit inside it (the screen may have become
    /// smaller, or its scaling larger). Null when no work area holds the
    /// title bar any more (a screen was unplugged), or the values make no
    /// sense: then the window opens where it would the first time.
    /// </summary>
    public static WindowFit? Fit(WindowPlacement saved, IReadOnlyList<WorkArea> areas)
    {
        if (saved.X is not { } left || saved.Y is not { } top) return null;
        if (!double.IsFinite(saved.Width) || !double.IsFinite(saved.Height) || saved.Width < 200 || saved.Height < 150) return null;
        // A point of the title bar, next to the icon: the window can be dragged by it.
        if (areas.Where(a => a.Contains(left + 24, top + 16)).Cast<WorkArea?>().FirstOrDefault() is not { } area) return null;
        double scale = area.Scale > 0 ? area.Scale : 1;
        double width = Math.Min(saved.Width, area.Width / scale);
        double height = Math.Min(saved.Height, area.Height / scale);
        int pixelWidth = (int)Math.Round(width * scale), pixelHeight = (int)Math.Round(height * scale);
        int x = Math.Max(area.Left, Math.Min(left, area.Left + area.Width - pixelWidth));
        int y = Math.Max(area.Top, Math.Min(top, area.Top + area.Height - pixelHeight));
        return new WindowFit(x, y, width, height, scale);
    }
}
