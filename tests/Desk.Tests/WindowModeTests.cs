using Xunit;

namespace Desk.Tests;

public class WindowModeTests
{
    [Fact]
    public void TheWindowFollowsTheAppMode()
    {
        // Only AppsUseLightTheme counts: a dark Windows mode with light apps
        // gives a light window, like other apps (the taskbar stays dark).
        Assert.Equal(WindowMode.Dark, AppMode.Decide(0, highContrast: false));
        Assert.Equal(WindowMode.Light, AppMode.Decide(1, highContrast: false));
    }

    [Fact]
    public void MissingOrOddValuesMeanLight()
    {
        // What Windows does without the value; a value of the wrong type is ignored.
        Assert.Equal(WindowMode.Light, AppMode.Decide(null, highContrast: false));
        Assert.Equal(WindowMode.Light, AppMode.Decide("0", highContrast: false));
    }

    [Fact]
    public void HighContrastWins()
    {
        Assert.Equal(WindowMode.HighContrast, AppMode.Decide(0, highContrast: true));
        Assert.Equal(WindowMode.HighContrast, AppMode.Decide(1, highContrast: true));
    }
}
