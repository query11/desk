using System.Globalization;
using System.Text;

namespace Desk;

/// <summary>How a parameter's number is shown next to its slider.</summary>
internal enum ParameterUnit
{
    /// <summary>"28 px".</summary>
    Pixels,

    /// <summary>"18".</summary>
    Plain,

    /// <summary>"35%": the value 0.35.</summary>
    Percent,
}

/// <summary>
/// The number next to a slider, shown and typed in the same way: in the
/// slider's units (for 透明程度 that is the transparency, not the stored tint).
/// </summary>
internal static class ParameterValue
{
    public static string Format(double value, ParameterUnit unit) => unit switch
    {
        ParameterUnit.Pixels => value.ToString("0", CultureInfo.InvariantCulture) + " px",
        ParameterUnit.Percent => (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%",
        _ => value.ToString("0", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// What was typed, in the slider's units: "12", "12 px", "12px"; "35",
    /// "35%" and "35 %" are 35 percent. Full-width digits and signs from a
    /// Chinese input method count too. False for anything else.
    /// </summary>
    public static bool TryParse(string? text, ParameterUnit unit, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string plain = HalfWidth(text).Trim();
        string suffix = unit switch { ParameterUnit.Pixels => "px", ParameterUnit.Percent => "%", _ => "" };
        if (suffix.Length > 0 && plain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            plain = plain[..^suffix.Length].TrimEnd();
        if (!double.TryParse(plain, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            return false;
        value = unit == ParameterUnit.Percent ? number / 100 : number;
        return true;
    }

    /// <summary>A typed value made one the slider can take: within its range, on its steps.</summary>
    public static double Settle(double value, double minimum, double maximum, double step)
    {
        double clamped = Math.Clamp(value, minimum, maximum);
        if (step <= 0) return clamped;
        double steps = Math.Round((clamped - minimum) / step, MidpointRounding.AwayFromZero);
        // Rounding the sum: 0.1 + 3 × 0.05 should be 0.25, not 0.25000000000000006.
        return Math.Clamp(Math.Round(minimum + steps * step, 6), minimum, maximum);
    }

    private static string HalfWidth(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            // Ｆｕｌｌ－ｗｉｄｔｈ forms (U+FF01–U+FF5E) and the ideographic space.
            result.Append(c switch
            {
                >= '！' and <= '～' => (char)(c - 0xFEE0),
                '　' => ' ',
                '。' => '.',
                _ => c,
            });
        }
        return result.ToString();
    }
}
