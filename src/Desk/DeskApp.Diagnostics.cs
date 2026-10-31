using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Desk;

/// <summary>The "export diagnostics" action of the settings window.</summary>
internal sealed partial class DeskApp
{
    /// <summary>
    /// Writes the diagnostics package to <paramref name="path"/> in the
    /// background. The summary is taken now, on the UI thread.
    /// </summary>
    public Task<DiagnosticsResult> ExportDiagnosticsAsync(string path)
    {
        var summary = DiagnosticsSummary();
        return Task.Run(() =>
        {
            // What was logged up to now should be in the package.
            Log.Flush();
            var files = DiagnosticsExport.CollectFiles(Paths.DataDir, Paths.SettingsFile);
            return DiagnosticsExport.Write(path, summary, files, Redactor.ForCurrentUser());
        });
    }

    private JsonObject DiagnosticsSummary()
    {
        var summary = new JsonObject
        {
            ["generated"] = DateTime.UtcNow.ToString("O"),
            ["desk"] = Version,
            ["engineDll"] = _dllPath,
            ["engineError"] = _engineError,
            ["hostSession"] = _host.Lease.Session,
            ["runningFor"] = (DateTime.UtcNow - _startedAt).ToString(@"d\.hh\:mm\:ss"),
            ["windows"] = WindowsInfo(),
            ["displays"] = DisplayInfo(),
            ["theme"] = new JsonObject
            {
                ["selected"] = _settings.Theme,
                ["loaded"] = _theme?.Id,
                ["name"] = _theme?.Name,
                ["file"] = _theme?.FilePath,
                ["windowsLightMode"] = _lightMode,
                ["startMenuLight"] = StartMenuLight,
                ["adjustments"] = new JsonObject(Overrides.Select(o =>
                    new KeyValuePair<string, JsonNode?>(o.Key, o.Value))),
            },
            ["settings"] = new JsonObject
            {
                ["enabled"] = _settings.Enabled,
                ["styleStartMenu"] = _settings.StyleStartMenu,
                ["startMenuScheme"] = _settings.StartMenuScheme.ToString(),
                ["maximizeMode"] = _settings.MaximizeMode.ToString(),
                ["clickThrough"] = _settings.ClickThrough,
                ["monitor"] = $"{_settings.ShowMonitor} (network {_settings.MonitorNetwork}, cpu {_settings.MonitorCpu}, memory {_settings.MonitorMemory})",
            },
            ["taskbar"] = TargetInfo(Taskbar, _targets?.Explorer, RuntimeWriter.ExplorerFile),
            ["startMenu"] = TargetInfo(StartMenu, _targets?.StartMenu, RuntimeWriter.StartMenuFile),
            ["autostart"] = new JsonObject
            {
                ["state"] = AutostartInfo.State.ToString(),
                ["source"] = AutostartInfo.Source?.ToString(),
                ["detail"] = AutostartInfo.Detail,
                ["message"] = AutostartMessage,
            },
            ["conflicts"] = new JsonArray(Conflicts.Select(c => (JsonNode)c).ToArray()),
            ["recentProblems"] = new JsonArray(Log.RecentProblems.Select(p => (JsonNode)p).ToArray()),
        };
        return summary;
    }

    private JsonObject TargetInfo(TargetStatus status, TargetSnapshot? injection, string file) => new()
    {
        ["status"] = status.Status.ToString(),
        ["detail"] = status.Detail,
        ["pid"] = injection?.Process.Id,
        ["phase"] = injection?.Phase.ToString(),
        ["lastFailure"] = injection?.LastFailure?.Describe(),
        ["failures"] = injection?.Failures,
        ["outdated"] = injection?.Outdated,
        ["expectedSerial"] = _runtime.LastSerial(file),
        ["report"] = $"state/{EngineStatusReader.FileName(file, injection?.Process.Id ?? 0)}",
    };

    private static JsonObject WindowsInfo()
    {
        var info = new JsonObject
        {
            ["version"] = Environment.OSVersion.Version.ToString(),
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
        };
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            foreach (var name in new[] { "ProductName", "DisplayVersion", "CurrentBuild", "UBR", "EditionID" })
            {
                if (key?.GetValue(name) is { } value)
                {
                    info[name] = value.ToString();
                }
            }
        }
        catch (Exception e)
        {
            info["registryError"] = e.Message;
        }
        return info;
    }

    private static JsonArray DisplayInfo()
    {
        var displays = new JsonArray();
        try
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var center = new NativeMethods.POINT
                {
                    X = screen.Bounds.Left + screen.Bounds.Width / 2,
                    Y = screen.Bounds.Top + screen.Bounds.Height / 2,
                };
                IntPtr monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
                uint dpi = NativeMethods.GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 ? x : 0;
                displays.Add(new JsonObject
                {
                    ["name"] = screen.DeviceName,
                    ["primary"] = screen.Primary,
                    ["bounds"] = $"{screen.Bounds.X},{screen.Bounds.Y} {screen.Bounds.Width}x{screen.Bounds.Height}",
                    ["workingArea"] = $"{screen.WorkingArea.X},{screen.WorkingArea.Y} {screen.WorkingArea.Width}x{screen.WorkingArea.Height}",
                    ["dpi"] = dpi,
                    ["scale"] = dpi > 0 ? $"{dpi * 100 / 96}%" : null,
                });
            }
        }
        catch (Exception e)
        {
            displays.Add(new JsonObject { ["error"] = e.Message });
        }
        return displays;
    }
}
