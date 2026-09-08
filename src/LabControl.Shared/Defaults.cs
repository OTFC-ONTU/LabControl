namespace LabControl.Shared;

/// <summary>
/// Every port, path, name and timeout used anywhere in LabControl. Nothing in the
/// product may repeat one of these values as a literal — see CLAUDE.md.
/// </summary>
public static class Defaults
{
    public const string SetupReadinessFileName = "setup-readiness.json";
    public const int SetupReadinessMaxBytes = 4096;
    public static readonly TimeSpan SetupReadinessPollInterval = TimeSpan.FromSeconds(10);
    public const string PasswordPolicyWorkDirectoryPrefix = "password-policy-";
    public const string SetupComputerSystemWmiNamespace = @"root\cimv2";
    public const string SetupComputerSystemWmiClass = "Win32_ComputerSystem";
    public static readonly TimeSpan SetupPasswordPolicyTimeout = TimeSpan.FromMinutes(2);
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

    /// <summary>
    /// The oldest <c>protocol_version</c> this console still drives fully. An agent below it
    /// is still accepted and shown, but its tile is marked <i>outdated</i> and only the
    /// frozen subset is used with it (PROTOCOL "Versioning").
    /// </summary>
    public const int MinimumProtocolVersion = 1;

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

    /// <summary>
    /// A magic packet is sent this many times, <see cref="WakePacketSpacing"/> apart: a NIC
    /// that has just been powered down, or a switch still learning the port, can miss one.
    /// </summary>
    public const int WakePacketRepeats = 3;

    public static readonly TimeSpan WakePacketSpacing = TimeSpan.FromSeconds(1);

    // ---------------------------------------------------------------- files (PullFile / PushFile)

    /// <summary>One <c>FileChunk</c> on the wire; small enough that a job is never delayed behind one.</summary>
    public const int FileChunkBytes = 64 * 1024;

    /// <summary>A file transfer must make progress at least this often, or it is abandoned.</summary>
    public static readonly TimeSpan FileChunkTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Retry a transient file failure without spinning while the control link reconnects.</summary>
    public static readonly TimeSpan FileRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// A linked agent whose certificate is due for renewal asks again on this backoff while
    /// the console's lab key is locked (D-25); the same ceiling as a reconnect.
    /// </summary>
    public static readonly TimeSpan RenewalRetryMax = ReconnectDelayMax;

    /// <summary>
    /// The console listens for beacons from other teacher machines and marks one gone
    /// after this long without a beacon (ARCHITECTURE §3.7.2).
    /// </summary>
    public static readonly TimeSpan OtherConsoleTimeout = TimeSpan.FromSeconds(10);

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

    /// <summary>
    /// A teacher device's leaf (M5, D-56 item 4): the console leaf's lifetime, renewed through
    /// a new device request from <see cref="CertificateRenewalLeadTime"/> before expiry.
    /// </summary>
    public static readonly TimeSpan TeacherCertificateLifetime = TimeSpan.FromDays(365);
    public static readonly TimeSpan AgentCertificateLifetime = TimeSpan.FromDays(5 * 365);
    public static readonly TimeSpan LabAuthorityLifetime = TimeSpan.FromDays(20 * 365);

    /// <summary>
    /// A leaf certificate is renewed over the existing link once it has less than this
    /// left (D-25). Long enough that the "certificates need renewing" banner is seen and the
    /// lab key unlocked at a convenient moment, well before anything actually expires.
    /// </summary>
    public static readonly TimeSpan CertificateRenewalLeadTime = TimeSpan.FromDays(60);

    /// <summary>
    /// How long the console keeps the unlocked lab key in memory after the teacher last used
    /// it (D-26). Long enough to enrol a room and renew its certificates in one sitting,
    /// short enough that a console left open is not a CA for the afternoon. An explicit
    /// <i>Lock</i> ends it sooner; closing the console always ends it.
    /// </summary>
    public static readonly TimeSpan LabKeyUnlockWindow = TimeSpan.FromMinutes(15);

    // ---------------------------------------------------------------- on-disk formats

    // Every persisted file carries its own schema_version and migrates forward on load;
    // a file written by a newer build is refused rather than partially read (D-20).

