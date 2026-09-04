namespace LabControl.Shared.Persistence;

/// <summary>
/// One single-use enrollment code and what became of it (D-14). Codes are kept in the
/// clear: the worst a found USB stick allows is enrolling a bogus PC, which shows up in
/// the console as an unexpected machine and is removed with one click.
/// </summary>
public sealed class EnrollmentCodeRecord
{
    /// <summary>The canonical, separator-free form. Comparison is always canonical.</summary>
    public string Code { get; set; } = string.Empty;

    public long CreatedAtUnix { get; set; }

    /// <summary>Which USB batch this code was written to, so a lost stick can be identified.</summary>
    public string Batch { get; set; } = string.Empty;

    public long UsedAtUnix { get; set; }

    public string? UsedByAgentId { get; set; }

    public int UsedByNumber { get; set; }

    /// <summary>A burned code is refused and the attempt is reported as an event (D-14).</summary>
    public bool IsBurned => UsedAtUnix != 0;
}

/// <summary>
/// <c>enrollment.json</c> — the codes this console has issued. Not a secret store; the
/// lab key is what enrollment actually depends on.
/// </summary>
public sealed class EnrollmentDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.EnrollmentSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.EnrollmentSchemaVersion;

    public string LabId { get; set; } = string.Empty;

    public List<EnrollmentCodeRecord> Codes { get; set; } = [];
}
