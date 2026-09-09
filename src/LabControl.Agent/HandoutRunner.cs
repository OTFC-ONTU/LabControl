using System.ComponentModel;
using System.Security;
using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;

namespace LabControl.Agent;

internal sealed class HandoutRunner(AgentLink link)
{
    private static readonly SemaphoreSlim DeliveryGate = new(1, 1);

    public async Task<JobResult> RunAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        while (!await DeliveryGate.WaitAsync(TimeSpan.FromSeconds(10), token))
            await report(new JobProgress { JobId = job.Id, Line = "Waiting for this PC's previous handout delivery." });
        try { return await RunCoreAsync(job, report, token); }
        finally { DeliveryGate.Release(); }
    }

    private async Task<JobResult> RunCoreAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        JobResult Result(bool ok, string message) => new() { JobId = job.Id, Ok = ok, ExitCode = ok ? 0 : -1, Message = message };
        if (!SendFileRequest.TryParse(job, out var request, out var error)) return Result(false, error);
        string? staged = null;
        try
        {
            using var student = ManagedStudentAccess.Open();
            var staging = Path.Combine(Defaults.AgentDataDirectory, Defaults.HandoutStagingDirectoryName);
            HandoutDelivery.RejectReparseAncestors(staging);
            Directory.CreateDirectory(staging);
            ManagedStudentAccess.RequirePrivateDirectory(staging);
            staged = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".partial");
            await using (var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                Defaults.FileChunkBytes, FileOptions.Asynchronous))
            {
                var lastReport = DateTimeOffset.MinValue;
                await link.PullFileAsync(request.Reference, request.Sha256, destination, token, async received =>
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - lastReport < TimeSpan.FromSeconds(1)) return;
                    lastReport = now;
                    await report(new JobProgress { JobId = job.Id, Line = $"Receiving {request.Name}: {received} bytes." });
                });
                await destination.FlushAsync(token);
            }
            // Open the private verified source while SYSTEM, then drop privileges for
            // every operation on the student-writable destination, including cleanup.
            using var source = File.OpenRead(staged);
            var bytes = WindowsIdentity.RunImpersonated(student.Token,
                () => HandoutDelivery.Deliver(source, student.MaterialsDirectory, request, token));
            await report(new JobProgress { JobId = job.Id, Percent = 100, Line = $"Delivered {request.Name}: {bytes} verified bytes." });
            if (request.MayOpen)
            {
                if (student.SessionId is not { } id)
                    return Result(true, $"Delivered {request.Name}. It was not opened because the managed student is not signed in.");
                try { UserProcessLauncher.OpenDocument(id, student.Sid, Path.Combine(student.MaterialsDirectory, request.Name)); }
                catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or IOException)
                { return Result(true, $"Delivered {request.Name}, but Windows could not open it: {ex.Message}"); }
                return Result(true, $"Delivered {request.Name}; requested opening in the managed student session.");
            }
            return Result(true, $"Delivered {request.Name} to the managed student's Materials folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilePullException
            or InvalidOperationException or Win32Exception or SecurityException or System.Text.Json.JsonException)
        {
            return Result(false, $"Could not deliver {request.Name}: {ex.Message}");
        }
        finally
        {
            if (staged is not null)
            {
                try { File.Delete(staged); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { link.ReportForJob(job.Id, Event.Types.Severity.Warning, "files.cleanup_failed", "A private staged handout could not be removed."); }
            }
        }
    }
}