    public const int LabKeySchemaVersion = 1;
    /// <summary>Version 2 (M5, D-55) adds the optional device-book and revocation-delivery fields; a version-1 file reads as version 2 with them absent.</summary>
    public const int LabSchemaVersion = 2;
    public const int InstanceSchemaVersion = 1;
    /// <summary>Version 2 (M5, D-60) adds the optional dormant/issuer fields and <c>batches[]</c>; a version-1 file reads as version 2 with them absent.</summary>
    public const int EnrollmentSchemaVersion = 2;
    public const int AgentConfigSchemaVersion = 1;
    public const int PackageCatalogSchemaVersion = 1;
    public const int BackupSchemaVersion = 1;
    public const int ScriptsSchemaVersion = 1;
    public const int SetupPayloadSchemaVersion = 1;
    public const int InstallationSchemaVersion = 1;
    public const int SetupSettingsSchemaVersion = 1;
    /// <summary><c>profiles.json</c>, the index of saved labs on this device (M5, D-55).</summary>
    public const int ProfilesSchemaVersion = 1;

    /// <summary>The signed envelopes exchanged offline (M5, D-56): lab file, device request, device grant.</summary>
    public const int LabFileSchemaVersion = 1;
    public const int DeviceRequestSchemaVersion = 1;
    public const int DeviceGrantSchemaVersion = 1;

    /// <summary><c>access.json</c>, a teacher profile's authorization state (M5, D-56).</summary>
    public const int AccessSchemaVersion = 1;

    /// <summary>Name of the version field, first in every persisted file.</summary>
    public const string SchemaVersionFieldName = "schema_version";

    // ---------------------------------------------------------------- console files

    public const string LabKeyFileName = "lab-key.lck";
    public const string LabFileName = "lab.json";
    public const string InstanceFileName = "instance.json";
    public const string EnrollmentFileName = "enrollment.json";
    public const string PackagesDirectoryName = "packages";
    /// <summary>The script library (D-31 item 4): beside <c>lab.json</c>, inside the backup.</summary>
    public const string ScriptsFileName = "scripts.json";
    public const string LogsDirectoryName = "logs";

    // ---------------------------------------------------------------- saved labs (M5, D-55)

    /// <summary>The index of every lab saved on this device, at the data root.</summary>
    public const string ProfilesFileName = "profiles.json";

    /// <summary>Holds one directory per saved lab: <c>&lt;data&gt;/labs/&lt;lab_id&gt;/</c>.</summary>
    public const string LabsDirectoryName = "labs";

    /// <summary>
    /// Suffix of a lab directory while the single-lab migration is still copying into it;
    /// it is renamed to the bare lab id in one step once the copy is complete.
    /// </summary>
    public const string MigratingDirectorySuffix = ".migrating";

    /// <summary>
    /// Where the migration parks root files that no longer match the copy of an already
    /// committed lab (<c>&lt;data&gt;/migration-conflict-&lt;yyyyMMdd-HHmmss&gt;/</c>):
    /// nothing that differs from its copy is ever deleted, only moved aside and reported.
    /// </summary>
    public const string MigrationConflictDirectoryPrefix = "migration-conflict-";

    /// <summary>
    /// Held open for the whole process lifetime, exclusively: one console process per data
    /// directory. A second launch on the same directory is refused, not silently shared.
    /// </summary>
    public const string ConsoleLockFileName = "console.lock";

    /// <summary>A teacher profile's authorization state (M5 portion 3); the name is reserved here.</summary>
    public const string AccessFileName = "access.json";

    /// <summary>The app-level Serilog file at the data root; Serilog inserts the date before the extension.</summary>
    public const string ConsoleLogFileName = ConsoleLogFilePrefix + ConsoleLogFileExtension;

    /// <summary>What every app-level log file starts with (<c>console-20260908.log</c>).</summary>
    public const string ConsoleLogFilePrefix = "console-";
    public const string ConsoleLogFileExtension = ".log";

    /// <summary>The encrypted backup archive (ARCHITECTURE §4, D-26): <c>&lt;lab&gt;-&lt;date&gt;.lcbak</c>.</summary>
    public const string BackupFileExtension = ".lcbak";

    /// <summary>The routine lab file a teacher device imports (M5, D-56): public CA, roster, layout, revocations — never a key.</summary>
    public const string LabFileExtension = ".lclab";

    /// <summary>A teacher device's authorization request: a self-signed CSR in a signed envelope (D-56).</summary>
    public const string DeviceRequestFileExtension = ".lcreq";

    /// <summary>The administrator's answer to a request: the device leaf, its endorsement and a lab snapshot, CA-signed (D-56).</summary>
    public const string DeviceGrantFileExtension = ".lcgrant";

