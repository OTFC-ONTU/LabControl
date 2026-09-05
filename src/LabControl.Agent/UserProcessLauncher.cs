using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.RemoteDesktop;
using Windows.Win32.System.Threading;

namespace LabControl.Agent;

/// <summary>A process started in the student's session as the student, with its output on pipes the service reads.</summary>
internal sealed record UserProcess(Process Process, StreamReader Stdout, StreamReader Stderr);

/// <summary>
/// Starts a program in the interactive session <b>as the account logged on there</b>
/// (<c>run_script</c> with <c>as: user</c>, D-32): the user's own primary token from
/// <c>WTSQueryUserToken</c> (SYSTEM may ask for it), the user's environment block, the
/// desktop the student sees, and stdout/stderr on anonymous pipes the child inherits and
/// the service reads. The service never impersonates the student for its own work — only
/// the script runs with the student's rights, which is the point of <c>as: user</c>.
/// </summary>
internal static class UserProcessLauncher
{
    public static unsafe UserProcess Start(uint sessionId, string executable, string arguments, string workingDirectory, Encoding outputEncoding)
    {
        HANDLE token = default;
        if (!PInvoke.WTSQueryUserToken(sessionId, ref token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WTSQueryUserToken({sessionId}) failed — is anyone logged on there?");
        }

        try
        {
            void* environment;
            if (!PInvoke.CreateEnvironmentBlock(&environment, token, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed");
            }

            try
            {
                var (stdinRead, stdinWrite) = Pipe();
                var (stdoutRead, stdoutWrite) = Pipe();
                var (stderrRead, stderrWrite) = Pipe();

                // The service's ends must not leak into the child, or the pipe never closes.
                foreach (var ours in new[] { stdinWrite, stdoutRead, stderrRead })
                {
                    if (!PInvoke.SetHandleInformation(ours, (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT, 0))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation failed");
                    }
                }

                try
                {
                    var commandLine = ($"\"{executable}\" {arguments}".TrimEnd() + '\0').ToCharArray();
                    var desktop = "winsta0\\default";

                    fixed (char* commandLinePtr = commandLine)
                    fixed (char* desktopPtr = desktop)
                    fixed (char* executablePtr = executable)
                    fixed (char* directoryPtr = workingDirectory)
                    {
                        var startup = new STARTUPINFOW
                        {
                            cb = (uint)sizeof(STARTUPINFOW),
                            lpDesktop = desktopPtr,
                            dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES,
                            hStdInput = (HANDLE)stdinRead.DangerousGetHandle(),
                            hStdOutput = (HANDLE)stdoutWrite.DangerousGetHandle(),
                            hStdError = (HANDLE)stderrWrite.DangerousGetHandle(),
                        };

                        PROCESS_INFORMATION info;
                        var ok = PInvoke.CreateProcessAsUser(
                            token,
                            executablePtr,
                            commandLinePtr,
                            null,
                            null,
                            true,
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

                        Process process;
                        try
                        {
                            process = Process.GetProcessById((int)info.dwProcessId);
                        }
                        catch (ArgumentException)
                        {
                            throw new Win32Exception($"the process (pid {info.dwProcessId}) exited immediately after starting");
                        }

                        return new UserProcess(
                            process,
                            new StreamReader(new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false), outputEncoding),
                            new StreamReader(new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false), outputEncoding));
                    }
                }
                finally
                {
                    // The child holds its own copies now; ours would only keep the pipes open.
                    stdinRead.Dispose();
                    stdinWrite.Dispose();
                    stdoutWrite.Dispose();
                    stderrWrite.Dispose();
                }
            }
            finally
            {
                PInvoke.DestroyEnvironmentBlock(environment);
            }
        }
        finally
        {
            PInvoke.CloseHandle(token);
        }
    }

    private static unsafe (SafeFileHandle Read, SafeFileHandle Write) Pipe()
    {
        var attributes = new SECURITY_ATTRIBUTES
        {
            nLength = (uint)sizeof(SECURITY_ATTRIBUTES),
            bInheritHandle = true,
        };

        if (!PInvoke.CreatePipe(out var read, out var write, attributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
        }

        return (read, write);
    }
}
