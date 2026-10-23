using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using Windows.Win32;

namespace LabControl.Agent;

/// <summary>
/// <c>reset_profile</c> on a real PC (M6 portion 5, D-78). Only the recorded student of an
/// installation that created it (D-40) is ever reset, and never while an exam is held.
/// <b>Light</b> empties Desktop, Documents and Downloads under the student's own token, links
/// removed and never entered. <b>Full</b> logs the student off, deletes the profile by its SID
/// with <c>DeleteProfile</c> (Setup's removal does the same) and reboots, so automatic sign-in
/// creates a new profile; the PC reboots even when the profile could not be removed, so it
/// always comes back online by itself.
/// </summary>
internal sealed class ProfileResetRunner(AgentLink link, ILogger log)
{
    public Func<bool>? ExamHeld { get; init; }

    public async Task<JobResult> RunAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        JobResult Result(bool ok, string message) => new() { JobId = job.Id, Ok = ok, ExitCode = ok ? 0 : -1, Message = message };
        if (!ResetProfileRequest.TryParse(job, out var request, out var error)) return Result(false, error);
        if (ExamHeld?.Invoke() == true)
            return Result(false, "An exam is in progress on this PC; end it before resetting the profile, or its work would be lost.");
        try
        {
            return request.Mode == ResetMode.Light ? await LightAsync(job, report, token) : await FullAsync(job, report, token);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("no managed student", StringComparison.OrdinalIgnoreCase))
        {
            return Result(false, "This PC was installed without the LabControl student account, so its profiles are never reset.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or Win32Exception or SecurityException)
        {
            return Result(false, $"Could not reset the profile: {ex.Message}");
        }
    }

    private static async Task<JobResult> LightAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        using var student = ManagedStudentAccess.Open();
        var removed = 0;
        var left = 0;
        foreach (var (name, id) in new[]
        {
            ("Desktop", Defaults.StudentDesktopKnownFolderId),
            ("Documents", Defaults.StudentDocumentsKnownFolderId),
            ("Downloads", Defaults.StudentDownloadsKnownFolderId),
        })
        {
            var folder = student.KnownFolder(id);
            var summary = WindowsIdentity.RunImpersonated(student.Token,
                () => ProfileWipe.Clear(folder, Defaults.ProfileWipeMaxEntries, Defaults.ProfileWipeMaxDuration, token));
            removed += summary.Removed;
            left += summary.Left;
            await report(new JobProgress { JobId = job.Id, Line = $"{name}: {summary.Removed} removed, {summary.Left} left." });
        }

        var message = $"Emptied Desktop, Documents and Downloads: {removed} item(s) removed.";
        if (left > 0) message += $" {left} could not be removed (open in a program?); log the student off and reset again.";
        return new JobResult { JobId = job.Id, Ok = left == 0, ExitCode = left == 0 ? 0 : 1, Message = message };
    }

    private async Task<JobResult> FullAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        string sid;
        uint? sessionId;
        using (var student = ManagedStudentAccess.Open())
        {
            sid = student.Sid;
            sessionId = student.SessionId;
        }

        var problem = PowerControl.PrepareShutdown();
        if (problem is not null)
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = $"Cannot reboot after the reset: {problem}" };

        if (sessionId is { } id)
        {
            ExamCoordination.PauseForPowerJob();
            var failure = PowerControl.Logoff(id);
            if (failure is not null)
                return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = $"Could not log the student off: {failure}" };
            await report(new JobProgress { JobId = job.Id, Line = "Logging the student off." });
        }

        var deleted = false;
        var deadline = DateTimeOffset.UtcNow + Defaults.ProfileUnloadTimeout;
        var lastError = 0;
        while (!token.IsCancellationRequested)
        {
            if (PInvoke.DeleteProfile(sid, null, null))
            {
                deleted = true;
                break;
            }

            lastError = Marshal.GetLastWin32Error();
            if (lastError is 2 or 3)
            {
                deleted = true; // no profile to delete: the next sign-in makes a new one anyway
                break;
            }

            if (DateTimeOffset.UtcNow > deadline) break;
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Defaults.PowerJobDelay);
            ExamCoordination.PauseForPowerJob();
            var failure = PowerControl.Shutdown(reboot: true);
            if (failure is not null)
            {
                log.LogError("reboot after a profile reset failed: {Failure}", failure);
                link.ReportForJob(job.Id, Event.Types.Severity.Error, "power.failed", $"The reboot after the profile reset did not happen: {failure}");
            }
        });

        return deleted
            ? new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = $"The student's profile was deleted; rebooting in {Defaults.PowerJobDelay.TotalSeconds:0} s into a new one." }
            : new JobResult { JobId = job.Id, Ok = false, ExitCode = lastError, Message = $"Windows still held the student's profile ({new Win32Exception(lastError).Message}); rebooting anyway — reset again once the PC is back." };
    }
}
