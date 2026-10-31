using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static Desk.NativeMethods;

namespace Desk;

/// <summary>The target processes and how DeskTap.dll gets into them, on Windows.</summary>
internal sealed class WindowsTargetPlatform : ITargetPlatform
{
    private readonly TapInjector _injector;
    private readonly int _sessionId = Process.GetCurrentProcess().SessionId;

    public WindowsTargetPlatform(TapInjector injector)
    {
        _injector = injector;
    }

    public TaskbarTarget? FindTaskbar()
    {
        IntPtr taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            return null;
        }
        uint threadId = GetWindowThreadProcessId(taskbar, out uint processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }
        return new TaskbarTarget(Instance(processId), threadId, taskbar);
    }

    public ProcessInstance FindStartMenu()
    {
        Process[] processes = Process.GetProcessesByName("StartMenuExperienceHost");
        try
        {
            foreach (var process in processes)
            {
                if (process.SessionId == _sessionId)
                {
                    return Instance((uint)process.Id);
                }
            }
            return default;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>The process with its creation time (0 when it can't be read).</summary>
    public static ProcessInstance Instance(uint processId) => new(processId, ProcessStartTime(processId));

    public static long ProcessStartTime(uint processId) => NativeMethods.ProcessStartTime(processId);

    public IReadOnlyList<string>? FindLoadedTaps(ProcessInstance target)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, target.Id);
        if (process == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            if (!SameInstance(process, target))
            {
                return null;
            }
            var modules = new IntPtr[1024];
            if (!EnumProcessModulesEx(process, modules, modules.Length * IntPtr.Size, out int needed, LIST_MODULES_ALL))
            {
                return null;
            }
            int count = Math.Min(modules.Length, needed / IntPtr.Size);
            var name = new char[1024];
            var found = new List<string>();
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
                    found.Add(path);
                }
            }
            return found;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool SameInstance(IntPtr process, ProcessInstance target) =>
        target.StartTime == 0 ||
        (GetProcessTimes(process, out long creation, out _, out _, out _) && creation == target.StartTime);

    private bool IsLoaded(ProcessInstance process) => FindLoadedTaps(process) is { Count: > 0 };

    /// <summary>
    /// Installs a WH_GETMESSAGE hook on the taskbar thread, which makes Windows
    /// load the DLL there (TranslucentTB uses the same approach). The DLL pins
    /// itself, so unhooking afterwards doesn't unload it.
    /// </summary>
    public InjectResult InjectWithHook(TaskbarTarget target, TimeSpan timeout, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested)
        {
            return InjectResult.Cancelled;
        }
        IntPtr hook = SetWindowsHookExW(WH_GETMESSAGE, _injector.HookProc, _injector.Module, target.ThreadId);
        if (hook == IntPtr.Zero)
        {
            return InjectResult.Fail(InjectStage.Hook, Marshal.GetLastWin32Error());
        }

        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                PostMessageW(target.Window, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                if (IsLoaded(target.Process))
                {
                    return InjectResult.Loaded;
                }
                // Stop as soon as possible: until the taskbar thread handles
                // a message, removing the hook keeps the DLL out.
                if (cancel.WaitHandle.WaitOne(200))
                {
                    break;
                }
            }
        }
        finally
        {
            UnhookWindowsHookEx(hook);
        }

        if (IsLoaded(target.Process))
        {
            return InjectResult.Loaded;
        }
        return cancel.IsCancellationRequested ? InjectResult.Cancelled : InjectResult.Fail(InjectStage.Timeout);
    }

    /// <summary>
    /// Loads the DLL with a remote LoadLibraryW call. Used for the Start menu
    /// host, which runs in an app container and is suspended until opened.
    /// </summary>
    public InjectResult StartRemoteLoad(ProcessInstance target, TimeSpan wait, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested)
        {
            return InjectResult.Cancelled;
        }
        IntPtr process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
                                     PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE,
            false, target.Id);
        if (process == IntPtr.Zero)
        {
            return InjectResult.Fail(InjectStage.OpenProcess, Marshal.GetLastWin32Error());
        }

        IntPtr remote = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        try
        {
            if (!SameInstance(process, target))
            {
                return InjectResult.Fail(InjectStage.OpenProcess);
            }

            byte[] path = Encoding.Unicode.GetBytes(_injector.DllPath + "\0");
            remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)path.Length, MEM_COMMIT | MEM_RESERVE,
                PAGE_READWRITE);
            if (remote == IntPtr.Zero)
            {
                return InjectResult.Fail(InjectStage.AllocateMemory, Marshal.GetLastWin32Error());
            }
            if (!WriteProcessMemory(process, remote, path, (UIntPtr)path.Length, out _))
            {
                return InjectResult.Fail(InjectStage.WriteMemory, Marshal.GetLastWin32Error());
            }

            // The last point where nothing has happened in the target yet.
            if (cancel.IsCancellationRequested)
            {
                return InjectResult.Cancelled;
            }
            IntPtr loadLibrary = GetProcAddress(GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero)
            {
                return InjectResult.Fail(InjectStage.CreateThread, Marshal.GetLastWin32Error());
            }

            // From here on the load owns the handles and the memory.
            var load = new RemoteLoad(process, thread, remote, () => IsLoaded(target));
            process = IntPtr.Zero;
            thread = IntPtr.Zero;
            remote = IntPtr.Zero;

            WaitForSingleObject(load.Thread, (uint)wait.TotalMilliseconds);
            var state = load.Poll();
            if (state == RemoteLoadState.Running)
            {
                return InjectResult.Wait(load);
            }
            load.Dispose();
            return state == RemoteLoadState.Succeeded
                ? InjectResult.Loaded
                : InjectResult.Fail(state == RemoteLoadState.ProcessExited ? InjectStage.OpenProcess : InjectStage.LoadLibrary);
        }
        finally
        {
            if (remote != IntPtr.Zero)
            {
                VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
            }
            if (thread != IntPtr.Zero)
            {
                CloseHandle(thread);
            }
            if (process != IntPtr.Zero)
            {
                CloseHandle(process);
            }
        }
    }

    public bool Terminate(ProcessInstance target)
    {
        try
        {
            if (target.StartTime != 0 && ProcessStartTime(target.Id) != target.StartTime)
            {
                return false;
            }
            using var process = Process.GetProcessById((int)target.Id);
            process.Kill();
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not end process {target.Id}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// A LoadLibraryW thread in another process. The path stays allocated
    /// until the thread has finished, since it reads it when it runs.
    /// </summary>
    private sealed class RemoteLoad : IRemoteLoad
    {
        private readonly Func<bool> _isLoaded;
        private IntPtr _process;
        private IntPtr _remote;

        public RemoteLoad(IntPtr process, IntPtr thread, IntPtr remote, Func<bool> isLoaded)
        {
            _process = process;
            Thread = thread;
            _remote = remote;
            _isLoaded = isLoaded;
        }

        public IntPtr Thread { get; private set; }

        public RemoteLoadState Poll()
        {
            if (Thread == IntPtr.Zero)
            {
                return RemoteLoadState.ProcessExited;
            }
            if (WaitForSingleObject(Thread, 0) == WAIT_OBJECT_0)
            {
                // The exit code is the low half of the module handle, which
                // can be zero for a loaded module, so check for the module.
                bool ok = GetExitCodeThread(Thread, out uint code) && code != 0;
                if (_remote != IntPtr.Zero)
                {
                    VirtualFreeEx(_process, _remote, UIntPtr.Zero, MEM_RELEASE);
                    _remote = IntPtr.Zero;
                }
                return ok || _isLoaded() ? RemoteLoadState.Succeeded : RemoteLoadState.Failed;
            }
            if (WaitForSingleObject(_process, 0) == WAIT_OBJECT_0)
            {
                return RemoteLoadState.ProcessExited;
            }
            return RemoteLoadState.Running;
        }

        public void Dispose()
        {
            // The memory is only freed once the thread is done with it (in
            // Poll); otherwise it goes away with the process.
            if (Thread != IntPtr.Zero)
            {
                CloseHandle(Thread);
                Thread = IntPtr.Zero;
            }
            if (_process != IntPtr.Zero)
            {
                CloseHandle(_process);
                _process = IntPtr.Zero;
            }
        }
    }
}
