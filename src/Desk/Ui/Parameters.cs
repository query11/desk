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
    ParameterUnit Unit,
    bool Advanced = false,
    bool Inverted = false)
{
    /// <summary>A value in the slider's units, as shown next to it.</summary>
    public string Format(double value) => ParameterValue.Format(value, Unit);
}

/// <summary>
/// The constants of the built-in themes that can be tuned. Material values
/// that depend on the tint (light or dark) are kept per Windows mode.
/// </summary>
internal static class Parameters
{
    private const ParameterUnit Pixels = ParameterUnit.Pixels;
    private const ParameterUnit Plain = ParameterUnit.Plain;
    private const ParameterUnit Percent = ParameterUnit.Percent;

    public static readonly IReadOnlyList<ParameterInfo> Taskbar =
    [
        new("iconSize", "图标大小", "只调整图标，推荐 28 px。", SymbolRegular.Apps24, 24, 36, 2, false, Pixels),
        new("islandOpacity", "透明程度", "越高着色越淡。", SymbolRegular.Color24, 0, 1, 0.01, true, Percent, Inverted: true),
        new("buttonWidth", "图标间距", "按钮占位宽度，越大越疏松。", SymbolRegular.ArrowAutofitWidth24, 40, 64, 2, false, Pixels),
        new("gap", "上下留白", "浮岛和任务栏上下边缘的距离。", SymbolRegular.ArrowAutofitHeight24, 0, 8, 1, false, Pixels),
        new("radius", "圆角", "浮岛四个角的圆润程度。", SymbolRegular.BorderOutside24, 0, 28, 1, false, Pixels),
        new("islandBlur", "模糊", "背后壁纸和窗口的模糊程度。", SymbolRegular.Blur24, 0, 60, 1, false, Plain),
        new("islandLuminosity", "明暗统一", "越高越少受到背景明暗影响，也会减弱透明感。", SymbolRegular.BrightnessHigh24, 0, 1, 0.01, true, Percent, Advanced: true),
        new("islandSaturation", "色彩饱和度", "背景颜色的鲜艳程度。", SymbolRegular.Drop24, 0.5, 2.5, 0.05, false, Percent, Advanced: true),
        new("islandNoise", "颗粒感", "亚克力表面的细微颗粒，通常只需要少量。", SymbolRegular.Sparkle24, 0, 0.1, 0.005, false, Percent, Advanced: true),
    ];

    public static readonly IReadOnlyList<ParameterInfo> StartMenu =
    [
        new("menuOpacity", "透明程度", "越高着色越淡。", SymbolRegular.Color24, 0, 1, 0.01, true, Percent, Inverted: true),
        new("menuRadius", "圆角", "开始菜单四个角的圆润程度。", SymbolRegular.BorderOutside24, 0, 32, 1, false, Pixels),
        new("menuBlur", "模糊", "越高背景轮廓越柔和；透明程度由下一项调整。", SymbolRegular.Blur24, 0, 60, 1, false, Plain),
        new("menuLuminosity", "明暗统一", "越高越少受到背景明暗影响，也会减弱透明感。", SymbolRegular.BrightnessHigh24, 0, 1, 0.01, true, Percent, Advanced: true),
        new("menuSaturation", "色彩饱和度", "背景颜色的鲜艳程度。", SymbolRegular.Drop24, 0.5, 2.5, 0.05, false, Percent, Advanced: true),
        new("menuNoise", "颗粒感", "亚克力表面的细微颗粒，通常只需要少量。", SymbolRegular.Sparkle24, 0, 0.1, 0.005, false, Percent, Advanced: true),
    ];
}
