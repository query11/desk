using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Desk;

/// <summary>Autostart through schtasks.exe and the HKCU Run key.</summary>
internal sealed class WindowsAutostartBackend : IAutostartBackend
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Desk";
    private static readonly TimeSpan SchtasksTimeout = TimeSpan.FromSeconds(15);
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private readonly Encoding _consoleEncoding;

    public WindowsAutostartBackend()
    {
        // schtasks writes in the console code page.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            _consoleEncoding = Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            _consoleEncoding = Encoding.Default;
        }
    }

    public string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Desk.exe");

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

    public bool StartsThisExe(string command)
    {
        string? path = ExtractPath(command);
        if (path == null)
        {
            return false;
        }
        // The task's command comes through the console code page, which may
        // not hold every character of the path; compare it the same way.
        string lossy = _consoleEncoding.GetString(_consoleEncoding.GetBytes(ExePath));
        return SamePath(path, ExePath) || SamePath(path, lossy);
    }

    public TaskInfo QueryTask()
    {
        var (code, output, error) = RunSchtasks("/Query", "/TN", TaskName, "/XML");
        if (code == 0)
        {
            try
            {
                var document = XDocument.Parse(output);
                var root = document.Root!;
                bool enabled = (string?)root.Element(TaskNs + "Settings")?.Element(TaskNs + "Enabled") != "false";
                var trigger = root.Element(TaskNs + "Triggers")?.Element(TaskNs + "LogonTrigger");
                if ((string?)trigger?.Element(TaskNs + "Enabled") == "false")
                {
                    enabled = false;
                }
                string? command = (string?)root.Element(TaskNs + "Actions")?.Element(TaskNs + "Exec")
                    ?.Element(TaskNs + "Command");
                return new TaskInfo(Presence.Present, enabled, command);
            }
            catch (Exception e)
            {
                return new TaskInfo(Presence.Unknown, Error: $"任务内容无法读取：{e.Message}");
            }
        }

        // The query fails both for a missing task and for other reasons, and
        // its message is localized. A listing that works and doesn't contain
        // the task tells them apart.
        var (listCode, listing, listError) = RunSchtasks("/Query", "/FO", "CSV", "/NH");
        if (listCode != 0)
        {
            return new TaskInfo(Presence.Unknown, Error: Describe("schtasks /Query", code, error) +
                                                         "，" + Describe("schtasks /Query /FO CSV", listCode, listError));
        }
        string wanted = "\\" + TaskName;
        foreach (string line in listing.Split('\n'))
        {
            string first = line.Trim().Split(',')[0].Trim('"');
            if (string.Equals(first, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return new TaskInfo(Presence.Unknown, Error: Describe("schtasks /Query /XML", code, error));
            }
        }
        return TaskInfo.Missing;
    }

    public Outcome CreateTask()
    {
        string file = Path.Combine(Path.GetTempPath(), $"desk-autostart-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, TaskXml(), Encoding.Unicode);
            var (code, _, error) = RunSchtasks("/Create", "/TN", TaskName, "/XML", file, "/F");
            return code == 0 ? Outcome.Success : Outcome.Failure(Describe("schtasks /Create", code, error));
        }
        catch (Exception e)
        {
            return Outcome.Failure(e.Message);
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

    public Outcome DeleteTask()
    {
        var (code, _, error) = RunSchtasks("/Delete", "/TN", TaskName, "/F");
        return code == 0 ? Outcome.Success : Outcome.Failure(Describe("schtasks /Delete", code, error));
    }

    public RunKeyInfo QueryRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string value
                ? new RunKeyInfo(Presence.Present, value)
                : RunKeyInfo.Missing;
        }
        catch (Exception e)
        {
            return new RunKeyInfo(Presence.Unknown, Error: e.Message);
        }
    }

    public Outcome WriteRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(RunValue, $"\"{ExePath}\" {Autostart.Argument}");
            return Outcome.Success;
        }
        catch (Exception e)
        {
            return Outcome.Failure(e.Message);
        }
    }

    public Outcome DeleteRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
            return Outcome.Success;
        }
        catch (Exception e)
        {
            return Outcome.Failure(e.Message);
        }
    }

    /// <summary>The program of a command line: the quoted part, or up to the first space.</summary>
    internal static string? ExtractPath(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            return null;
        }
        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Describe(string what, int code, string error)
    {
        string message = error.Trim();
        return code == -1 && message.Length == 0
            ? $"{what} 没有完成"
            : $"{what} 返回 {code}{(message.Length > 0 ? "：" + message : "")}";
    }

    private string TaskXml()
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
                  <Arguments>{Autostart.Argument}</Arguments>
                  <WorkingDirectory>{directory}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Runs schtasks.exe; the exit code (-1 when it couldn't run or timed out) and its output.</summary>
    private (int Code, string Output, string Error) RunSchtasks(params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = _consoleEncoding,
                StandardErrorEncoding = _consoleEncoding,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
            using var process = Process.Start(info);
            if (process == null)
            {
                return (-1, "", "schtasks.exe 无法启动");
            }
            // Drain the output so that schtasks can't block on a full pipe.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(SchtasksTimeout))
            {
                process.Kill();
                return (-1, "", "schtasks.exe 超时");
            }
            Task.WaitAll(output, error);
            return (process.ExitCode, output.Result, error.Result);
        }
        catch (Exception e)
        {
            Log.Warn($"schtasks {arguments.FirstOrDefault()} failed: {e.Message}");
            return (-1, "", e.Message);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
