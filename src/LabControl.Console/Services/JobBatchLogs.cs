using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using LabControl.Shared;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;

namespace LabControl.Console.Services;

public sealed class JobBatchLogDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.JobBatchSchemaVersion);
    public int SchemaVersion { get; set; } = Defaults.JobBatchSchemaVersion;
    public string BatchId { get; set; } = string.Empty;
    public long CapturedAtUnix { get; set; }
    public bool IsComplete { get; set; }
    public List<PcJobLog> Computers { get; set; } = [];
}

public sealed record PcJobLog(int Number, string AgentId, IReadOnlyList<JobLogSnapshot> Jobs);

/// <summary>
/// Durable batch snapshots and a ZIP with one JSON log per PC (M4, D-43).
/// Uses the existing job output; it never reads agent disks, credentials or job arguments.
/// </summary>
public sealed class JobBatchLogs(string logsDirectory, JobQueue jobs, Func<string, int> numberOf, Func<DateTimeOffset> clock)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, int>> _numbers = new(StringComparer.Ordinal);

    public void Register(string batchId)
    {
        lock (_gate)
        {
            var snapshot = jobs.SnapshotBatch(batchId);
            if (snapshot.Count == 0)
            {
                return;
            }

            _numbers[batchId] = snapshot.Select(j => j.AgentId).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(id => id, numberOf, StringComparer.OrdinalIgnoreCase);
            Save(Capture(batchId));
        }
    }

    public void Record(string batchId)
    {
        lock (_gate)
        {
            // Create raises Updated before the entire roster exists. Register saves it once complete.
            if (_numbers.ContainsKey(batchId))
            {
                Save(Capture(batchId));
            }
        }
    }

    public string PathFor(string batchId) => Path.Combine(logsDirectory,
        Defaults.JobBatchesDirectoryName, Guid.Parse(batchId).ToString("d") + ".json");

    private JobBatchLogDocument Capture(string batchId)
    {
        var snapshot = jobs.SnapshotBatch(batchId);
        if (snapshot.Count == 0 || !_numbers.TryGetValue(batchId, out var numbers))
        {
            throw new InvalidOperationException("This batch has no registered job logs.");
        }

        return new JobBatchLogDocument
        {
            BatchId = batchId,
            CapturedAtUnix = clock().ToUnixTimeSeconds(),
            IsComplete = snapshot.All(j => j.State is JobState.Succeeded or JobState.Failed or JobState.NotDelivered or JobState.TimedOut),
            Computers = snapshot.GroupBy(j => j.AgentId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PcJobLog(numbers[group.Key], group.Key, group.ToArray()))
                .OrderBy(pc => pc.Number).ThenBy(pc => pc.AgentId, StringComparer.Ordinal).ToList(),
        };
    }

    private void Save(JobBatchLogDocument document) =>
        JsonStore.Save(PathFor(document.BatchId), document, JobBatchLogDocument.Migrations);

    /// <summary>Exports a consistent live snapshot; pending/running PCs remain explicitly unfinished.</summary>
    public void Export(string batchId, string destination)
    {
        JobBatchLogDocument document;
        lock (_gate)
        {
            document = Capture(batchId);
        }

        var fullPath = Path.GetFullPath(destination);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, Defaults.JobBatchManifestFileName, document);
                for (var index = 0; index < document.Computers.Count; index++)
                {
                    var pc = document.Computers[index];
                    // The index also separates an unknown or replaced PC with the same number.
                    var name = string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, pc.Number);
                    WriteEntry(archive, $"{name}-{index + 1:D2}.json", new JobBatchLogDocument
                    {
                        BatchId = document.BatchId,
                        CapturedAtUnix = document.CapturedAtUnix,
                        IsComplete = pc.Jobs.All(j => j.State is JobState.Succeeded or JobState.Failed or JobState.NotDelivered or JobState.TimedOut),
                        Computers = [pc],
                    });
                }
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, JobBatchLogDocument document)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
        JsonSerializer.Serialize(stream, document, JsonStore.Options);
    }
}
