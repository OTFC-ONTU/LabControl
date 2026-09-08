namespace LabControl.Shared.Persistence;

/// <summary>
/// One student PC as this console knows it. The list is a <b>cache of the lab</b>, not its
/// truth (ARCHITECTURE §3.7): a PC that connects with a valid lab-issued certificate and is
/// not here is added from its <c>Hello</c>, which is what lets a second teacher machine
/// catch up without being told anything.
/// </summary>
public sealed class MachineRecord
{
    public string AgentId { get; set; } = string.Empty;

    /// <summary>The sticker on the PC. Also the tile order and the default layout.</summary>
    public int Number { get; set; }

    public string Hostname { get; set; } = string.Empty;

    /// <summary>Identifies the PC together with the agent id; Wake-on-LAN needs it.</summary>
    public string Mac { get; set; } = string.Empty;

    public string CertificateSerial { get; set; } = string.Empty;

    /// <summary>
    /// When the PC's certificate runs out, read off the leaf it presented at <c>Hello</c>.
    /// Drives the <i>certificates need renewing</i> banner (D-25, ARCHITECTURE §3.8).
    /// </summary>
    public long CertificateNotAfterUnix { get; set; }

    /// <summary>The address the PC last connected from; Wake-on-LAN sends a directed packet here too.</summary>
    public string? LastIp { get; set; }

    public long EnrolledAtUnix { get; set; }

    public long LastSeenUnix { get; set; }

    /// <summary>Last authenticated Setup advisory snapshot, retained while offline.</summary>
    public string[] SetupReadinessCodes { get; set; } = [];

    public string? AgentVersion { get; set; }

    public int ProtocolVersion { get; set; }

    public string? LoggedOnUser { get; set; }

    /// <summary>Which console instance this PC was last linked to; drives the "held by" banner.</summary>
    public string? LastInstanceId { get; set; }

    /// <summary>
    /// When <see cref="LastInstanceId"/> was positively learned — a take-over reason or a
    /// <c>Hello.previous_instance_id</c> — rather than presumed (M5 §4.6). 0 = never.
    /// </summary>
    public long LastInstanceObservedUnix { get; set; }

    /// <summary>Revocation serials this PC has confirmed holding (M5 §3.5); shows delivery, never claims it.</summary>
    public string[] RevocationSerialsSeen { get; set; } = [];
}

/// <summary>
/// A console installation this lab has seen — this machine, or another teacher machine
/// (ARCHITECTURE §3.7.1). Settings lists them; the banner names them.
/// </summary>
public sealed class InstanceRecord
{
    public string InstanceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string CertificateSerial { get; set; } = string.Empty;

    public long FirstSeenUnix { get; set; }

    public long LastSeenUnix { get; set; }

    /// <summary>True for the console that owns this copy of <c>lab.json</c>.</summary>
    public bool IsThisMachine { get; set; }

    /// <summary>The administrator's device book (M5 §2.4): what this console was authorized as.</summary>
    public ProfileAccess Access { get; set; }

    public long AuthorizedAtUnix { get; set; }

    public long RevokedAtUnix { get; set; }

    /// <summary>
    /// Every leaf serial this instance was ever recorded with, the current one included
    /// (D-56 item 6): a withdrawal revokes all of them, not only the latest. Optional within
    /// schema 2; empty for a record written by an older build.
    /// </summary>
    public List<string> CertificateSerials { get; set; } = [];

    /// <summary>
    /// SHA-256 hex of the public key the last grant certified (D-56 item 4): a renewal must
    /// present a fresh key, so re-approving an old request cannot mint a second leaf for
    /// the same one. Empty for administrator machines and older records.
    /// </summary>
    public string PublicKeyFingerprint { get; set; } = string.Empty;
}

/// <summary>Where a PC's tile sits in the room view. Absent tiles fall back to number order.</summary>
public sealed class LayoutTile
{
    public int Number { get; set; }

    public int Column { get; set; }

    public int Row { get; set; }
}

/// <summary>
/// <c>lab.json</c> — everything this console knows about the lab that is not a secret.
/// Machines, the consoles it has seen, the revocations it holds and the room layout.
/// </summary>
public sealed class LabDocument : ISchemaVersioned
{
    /// <summary>
    /// 1 → 2 (M5, D-55): <c>instances[].access/authorized_at_unix/revoked_at_unix</c> and
    /// <c>machines[].revocation_serials_seen/last_instance_observed_unix</c> were added, all
    /// optional. A version-1 document is a valid version-2 document with them absent, so the
    /// step rewrites nothing; it exists so that the chain is complete (D-20) and a version-1
    /// file is stamped 2 on its next save.
    /// </summary>
    public static readonly SchemaMigrations Migrations = new(
        Defaults.LabSchemaVersion,
        new SchemaMigration(1, root => root));

    public int SchemaVersion { get; set; } = Defaults.LabSchemaVersion;

    public string LabId { get; set; } = string.Empty;

    public string LabName { get; set; } = string.Empty;

    public List<MachineRecord> Machines { get; set; } = [];

    public List<InstanceRecord> Instances { get; set; } = [];

    /// <summary>The union of every signed revocation this console has seen (D-21).</summary>
    public List<RevocationRecord> Revocations { get; set; } = [];

    /// <summary>Hand-arranged tile positions. Travels only in a backup, by design (§3.7).</summary>
    public List<LayoutTile> Layout { get; set; } = [];

    /// <summary>
    /// The <c>snapshot_version</c> of the newest lab file or grant applied here (M5, D-56
    /// item 3): an older snapshot never replaces the layout. Optional within schema 2; 0 = none.
    /// </summary>
    public long ImportedSnapshotVersion { get; set; }

    /// <summary>
    /// The <c>snapshot_version</c> this console stamped on the lab file it exported last
    /// (D-56 item 2): monotonic per issuing console. Optional within schema 2; 0 = never exported.
    /// </summary>
    public long ExportedSnapshotVersion { get; set; }
}
