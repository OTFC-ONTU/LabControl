namespace LabControl.Shared;

/// <summary>
/// Every port, path, name and timeout used anywhere in LabControl. Nothing in the
/// product may repeat one of these values as a literal — see CLAUDE.md.
/// </summary>
public static class Defaults
{
    /// <summary>
    /// Wire protocol version reported in <c>Hello</c>; bump on a breaking change. It does
    /// <b>not</b> gate the connection: the console accepts an older agent, marks it outdated
    /// and offers an update, because <c>Hello</c>, <c>Heartbeat</c>, <c>Job{self_update}</c>
    /// and <c>JobResult</c> are frozen at version 1 forever (D-19, PROTOCOL "Versioning").
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// The wire version of the frozen subset. This value may never change — an agent of any
    /// age must always be able to connect and be told to update itself.
    /// </summary>
    public const int FrozenProtocolVersion = 1;

    // ---------------------------------------------------------------- networking

    /// <summary>gRPC server hosted by the console; agents dial in (ARCHITECTURE §3.4).</summary>
    public const int ConsolePort = 47800;

    /// <summary>UDP discovery beacon broadcast by the console.</summary>
    public const int BeaconPort = 47801;

    /// <summary>
    /// Beacon wire format. Version 2 is the offline-verifiable one: instance public key,
    /// CA endorsement, instance signature, no shared secret anywhere (D-13).
    /// </summary>
    public const int BeaconVersion = 2;

    /// <summary>A beacon must fit one datagram nothing will fragment (PROTOCOL, "Discovery beacon").</summary>
    public const int BeaconMaxBytes = 512;

    /// <summary>
    /// How long a console keeps the <c>take</c> field in its beacon after the teacher presses
    /// <i>Take over the lab</i> (ARCHITECTURE §3.7.2).
    /// </summary>
    public static readonly TimeSpan TakeOverWindow = TimeSpan.FromSeconds(30);

    /// <summary>Wake-on-LAN magic packets.</summary>
    public const int WolPort = 9;

    public static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(2);

    /// <summary>A beacon whose timestamp is further away than this is ignored.</summary>
    public static readonly TimeSpan BeaconMaxSkew = TimeSpan.FromSeconds(60);

    /// <summary>Minimum spacing between dial attempts, so a beacon flood costs nothing.</summary>
    public static readonly TimeSpan MinDialInterval = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>No heartbeat for this long and the console marks the machine offline.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan ReconnectDelayMin = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReconnectDelayMax = TimeSpan.FromSeconds(30);

    /// <summary>How long a machine has to appear after a magic packet before WoL is called failed.</summary>
    public static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(90);

    // ---------------------------------------------------------------- lab shape

    /// <summary>
    /// The largest lab the design is tested for (D-17). The actual count always comes
    /// from lab.json; this only bounds sizing decisions and load tests.
    /// </summary>
    public const int MaxStudentPcs = 30;

    /// <summary>Hostname and display-name pattern for a student PC: <c>PC-07</c>.</summary>
    public const string MachineNameFormat = "PC-{0:00}";

    /// <summary>
    /// Tiles per row when nobody has arranged the room by hand. A console that has never
    /// seen this lab before still looks right, because the default layout comes from the PC
    /// numbers (ARCHITECTURE §3.7).
    /// </summary>
    public const int DefaultTilesPerRow = 6;

    // ---------------------------------------------------------------- crypto

    /// <summary>PBKDF2 iterations protecting each key holder's wrapping of the lab key (§3.2).</summary>
    public const int KeyDerivationIterations = 600_000;

    public const int MasterKeyBytes = 32;
    public const int RecoveryCodeBytes = 16;

    public static readonly TimeSpan ConsoleCertificateLifetime = TimeSpan.FromDays(365);
    public static readonly TimeSpan AgentCertificateLifetime = TimeSpan.FromDays(5 * 365);
    public static readonly TimeSpan LabAuthorityLifetime = TimeSpan.FromDays(20 * 365);

    /// <summary>
    /// A leaf certificate is renewed over the existing link once it has less than this
    /// left (D-25). Long enough that the "certificates need renewing" banner is seen and the
    /// lab key unlocked at a convenient moment, well before anything actually expires.
    /// </summary>
    public static readonly TimeSpan CertificateRenewalLeadTime = TimeSpan.FromDays(60);

    // ---------------------------------------------------------------- on-disk formats

    // Every persisted file carries its own schema_version and migrates forward on load;
    // a file written by a newer build is refused rather than partially read (D-20).

    public const int LabKeySchemaVersion = 1;
    public const int LabSchemaVersion = 1;
    public const int InstanceSchemaVersion = 1;
    public const int EnrollmentSchemaVersion = 1;
    public const int AgentConfigSchemaVersion = 1;
    public const int PackageCatalogSchemaVersion = 1;
    public const int BackupSchemaVersion = 1;
    public const int SetupPayloadSchemaVersion = 1;

    /// <summary>Name of the version field, first in every persisted file.</summary>
    public const string SchemaVersionFieldName = "schema_version";

    // ---------------------------------------------------------------- console files

    public const string LabKeyFileName = "lab-key.lck";
    public const string LabFileName = "lab.json";
    public const string InstanceFileName = "instance.json";
    public const string EnrollmentFileName = "enrollment.json";
    public const string PackagesDirectoryName = "packages";
    public const string ScriptsDirectoryName = "scripts";
    public const string LogsDirectoryName = "logs";

