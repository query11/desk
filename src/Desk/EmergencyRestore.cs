namespace Desk;

/// <summary>What the emergency restore does to the system; replaced in tests.</summary>
internal interface IRestoreEnvironment
{
    /// <summary>Whether a Desk.exe (the tray app) is running right now.</summary>
    bool HostRunning();

    /// <summary>Asks the running Desk.exe to restore and quit; true once it is gone.</summary>
    bool AskHostToExit(TimeSpan timeout);

    /// <summary>The running Desk.exe according to its heartbeat, if known.</summary>
    HostLease? FindHost();

    /// <summary>Ends a Desk.exe that doesn't respond; true once it is gone.</summary>
    bool TerminateHost(HostLease host, TimeSpan timeout);

    /// <summary>Writes configurations that turn styling off, owned by nobody.</summary>
    bool WriteDisabledConfigurations();

    AutoHideRestore RestoreAutoHide();

    /// <summary>Saves "styling off" in the settings, so the next start doesn't style again.</summary>
    bool TurnStylingOff();
}

internal sealed record RestoreReport(
    bool HostWasRunning,
    bool HostExited,
    bool HostTerminated,
    bool ConfigurationsWritten,
    AutoHideRestore AutoHide,
    bool SettingSaved)
{
    public bool Succeeded => ConfigurationsWritten && (!HostWasRunning || HostExited || HostTerminated) &&
                             AutoHide != AutoHideRestore.Failed;

    public string Describe()
    {
        var lines = new List<string>();
        if (HostWasRunning)
        {
            lines.Add(HostExited ? "已让正在运行的 Desk 还原并退出。"
                : HostTerminated ? "正在运行的 Desk 没有响应，已将它结束。"
                : "正在运行的 Desk 没有响应，也无法结束它。");
        }
        lines.Add(ConfigurationsWritten
            ? "已通知任务栏和开始菜单恢复原样（引擎会在几秒内还原）。"
            : "无法写入还原配置，请检查 %LOCALAPPDATA%\\Desk 的访问权限。");
        lines.Add(AutoHide switch
        {
            AutoHideRestore.Restored => "已关闭 Desk 打开的任务栏自动隐藏。",
            AutoHideRestore.AlreadyRestored => "任务栏自动隐藏已经是你设置的状态。",
            AutoHideRestore.Failed => "任务栏自动隐藏没能恢复，可以在系统设置里手动关闭。",
            _ => "",
        });
        lines.Add(SettingSaved ? "美化已停用，下次启动 Desk 时需要手动重新启用。" : "停用美化的设置没能保存。");
        return string.Join(Environment.NewLine, lines.Where(l => l.Length > 0));
    }
}

/// <summary>
/// <c>Desk.exe --restore</c>: puts the taskbar and the Start menu back
/// without the settings window or the tray, also when Desk.exe hangs or
/// isn't running.
/// </summary>
/// <remarks>
/// - Desk.exe running: asked to restore and quit (as from its tray menu).
/// - Desk.exe hung: ended, after checking (process id and creation time
///   from its heartbeat) that it is the Desk.exe in question.
/// - Then, in every case: configurations that turn styling off and name no
///   owner, which every engine (also an older build) applies; the auto-hide
///   record is undone; styling is saved as off.
/// Running it again changes nothing that is already restored.
/// </remarks>
internal static class EmergencyRestore
{
    public const string Argument = "--restore";

    public static RestoreReport Run(IRestoreEnvironment environment)
    {
        bool running = environment.HostRunning();
        bool exited = false;
        bool terminated = false;
        if (running)
        {
            Log.Info("Restore: asking the running Desk to restore and quit");
            exited = environment.AskHostToExit(TimeSpan.FromSeconds(5));
            if (!exited)
            {
                var host = environment.FindHost();
                Log.Warn($"Restore: Desk doesn't respond; ending it ({(host == null ? "not found" : $"pid {host.ProcessId}")})");
                terminated = host != null && environment.TerminateHost(host, TimeSpan.FromSeconds(3));
            }
        }

        bool written = environment.WriteDisabledConfigurations();
        var autoHide = environment.RestoreAutoHide();
        bool saved = environment.TurnStylingOff();
        var report = new RestoreReport(running, exited, terminated, written, autoHide, saved);
        Log.Info($"Restore finished: {report}");
        return report;
    }
}
