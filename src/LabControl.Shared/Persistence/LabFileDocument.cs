using LabControl.Shared.Identity;

namespace LabControl.Shared.Persistence;

/// <summary>
/// The one shape every file exchanged offline has (M5, D-56 item 1): a JSON document with
/// <c>schema_version</c> first, the kind, the lab it is about, the payload as base64 of
/// its UTF-8 JSON bytes, and a P-256/SHA-256 signature (IEEE P1363) over
/// <i>domain + payload bytes</i>. Who signs and what verifies it depends on the kind;
/// <c>Shared/Lab/LabFile.cs</c> and <c>DeviceAuthorization.cs</c> know.
/// </summary>
public abstract class SignedEnvelopeDocument : ISchemaVersioned
{
    public int SchemaVersion { get; set; }

    /// <summary><c>lab_file</c>, <c>device_request</c> or <c>device_grant</c>; refused when it does not match the file's extension.</summary>
    public string Kind { get; set; } = string.Empty;

    public string LabId { get; set; } = string.Empty;

    /// <summary>In the clear so a picker row can name the lab before anything is verified.</summary>
    public string LabName { get; set; } = string.Empty;

    /// <summary>Base64 of the payload's UTF-8 JSON bytes — the exact bytes the signature covers.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Base64 of the 64-byte P-256 signature over <i>domain + payload bytes</i>.</summary>
    public string Signature { get; set; } = string.Empty;
}

/// <summary><c>.lclab</c> — the routine lab file a teacher device imports (D-56 item 2). Signed by the lab key.</summary>
public sealed class LabFileDocument : SignedEnvelopeDocument
{
    public const string KindValue = "lab_file";

    public static readonly SchemaMigrations Migrations = new(Defaults.LabFileSchemaVersion);

    public LabFileDocument()
    {
        SchemaVersion = Defaults.LabFileSchemaVersion;
        Kind = KindValue;
    }
}

/// <summary><c>.lcreq</c> — a teacher device's request for its own identity (D-56 item 4). Signed by the device key.</summary>
public sealed class DeviceRequestDocument : SignedEnvelopeDocument
{
    public const string KindValue = "device_request";

    public static readonly SchemaMigrations Migrations = new(Defaults.DeviceRequestSchemaVersion);

    public DeviceRequestDocument()
    {
        SchemaVersion = Defaults.DeviceRequestSchemaVersion;
        Kind = KindValue;
    }
}

/// <summary><c>.lcgrant</c> — the administrator's answer: the device leaf, its endorsement, a lab snapshot (D-56 item 4). Signed by the lab key.</summary>
public sealed class DeviceGrantDocument : SignedEnvelopeDocument
{
    public const string KindValue = "device_grant";

    public static readonly SchemaMigrations Migrations = new(Defaults.DeviceGrantSchemaVersion);

    public DeviceGrantDocument()
    {
        SchemaVersion = Defaults.DeviceGrantSchemaVersion;
        Kind = KindValue;
    }
}

/// <summary>One PC as a lab file describes it: what a teacher console needs to show the tile before the PC links.</summary>
public sealed class RosterEntry
{
    public string AgentId { get; set; } = string.Empty;

    public int Number { get; set; }

    public string Hostname { get; set; } = string.Empty;

    public string Mac { get; set; } = string.Empty;

    public string? LastIp { get; set; }

    public string CertificateSerial { get; set; } = string.Empty;

    public long CertificateNotAfterUnix { get; set; }
}

/// <summary>
/// What a <c>.lclab</c> carries (D-56 item 2) and what a grant embeds as its snapshot. It
/// must never carry the lab key document, a wrapping, the CA private key, recovery
/// material, an enrollment document or code, an instance document or private key, a
/// package binary or a log — a test serialises one and asserts every absence.
/// </summary>
public sealed class LabFilePayload
{
    public string LabId { get; set; } = string.Empty;

    public string LabName { get; set; } = string.Empty;

    public long IssuedAtUnix { get; set; }

    public string IssuedByInstanceId { get; set; } = string.Empty;

    public string IssuedByInstanceName { get; set; } = string.Empty;

    /// <summary>The public CA certificate, DER: what the device pins on first import.</summary>
    public byte[] Authority { get; set; } = [];

    /// <summary>SHA-256 hex of <see cref="Authority"/>; must equal the profile's on re-import.</summary>
    public string AuthorityFingerprint { get; set; } = string.Empty;

