using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Desk;

/// <summary>
/// Starts Desk when the user signs in. A scheduled task with a logon trigger
/// runs right away, while programs in the Run key are started by Explorer
/// only after a delay, so the task is preferred and the Run key is the
/// fallback when the task can't be created.
/// </summary>
internal static class Autostart
{
    public const string Argument = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Desk";

    private static readonly object Lock = new();

    /// <summary>Last known state; refreshed by <see cref="Refresh"/>.</summary>
    public static bool IsEnabled { get; private set; }

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Desk.exe");

    private static string TaskName
    {
        get
        {
            // Task names are global to the machine, so include the user.
            var name = new StringBuilder();
            foreach (char c in Environment.UserName)
            {
                name.Append("\\/:*?\"<>|".Contains(c) ? '_' : c);
            }
            return $"Desk Autostart ({name})";
        }
    }

    /// <summary>
    /// Makes sure autostart is set up the way it should be: on by default the
    /// first time, pointing at this Desk.exe, and using the scheduled task.
    /// Slow (starts schtasks.exe); call it off the UI thread. Updates the
    /// autostart fields of the settings, which the caller saves.
    /// </summary>
    public static void Refresh(Settings settings)
    {
        lock (Lock)
        {
            bool task = TaskExists();
            bool runKey = RunKeyExists();
            IsEnabled = task || runKey;

            if (!settings.AutostartConfigured)
            {
                Log.Info("Turning on autostart (first run)");
                EnableCore(settings);
                settings.AutostartConfigured = true;
            }
            else if (IsEnabled && (!task ||
                                   !string.Equals(settings.AutostartPath, ExePath, StringComparison.OrdinalIgnoreCase)))
            {
                // Desk.exe moved, or autostart still uses the Run key.
                Log.Info("Updating autostart");
                EnableCore(settings);
            }
        }
    }

    /// <summary>Turns autostart on or off. Slow; call it off the UI thread, then save the settings.</summary>
    public static void Set(Settings settings, bool enabled)
    {
        lock (Lock)
        {
            if (enabled)
            {
                EnableCore(settings);
            }
            else
            {
                RunSchtasks("/Delete", "/TN", TaskName, "/F");
                DeleteRunKey();
                IsEnabled = false;
                Log.Info("Autostart turned off");
            }
            settings.AutostartConfigured = true;
        }
    }

    private static void EnableCore(Settings settings)
    {
        if (CreateTask())
        {
            DeleteRunKey();
            Log.Info($"Autostart: scheduled task '{TaskName}'");
        }
        else
        {
            WriteRunKey();
            Log.Warn("Autostart: the scheduled task could not be created, using the Run key");
        }
        settings.AutostartPath = ExePath;
        IsEnabled = true;
    }

    private static bool TaskExists() => RunSchtasks("/Query", "/TN", TaskName) == 0;

    private static bool CreateTask()
    {
        string file = Path.Combine(Path.GetTempPath(), $"desk-autostart-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, TaskXml(), Encoding.Unicode);
            return RunSchtasks("/Create", "/TN", TaskName, "/XML", file, "/F") == 0;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not create the autostart task: {e.Message}");
            return false;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }
    }

    private static string TaskXml()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string user = SecurityElement.Escape(identity.Name);
        string sid = SecurityElement.Escape(identity.User?.Value ?? identity.Name);
        string command = SecurityElement.Escape($"\"{ExePath}\"");
        string directory = SecurityElement.Escape(Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory);

        // Priority 4 is normal priority; tasks default to below normal.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Desk</Author>
                <Description>Starts Desk when you sign in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{Argument}</Arguments>
                  <WorkingDirectory>{directory}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static int RunSchtasks(params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
            using var process = Process.Start(info);
            if (process == null)
            {
                return -1;
            }
            // Drain the output so that schtasks can't block on a full pipe.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                return -1;
            }
            Task.WaitAll(output, error);
            return process.ExitCode;
        }
        catch (Exception e)
        {
            Log.Warn($"schtasks {arguments.FirstOrDefault()} failed: {e.Message}");
            return -1;
        }
    }

    private static bool RunKeyExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private static void WriteRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(RunValue, $"\"{ExePath}\" {Argument}");
        }
        catch (Exception e)
        {
            Log.Warn($"Could not write the Run key: {e.Message}");
        }
    }

    private static void DeleteRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception e)
        {
            Log.Warn($"Could not remove the Run key: {e.Message}");
        }
    }
}
