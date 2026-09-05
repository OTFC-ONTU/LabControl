using System.ComponentModel;
using System.Runtime.InteropServices;
using LabControl.Shared.Protocol;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LabControl.Agent.Session;

/// <summary>
/// What only a process inside the interactive session can see: which desktop currently
/// owns the input ("Default" while the student works, "Winlogon" at the lock screen, the
/// logon screen and a UAC prompt) and the size of the primary display. In M2 this is the
/// whole visible output of the helper; M3 builds capture on the same footing.
/// </summary>
internal static class DesktopProbe
{
    private static readonly DateTime StartedAt = DateTime.UtcNow;

    public static HelperStatus Status() => new()
    {
        InputDesktop = InputDesktopName(),
        ScreenWidth = Metric(SYSTEM_METRICS_INDEX.SM_CXSCREEN),
        ScreenHeight = Metric(SYSTEM_METRICS_INDEX.SM_CYSCREEN),
        UptimeMs = (long)(DateTime.UtcNow - StartedAt).TotalMilliseconds,
    };

    /// <summary>The session this process runs in, or <c>null</c> when Windows will not say.</summary>
    public static uint? OwnSessionId()
    {
        uint session;
        return PInvoke.ProcessIdToSessionId(PInvoke.GetCurrentProcessId(), out session) ? session : null;
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
            var buffer = stackalloc char[256];
            uint needed = 0;
            if (!PInvoke.GetUserObjectInformation((HANDLE)desktop.Value, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, buffer, 256 * sizeof(char), &needed))
            {
                return $"? ({new Win32Exception(Marshal.GetLastWin32Error()).Message})";
            }

            return new string(buffer).TrimEnd('\0');
        }
        finally
        {
            PInvoke.CloseDesktop(desktop);
        }
    }

    private static int Metric(SYSTEM_METRICS_INDEX index)
    {
        try
        {
            return PInvoke.GetSystemMetrics(index);
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException)
        {
            return 0;
        }
    }
}