    /// <summary>
    /// Where the console keeps the lab: <c>~/.labcontrol</c> on macOS and Linux,
    /// <c>%APPDATA%\LabControl</c> on Windows (ARCHITECTURE §4).
    /// </summary>
    public static string ConsoleDataDirectory =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabControl")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".labcontrol");

    // ---------------------------------------------------------------- agent files

    public const string ServiceName = "LabControl";
    public const string ServiceDisplayName = "LabControl Agent";

    public const string AgentInstallDirectory = @"C:\Program Files\LabControl";
    public const string AgentDataDirectory = @"C:\ProgramData\LabControl";

    // ------------------------------------------------------------ agent self-update

    // Versions are installed side by side under <install>\app\<version>\ and the service's
    // binary path points inside one of them, so an update never overwrites a running binary
    // and a rollback is one line of text plus a restart (D-19, ARCHITECTURE §7.2).

    /// <summary>Parent of the per-version directories, under <see cref="AgentInstallDirectory"/>.</summary>
    public const string AgentAppDirectoryName = "app";

    /// <summary>Names the version the service currently runs.</summary>
    public const string CurrentVersionFileName = "current";

    /// <summary>Names the version to roll back to; absent once a version is accepted.</summary>
    public const string PreviousVersionFileName = "previous";

    /// <summary>Staging area for an incoming bundle, under <see cref="AgentDataDirectory"/>.</summary>
    public const string UpdateStagingDirectoryName = "update";

    /// <summary>Command line that makes the agent repoint the service back at the previous version.</summary>
    public const string RollbackSwitch = "--rollback";

    /// <summary>
    /// A freshly installed version is on trial until it has completed a <c>Hello</c> and held
    /// the link for this long. Until then the scheduled rollback task and the service recovery
    /// action can still undo it.
    /// </summary>
    public static readonly TimeSpan UpdateProbation = TimeSpan.FromMinutes(10);

    /// <summary>Service recovery restarts this many times before the rollback action is run.</summary>
    public const int ServiceRestartsBeforeRollback = 3;

    public static readonly TimeSpan ServiceRestartDelay = TimeSpan.FromSeconds(10);

    public const string AgentConfigFileName = "agent.json";
    public const string AgentKeyFileName = "agent.key";
    public const string AgentCertificateFileName = "agent.crt";
    public const string CaCertificateFileName = "ca.crt";
    public const string SetupLogFileName = "setup.log";

    /// <summary>Agent service ↔ session helper. Local only; never reaches the network.</summary>
    public const string SessionPipeName = @"labcontrol-session";

    /// <summary>Agent logs are kept for this many days, then rolled off.</summary>
    public const int AgentLogRetentionDays = 7;

    // ---------------------------------------------------------------- student account

    public const string StudentAccountName = "student";

    /// <summary>
    /// Owner's explicit requirement (D-09): an isolated lab, a standard user, a trivial
    /// password so a 15-year-old can log in unaided. Never log this value.
    /// </summary>
    public const string StudentDefaultPassword = "1";

    // ---------------------------------------------------------------- video

    /// <summary>Thumbnail width in the mosaic; the height follows the screen's aspect ratio.</summary>
    public const int ThumbnailWidth = 320;

    public const int ThumbnailJpegQuality = 50;
    public const int FullJpegQuality = 75;
    public const double ThumbnailFramesPerSecond = 2;
    public const double FullFramesPerSecond = 20;

    /// <summary>Dirty-rectangle tile size for full-resolution streaming (PROTOCOL "Video").</summary>
    public const int VideoTileSize = 64;

    /// <summary>Per-agent bandwidth cap for full-resolution video.</summary>
    public const int FullModeBitsPerSecond = 8 * 1024 * 1024;

    public static readonly TimeSpan KeyframeInterval = TimeSpan.FromSeconds(5);

    // ---------------------------------------------------------------- exam mode

    /// <summary>
    /// An exam session may never outlive this, whatever the console asked for. Past it the
    /// agent restores the machine on its own, so a closed laptop cannot strand a PC (D-16).
    /// </summary>
    public static readonly TimeSpan ExamHardLimit = TimeSpan.FromHours(4);

    // ---------------------------------------------------------------- internet control

    /// <summary>
    /// Firewall rule group for every internet policy — standalone or the exam's internet
    /// switch — so the whole set is removed in one call on restore (D-22, ARCHITECTURE §6.2).
    /// </summary>
    public const string InternetFirewallRuleGroup = "LabControl Internet";

    /// <summary>
    /// A standalone internet policy may never outlive this, whatever the console asked for;
    /// past it the agent restores the machine on its own, like an exam (D-22).
    /// </summary>
    public static readonly TimeSpan InternetPolicyHardLimit = TimeSpan.FromHours(8);

    /// <summary>Default duration offered for a standalone policy: "this lesson".</summary>
    public static readonly TimeSpan InternetPolicyLessonDuration = TimeSpan.FromMinutes(90);

    /// <summary>Where the whitelist resolver listens while a whitelist policy is active.</summary>
    public const string WhitelistResolverAddress = "127.0.0.1";
    public const int WhitelistResolverPort = 53;

    /// <summary>Allow rules learned from the resolver outlive the DNS record's TTL by this much.</summary>
    public static readonly TimeSpan WhitelistRuleGrace = TimeSpan.FromMinutes(5);

    // ---------------------------------------------------------------- handouts

    /// <summary>
    /// Where <c>send_file</c> lands, relative to the <c>student</c> profile: a folder on the
    /// desktop the student cannot miss and a profile reset wipes (D-23). Installers never
    /// land here; they go to the agent's staging directory.
    /// </summary>
    public const string MaterialsRelativePath = @"Desktop\Materials";
}
