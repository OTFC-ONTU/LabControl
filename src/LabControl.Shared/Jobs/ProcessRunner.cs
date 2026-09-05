using System.ComponentModel;
using System.Diagnostics;

namespace LabControl.Shared.Jobs;

/// <summary>How a supervised process ended.</summary>
public sealed record ProcessOutcome(int ExitCode, bool TimedOut, bool Cancelled, int Lines, TimeSpan Elapsed)
{
    public bool Ok => !TimedOut && !Cancelled && ExitCode == 0;
}

/// <summary>
/// Runs one process to the end, streaming every line of stdout and stderr as it appears,
/// and kills the whole process tree when it goes silent for longer than the timeout
/// (PROTOCOL, <c>timeout_s</c> is a timeout of <i>inactivity</i>). Nothing here is
/// Windows-specific: the tests drive it with <c>sh</c> on the Mac, the agent with
/// <c>powershell.exe</c> and <c>cmd.exe</c>, and a process started with a foreign token is
/// handed over already running.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Prefix on a line that came from stderr, so an error is recognisable in the jobs panel.</summary>
    public const string StderrPrefix = "[stderr] ";

    /// <summary>Starts <paramref name="startInfo"/> with both streams redirected and supervises it.</summary>
    public static async Task<ProcessOutcome> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan inactivityTimeout,
        Func<string, Task> onLine,
        CancellationToken token)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;
        startInfo.CreateNoWindow = true;

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new Win32Exception("the process did not start");
        }

        // Nothing will ever type into it; a script that waits for input hangs and is killed.
        process.StandardInput.Close();

        return await SuperviseAsync(process, process.StandardOutput, process.StandardError, inactivityTimeout, onLine, token);
    }

    /// <summary>
    /// Supervises a process someone else started — the agent's <i>run as the student</i>
    /// path creates it with <c>CreateProcessAsUser</c> and hands the pipes over here.
    /// Disposes the process and both readers.
    /// </summary>
    public static async Task<ProcessOutcome> SuperviseAsync(
        Process process,
        TextReader stdout,
        TextReader stderr,
        TimeSpan inactivityTimeout,
        Func<string, Task> onLine,
        CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        var lines = 0;
        var truncated = false;
        var lastActivity = Stopwatch.StartNew();
        var gate = new SemaphoreSlim(1, 1);

        async Task Emit(string line)
        {
            lastActivity.Restart();
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                if (Interlocked.Increment(ref lines) <= Defaults.ScriptOutputMaxLines)
                {
                    await onLine(line);
                }
                else if (!truncated)
                {
                    truncated = true;
                    await onLine($"… output cut after {Defaults.ScriptOutputMaxLines} lines");
                }
            }
            finally
            {
                gate.Release();
            }
        }

        using (process)
        using (stdout)
        using (stderr)
        {
            var pumpOut = PumpAsync(stdout, string.Empty, Emit);
            var pumpErr = PumpAsync(stderr, StderrPrefix, Emit);
            var timedOut = false;
            var cancelled = false;

            while (true)
            {
                if (process.HasExited)
                {
                    break;
                }

                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    Kill(process);
                    break;
                }

                if (lastActivity.Elapsed > inactivityTimeout)
                {
                    timedOut = true;
                    Kill(process);
                    break;
                }

                try
                {
                    await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(250)).Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            // The pipes close when the last process holding them dies; a killed tree lets go
            // within moments, and a grandchild that survived the kill must not hold the job.
            var pumps = Task.WhenAll(pumpOut, pumpErr);
            try
            {
                await pumps.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (TimeoutException)
            {
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }

            var exitCode = -1;
            try
            {
                if (process.HasExited)
                {
                    exitCode = process.ExitCode;
                }
            }
            catch (InvalidOperationException)
            {
            }

            return new ProcessOutcome(exitCode, timedOut, cancelled, Math.Min(lines, Defaults.ScriptOutputMaxLines), started.Elapsed);
        }
    }

    private static async Task PumpAsync(TextReader reader, string prefix, Func<string, Task> emit)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await emit(prefix + line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The pipe went away with the process; whatever was read is delivered.
        }
    }

    /// <summary>Kills the process and everything it started; failures are ignored (the process may already be gone).</summary>
    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
        }
    }
}
