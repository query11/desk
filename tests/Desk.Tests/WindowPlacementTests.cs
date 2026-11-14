using Xunit;

namespace Desk.Tests;

public class WindowPlacementTests
{
    // A 1920 × 1080 screen with the taskbar at the bottom, and a 150% one to its right.
    private static readonly WorkArea Primary = new(0, 0, 1920, 1032, 1);
    private static readonly WorkArea Right = new(1920, 0, 2560, 1392, 1.5);

    private static WindowPlacement Saved(int x, int y, double width = 1040, double height = 800) =>
        new() { X = x, Y = y, Width = width, Height = height };

    [Fact]
    public void AWindowThatStillFitsOpensWhereItWas()
    {
        var fit = WindowPlacementRules.Fit(Saved(20, 20, 1040, 1000), [Primary, Right]);
        Assert.Equal(new WindowFit(20, 20, 1040, 1000, 1), fit);
        // On the scaled screen the size is in its units: 1040 × 800 is 1560 × 1200 pixels.
        Assert.Equal(new WindowFit(2400, 100, 1040, 800, 1.5), WindowPlacementRules.Fit(Saved(2400, 100), [Primary, Right]));
    }

    [Fact]
    public void AScreenThatIsGoneMeansTheDefaultPlace()
    {
        // Closed on the right screen, which was unplugged since.
        Assert.Null(WindowPlacementRules.Fit(Saved(2400, 100), [Primary]));
        // Never shown un-maximized: no place to go back to.
        Assert.Null(WindowPlacementRules.Fit(new WindowPlacement { Width = 1040, Height = 800, Maximized = true }, [Primary]));
        Assert.Null(WindowPlacementRules.Fit(Saved(20, 20, double.NaN), [Primary]));
    }

    [Fact]
    public void AWindowPartlyOffItsScreenIsMovedBackOn()
    {
        // The title bar is on the screen, the rest hangs below and to the right.
        var fit = WindowPlacementRules.Fit(Saved(1500, 600), [Primary])!.Value;
        Assert.Equal((1920 - 1040, 1032 - 800), (fit.X, fit.Y));
        // The title bar off the top of every screen: default.
        Assert.Null(WindowPlacementRules.Fit(Saved(100, -40), [Primary]));
    }

    [Fact]
    public void AWindowLargerThanItsScreenNowIsMadeSmaller()
    {
        // Scaling went up from 100% to 150% on a 1920 × 1032 work area: 1280 × 688 units.
        var fit = WindowPlacementRules.Fit(Saved(20, 20, 1160, 840), [Primary with { Scale = 1.5 }])!.Value;
        Assert.Equal(1160, fit.Width);
        Assert.Equal(688, fit.Height);
        Assert.Equal(0, fit.Y);
    }

    [Fact]
    public void SettingsKeepThePlacementAndOldFilesHaveNone()
    {
        using var dir = new TempDir();
        string file = dir.File("settings.json");
        File.WriteAllText(file, "{ \"Enabled\": true, \"Theme\": \"desk-air\" }");
        Assert.Null(Settings.Load(file).Window);
        var settings = new Settings { Window = new WindowPlacement { X = -1900, Y = 40, Width = 900.5, Height = 700, Maximized = true, Page = "TaskbarPage" } };
        Assert.True(settings.Save(file));
        var loaded = Settings.Load(file).Window!;
        Assert.Equal((-1900, 40, 900.5, 700.0, true, "TaskbarPage"), (loaded.X, loaded.Y, loaded.Width, loaded.Height, loaded.Maximized, loaded.Page));
    }
}
