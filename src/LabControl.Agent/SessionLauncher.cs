using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace LabControl.Agent;

/// <summary>
/// Starts <c>session.exe</c> inside the interactive session as SYSTEM (D-06). A session-0
/// service cannot simply <c>Process.Start</c> it — the child would land in session 0 with no
/// desktop — so the service's own token is duplicated, its session id rewritten to the
/// target session (needs <c>SeTcbPrivilege</c>, which SYSTEM has) and the child created with
/// <c>CreateProcessAsUser</c> on <c>winsta0\default</c>, the desktop the student sees.
/// </summary>
internal static class SessionLauncher
{
    /// <summary>
    /// Starts <paramref name="executable"/> in <paramref name="sessionId"/> and returns the
    /// process, or throws <see cref="Win32Exception"/> with the failing call in the message.
    /// When the service itself already lives in that session (a developer running
    /// <c>agent.exe --run</c> from a terminal) a plain start is used instead, because the
    /// token surgery below needs privileges an administrator's shell does not have.
    /// </summary>
    public static Process Start(string executable, string arguments, uint sessionId)
    {
        using var current = Process.GetCurrentProcess();
        if (current.SessionId == sessionId)
        {
            var started = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable),
            }) ?? throw new Win32Exception("Process.Start returned no process");
            return started;
        }

        var pid = StartInSession(executable, arguments, sessionId);
        try
        {
            return Process.GetProcessById((int)pid);
        }
        catch (ArgumentException)
        {
            // Gone before we could look: it is a crash, and the supervisor treats it as one.
            throw new Win32Exception($"session.exe (pid {pid}) exited immediately after starting");
        }
    }

    private static unsafe uint StartInSession(string executable, string arguments, uint sessionId)
    {
        using var self = Process.GetCurrentProcess();

        if (!PInvoke.OpenProcessToken(self.SafeHandle, TOKEN_ACCESS_MASK.TOKEN_DUPLICATE | TOKEN_ACCESS_MASK.TOKEN_QUERY, out var ownToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");
        }

        using (ownToken)
        {
            if (!PInvoke.DuplicateTokenEx(ownToken, TOKEN_ACCESS_MASK.TOKEN_ALL_ACCESS, null,
                    SECURITY_IMPERSONATION_LEVEL.SecurityIdentification, TOKEN_TYPE.TokenPrimary, out var token))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");
            }

            using (token)
            {
                Span<byte> session = stackalloc byte[sizeof(uint)];
                BitConverter.TryWriteBytes(session, sessionId);
                if (!PInvoke.SetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenSessionId, session))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation(TokenSessionId) failed — is the service running as SYSTEM?");
                }

                if (!PInvoke.CreateEnvironmentBlock(out void* environment, token, false))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed");
                }

                try
                {
                    // The command line buffer must be writable: CreateProcess edits it in place.
                    var commandLine = ($"\"{executable}\" {arguments}".TrimEnd() + '\0').ToCharArray();
                    var desktop = "winsta0\\default";

                    fixed (char* commandLinePtr = commandLine)
                    fixed (char* desktopPtr = desktop)
                    fixed (char* executablePtr = executable)
                    fixed (char* directoryPtr = Path.GetDirectoryName(executable) ?? string.Empty)
                    {
                        var startup = new STARTUPINFOW
                        {
                            cb = (uint)sizeof(STARTUPINFOW),
                            lpDesktop = desktopPtr,
                        };

                        PROCESS_INFORMATION info;
                        var ok = PInvoke.CreateProcessAsUser(
                            (HANDLE)token.DangerousGetHandle(),
                            executablePtr,
                            commandLinePtr,
                            null,
                            null,
                            false,
                            PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT | PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW,
                            environment,
                            directoryPtr,
                            &startup,
                            &info);

                        if (!ok)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");
                        }

                        PInvoke.CloseHandle(info.hThread);
                        PInvoke.CloseHandle(info.hProcess);
                        return info.dwProcessId;
                    }
                }
                finally
                {
                    PInvoke.DestroyEnvironmentBlock(environment);
                }
            }
        }
    }
}
