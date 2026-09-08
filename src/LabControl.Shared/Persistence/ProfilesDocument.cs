namespace LabControl.Shared.Persistence;

/// <summary>What this device may do in a lab (M5, D-55).</summary>
public enum ProfileAccess
{
    Unknown = 0,

    /// <summary>Holds <c>lab-key.lck</c>: can enrol, renew, revoke, sign updates, export backups.</summary>
    Administrator = 1,

    /// <summary>Holds only a device certificate issued by an administrator: drives the room, never the CA.</summary>
    Teacher = 2,
}

/// <summary>Whether this device can open the lab right now (M5, D-55).</summary>
public enum ProfileAuthorization
{
    Unknown = 0,

    Authorized = 1,

    /// <summary>A lab file was imported; no device certificate yet.</summary>
    NeedsAuthorization = 2,

    /// <summary>A device request was written and awaits the administrator's grant.</summary>
    RequestPending = 3,

    Expired = 4,

    Revoked = 5,
}

/// <summary>How the lab arrived on this device.</summary>
public enum ProfileSource
{
    Unknown = 0,

    /// <summary>The pre-M5 single-lab directory, moved into <c>labs/&lt;lab_id&gt;/</c>.</summary>
    Migrated = 1,

    Backup = 2,

    LabFile = 3,

    Created = 4,
}

/// <summary>One saved lab on this device: the row the chooser shows and where its files are.</summary>
public sealed class ProfileRecord
{
    public string LabId { get; set; } = string.Empty;

    public string LabName { get; set; } = string.Empty;

    /// <summary>Relative to the data directory, with forward slashes: <c>labs/&lt;lab_id&gt;</c>.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>SHA-256 hex of the public CA certificate (DER). Same lab id with a different CA is refused.</summary>
    public string AuthorityFingerprint { get; set; } = string.Empty;

    public ProfileAccess Access { get; set; }

    public ProfileAuthorization Authorization { get; set; }

    public string InstanceId { get; set; } = string.Empty;

    public string InstanceName { get; set; } = string.Empty;

    /// <summary>When a teacher device's certificate runs out; 0 for an administrator profile.</summary>
    public long AccessExpiresUnix { get; set; }

    /// <summary>Saved metadata for the chooser; refreshed whenever the lab is used, never a limit.</summary>
    public int PcCount { get; set; }

    public long AddedAtUnix { get; set; }

    public long LastUsedUnix { get; set; }

    public ProfileSource Source { get; set; }

    /// <summary>SHA-256 of the public CA certificate (DER), lowercase hex — the value of <see cref="AuthorityFingerprint"/>.</summary>
    public static string AuthorityFingerprintOf(ReadOnlySpan<byte> authorityDer) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(authorityDer));
}

/// <summary>
/// <c>profiles.json</c> — the index of every lab saved on this device (M5, D-55). It holds
/// no secret and no certificate: those stay inside each lab's own directory. A device
/// without this file has at most the pre-M5 single lab, which the migration moves.
/// </summary>
public sealed class ProfilesDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.ProfilesSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.ProfilesSchemaVersion;

    /// <summary>The lab the chooser highlights and a single-lab device opens straight away.</summary>
    public string? LastUsedLabId { get; set; }

    public List<ProfileRecord> Labs { get; set; } = [];
}
