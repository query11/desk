namespace Desk;

/// <summary>How a part (taskbar, Start menu) is doing, from good to bad: the order picks the worst.</summary>
internal enum PartHealth
{
    Fine,
    Busy,
    Problem,
}

/// <summary>The state the overview shows next to its title.</summary>
internal enum OverallState
{
    Unavailable,
    Off,
    Problem,
    Busy,
    Running,
}

/// <summary>
/// The label and the line under the overview's title. They say the same
/// thing: the line used to read "已启用" next to a label saying the engine
/// can't run. The switch beside them shows the user's choice, not this.
/// </summary>
internal static class OverviewStatus
{
    public static PartHealth HealthOf(EngineStatus status) => status switch
    {
        EngineStatus.Active or EngineStatus.EffectsReduced or EngineStatus.Waiting or EngineStatus.WaitingForUi
            or EngineStatus.Outdated or EngineStatus.Off => PartHealth.Fine,
        EngineStatus.Connecting or EngineStatus.Initializing or EngineStatus.Applying => PartHealth.Busy,
        _ => PartHealth.Problem,
    };

    public static OverallState Of(bool engineError, bool enabled, PartHealth taskbar, PartHealth startMenu) =>
        engineError ? OverallState.Unavailable
        : !enabled ? OverallState.Off
        : (PartHealth)Math.Max((int)taskbar, (int)startMenu) switch
        {
            PartHealth.Problem => OverallState.Problem,
            PartHealth.Busy => OverallState.Busy,
            _ => OverallState.Running,
        };

    public static string Label(OverallState state) => state switch
    {
        OverallState.Unavailable => "无法运行",
        OverallState.Off => "已停用",
        OverallState.Problem => "需要注意",
        OverallState.Busy => "正在连接",
        _ => "运行中",
    };

    /// <summary>What is going on, and what to do next.</summary>
    public static string Summary(OverallState state) => state switch
    {
        OverallState.Unavailable => "美化引擎没有启动，任务栏和开始菜单保持系统原样。原因和处理办法见下方的提示。",
        OverallState.Off => "任务栏和开始菜单现在是系统原样。打开右边的开关，就会换回这套外观。",
        OverallState.Problem => "有一部分外观没有生效。原因写在下方的状态卡片里，可以在「维护与恢复」中重新应用。",
        OverallState.Busy => "正在把外观应用到任务栏和开始菜单，通常几秒内完成。",
        _ => "美化正在运行。各部分的生效情况见下方，想换一种风格可以浏览外观方案。",
    };
}
