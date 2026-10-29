using System.Globalization;
using Wpf.Ui.Controls;

namespace Desk.Ui;

/// <summary>A theme constant that the settings window offers as a slider.</summary>
internal sealed record ParameterInfo(
    string Name,
    string Title,
    string Description,
    SymbolRegular Icon,
    double Minimum,
    double Maximum,
    double Step,
    bool PerMode,
    Func<double, string> Format);

/// <summary>
/// The constants of the built-in themes that can be tuned. Material values
/// that depend on the tint (light or dark) are kept per Windows mode.
/// </summary>
internal static class Parameters
{
    public static readonly IReadOnlyList<ParameterInfo> Taskbar =
    [
        new("radius", "圆角", "浮岛四个角的圆润程度。", SymbolRegular.BorderOutside24, 0, 20, 1, false, Pixels),
        new("gap", "上下留白", "浮岛和任务栏上下边缘的距离，越大越像“浮”起来。", SymbolRegular.ArrowAutofitHeight24, 0, 8, 1, false, Pixels),
        new("islandBlur", "模糊", "背后壁纸和窗口的模糊程度。", SymbolRegular.Blur24, 0, 60, 1, false, Plain),
        new("islandOpacity", "着色浓度", "越高越接近纯色，越低越透明。", SymbolRegular.Color24, 0, 1, 0.01, true, Percent),
        new("islandLuminosity", "明暗统一", "越高文字越清晰、越不受壁纸明暗影响；越低越能看出背后的明暗。", SymbolRegular.BrightnessHigh24, 0, 1, 0.01, true, Percent),
        new("islandSaturation", "色彩饱和度", "壁纸透过来的颜色有多鲜艳。", SymbolRegular.Drop24, 0.5, 2.5, 0.05, false, Percent),
        new("islandNoise", "颗粒感", "像真实亚克力材质那样的细微颗粒。", SymbolRegular.Sparkle24, 0, 0.1, 0.005, false, Percent),
    ];

    public static readonly IReadOnlyList<ParameterInfo> StartMenu =
    [
        new("menuRadius", "圆角", "开始菜单四个角的圆润程度。", SymbolRegular.BorderOutside24, 0, 20, 1, false, Pixels),
        new("menuBlur", "模糊", "背后壁纸和窗口的模糊程度。越低越通透。", SymbolRegular.Blur24, 0, 60, 1, false, Plain),
        new("menuOpacity", "着色浓度", "越高越接近纯色，越低越透明。", SymbolRegular.Color24, 0, 1, 0.01, true, Percent),
        new("menuLuminosity", "明暗统一", "越高文字越清晰、越不受壁纸明暗影响；越低越能看出背后的明暗。", SymbolRegular.BrightnessHigh24, 0, 1, 0.01, true, Percent),
        new("menuSaturation", "色彩饱和度", "壁纸透过来的颜色有多鲜艳。", SymbolRegular.Drop24, 0.5, 2.5, 0.05, false, Percent),
        new("menuNoise", "颗粒感", "像真实亚克力材质那样的细微颗粒。", SymbolRegular.Sparkle24, 0, 0.1, 0.005, false, Percent),
    ];

    private static string Pixels(double value) => value.ToString("0", CultureInfo.InvariantCulture) + " px";

    private static string Plain(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static string Percent(double value) => (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