    /// <summary>
    /// Every file the console opens as a document (M5, D-59 item 5): what a positional
    /// command-line argument, a forwarded launch or a LaunchServices open may name. The
    /// order is the order of the file-picker filters.
    /// </summary>
    public static readonly IReadOnlyList<string> ConsoleDocumentExtensions =
        [LabFileExtension, BackupFileExtension, DeviceGrantFileExtension, DeviceRequestFileExtension];

    /// <summary>
    /// The largest document the console opens (D-59 item 5). Every one of
    /// <see cref="ConsoleDocumentExtensions"/> is a JSON text of a few kilobytes — a backup
    /// with a full roster and a script library is the biggest, and stays far under this. The
    /// cap is what stops a named pipe, a device node or a gigabyte of junk handed over by a
    /// file manager from being read into memory on the way to an import.
    /// </summary>
    public const long ConsoleDocumentMaxBytes = 8L * 1024 * 1024;

    /// <summary>
    /// The single-instance endpoint (D-59 item 5): <c>labcontrol-console-&lt;hash&gt;</c>, where the
    /// hash is the first 16 hex digits of SHA-256 over the data directory — a Windows named
    /// pipe of that name, or a Unix socket <see cref="SingleInstanceSocketPrefix"/><c>&lt;hash&gt;</c>
    /// <see cref="SingleInstanceSocketExtension"/> inside this user's private socket directory.
    /// Two <c>--data</c> directories get two endpoints.
    /// </summary>
    public const string SingleInstanceNamePrefix = "labcontrol-console-";
    public const string SingleInstanceSocketExtension = ".sock";
    public const int SingleInstanceNameHashLength = 16;

    /// <summary>
    /// The per-user directory the Unix socket lives in, created with mode 0700 under
    /// <c>$XDG_RUNTIME_DIR</c> or the temp directory: <c>labcontrol-&lt;uid&gt;</c>. The socket
    /// itself is <see cref="SingleInstanceSocketPrefix"/><c>&lt;hash&gt;</c>, shorter than the
    /// pipe name because <c>sun_path</c> holds only 104 bytes on macOS and the temp directory
    /// there is already 48 of them.
    /// </summary>
    public const string SingleInstanceDirectoryPrefix = "labcontrol-";
    public const string SingleInstanceSocketPrefix = "console-";

    /// <summary>
    /// The most documents one forwarded launch may carry (D-59 item 5). A teacher selects a
    /// handful of files in Finder; anything past this is a mistake or an attempt to make the
    /// console do work for whoever can reach the socket, and the whole batch is refused.
    /// </summary>
    public const int SingleInstanceMaxOpenPaths = 64;

    /// <summary>
    /// The pseudo-serial that revokes a teacher device across renewals (D-56 item 6):
    /// <c>instance:&lt;instance_id&gt;</c>, signed like any serial. An agent older than M5
    /// cannot hold it: its <c>NormalizeSerial</c> upper-cases the id, the signature no longer
    /// verifies and the entry is dropped, so it is re-pushed on every link and the console
    /// shows that PC as unable to hold device withdrawals until its agent is updated.
    /// </summary>
    public const string InstanceRevocationPrefix = "instance:";

    /// <summary>
    /// The longest device name a request may carry (D-56 item 4): it becomes a certificate
    /// <c>CN</c>, a file name and a row in Settings, none of which wants a paragraph.
    /// </summary>
    public const int MaxInstanceNameLength = 64;

    /// <summary>The subject OU of an administrator console leaf (D-56 item 5).</summary>
    public const string ConsoleOrganizationalUnit = "LabControl Console";

    /// <summary>The subject OU of a teacher device leaf; the SAN is the same as a console's (D-56 item 5).</summary>
    public const string TeacherOrganizationalUnit = "LabControl Teacher";

    /// <summary>Events and job results are appended to these, one file per day, under <c>logs/</c>.</summary>
    public const string EventLogFilePattern = "events-{0:yyyy-MM-dd}.jsonl";
    public const string JobLogFilePattern = "jobs-{0:yyyy-MM-dd}.jsonl";
    public const string JobBatchesDirectoryName = "batches";
    public const string JobBatchManifestFileName = "manifest.json";
    public const string JobBatchArchiveExtension = ".zip";
    public const int JobBatchSchemaVersion = 1;

