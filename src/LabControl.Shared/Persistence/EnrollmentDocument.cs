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

    /// <summary>
    /// Set when a newer USB payload was written: a new stick replaces the old one, so the
    /// unused codes of every earlier stick stop working (D-28).
    /// </summary>
    public long VoidedAtUnix { get; set; }

    /// <summary>
    /// Set when the code arrived in a backup imported into a saved lab (M5, D-60): two holders of
    /// the CA cannot enforce single use from separate journals, so imported codes are refused
    /// until the administrator activates them on this profile. 0 = active.
    /// </summary>
    public long DormantSinceImportUnix { get; set; }

    /// <summary>The console instance that wrote the batch this code belongs to; empty for codes from before M5.</summary>
    public string? IssuedByInstanceId { get; set; }

    /// <summary>A burned code is refused and the attempt is reported as an event (D-14).</summary>
    public bool IsBurned => UsedAtUnix != 0;

    public bool IsVoided => VoidedAtUnix != 0;

    /// <summary>Imported and not yet activated here (D-60).</summary>
    public bool IsDormant => DormantSinceImportUnix != 0;

    /// <summary>Still able to enrol a PC: neither used, voided nor dormant.</summary>
    public bool IsUsable => !IsBurned && !IsVoided && !IsDormant;

    /// <summary>Neither used nor voided: usable once activated, and voided by a newer stick (D-28).</summary>
    public bool IsOutstanding => !IsBurned && !IsVoided;
}

/// <summary>One USB batch and the console that wrote it (M5, D-60): what <i>Use codes from the imported backup</i> names.</summary>
public sealed class EnrollmentBatchRecord
{
    public string Batch { get; set; } = string.Empty;

    public string? IssuedByInstanceId { get; set; }

    public string? IssuedByInstanceName { get; set; }

    public long CreatedAtUnix { get; set; }

    public long DormantSinceImportUnix { get; set; }

    public bool IsDormant => DormantSinceImportUnix != 0;
}

/// <summary>
/// <c>enrollment.json</c> — the codes this console has issued. Not a secret store; the
/// lab key is what enrollment actually depends on.
/// </summary>
public sealed class EnrollmentDocument : ISchemaVersioned
{
    /// <summary>
    /// 1 → 2 (M5, D-60): <c>codes[].dormant_since_import_unix/issued_by_instance_id</c> and
    /// <c>batches[]</c> were added, all optional; a version-1 document reads as version 2 with
    /// them absent, so the step rewrites nothing (D-20).
    /// </summary>
    public static readonly SchemaMigrations Migrations = new(
        Defaults.EnrollmentSchemaVersion,
        new SchemaMigration(1, root => root));

    public int SchemaVersion { get; set; } = Defaults.EnrollmentSchemaVersion;

    public string LabId { get; set; } = string.Empty;

    public List<EnrollmentCodeRecord> Codes { get; set; } = [];

    /// <summary>One entry per USB batch, naming the console that wrote it (D-60).</summary>
    public List<EnrollmentBatchRecord> Batches { get; set; } = [];
}
