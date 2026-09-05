using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Shutdown;

namespace LabControl.Agent;

/// <summary>
/// The power jobs (ROADMAP M2, ARCHITECTURE §6): shutdown and reboot through
/// <c>InitiateSystemShutdownEx</c> with <c>SE_SHUTDOWN_NAME</c> enabled on the service's
/// own token, immediate and forced (owner's choice, D-32); log off through
/// <c>WTSLogoffSession</c>, because <c>ExitWindowsEx</c> only ever logs off the caller's
/// session and the service lives in session 0. Every call answers with <c>null</c> or the
/// failure in plain language; nothing here throws.
/// </summary>
internal static class PowerControl
{
    private const string ShutdownMessage = "LabControl: the teacher is switching this PC off.";
    private const string RebootMessage = "LabControl: the teacher is restarting this PC.";

    /// <summary>Enables the shutdown privilege now, so a job can fail early instead of after it answered.</summary>
    public static string? PrepareShutdown()
    {
        try
        {
            return EnablePrivilege(PInvoke.SE_SHUTDOWN_NAME);
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or InvalidOperationException)
        {
            return $"could not enable the shutdown privilege: {ex.Message}";
        }
    }

    /// <summary>Shuts down or reboots at once, closing applications by force.</summary>
    public static string? Shutdown(bool reboot)
    {
        var privilege = PrepareShutdown();
        if (privilege is not null)
        {
            return privilege;
        }

        try
        {
            var reason = SHUTDOWN_REASON.SHTDN_REASON_MAJOR_OTHER | SHUTDOWN_REASON.SHTDN_REASON_MINOR_OTHER | SHUTDOWN_REASON.SHTDN_REASON_FLAG_PLANNED;
            var message = reboot ? RebootMessage : ShutdownMessage;
            unsafe
            {
                fixed (char* messagePtr = message)
                {
                    if (!PInvoke.InitiateSystemShutdownEx(default, new PWSTR(messagePtr), 0, true, reboot, reason))
                    {
                        return $"InitiateSystemShutdownEx failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException)
        {
            return $"InitiateSystemShutdownEx failed: {ex.Message}";
        }
    }

    /// <summary>Logs the interactive session off without waiting for it to finish.</summary>
    public static string? Logoff(uint sessionId)
    {
        try
        {
            if (!PInvoke.WTSLogoffSession(HANDLE.Null, sessionId, false))
            {
                return $"WTSLogoffSession({sessionId}) failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            }

            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException)
        {
            return $"WTSLogoffSession({sessionId}) failed: {ex.Message}";
        }
    }

    private static unsafe string? EnablePrivilege(string name)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        if (!PInvoke.OpenProcessToken(self.SafeHandle, TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            return $"OpenProcessToken failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        using (token)
        {
            if (!PInvoke.LookupPrivilegeValue(null, name, out var luid))
            {
                return $"LookupPrivilegeValue({name}) failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            }

            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
            privileges.Privileges[0] = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED };

            if (!PInvoke.AdjustTokenPrivileges(token, false, &privileges, Span<byte>.Empty))
            {
                return $"AdjustTokenPrivileges({name}) failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            }

            // AdjustTokenPrivileges returns true even when the privilege is not held at all.
            var error = Marshal.GetLastWin32Error();
            if (error == (int)WIN32_ERROR.ERROR_NOT_ALL_ASSIGNED)
            {
                return $"this process does not hold {name}; is the service running as SYSTEM?";
            }

            return null;
        }
    }
}
