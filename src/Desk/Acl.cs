using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Desk;

/// <summary>
/// The Start menu host runs in an app container, so DeskTap.dll and its files
/// have to be readable by app containers, and the log folder writable from low
/// integrity.
/// </summary>
internal static class Acl
{
    private static readonly SecurityIdentifier[] AppContainerGroups =
    [
        new("S-1-15-2-1"), // ALL APPLICATION PACKAGES
        new("S-1-15-2-2"), // ALL RESTRICTED APPLICATION PACKAGES
    ];

    private const int SE_FILE_OBJECT = 1;
    private const uint LABEL_SECURITY_INFORMATION = 0x00000010;

    public static void GrantAppContainers(string directory, FileSystemRights rights)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            foreach (var sid in AppContainerGroups)
            {
                // Synchronize is needed to wait on handles (and part of GENERIC_READ).
                security.AddAccessRule(new FileSystemAccessRule(sid, rights | FileSystemRights.Synchronize,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
        catch (Exception e)
        {
            Log.Warn($"Could not grant app container access to {directory}: {e.Message}");
        }
    }

    /// <summary>Gives the folder a low integrity label (like %LOCALAPPDATA%Low).</summary>
    public static void SetLowIntegrity(string directory)
    {
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    "S:(ML;OICI;NW;;;LW)", 1, out descriptor, out _) ||
                !NativeMethods.GetSecurityDescriptorSacl(descriptor, out _, out IntPtr sacl, out _))
            {
                Log.Warn($"Could not build the low integrity label: {Marshal.GetLastWin32Error()}");
                return;
            }

            uint result = NativeMethods.SetNamedSecurityInfoW(directory, SE_FILE_OBJECT,
                LABEL_SECURITY_INFORMATION, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (result != 0)
            {
                Log.Warn($"Could not label {directory} low integrity: {result}");
            }
        }
        finally
        {
            if (descriptor != IntPtr.Zero)
            {
                NativeMethods.LocalFree(descriptor);
            }
        }
    }
}
