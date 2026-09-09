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

    /// <summary>The lab the batch ran in and the console instance that delivered it (M5, D-57 item 4); every row repeats them.</summary>
    public string LabId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
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

    /// <summary>
    /// Rows a saved batch document holds that the live queue no longer has (M5, D-57 item 4):
    /// the PCs that finished before the console left the lab. They are merged into every later
    /// capture, so restoring the batch adds to its log instead of shrinking it.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<JobLogSnapshot>> _carried = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Registers a batch whose rows a new session brought back from <c>jobs-inflight.json</c>
    /// (M5, D-57 item 4). The live queue holds only the jobs that were still unfinished, so the
    /// document already on disk — with the results of the PCs that finished first — is read and
    /// its missing rows are kept. A file that cannot be read leaves the existing behaviour: the
    /// batch is registered from the live rows alone.
    /// </summary>
    public void Restore(string batchId)
    {
        lock (_gate)
        {
            var existing = TryLoad(batchId);
            var live = jobs.SnapshotBatch(batchId);
            var liveIds = live.Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
            var carried = existing?.Computers.SelectMany(pc => pc.Jobs).Where(j => !liveIds.Contains(j.Id)).ToArray() ?? [];
            if (live.Count == 0 && carried.Length == 0)
            {
                return;
            }

            var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var pc in existing?.Computers ?? [])
            {
                numbers[pc.AgentId] = pc.Number;
            }

            foreach (var agentId in live.Select(j => j.AgentId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // A PC the roster already named keeps the number it was recorded with (D-43 item 1).
                numbers.TryAdd(agentId, numberOf(agentId));
            }

            _numbers[batchId] = numbers;
            if (carried.Length > 0)
            {
                _carried[batchId] = carried;
            }

            Save(Capture(batchId));
        }
    }

    private JobBatchLogDocument? TryLoad(string batchId)
    {
        try
        {
            return JsonStore.LoadIfExists<JobBatchLogDocument>(PathFor(batchId), JobBatchLogDocument.Migrations);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SchemaVersionException)
        {
            return null;
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
        var live = jobs.SnapshotBatch(batchId);
        var liveIds = live.Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
        var carried = _carried.TryGetValue(batchId, out var kept) ? kept.Where(j => !liveIds.Contains(j.Id)) : [];
        var snapshot = live.Concat(carried)
            .OrderBy(j => j.AgentId, StringComparer.Ordinal).ThenBy(j => j.Id, StringComparer.Ordinal)
            .ToArray();
        if (snapshot.Length == 0 || !_numbers.TryGetValue(batchId, out var numbers))
        {
            throw new InvalidOperationException("This batch has no registered job logs.");
        }

        return new JobBatchLogDocument
        {
            BatchId = batchId,
            LabId = jobs.LabId,
            InstanceId = jobs.InstanceId,
            CapturedAtUnix = clock().ToUnixTimeSeconds(),
            IsComplete = snapshot.All(j => j.State is JobState.Succeeded or JobState.Failed or JobState.NotDelivered or JobState.TimedOut),
            Computers = snapshot.GroupBy(j => j.AgentId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PcJobLog(numbers.TryGetValue(group.Key, out var number) ? number : numberOf(group.Key), group.Key, group.ToArray()))
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
                        LabId = document.LabId,
                        InstanceId = document.InstanceId,
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
