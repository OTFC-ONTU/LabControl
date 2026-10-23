using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LabControl.Shared.Exam;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace LabControl.Agent;

/// <summary>
/// The student's processes as the exam's allowed-programs watch sees them (M6 portion 4, D-75):
/// <see cref="Snapshot"/> lists the processes of one session with the token's user, the image
/// path and the creation time — what <see cref="ProgramWhitelist"/> judges by — and
/// <see cref="End"/> ends one only if it is still the same process. The list comes from
/// <see cref="Process.GetProcesses()"/> (one system call); the per-process details are read
/// once per process and cached, so a look every second costs a few new processes, not all of
/// them. A process whose token cannot be read has no user here and is never judged, and
/// neither is a protected process.
/// </summary>
internal enum EndOutcome
{
    Ended,
    Gone,
    NotTheSame,
    Failed,
}

internal sealed class WindowsProcesses
{
    private readonly ConcurrentDictionary<int, ProcessInfo> _known = new();

    /// <summary>The processes of <paramref name="sessionId"/>, with details; never throws for one process that cannot be read.</summary>
    public IReadOnlyCollection<ProcessInfo> Snapshot(int sessionId)
    {
        var result = new List<ProcessInfo>();
        var alive = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                int id, session;
                string name;
                try
                {
                    id = process.Id;
                    session = process.SessionId;
                    name = process.ProcessName;
                }
                catch (Exception)
                {
                    continue;
                }

                if (session != sessionId || id <= 4)
                {
                    continue;
                }

                alive.Add(id);

                // A cached entry is reused only while it is provably the same process: the
                // creation time is read again (Windows hands freed ids back fast), and an entry
                // whose user or path could not be read is read again (review finding 1).
                if (_known.TryGetValue(id, out var cached) && cached.UserSid is not null && cached.ImagePath is not null
                    && cached.StartedAt != 0 && StartedAt(id) == cached.StartedAt)
                {
                    result.Add(cached);
                    continue;
                }

                var info = Describe(id, name, session);
                _known[id] = info;
                result.Add(info);
            }
        }

        foreach (var gone in _known.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _known.TryRemove(gone, out _);
        }

        return result;
    }

    /// <summary>
    /// Ends <paramref name="process"/> if the id still names the process that was judged (the
    /// creation time is compared: Windows reuses ids). Only <see cref="EndOutcome.Ended"/> is
    /// something this call did; a process already gone or replaced is neither counted nor
    /// reported as closed.
    /// </summary>
    public static (EndOutcome Outcome, string Problem) End(ProcessInfo process)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)process.ProcessId);
        if (handle.IsNull)
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_INVALID_PARAMETER: no such process any more — nothing to end.
            return error == 87 ? (EndOutcome.Gone, string.Empty) : (EndOutcome.Failed, new Win32Exception(error).Message);
        }

        try
        {
            if (process.StartedAt == 0 || CreationTime(handle) is not { } started || started != process.StartedAt)
            {
                return (EndOutcome.NotTheSame, string.Empty);
            }

            if (HasExited(handle))
            {
                return (EndOutcome.Gone, string.Empty);
            }

            if (!PInvoke.TerminateProcess(handle, 1))
            {
                var error = Marshal.GetLastWin32Error();
                // ERROR_ACCESS_DENIED after the process exited on its own is the usual race.
                return error == 5 && HasExited(handle) ? (EndOutcome.Gone, string.Empty) : (EndOutcome.Failed, new Win32Exception(error).Message);
            }

            return (EndOutcome.Ended, string.Empty);
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    /// <summary>The creation time of process <paramref name="id"/>, or 0 when it cannot be read.</summary>
    private static long StartedAt(int id)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)id);
        if (handle.IsNull)
        {
            return 0;
        }

        try
        {
            return CreationTime(handle) ?? 0;
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    private static ProcessInfo Describe(int id, string name, int session)
    {
        string? sid = null;
        string? path = null;
        long started = 0;
        var isProtected = false;
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)id);
        if (!handle.IsNull)
        {
            try
            {
                started = CreationTime(handle) ?? 0;
                path = ImagePath(handle);
                sid = TokenUser(handle);
                isProtected = IsProtectedProcess(handle);
            }
            catch (Exception)
            {
                // Whatever could not be read stays unknown; an unknown user is never judged.
            }
            finally
            {
                PInvoke.CloseHandle(handle);
            }
        }

        var file = path is { Length: > 0 } ? Path.GetFileName(path) : name + ".exe";
        return new ProcessInfo(id, file, session, sid, path, started) { IsProtectedProcess = isProtected };
    }

    /// <summary>True when Windows protects the process (PPL or full): <c>TerminateProcess</c> is refused whatever the caller.</summary>
    private static unsafe bool IsProtectedProcess(HANDLE handle)
    {
        const uint ProtectionLevelNone = 0xFFFFFFFE;
        PROCESS_PROTECTION_LEVEL_INFORMATION information = default;
        return OperatingSystem.IsWindowsVersionAtLeast(8)
            && PInvoke.GetProcessInformation(handle, PROCESS_INFORMATION_CLASS.ProcessProtectionLevelInfo, &information, (uint)sizeof(PROCESS_PROTECTION_LEVEL_INFORMATION))
            && (uint)information.ProtectionLevel != ProtectionLevelNone;
    }

    private static unsafe long? CreationTime(HANDLE handle)
    {
        System.Runtime.InteropServices.ComTypes.FILETIME created, exited, kernel, user;
        if (!PInvoke.GetProcessTimes(handle, &created, &exited, &kernel, &user))
        {
            return null;
        }

        return ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
    }

    private static unsafe bool HasExited(HANDLE handle)
    {
        System.Runtime.InteropServices.ComTypes.FILETIME created, exited, kernel, user;
        return PInvoke.GetProcessTimes(handle, &created, &exited, &kernel, &user) && (exited.dwHighDateTime != 0 || exited.dwLowDateTime != 0);
    }

    private static unsafe string? ImagePath(HANDLE handle)
    {
        const int capacity = 1024;
        var buffer = stackalloc char[capacity];
        var length = (uint)capacity;
        return PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &length)
            ? new string(buffer, 0, (int)length)
            : null;
    }

    private static unsafe string? TokenUser(HANDLE process)
    {
        HANDLE token;
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
        {
            return null;
        }

        try
        {
            uint needed = 0;
            PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, null, 0, &needed);
            if (needed == 0 || needed > 4096)
            {
                return null;
            }

            var buffer = stackalloc byte[(int)needed];
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, buffer, needed, &needed))
            {
                return null;
            }

            var user = (TOKEN_USER*)buffer;
            return new SecurityIdentifier((nint)user->User.Sid.Value).Value;
        }
        finally
        {
            PInvoke.CloseHandle(token);
        }
    }

    /// <summary>The SID of the user signed in to <paramref name="sessionId"/>, or <c>null</c> when nobody is.</summary>
    public static unsafe string? SessionUser(uint sessionId)
    {
        HANDLE token = default;
        if (!PInvoke.WTSQueryUserToken(sessionId, ref token))
        {
            return null;
        }

        try
        {
            using var identity = new WindowsIdentity((nint)token.Value);
            return identity.User?.Value;
        }
        finally
        {
            PInvoke.CloseHandle(token);
        }
    }
}