    /// <summary>Monotonic per issuing console; an older snapshot never replaces the layout.</summary>
    public long SnapshotVersion { get; set; }

    public List<RosterEntry> Roster { get; set; } = [];

    public List<LayoutTile> Layout { get; set; } = [];

    /// <summary>Signed entries, self-authenticating: unioned into the importer's set (D-21).</summary>
    public List<RevocationRecord> Revocations { get; set; } = [];

    /// <summary>The script library; imported only into a profile that has no <c>scripts.json</c> yet.</summary>
    public ScriptsDocument? Scripts { get; set; }
}

/// <summary>What a <c>.lcreq</c> carries (D-56 item 4).</summary>
public sealed class DeviceRequestPayload
{
    public const string TeacherAccess = "teacher";

    public string LabId { get; set; } = string.Empty;

    public string InstanceId { get; set; } = string.Empty;

    public string InstanceName { get; set; } = string.Empty;

    public string RequestedAccess { get; set; } = TeacherAccess;

    public long CreatedAtUnix { get; set; }

    /// <summary>PKCS#10 from the device's pending key, <c>CN</c> = the instance name; only the public half travels.</summary>
    public byte[] Csr { get; set; } = [];

    public string ConsoleVersion { get; set; } = string.Empty;
}

/// <summary>What a <c>.lcgrant</c> carries (D-56 item 4).</summary>
public sealed class DeviceGrantPayload
{
    public string LabId { get; set; } = string.Empty;

    public string InstanceId { get; set; } = string.Empty;

    /// <summary>The device leaf, DER: SAN <c>console/&lt;instance&gt;</c>, <c>OU=LabControl Teacher</c>.</summary>
    public byte[] Certificate { get; set; } = [];

    /// <summary>The lab key's signature over <c>lab|inst|base64(pub)</c>, what the device's beacons carry (D-24).</summary>
    public byte[] Endorsement { get; set; } = [];

    public long IssuedAtUnix { get; set; }

    public long ExpiresUnix { get; set; }

    /// <summary>A full lab file payload, applied on the device as a refresh.</summary>
    public LabFilePayload Snapshot { get; set; } = new();
}

/// <summary>Where a teacher profile stands (M5, D-56 item 3).</summary>
public enum AccessState
{
    Unknown = 0,

    /// <summary>A lab file was imported; the pending identity exists, nothing beacons.</summary>
    NeedsAuthorization = 1,

    /// <summary>A request file was written; re-running it regenerates the same file from the same key.</summary>
    RequestPending = 2,

    /// <summary>The grant was imported; <c>instance.json</c> holds the device leaf.</summary>
    Authorized = 3,

    Expired = 4,

    Revoked = 5,
}

/// <summary>
/// <c>access.json</c> — a teacher profile's authorization state and pending identity
/// (D-56 item 3). Administrator profiles have none: the lab key is their authorization.
/// The pending private key is protected by the OS keystore like an instance key.
/// </summary>
public sealed class AccessDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.AccessSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.AccessSchemaVersion;

    public string LabId { get; set; } = string.Empty;

    public AccessState State { get; set; }

    /// <summary>The device's instance id, minted at import and kept across renewals.</summary>
    public string InstanceId { get; set; } = string.Empty;

    public string InstanceName { get; set; } = string.Empty;

    /// <summary>The public CA certificate pinned at import, DER: what verifies later files.</summary>
    public byte[] Authority { get; set; } = [];

    /// <summary>The key a request was (or will be) made from; <c>null</c> once a grant has consumed it.</summary>
    public ProtectedSecret? PendingKey { get; set; }

    public long RequestedAtUnix { get; set; }

    /// <summary>SHA-256 hex of the public key the request carries (a CSR's bytes vary per signing), so a re-run is recognisably the same request.</summary>
    public string? CsrFingerprint { get; set; }

    public long AuthorizedAtUnix { get; set; }

    /// <summary>When the device leaf runs out; 0 until authorized.</summary>
    public long ExpiresUnix { get; set; }

    public long RevokedAtUnix { get; set; }

    /// <summary>Names the pending key inside the OS keystore, apart from the instance key it may replace.</summary>
    public static string PendingKeyReference(string instanceId) => ConsoleInstance.ProtectionReference(instanceId) + "-pending";
}
