using Xunit;

namespace Desk.Tests;

public class ParameterValueTests
{
    private static double? Parse(string text, ParameterUnit unit) =>
        ParameterValue.TryParse(text, unit, out double value) ? value : null;

    [Fact]
    public void TypedNumbersAreInTheSlidersUnits()
    {
        Assert.Equal(12, Parse("12", ParameterUnit.Pixels));
        Assert.Equal(12, Parse("12 px", ParameterUnit.Pixels));
        Assert.Equal(12.5, Parse(" 12.5PX ", ParameterUnit.Pixels));
        Assert.Equal(.35, Parse("35%", ParameterUnit.Percent)!.Value, 6);
        Assert.Equal(.35, Parse("35 %", ParameterUnit.Percent)!.Value, 6);
        // Without the sign it is still a percentage: what the box shows.
        Assert.Equal(.35, Parse("35", ParameterUnit.Percent)!.Value, 6);
        Assert.Equal(18, Parse("18", ParameterUnit.Plain));
    }

    [Fact]
    public void FullWidthInputFromAChineseInputMethodCounts()
    {
        Assert.Equal(.8, Parse("８０％", ParameterUnit.Percent)!.Value, 6);
        Assert.Equal(12.5, Parse("１２。５ｐｘ", ParameterUnit.Pixels));
    }

    [Theory]
    [InlineData("")]
    [InlineData("很透")]
    [InlineData("12 em")]
    [InlineData("NaN")]
    [InlineData("1e400")]
    public void AnythingElseIsNotANumber(string text) => Assert.Null(Parse(text, ParameterUnit.Pixels));

    [Fact]
    public void TypedValuesLandOnTheSlider()
    {
        // Icon size: 24–36 in steps of 2.
        Assert.Equal(28, ParameterValue.Settle(27, 24, 36, 2));
        Assert.Equal(36, ParameterValue.Settle(40, 24, 36, 2));
        Assert.Equal(24, ParameterValue.Settle(-3, 24, 36, 2));
        // Grain: 0–0.1 in steps of 0.005.
        Assert.Equal(.01, ParameterValue.Settle(.0123, 0, .1, .005));
        // Saturation: 0.5–2.5 in steps of 0.05, without float noise.
        Assert.Equal(1.25, ParameterValue.Settle(1.2499, .5, 2.5, .05));
    }

    [Fact]
    public void ShownTheWayItIsTyped()
    {
        Assert.Equal("28 px", ParameterValue.Format(28, ParameterUnit.Pixels));
        Assert.Equal("12.5%", ParameterValue.Format(.125, ParameterUnit.Percent));
        Assert.Equal("18", ParameterValue.Format(18, ParameterUnit.Plain));
        // What the box shows can be typed back as it is.
        Assert.Equal(28, Parse(ParameterValue.Format(28, ParameterUnit.Pixels), ParameterUnit.Pixels));
        Assert.Equal(18, Parse(ParameterValue.Format(18, ParameterUnit.Plain), ParameterUnit.Plain));
        Assert.Equal(.125, Parse(ParameterValue.Format(.125, ParameterUnit.Percent), ParameterUnit.Percent)!.Value, 6);
    }
}
