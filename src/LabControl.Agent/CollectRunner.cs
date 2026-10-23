using System.ComponentModel;
using System.Security;
using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;

namespace LabControl.Agent;

/// <summary>
/// <c>collect_files</c> on a real PC (M6 portion 5, D-77): the recorded student's folder is
/// zipped into a private staging file and uploaded to the console through the grant
/// <c>RequestUpload</c> hands out. The output is opened while SYSTEM; every read of the
/// student's files happens under the student's own token, so nothing the student could not
/// open is ever collected. An installation without the managed student (D-40) refuses.
/// </summary>
internal sealed class CollectRunner(AgentLink link)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<JobResult> RunAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        while (!await Gate.WaitAsync(TimeSpan.FromSeconds(10), token))
            await report(new JobProgress { JobId = job.Id, Line = "Waiting for this PC's previous collection." });
        try { return await RunCoreAsync(job, report, token); }
        finally { Gate.Release(); }
    }

    /// <summary>The folder a request names, under the student's own known folder or profile.</summary>
    internal static string Resolve(ManagedStudentAccess student, CollectRequest request)
    {
        var (root, below) = request.Split();
        var start = root switch
        {
            CollectRoot.Desktop => student.KnownFolder(Defaults.StudentDesktopKnownFolderId),
            CollectRoot.Documents => student.KnownFolder(Defaults.StudentDocumentsKnownFolderId),
            CollectRoot.Downloads => student.KnownFolder(Defaults.StudentDownloadsKnownFolderId),
            _ => student.ProfileDirectory(),
        };
        var folder = Path.GetFullPath(Path.Combine([start, .. below]));
        var profile = student.ProfileDirectory() + Path.DirectorySeparatorChar;
        if (!folder.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The folder to collect must be inside the student's profile.");
        return folder;
    }

    private async Task<JobResult> RunCoreAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        JobResult Result(bool ok, string message, string artifact = "") =>
            new() { JobId = job.Id, Ok = ok, ExitCode = ok ? 0 : -1, Message = message, ArtifactRef = artifact };
        if (!CollectRequest.TryParse(job, out var request, out var error)) return Result(false, error);
        string? staged = null;
        try
        {
            var staging = Path.Combine(Defaults.AgentDataDirectory, Defaults.CollectStagingDirectoryName);
            HandoutDelivery.RejectReparseAncestors(staging);
            Directory.CreateDirectory(staging);
            ManagedStudentAccess.RequirePrivateDirectory(staging);
            staged = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".zip");
            await using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                Defaults.FileChunkBytes, FileOptions.Asynchronous);

            // The student's access — and with it the Setup lock every handout and reset needs — is
            // held only while packing; the upload needs neither (review 2026-10-23).
            CollectSummary summary;
            using (var student = ManagedStudentAccess.Open())
            {
                var folder = Resolve(student, request);
                // Reads drop to the student's token; the output stays the SYSTEM handle opened above.
                var exists = WindowsIdentity.RunImpersonated(student.Token, () => Directory.Exists(folder));
                if (!exists)
                    return Result(true, $"{request.Folder} does not exist on this PC: nothing to collect.");
                await report(new JobProgress { JobId = job.Id, Line = $"Packing {request.Folder}." });
                summary = WindowsIdentity.RunImpersonated(student.Token,
                    () => WorkArchive.Create(folder, output, Defaults.CollectMaxBytes, Defaults.CollectMaxFiles, token));
            }

            await output.FlushAsync(token);
            output.Position = 0;
            var sha256 = FileHash.Sha256Hex(output);
            var size = output.Length;
            output.Position = 0;

            var reference = await link.RequestUploadAsync(job.Id, size, sha256, token);
            await report(new JobProgress { JobId = job.Id, Percent = 50, Line = $"Sending {summary.Files} file(s), {size} bytes packed." });

            // The upload says nothing on Link; a line every few seconds keeps a slow upload over
            // Wi-Fi from looking like a silent, dead job to the console's timeout.
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(token);
            var keepAlive = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(Defaults.CollectProgressInterval, sending.Token);
                        await report(new JobProgress { JobId = job.Id, Percent = 50, Line = "Still sending the collected work." });
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None);
            try
            {
                await link.PushFileAsync(reference, sha256, output, token);
            }
            finally
            {
                await sending.CancelAsync();
                await keepAlive;
            }

            return Result(true, Describe(summary), reference);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilePushException
            or InvalidOperationException or Win32Exception or SecurityException or ArgumentException)
        {
            return Result(false, $"Could not collect {request.Folder}: {ex.Message}");
        }
        finally
        {
            if (staged is not null)
            {
                try { File.Delete(staged); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { link.ReportForJob(job.Id, Event.Types.Severity.Warning, "files.cleanup_failed", "A private staged collection could not be removed."); }
            }
        }
    }

    internal static string Describe(CollectSummary summary)
    {
        var text = $"Collected {summary.Files} file(s), {summary.Bytes} bytes.";
        if (summary.InUse.Count > 0) text += $" Could not read (open in a program?): {string.Join(", ", summary.InUse)}.";
        if (summary.Links > 0) text += $" {summary.Links} link(s) left out.";
        if (summary.Skipped.Count > 0) text += $" Left out (a name or depth the console cannot store): {string.Join(", ", summary.Skipped)}.";
        return text;
    }
}
