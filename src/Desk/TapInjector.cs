using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using static Desk.NativeMethods;

namespace Desk;

/// <summary>Deploys DeskTap.dll and loads it into the target processes.</summary>
internal sealed class TapInjector
{
    private readonly string _dllPath;
    private IntPtr _module;
    private IntPtr _hookProc;

    private TapInjector(string dllPath)
    {
        _dllPath = dllPath;
    }

    public string DllPath => _dllPath;

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

        var injector = new TapInjector(target);
        injector._module = LoadLibraryW(target);
        if (injector._module == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"LoadLibrary({target}) failed");
        }
        injector._hookProc = GetProcAddress(injector._module, "DeskTap_HookProc");
        if (injector._hookProc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "DeskTap_HookProc not found");
        }
        Log.Info($"DeskTap deployed: {target}");
        return injector;
    }

    /// <summary>The path of the DeskTap build loaded in the process, if any.</summary>
    public static string? FindLoadedTap(uint processId)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var modules = new IntPtr[1024];
            if (!EnumProcessModulesEx(process, modules, modules.Length * IntPtr.Size, out int needed, LIST_MODULES_ALL))
            {
                return null;
            }
            int count = Math.Min(modules.Length, needed / IntPtr.Size);
            var name = new char[1024];
            for (int i = 0; i < count; i++)
            {
                uint length = GetModuleFileNameExW(process, modules[i], name, name.Length);
                if (length == 0)
                {
                    continue;
                }
                string path = new(name, 0, (int)length);
                string fileName = Path.GetFileName(path);
                if (fileName.StartsWith("DeskTap", StringComparison.OrdinalIgnoreCase) &&
                    fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// Installs a WH_GETMESSAGE hook on a UI thread of the target, which makes
    /// Windows load the DLL there (TranslucentTB uses the same approach).
    /// </summary>
    public bool InjectWithHook(uint processId, uint threadId, IntPtr window, TimeSpan timeout)
    {
        IntPtr hook = SetWindowsHookExW(WH_GETMESSAGE, _hookProc, _module, threadId);
        if (hook == IntPtr.Zero)
        {
            Log.Warn($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                PostMessageW(window, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                if (FindLoadedTap(processId) != null)
                {
                    return true;
                }
                Thread.Sleep(200);
            }
            return FindLoadedTap(processId) != null;
        }
        finally
        {
            UnhookWindowsHookEx(hook);
        }
    }

    /// <summary>
    /// Loads the DLL with a remote LoadLibraryW call. Used for the Start menu
    /// host, which runs in an app container and may be suspended.
    /// </summary>
    public bool InjectWithRemoteThread(uint processId, TimeSpan timeout)
    {
        IntPtr process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
                                     PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
            false, processId);
        if (process == IntPtr.Zero)
        {
            Log.Warn($"OpenProcess({processId}) failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            byte[] path = Encoding.Unicode.GetBytes(_dllPath + "\0");
            IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)path.Length,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero)
            {
                Log.Warn($"VirtualAllocEx failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!WriteProcessMemory(process, remote, path, (UIntPtr)path.Length, out _))
            {
                Log.Warn($"WriteProcessMemory failed: {Marshal.GetLastWin32Error()}");
                VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
                return false;
            }

            IntPtr loadLibrary = GetProcAddress(GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            IntPtr thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero)
            {
                Log.Warn($"CreateRemoteThread failed: {Marshal.GetLastWin32Error()}");
                VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
                return false;
            }

            uint wait = WaitForSingleObject(thread, (uint)timeout.TotalMilliseconds);
            CloseHandle(thread);
            if (wait != WAIT_OBJECT_0)
            {
                // The process is probably suspended; the thread still needs
                // the path once it runs, so the memory is left alone.
                Log.Info($"Start menu host {processId} hasn't run the loader yet");
                return false;
            }

            VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
            return FindLoadedTap(processId) != null;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
