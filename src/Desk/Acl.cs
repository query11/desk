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

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint DACL_SECURITY_INFORMATION = 0x00000004;

    /// <summary>
    /// Lets app containers (the Start menu engine) wait on this process, so
    /// they notice right away when Desk.exe is gone instead of relying on the
    /// heartbeat. Only SYNCHRONIZE and limited query rights are granted.
    /// </summary>
    public static void AllowAppContainersToWatchThisProcess()
    {
        try
        {
            IntPtr process = NativeMethods.GetCurrentProcess();
            NativeMethods.GetKernelObjectSecurity(process, DACL_SECURITY_INFORMATION, null, 0, out uint needed);
            var buffer = new byte[needed];
            if (needed == 0 ||
                !NativeMethods.GetKernelObjectSecurity(process, DACL_SECURITY_INFORMATION, buffer, needed, out _))
            {
                Log.Warn($"Could not read this process's security: {Marshal.GetLastWin32Error()}");
                return;
            }
            var descriptor = new RawSecurityDescriptor(buffer, 0);
            var dacl = descriptor.DiscretionaryAcl ?? new RawAcl(GenericAcl.AclRevision, 1);
            dacl.InsertAce(dacl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
                (int)(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION), AppContainerGroups[0], false, null));
            descriptor.DiscretionaryAcl = dacl;
            var updated = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(updated, 0);
            if (!NativeMethods.SetKernelObjectSecurity(process, DACL_SECURITY_INFORMATION, updated))
            {
                Log.Warn($"Could not let app containers watch this process: {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Could not let app containers watch this process: {e.Message}");
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
