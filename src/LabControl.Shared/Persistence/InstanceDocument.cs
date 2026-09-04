namespace LabControl.Shared.Persistence;

/// <summary>
/// <c>instance.json</c> — this teacher machine's own identity within the lab
/// (ARCHITECTURE §3.1). It is never shared and never restored from a backup: a new machine
/// mints its own, and that is the point.
/// </summary>
public sealed class InstanceDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.InstanceSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.InstanceSchemaVersion;

    public string LabId { get; set; } = string.Empty;

    public string InstanceId { get; set; } = string.Empty;

    /// <summary>What logs and the other machine's banner call this console: "MacBook-2026".</summary>
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>The leaf certificate, DER, signed by the lab key.</summary>
    public byte[] Certificate { get; set; } = [];

    /// <summary>
    /// The lab key's signature over this instance's compressed public key. Minted with the
    /// certificate so that beaconing never needs the lab key again (PROTOCOL, beacon <c>end</c>).
    /// </summary>
    public byte[] Endorsement { get; set; } = [];

    /// <summary>
    /// The instance private key, protected by the operating system (macOS Keychain, Windows
    /// DPAPI, libsecret) or by the encrypted-file fallback. Never the raw key.
    /// </summary>
    public ProtectedSecret PrivateKey { get; set; } = new();

    public long CreatedAtUnix { get; set; }

    /// <summary>
    /// When a backup of the lab key was last exported, and where the teacher said it went.
    /// The first-run wizard refuses to finish without one (ARCHITECTURE §3.7.3).
    /// </summary>
    public long BackupExportedAtUnix { get; set; }

    public string? BackupLocation { get; set; }

    /// <summary>
    /// <see cref="JsonStore.Fingerprint"/> of <c>lab-key.lck</c> as it was when the backup was
    /// exported. A backup is stale when the file no longer matches — a holder added or a
    /// recovery code reprinted on <i>any</i> teacher machine changes the file, and a date
    /// alone cannot see that (D-25).
    /// </summary>
    public string? BackupFingerprint { get; set; }

    /// <summary>Set once the teacher has confirmed they wrote the recovery code down.</summary>
    public bool RecoveryCodeAcknowledged { get; set; }
}