    // ---------------------------------------------------------------- USB payload

    /// <summary>The directory the payload is written to on the stick: <c>&lt;USB&gt;\LabControl\</c>.</summary>
    public const string PayloadDirectoryName = "LabControl";

    /// <summary>The manifest on the stick (INSTALLER.md): lab id, codes, defaults. No secret.</summary>
    public const string SetupFileName = "setup.json";

    /// <summary>Codes written to a stick beyond the number of PCs, so a failed install can be retried.</summary>
    public const int SpareEnrollmentCodes = 4;

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

    public const string AgentExecutableName = "agent.exe";
    public const string SessionExecutableName = "session.exe";

    /// <summary>The agent's rolling log under <c>&lt;data&gt;\logs\</c>; the date goes where the dash is.</summary>
    public const string AgentLogFilePattern = "agent-.log";

    /// <summary>
    /// Command line that makes the agent provision this PC from a USB payload — the trust
    /// half of INSTALLER.md step 4, shared by the M4 installer and <c>scripts/dev-install.ps1</c>.
    /// </summary>
    public const string AgentInstallSwitch = "--install";

    /// <summary>Command line that runs the agent in the foreground with console logging, for development.</summary>
    public const string AgentForegroundSwitch = "--run";

    /// <summary>Prints the version and exits; the install script names the version directory from it.</summary>
    public const string AgentVersionSwitch = "--version";

    /// <summary>
    /// An installed but unprovisioned PC — no <c>agent.json</c> yet — looks again this often
    /// instead of exiting, because a service that exits is restarted by its recovery action
    /// and would spin (D-29).
    /// </summary>
    public static readonly TimeSpan UnprovisionedRetryInterval = TimeSpan.FromSeconds(30);

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
    public const long SetupMinimumFreeBytes = 2L * 1024 * 1024 * 1024;
    public const string InstallDirectoryIdentityFileName = "installation-id";
    public const string SetupProcessMutexName = @"Global\LabControl.Setup";
    public const string UninstallCleanupDirectoryPrefix = "LabControl-removal-";
    public const string FinishUninstallSwitch = "--finish-uninstall";
    public const string SetupUninstallRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LabControl";
    public const string SetupServiceRegistryKey = @"SYSTEM\CurrentControlSet\Services\LabControl";
    public const string ServiceDescription = "LabControl classroom management agent";
    public const string UpdateTrialFileName = "update-trial.json";
    public const string UpdateTrialLockFileName = "update-trial.lock";
    public const string UpdateRollbackTaskName = "LabControl Update Rollback";
    public const string UpdateRollbackTaskFileName = "update-rollback-task.xml";
    public const string RollbackDeadlineSwitch = "--rollback-deadline";

    /// <summary>
    /// Command line that stops the <see cref="ServiceName"/> service and starts it again. The
    /// outgoing agent spawns its own executable with it after a push (D-33): a service cannot
    /// restart itself, and the service manager starts whatever <c>binPath</c> names by then.
    /// </summary>
    public const string RestartServiceSwitch = "--restart-service";

    /// <summary>
    /// How long a version directory name may carry of the build's SHA-256: a push names its
    /// directory <c>&lt;version&gt;+&lt;these hex digits&gt;</c> so that two builds with the same
    /// version number — the normal case while developing — never collide (D-33).
    /// </summary>
    public const int BuildIdLength = 8;

    /// <summary>
    /// A pushed <c>agent.exe</c> is run with <see cref="AgentVersionSwitch"/> before it is
    /// installed, to prove it runs on this PC at all and says the version the bundle claims;
    /// it must answer within this long.
    /// </summary>
    public static readonly TimeSpan UpdatePreflightTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the agent keeps trying to move a staged version into <c>app\</c> while an
    /// antivirus still holds the freshly written executable (D-33 item 9), and the pause
    /// between attempts.
    /// </summary>
    public static readonly TimeSpan UpdatePlaceTimeout = TimeSpan.FromSeconds(45);

    public static readonly TimeSpan UpdatePlaceRetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// After asking for the restart the outgoing agent waits this long to be stopped. If it
    /// is still running afterwards, the restart did not happen and it puts everything back.
    /// </summary>
    public static readonly TimeSpan ServiceRestartWait = TimeSpan.FromSeconds(60);

