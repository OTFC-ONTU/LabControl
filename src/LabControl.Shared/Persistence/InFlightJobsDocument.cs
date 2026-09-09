using LabControl.Shared.Lab;

namespace LabControl.Shared.Persistence;

/// <summary>
/// <c>logs/jobs-inflight.json</c>: the jobs a session had delivered to PCs and not yet closed
/// when it ended (M5, D-57 items 3–4). The departure report promises that a script still
/// running on lab A shows its result when the console comes back to A; this document is how
/// the next session of the same lab, on the same console instance, knows which jobs to send
/// again so the PCs answer them from their ledgers.
/// <para>
/// It is a hint, never an authority: a file this build cannot read means nothing is owed, and
/// what may be sent again is decided by <see cref="InFlightJobPolicy"/>, not by the file.
/// </para>
/// </summary>
public sealed class InFlightJobsDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.InFlightJobsSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.InFlightJobsSchemaVersion;

    /// <summary>The lab and console instance that wrote the file; a file naming another pair is not this console's.</summary>
    public string LabId { get; set; } = string.Empty;

    public string InstanceId { get; set; } = string.Empty;

    /// <summary>When the session that owed these jobs closed; the age bound counts from here.</summary>
    public long SavedAtUnix { get; set; }

    public List<InFlightJob> Jobs { get; set; } = [];
}
