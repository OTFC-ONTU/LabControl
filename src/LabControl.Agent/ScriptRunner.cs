using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LabControl.Shared;
using LabControl.Shared.Jobs;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using Windows.Win32;

namespace LabControl.Agent;

/// <summary>
/// <c>run_script</c> on a real PC (ROADMAP M2, PROTOCOL <c>Job</c>, D-31, D-32): pull the
/// script through <c>PullFile</c>, verify its hash, write it where its runner can read it,
/// run it with <c>powershell.exe</c> or <c>cmd.exe</c> as SYSTEM or as the student, stream
/// every line back as <c>JobProgress</c>, kill the whole tree when it goes silent for longer
/// than its timeout, and put the exit code in <c>JobResult</c>. The job's directory is
/// removed afterwards; the console's journal keeps the output.
/// </summary>
internal sealed class ScriptRunner
{
    private readonly AgentLink _link;
    private readonly ILogger _log;
    private readonly Lazy<Encoding> _oem = new(OemEncoding);

    public ScriptRunner(AgentLink link, ILogger log)
    {
        _link = link;
        _log = log;
    }

    public async Task<JobResult> RunAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        if (!RunScriptRequest.TryParse(job, out var request, out var error))
        {
            return Fail(job, $"This job cannot run: {error}.");
        }

        // ---- pull and verify

        byte[] script;
        try
        {
            using var buffer = new MemoryStream();
            await _link.PullFileAsync(request.Reference, request.Sha256, buffer, token);
            script = buffer.ToArray();
        }
        catch (FilePullException ex)
        {
            return Fail(job, ex.Message);
        }

        // ---- where it runs, and as whom

        uint? sessionId = null;
        if (request.RunAs == ScriptRunAs.User)
        {
            var session = InteractiveSession.Snapshot();
            if (!session.HasSession || !session.HasUser)
            {
                return Fail(job, "The script was to run as the logged-on user, but nobody is logged on to this PC right now.");
            }

            sessionId = session.SessionId;
        }

        var root = request.RunAs == ScriptRunAs.User ? Defaults.UserJobsDirectory : Path.Combine(Defaults.AgentDataDirectory, Defaults.JobsDirectoryName);
        var directory = Path.Combine(root, RunScriptRequest.SafeName(job.Id));
        var path = Path.Combine(directory, request.Name + request.Extension);

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(path, request.Shell == ScriptShell.Cmd ? ForCmd(script) : ForPowerShell(script), token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(directory);
            return Fail(job, $"Could not write the script to {path}: {ex.Message}");
        }

        var (executable, arguments) = CommandLine(request.Shell, path);
        _log.LogInformation("job {Job}: running {Path} with {Shell} as {RunAs}, timeout {Timeout:0} s", job.Id, path, request.Shell, request.RunAs, request.Timeout.TotalSeconds);

        // ---- run

        var stopwatch = Stopwatch.StartNew();
        ProcessOutcome outcome;
        try
        {
            Task Line(string line) => report(new JobProgress { JobId = job.Id, Percent = 0, Line = line });

            if (sessionId is { } inSession)
            {
                UserProcess started;
                try
                {
                    started = UserProcessLauncher.Start(inSession, executable, arguments, directory, _oem.Value);
                }
                catch (Win32Exception ex)
                {
                    return Fail(job, $"Could not start the script in session {inSession} as the logged-on user: {ex.Message}");
                }

                outcome = await ProcessRunner.SuperviseAsync(started.Process, started.Stdout, started.Stderr, request.Timeout, Line, token);
            }
            else
            {
                var info = new ProcessStartInfo(executable, arguments)
                {
                    WorkingDirectory = directory,
                    StandardOutputEncoding = _oem.Value,
                    StandardErrorEncoding = _oem.Value,
                };

                try
                {
                    outcome = await ProcessRunner.RunAsync(info, request.Timeout, Line, token);
                }
                catch (Win32Exception ex)
                {
                    return Fail(job, $"Could not start {executable}: {ex.Message}");
                }
            }
        }
        finally
        {
            TryDelete(directory);
        }

        // ---- report

        var elapsed = stopwatch.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        if (outcome.TimedOut)
        {
            return new JobResult
            {
                JobId = job.Id,
                Ok = false,
                ExitCode = -1,
                Message = $"Killed after {request.Timeout.TotalSeconds:0} s without output ({outcome.Lines} line(s) received, {elapsed} s in total). The script and everything it started were terminated.",
            };
        }

        if (outcome.Cancelled)
        {
            return Fail(job, "The agent was stopped while the script was running; the script was terminated.");
        }

        return new JobResult
        {
            JobId = job.Id,
            Ok = outcome.ExitCode == 0,
            ExitCode = outcome.ExitCode,
            Message = $"exit {outcome.ExitCode} after {elapsed} s, {outcome.Lines} line(s)",
        };
    }

    private static (string Executable, string Arguments) CommandLine(ScriptShell shell, string path) => shell switch
    {
        ScriptShell.Cmd => (Path.Combine(Environment.SystemDirectory, Defaults.CmdExecutable), $"/d /c \"{path}\""),
        _ => (Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", Defaults.PowerShellExecutable),
              $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\""),
    };

    /// <summary>Windows PowerShell 5.1 reads a file with a BOM as UTF-8 and one without as ANSI (D-29 item 7): make sure the BOM is there.</summary>
    private static byte[] ForPowerShell(byte[] script)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        return script.AsSpan().StartsWith(preamble) ? script : [.. preamble, .. script];
    }

    /// <summary>cmd.exe reads a batch file in the OEM code page; the console sends UTF-8, so transcode.</summary>
    private byte[] ForCmd(byte[] script)
    {
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(script);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        return _oem.Value.GetBytes(text);
    }

    /// <summary>The code page powershell.exe and cmd.exe print in when their output is redirected.</summary>
    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)PInvoke.GetOEMCP());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private static JobResult Fail(Job job, string message) => new() { JobId = job.Id, Ok = false, ExitCode = -1, Message = message };

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "could not remove {Directory}: {Message}", directory, ex.Message);
        }
    }
}