    /// <summary>How long <see cref="RestartServiceSwitch"/> waits for the service to reach <i>stopped</i> before starting it.</summary>
    public static readonly TimeSpan ServiceStopTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The console's inactivity timeout on a <c>self_update</c> job: it has to outlast the pull
    /// (which reports progress), the preflight, the service restart and the new version's
    /// first link, because the result comes from the new version (PROTOCOL, <c>self_update</c>).
    /// </summary>
    public static readonly TimeSpan SelfUpdateJobTimeout = TimeSpan.FromMinutes(5);

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
    public const string InstallationFileName = "installation.json";
    public const string SetupLockFileName = "setup.lock";
    public const string SetupSettingsFileName = "setup-settings.json";
    public const string AutoLogonSecretName = "DefaultPassword";
    public const string FastStartupPolicyKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    public const string FastStartupPolicyValue = "HiberbootEnabled";
    public const string WindowsUpdatePolicyParentKey = @"SOFTWARE\Policies\Microsoft\Windows";
    public const string WindowsUpdatePolicySubKey = "WindowsUpdate";
    public const string UpdateActiveHoursEnabledValue = "SetActiveHours";
    public const string UpdateActiveHoursStartValue = "ActiveHoursStart";
    public const string UpdateActiveHoursEndValue = "ActiveHoursEnd";
    public const uint SetupActiveHoursStart = 7;
    public const uint SetupActiveHoursEnd = 20;
    public const uint SetupSleepAcSeconds = 0;
    public const uint SetupDisplayAcSeconds = 20 * 60;
    public const uint SetupDiskAcSeconds = 0;
    public static readonly Guid PowerSleepSubgroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    public static readonly Guid PowerSleepTimeout = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    public static readonly Guid PowerDisplaySubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid PowerDisplayTimeout = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    public static readonly Guid PowerDiskSubgroup = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    public static readonly Guid PowerDiskTimeout = new("6738e2c4-e8a5-4a42-b16a-e040e769756e");
    public const string StudentCreationCommentPrefix = "LabControl account creation ";

    // ------------------------------------------------------------ session helper

    // The service spawns session.exe into the interactive session with a SYSTEM token and
    // supervises it over a local named pipe (ARCHITECTURE §2, D-06, D-30).

    /// <summary>Agent service ↔ session helper. Local only; never reaches the network.</summary>
    public const string SessionPipeName = @"labcontrol-session";

    /// <summary>The helper's rolling log next to the agent's; the date goes where the dash is.</summary>
    public const string SessionLogFilePattern = "session-.log";

    /// <summary>Prints what the helper sees of its session (desktop, screen) and exits; for a hand check on a PC.</summary>
    public const string SessionProbeSwitch = "--probe";

    /// <summary>The largest message either side accepts on the pipe; a full-screen JPEG fits many times over.</summary>
    public const int SessionPipeMaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The pipe's buffers: helper → service carries video frames, so it is sized for a few
    /// of them; service → helper carries controls and input, a few kilobytes at most. A
    /// write that fits in the buffer never waits for the reader (D-35 item 8).
    /// </summary>
    public const int SessionPipeInBufferBytes = 2 * 1024 * 1024;

    public const int SessionPipeOutBufferBytes = 64 * 1024;

    /// <summary>The helper reports its <c>HelperStatus</c> this often, whether or not anything changed.</summary>
    public static readonly TimeSpan HelperStatusInterval = TimeSpan.FromSeconds(2);

    /// <summary>A helper that has not spoken for this long is hung: the service kills and restarts it.</summary>
    public static readonly TimeSpan HelperSilenceTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A freshly spawned helper must connect to the pipe within this long, or it is killed and tried again.</summary>
    public static readonly TimeSpan HelperConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Pause before respawning a helper that exited; keeps "back within 5 s" (ROADMAP M2) with room for the spawn itself.</summary>
    public static readonly TimeSpan HelperRestartDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Windows ends the helper a moment before the logoff or session change that caused it
    /// is visible to the service. An exit is held this long for a planned reason to show
    /// up before it is called a crash (D-32 item 12).
    /// </summary>
    public static readonly TimeSpan HelperExitGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A helper that dies this many times within <see cref="HelperCrashLoopWindow"/> is in a
    /// crash loop: the service reports it once and waits <see cref="HelperCrashLoopBackoff"/>
    /// between further attempts instead of spinning.
    /// </summary>
    public const int HelperCrashLoopThreshold = 5;

