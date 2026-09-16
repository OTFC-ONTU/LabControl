using LabControl.Shared.Policy;

namespace LabControl.Shared.Persistence;

/// <summary>
/// <c>policy.json</c> in the agent's data directory (M6, D-69): the sessions this PC is
/// enforcing and the machine settings they displaced. It is written by every change and
/// every restore pass, and read <b>only after</b> the machine has been restored to Windows
/// defaults — the document decides what is re-applied, never what is restored, so a file
/// that is torn, foreign, from a newer build or full of nulls costs nothing but a
/// <c>policy.unreadable</c> event.
/// <para>
/// Every member the journal reads is nullable on purpose: a hand-edited or half-written
/// file must be refused by <see cref="PolicyJournal"/>, not thrown at the service loop.
/// </para>
/// </summary>
public sealed class PolicyDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.PolicySchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.PolicySchemaVersion;

    /// <summary>The lab whose console set the policy; a file naming another lab is not this PC's.</summary>
    public string? LabId { get; set; }

    public long SavedAtUnix { get; set; }

    public ClassroomPolicy? Policy { get; set; }
}
