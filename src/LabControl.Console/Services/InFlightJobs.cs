using LabControl.Shared;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;

namespace LabControl.Console.Services;

/// <summary>What a lab's <c>jobs-inflight.json</c> held: the rows and the moment they were saved.</summary>
public sealed record InFlightJobsFile(IReadOnlyList<InFlightJob> Jobs, DateTimeOffset SavedAt)
{
    public static readonly InFlightJobsFile Nothing = new([], DateTimeOffset.UnixEpoch);
}

/// <summary>
/// Reads and writes <see cref="InFlightJobsDocument"/> for one lab. A missing, empty,
/// unreadable or foreign file all mean the same thing: nothing is owed (M5, D-57 item 4).
/// </summary>
public static class InFlightJobs
{
    /// <summary>
    /// The rows saved for this lab and instance. Anything wrong with the file — corrupt JSON, a
    /// schema a newer build wrote (D-20), a lab or instance that is not this one — yields
    /// nothing owed and <paramref name="problem"/>, never an exception: opening a lab must not
    /// fail because of a log.
    /// </summary>
    public static InFlightJobsFile Load(LabStore store, string labId, string instanceId, out string? problem)
    {
        problem = null;
        InFlightJobsDocument? document;
        try
        {
            document = JsonStore.LoadIfExists<InFlightJobsDocument>(store.InFlightJobsPath, InFlightJobsDocument.Migrations);
        }
        catch (Exception ex)
        {
            // Any exception at all: SchemaVersionException from a newer build, InvalidDataException
            // from a truncated file, an IO error, a value the deserializer refuses.
            problem = ex.Message;
            return InFlightJobsFile.Nothing;
        }

        if (document is null)
        {
            return InFlightJobsFile.Nothing;
        }

        if (!string.Equals(document.LabId, labId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(document.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"it was written for lab '{document.LabId}' by console instance '{document.InstanceId}'";
            return InFlightJobsFile.Nothing;
        }

        return new InFlightJobsFile(document.Jobs, DateTimeOffset.FromUnixTimeSeconds(document.SavedAtUnix));
    }

    /// <summary>Writes the current in-flight set, or removes the file when there is nothing to keep.</summary>
    public static void Save(LabStore store, JobQueue jobs, DateTimeOffset now)
    {
        var inFlight = jobs.SnapshotInFlight();
        if (inFlight.Count == 0)
        {
            if (File.Exists(store.InFlightJobsPath))
            {
                File.Delete(store.InFlightJobsPath);
            }

            return;
        }

        JsonStore.Save(store.InFlightJobsPath, new InFlightJobsDocument
        {
            LabId = jobs.LabId,
            InstanceId = jobs.InstanceId,
            SavedAtUnix = now.ToUnixTimeSeconds(),
            Jobs = inFlight.ToList(),
        }, InFlightJobsDocument.Migrations);
    }
}
