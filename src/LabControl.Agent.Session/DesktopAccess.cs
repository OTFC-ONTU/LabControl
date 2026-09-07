using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LabControl.Agent.Session;

/// <summary>
/// The desktop plumbing every capture needs (D-35): the process sees real pixels rather
/// than DPI-virtualised ones, and the thread that captures is attached to whichever desktop
/// has the input — <c>Default</c> while the student works, <c>Winlogon</c> at the lock
/// screen and under a UAC prompt. A SYSTEM process may attach to the secure desktop; the
/// student's could not, which is why the helper runs as SYSTEM (ARCHITECTURE §2).
/// </summary>
internal static class DesktopAccess
{
    /// <summary>Once, before any window or GDI call; <c>null</c> on success, else why not.</summary>
    public static string? DeclareDpiAware()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
        {
            return "Windows 10 1703 or later is needed";
        }

        try
        {
            return PInvoke.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)
                ? null
                : new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return ex.Message;
        }
    }

    /// <summary>"Default", "Winlogon", "Screen-saver" — or "?" plus the error when the desktop cannot be opened.</summary>
    public static unsafe string InputDesktopName()
    {
        var desktop = PInvoke.OpenInputDesktop(default, false, DESKTOP_ACCESS_FLAGS.DESKTOP_READOBJECTS);
        if (desktop.IsNull)
        {
            return $"? ({new Win32Exception(Marshal.GetLastWin32Error()).Message})";
        }

        try
        {
            return NameOf((HANDLE)desktop.Value) ?? $"? ({new Win32Exception(Marshal.GetLastWin32Error()).Message})";
        }
        finally
        {
            PInvoke.CloseDesktop(desktop);
        }
    }

    /// <summary>
    /// Attaches the calling thread to the input desktop when it is not there already, and
    /// returns that desktop's name. Throws when the input desktop cannot be opened or joined
    /// — there is no session to capture, or the thread owns windows.
    /// </summary>
    public static unsafe string AttachToInputDesktop()
    {
        var input = PInvoke.OpenInputDesktop(default, false, (DESKTOP_ACCESS_FLAGS)0x10000000 /* GENERIC_ALL */);
        if (input.IsNull)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "the input desktop cannot be opened");
        }

        var name = NameOf((HANDLE)input.Value) ?? "?";
        var current = PInvoke.GetThreadDesktop(PInvoke.GetCurrentThreadId());
        var currentName = current.IsNull ? null : NameOf((HANDLE)current.Value);
        if (string.Equals(currentName, name, StringComparison.OrdinalIgnoreCase))
        {
            PInvoke.CloseDesktop(input);
            return name;
        }

        // The handle must stay open for as long as the thread uses the desktop; the previous
        // one (if we opened it) leaks one handle per switch, which is once per lock/unlock.
        if (!PInvoke.SetThreadDesktop(input))
        {
            var error = Marshal.GetLastWin32Error();
            PInvoke.CloseDesktop(input);
            throw new Win32Exception(error, $"the capture thread cannot join desktop {name}");
        }

        return name;
    }

    private static unsafe string? NameOf(HANDLE desktop)
    {
        var buffer = stackalloc char[256];
        uint needed = 0;
        if (!PInvoke.GetUserObjectInformation(desktop, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, buffer, 256 * sizeof(char), &needed))
        {
            return null;
        }

        return new string(buffer).TrimEnd('\0');
    }
}