    public static readonly TimeSpan HelperCrashLoopWindow = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan HelperCrashLoopBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the service re-reads the interactive session (which one is active, who is
    /// in it, locked or not) when Windows has not told it; a session-change notification
    /// wakes it at once (D-30).
    /// </summary>
    public static readonly TimeSpan SessionPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Agent logs are kept for this many days, then rolled off.</summary>
    public const int AgentLogRetentionDays = 7;

    // ---------------------------------------------------------------- jobs and scripts

    /// <summary>
    /// Where a job's files land on the PC when it runs as SYSTEM: <c>&lt;data&gt;\jobs\&lt;id&gt;\</c>,
    /// under the directory only SYSTEM and Administrators can read (ARCHITECTURE §5).
    /// Deleted once the result has been sent (D-32).
    /// </summary>
    public const string JobsDirectoryName = "jobs";

    /// <summary>
    /// Where a job's files land when it runs in the student session as the student: the
    /// student must be able to read the script, and nobody weaker than the student can
    /// write there, so <c>%PUBLIC%\LabControl\jobs\&lt;id&gt;\</c> (D-32).
    /// </summary>
    public static string UserJobsDirectory =>
        Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "LabControl", JobsDirectoryName);

    /// <summary>
    /// The script's own inactivity timeout when the console did not say: killed after this
    /// long without a line of output (PROTOCOL, <c>run_script</c>).
    /// </summary>
    public static readonly TimeSpan ScriptDefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The console's inactivity timeout on a <c>run_script</c> job is the script's timeout
    /// plus this, so the agent's own "killed after N s" result always arrives before the
    /// console gives up on the job (D-32).
    /// </summary>
    public static readonly TimeSpan JobTimeoutGrace = TimeSpan.FromSeconds(30);

    /// <summary>Output lines kept per job on both sides; a script that prints more is cut with a note.</summary>
    public const int ScriptOutputMaxLines = 10_000;

    /// <summary>
    /// A power job answers first and acts after this pause, so the <c>JobResult</c> is on
    /// the wire before the OS starts tearing the link down (D-32).
    /// </summary>
    public static readonly TimeSpan PowerJobDelay = TimeSpan.FromSeconds(2);

    /// <summary>The interpreter for <c>shell: powershell</c> — Windows PowerShell 5.1, present on every Windows 10/11.</summary>
    public const string PowerShellExecutable = "powershell.exe";

    public const string CmdExecutable = "cmd.exe";

    // ---------------------------------------------------------------- machine setup

    public const string ActiveComputerNameRegistryKey = @"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName";
    public const string PendingComputerNameRegistryKey = @"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName";
    public const string ComputerNameRegistryValue = "ComputerName";
    public const string TcpipParametersRegistryKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
    public const string PendingDnsHostnameRegistryValue = "NV Hostname";
    public const string SetupFirewallPolicyProgId = "HNetCfg.FwPolicy2";
    public const string SetupFirewallRuleProgId = "HNetCfg.FWRule";
    public const string SetupFirewallGroup = "LabControl";
    public const string SetupDiscoveryFirewallRule = "LabControl Discovery";
    public const string SetupEchoFirewallRule = "LabControl Echo";

    public const string NicWmiNamespace = @"root\cimv2";
    public const string NicAdvancedWmiNamespace = @"root\StandardCimv2";
    public const string NicPowerWmiNamespace = @"root\wmi";
    public const string NicAdapterWmiClass = "Win32_NetworkAdapter";
    public const string NicDriverWmiClass = "Win32_PnPSignedDriver";
    public const string NicAdvancedWmiClass = "MSFT_NetAdapterAdvancedPropertySettingData";
    public const string NicWakeWmiClass = "MSPower_DeviceWakeEnable";
    public const string NicPowerWmiClass = "MSPower_DeviceEnable";
    public const string NicMagicOnlyWmiClass = "MSNdis_DeviceWakeOnMagicPacketOnly";
    public const string NicPowerEnableProperty = "Enable";
    public const string NicMagicOnlyEnableProperty = "EnableWakeOnMagicPacketOnly";
    public const string NicClassRegistryKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    public const string NicInterfaceRegistryValue = "NetCfgInstanceId";
    public const string NicPnpRegistryValue = "DeviceInstanceID";
    public const string NicMagicRegistryValue = "*WakeOnMagicPacket";
    public const string NicEeeRegistryValue = "*EEE";
    public const string NicPatternRegistryValue = "*WakeOnPattern";

