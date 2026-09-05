using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RemoteDesktop;

namespace LabControl.Agent;

/// <summary>What the service can see of the interactive session from session 0, via WTS.</summary>
/// <param name="SessionId">The physical console session, or <c>null</c> when there is none (very early in boot).</param>
/// <param name="User">The account logged on there, empty at the logon screen.</param>
/// <param name="Locked"><c>true</c> when the lock screen is up; <c>null</c> when Windows would not say.</param>
internal readonly record struct SessionSnapshot(uint? SessionId, string User, bool? Locked)
{
    public bool HasSession => SessionId is not null;

    public bool HasUser => User.Length > 0;

    public override string ToString() =>
        !HasSession ? "no interactive session"
        : $"session {SessionId}, {(HasUser ? User : "nobody logged on")}{Locked switch { true => ", locked", false => ", unlocked", _ => string.Empty }}";
}

/// <summary>
/// Reads the interactive session the way the service must: through WTS, never through
/// <c>Environment</c>, which describes session 0 and SYSTEM. Every call is guarded; a
/// field Windows will not answer is left unknown rather than failing the snapshot.
/// </summary>
internal static class InteractiveSession
{
    private const uint NoSession = 0xFFFFFFFF;

    public static SessionSnapshot Snapshot()
    {
        var id = PInvoke.WTSGetActiveConsoleSessionId();
        if (id == NoSession)
        {
            return new SessionSnapshot(null, string.Empty, null);
        }

        return new SessionSnapshot(id, UserOf(id), LockedState(id));
    }

    /// <summary>"student", or "DOMAIN\name" when the account is not local; empty at the logon screen.</summary>
    public static string UserOf(uint session)
    {
        var user = QueryString(session, WTS_INFO_CLASS.WTSUserName);
        if (user.Length == 0)
        {
            return string.Empty;
        }

        var domain = QueryString(session, WTS_INFO_CLASS.WTSDomainName);
        return domain.Length == 0 || string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            ? user
            : $"{domain}\\{user}";
    }

    /// <summary>
    /// The lock flag from <c>WTSSessionInfoEx</c>. Reliable on Windows 8 and later (on 7 the
    /// two values were famously swapped, which this project does not support).
    /// </summary>
    public static unsafe bool? LockedState(uint session)
    {
        PWSTR buffer = default;
        uint bytes = 0;
        if (!PInvoke.WTSQuerySessionInformation(HANDLE.Null, session, WTS_INFO_CLASS.WTSSessionInfoEx, &buffer, &bytes) || buffer.Value is null)
        {
            return null;
        }

        try
        {
            if (bytes < sizeof(WTSINFOEXW))
            {
                return null;
            }

            var info = (WTSINFOEXW*)buffer.Value;
            if (info->Level != 1)
            {
                return null;
            }

            return info->Data.WTSInfoExLevel1.SessionFlags switch
            {
                (int)PInvoke.WTS_SESSIONSTATE_LOCK => true,
                (int)PInvoke.WTS_SESSIONSTATE_UNLOCK => false,
                _ => null,
            };
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer.Value);
        }
    }

    private static unsafe string QueryString(uint session, WTS_INFO_CLASS what)
    {
        PWSTR buffer = default;
        uint bytes = 0;
        if (!PInvoke.WTSQuerySessionInformation(HANDLE.Null, session, what, &buffer, &bytes) || buffer.Value is null)
        {
            return string.Empty;
        }

        try
        {
            return buffer.ToString().Trim();
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer.Value);
        }
    }
}
