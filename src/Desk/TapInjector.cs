using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using static Desk.NativeMethods;

namespace Desk;

/// <summary>Deploys DeskTap.dll and keeps it loaded here for the window hook.</summary>
internal sealed class TapInjector
{
    private TapInjector(string dllPath, IntPtr module, IntPtr hookProc)
    {
        DllPath = dllPath;
        Module = module;
        HookProc = hookProc;
    }

    /// <summary>The deployed copy, %LOCALAPPDATA%\Desk\bin\DeskTap-&lt;hash&gt;.dll.</summary>
    public string DllPath { get; }

    /// <summary>The DLL loaded in Desk.exe.</summary>
    public IntPtr Module { get; }

    /// <summary>DeskTap_HookProc, installed as a WH_GETMESSAGE hook to load the DLL into explorer.</summary>
    public IntPtr HookProc { get; }

    /// <summary>
    /// Copies DeskTap.dll to %LOCALAPPDATA%\Desk\bin under a content-hashed
    /// name (a loaded copy stays locked until the process exits), makes it
    /// readable by app containers and loads it here to get the hook procedure.
    /// </summary>
    public static TapInjector Deploy()
    {
        string source = Path.Combine(Paths.AppDir, "DeskTap.dll");
        byte[] bytes = File.ReadAllBytes(source);
        string hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();

        Directory.CreateDirectory(Paths.BinDir);
        Acl.GrantAppContainers(Paths.BinDir, FileSystemRights.ReadAndExecute);

        string target = Path.Combine(Paths.BinDir, $"DeskTap-{hash}.dll");
        if (!File.Exists(target))
        {
            string temp = target + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, target, overwrite: true);
        }

        // Older copies which are no longer loaded anywhere can go.
        foreach (string old in Directory.GetFiles(Paths.BinDir, "DeskTap-*.dll"))
        {
            if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(old);
                }
                catch
                {
                    // Still loaded by explorer or the Start menu.
                }
            }
        }

        IntPtr module = LoadLibraryW(target);
        if (module == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"LoadLibrary({target}) failed");
        }
        IntPtr hookProc = GetProcAddress(module, "DeskTap_HookProc");
        if (hookProc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DeskTap_HookProc not found");
        }
        Log.Info($"DeskTap deployed: {target}");
        return new TapInjector(target, module, hookProc);
    }
}