    // ---------------------------------------------------------------- student account

    public const string StudentPrivacyPolicyRegistryKey = @"SOFTWARE\Policies\Microsoft\Windows\OOBE";
    public const string StudentPrivacyPolicyRegistryValue = "DisablePrivacyExperience";
    public const string StudentEdgePolicyRegistryKey = @"SOFTWARE\Policies\Microsoft\Edge";
    public const string StudentEdgePolicyRegistryValue = "HideFirstRunExperience";
    public const string StudentOneDrivePolicyRegistryKey = @"SOFTWARE\Policies\Microsoft\OneDrive";
    public const string StudentOneDrivePolicyRegistryValue = "DisableNewAccountDetection";
    public const string AdministratorVisibilitySubKey = @"SpecialAccounts\UserList";
    public const string AdministratorCredentialRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\CredUI";
    public const string AdministratorCredentialRegistryValue = "EnumerateAdministrators";

    public const string WinlogonRegistryKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string AutoAdminLogonValue = "AutoAdminLogon";
    public const string AutoLogonCountValue = "AutoLogonCount";
    public const string AutoLogonSidValue = "AutoLogonSID";
    public const string AutoLogonUserValue = "DefaultUserName";
    public const string AutoLogonDomainValue = "DefaultDomainName";
    public const string AutoLogonPasswordValue = "DefaultPassword";

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

    /// <summary>
    /// <c>VideoControl.quality</c> of 0 in full mode: the producer picks the quality itself
    /// between <see cref="FullJpegQualityMin"/> and <see cref="FullJpegQuality"/> from how
    /// often the bandwidth cap makes it wait (D-37). An older agent reads 0 as q75.
    /// </summary>
    public const int VideoQualityAuto = 0;

    /// <summary>The lowest quality auto mode goes to; below this text in a browser is hard to read.</summary>
    public const int FullJpegQualityMin = 40;

    /// <summary>Auto mode: one step down each time a frame had to wait for the cap, one step up after this many frames that did not.</summary>
    public const int AutoQualityStepDown = 10;
    public const int AutoQualityStepUp = 5;
    public const int AutoQualityFreeFrames = 30;

    /// <summary>The manual choices the single-PC window offers besides auto (D-37).</summary>
    public const int FullJpegQualityHigh = 75;
    public const int FullJpegQualityMedium = 60;
    public const int FullJpegQualityLow = 45;
    public const double ThumbnailFramesPerSecond = 2;
    public const double FullFramesPerSecond = 20;

    /// <summary>Dirty-rectangle tile size for full-resolution streaming (PROTOCOL "Video").</summary>
    public const int VideoTileSize = 64;

    /// <summary>
    /// Per-agent bandwidth cap for full-resolution video. 8 Mbit/s (1 MB/s) gave 3–4 fps on
    /// `PC-10` while a browser page scrolled — every frame is a near-whole-screen JPEG of
    /// 250–350 KB — so the one full stream may take 24 Mbit/s; the console sends this in
    /// every full <c>VideoControl</c>, so a change here needs no agent push (D-36 item 11).
    /// </summary>
    public const int FullModeBitsPerSecond = 24 * 1024 * 1024;

    /// <summary>
    /// Per-agent cap for thumbnails; thirty PCs at this ceiling stay well inside the Wi-Fi
    /// headroom the console hangs on (ROADMAP M3, D-10).
    /// </summary>
    public const int ThumbnailModeBitsPerSecond = 512 * 1024;

    public static readonly TimeSpan KeyframeInterval = TimeSpan.FromSeconds(5);

    /// <summary>The only codec in this build; <c>VideoFrame.codec</c> names it (D-11).</summary>
    public const string VideoCodecJpeg = "jpeg";

    /// <summary>A producer never runs faster than this whatever the console asks.</summary>
    public const double VideoMaxFramesPerSecond = 30;

    /// <summary>
    /// The largest <c>VideoFrame</c> either side accepts: a 1080p keyframe at q75 is well
    /// under a megabyte, so this only stops a runaway producer (D-34).
    /// </summary>
    public const int VideoFrameMaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Frames queued on the PC beyond the one on the wire. Video is latest-wins: a producer
    /// whose frame is refused keeps its dirty state and tries again, so a slow console never
    /// piles frames up on the PC (D-34).
    /// </summary>
    public const int VideoUplinkQueueLength = 1;

