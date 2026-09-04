using System.Globalization;
using System.Text.Json;
using LabControl.Shared;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;

namespace LabControl.Console.Services;

/// <summary>
/// Appends every finished job to <c>logs/jobs-&lt;day&gt;.jsonl</c> (ARCHITECTURE §8, "Jobs
/// persist in logs/"). One line per PC per job, with the captured output, so an install
/// that failed on PC-07 last Tuesday can still be read.
/// </summary>
public sealed class JobJournal
{
    private readonly string _directory;
    private readonly Lock _gate = new();

    public JobJournal(string directory) => _directory = directory;

    public void Record(JobRecord job)
    {
        if (!job.IsFinished)
        {
            return;
        }

        var line = JsonSerializer.Serialize(new
        {
            job.Id,
            job.BatchId,
            job.AgentId,
            Kind = job.Kind.ToString(),
            State = job.State.ToString(),
            job.Args,
            job.CreatedAtUnix,
            job.DeliveredAtUnix,
            job.CompletedAtUnix,
            job.Ok,
            job.ExitCode,
            job.Message,
            job.ArtifactRef,
            job.Output,
        }, JsonStore.Options.WithoutIndent());

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var at = DateTimeOffset.FromUnixTimeSeconds(job.CompletedAtUnix).ToLocalTime();
                var file = Path.Combine(_directory, string.Format(CultureInfo.InvariantCulture, Defaults.JobLogFilePattern, at));
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
