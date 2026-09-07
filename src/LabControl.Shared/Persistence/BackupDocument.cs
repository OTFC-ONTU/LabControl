using LabControl.Shared.Identity;

namespace LabControl.Shared.Persistence;

/// <summary>
/// The backup archive (ARCHITECTURE §4, D-26): the lab key document exactly as it stands —
/// it is already encrypted under its holders' passphrases and the recovery code — and
/// everything else the lab needs sealed under the same master key. Whoever can open the
/// lab key can open the backup; nobody else can read even the machine list.
/// </summary>
public sealed class BackupDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.BackupSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.BackupSchemaVersion;

    public string LabId { get; set; } = string.Empty;

    public string LabName { get; set; } = string.Empty;

    public long ExportedAtUnix { get; set; }

    /// <summary>The console instance that wrote the backup, so a stale copy can be told apart.</summary>
    public string ExportedBy { get; set; } = string.Empty;

    /// <summary><c>lab-key.lck</c>, verbatim.</summary>
    public LabKeyDocument LabKey { get; set; } = new();

    /// <summary>A serialized <see cref="BackupPayload"/>, AES-256-GCM under the master key.</summary>
    public SealedSecret Payload { get; set; } = new();
}

/// <summary>What the sealed part of a backup holds.</summary>
public sealed class BackupPayload
{
    /// <summary><c>lab.json</c>: machines, instances, revocations, layout.</summary>
    public LabDocument Lab { get; set; } = new();

    /// <summary>The package catalog files (<c>packages/*.yaml</c>) by relative path. Empty until M6.</summary>
    public Dictionary<string, string> Catalog { get; set; } = [];

    /// <summary>
    /// <c>enrollment.json</c>: the codes written to USB sticks and what became of them. Carried
    /// so that a PC installed from a stick written on one teacher machine can still enrol on
    /// the machine that replaced it (D-28). Null in backups from before this field existed.
    /// </summary>
    public EnrollmentDocument? Enrollment { get; set; }

    /// <summary><c>scripts.json</c>: the script library (D-31 item 4). Null in backups from before M4.</summary>
    public ScriptsDocument? Scripts { get; set; }
}