    /// <summary>How long the PC waits for the console's <c>VideoAck</c> after closing a video call.</summary>
    public static readonly TimeSpan VideoCloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A linked PC that has sent no frame for this long is shown with its last picture
    /// dimmed and a note, instead of a frozen image that looks live.
    /// </summary>
    public static readonly TimeSpan VideoStallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A producer whose capture failed (no duplication, the desktop switched and could not be
    /// followed, no session) tries to open the screen again this often while video is wanted (D-35).
    /// </summary>
    public static readonly TimeSpan CaptureRetryInterval = TimeSpan.FromSeconds(2);

    // ---------------------------------------------------------------- input (M3 portion 3)

    /// <summary>
    /// While the teacher controls a PC, held mouse moves are flushed this often — the latest
    /// position wins, so a sweep across the picture costs ~60 messages a second at most (D-36).
    /// </summary>
    public static readonly TimeSpan InputFlushInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>Input messages the helper holds before the newest are dropped; a flood, not a lesson.</summary>
    public const int InputQueueLength = 1024;

    /// <summary>
    /// The policy that lets a service raise Ctrl+Alt+Del with <c>SendSAS</c>
    /// (<c>SoftwareSASGeneration</c>: 1 = services, 3 = services and Ease of Access). The agent
    /// sets it to 1 the first time it is asked, if nothing allows services already (D-36).
    /// </summary>
    public const string SoftwareSasPolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    public const string SoftwareSasPolicyValue = "SoftwareSASGeneration";

    /// <summary>
    /// The longest one capture waits for the screen to change before the producer's loop
    /// looks at its control again; a mode switch or a stop is never delayed by more than this.
    /// </summary>
    public static readonly TimeSpan CaptureAcquireTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>A screen that has delivered no first picture for this long after opening is reported once.</summary>
    public static readonly TimeSpan CaptureFirstFrameWarning = TimeSpan.FromSeconds(5);

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
    public const string MaterialsFolderName = "Materials";
    public const string MaterialsRelativePath = @"Desktop\" + MaterialsFolderName;

    public const string SetupExecutableName = "Setup.exe";
    public const string UninstallExecutableName = "Uninstall.exe";
    public const string UsbBinariesDirectoryName = "payload";
    public const string UsbInstructionsFileName = "INSTALL.txt";
    public const string TrustRekeyFileName = "trust-rekey.json";
    public const string UninstallRecoveryRootDirectory = @"C:\ProgramData\LabControl Removal";
    public const string UninstallCleanupReceiptFileName = "cleanup.json";

    public const string DefenderWmiNamespace = @"root\Microsoft\Windows\Defender";
    public const string DefenderPreferenceClass = "MSFT_MpPreference";
    public const string DefenderExclusionPathProperty = "ExclusionPath";
    public const string SecurityCenterWmiNamespace = @"root\SecurityCenter2";
    public const string AntivirusProductClass = "AntiVirusProduct";
    public const string DefenderReportingRelativePath = @"Windows Defender\MsMpEng.exe";
    public const string HibernationRegistryKey = @"SYSTEM\CurrentControlSet\Control\Power";
    public const string HibernateEnabledValue = "HibernateEnabled";
    public const string HibernationFileTypeValue = "HiberFileType";
    public const string HibernationFileSizeValue = "HiberFileSizePercent";
    public const string PowerConfigurationExecutableName = "powercfg.exe";
    public static readonly TimeSpan SetupWmiTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SetupPowerCommandTimeout = TimeSpan.FromSeconds(60);
    public static readonly Guid StudentDesktopKnownFolderId = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public const string WindowsExplorerExecutableName = "explorer.exe";
    public const string HandoutStagingDirectoryName = "handouts";
    public const string ProfileTemplateArchiveFileName = "profile-template.zip";
    public const string ProfileTemplatePlanFileName = "profile-template.json";
    public const string ProfileTemplateStagingDirectoryName = "profile-template-staging";
    public const string ProfileTemplateDesktopDirectoryName = "Desktop";
    public const string ProfileTemplateDocumentsDirectoryName = "Documents";
    public const int ProfileTemplateMaxEntries = 128;
    public const int ProfileTemplateMaxFileBytes = 2 * 1024 * 1024;
    public const int ProfileTemplateMaxTotalBytes = 16 * 1024 * 1024;
    public const int ProfileTemplateMaxPathCharacters = 200;
    public const string SetupInstallStagingSuffix = ".setup-staging";
}
