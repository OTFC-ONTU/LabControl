using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using LabControl.Console.Localization;
using LabControl.Console.Server;
using LabControl.Shared;
using LabControl.Shared.Discovery;
using LabControl.Shared.Files;
using LabControl.Shared.Identity;
using LabControl.Shared.Jobs;
using LabControl.Shared.Setup;
using LabControl.Shared.Lab;
using LabControl.Shared.Localization;
using LabControl.Shared.Link;
using LabControl.Shared.Packages;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using LabControl.Shared.Power;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using LabControl.Shared.Video.H264;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>
/// Another teacher machine heard beaconing (ARCHITECTURE §3.7.2). <paramref name="TookOverAt"/>
/// is the taker's own timestamp and is only ever shown; <paramref name="TookOverSeenAt"/> is
/// when this console received that press, on its own clock, which is what decides whether a
/// take-over is still live (M5, D-58).
/// </summary>
public sealed record OtherConsole(
    string InstanceId,
    string Name,
    string Endpoint,
    DateTimeOffset LastSeen,
    DateTimeOffset? TookOverAt,
    DateTimeOffset? TookOverSeenAt = null);

/// <summary>How sure this console is about who holds a PC (M5 §4.6, D-58).</summary>
public enum OwnershipKind
{
    /// <summary>Linked to this console right now.</summary>
    LinkedHere = 0,

    /// <summary>Positively observed with another teacher machine that is still on the network.</summary>
    ObservedElsewhere = 1,

    /// <summary>Nothing links it, nothing was observed and no other console is live: the PC is off or away.</summary>
    Offline = 2,

    /// <summary>Another console is live and this one simply does not know — the honest answer, not a guess.</summary>
    Unknown = 3,
}

/// <summary>
/// Who holds one PC, as far as this console actually knows. <see cref="Holder"/> is set
/// only for <see cref="OwnershipKind.ObservedElsewhere"/>; <see cref="At"/> is when that was
/// learned, and <see cref="LastSeen"/> the PC's own last contact with this console.
/// </summary>
public sealed record MachineOwnership(OwnershipKind Kind, OtherConsole? Holder = null, DateTimeOffset? At = null, DateTimeOffset? LastSeen = null)
{
    public bool IsObserved => Kind == OwnershipKind.ObservedElsewhere;

    /// <summary>The holder's display name, or empty when there is no positively known holder.</summary>
    public string HolderName => Holder?.Name ?? string.Empty;
}

/// <summary>A Wake-on-LAN in progress: the packets went out, the PC has until <see cref="Deadline"/> to link.</summary>
public sealed record PendingWake(string AgentId, int Number, DateTimeOffset StartedAt, DateTimeOffset Deadline);

/// <summary>The steps of <see cref="LabSession.CloseAsync"/>, in order (D-57 item 2).</summary>
public enum LabCloseStep
{
    Cancel = 0,
    Broadcast = 1,
    Beacons = 2,
    Links = 3,
    Listener = 4,
    Server = 5,
    Housekeeping = 6,
    Save = 7,
    Screens = 8,
    Vault = 9,
    Instance = 10,
}

/// <summary>
/// What a session serves as, apart from the key (M5, D-56 item 5): an administrator profile
/// derives it from <c>lab-key.lck</c>, a teacher profile from <c>access.json</c>.
/// </summary>
public sealed record LabIdentity(string LabId, string LabName, byte[] Authority)
{
    public static LabIdentity Of(LabKeyDocument document) => new(document.LabId, document.LabName, document.Authority);

    public static LabIdentity Of(AccessDocument document, string labName) => new(document.LabId, labName, document.Authority);
}

/// <summary>
/// Where a revocation entry stands across the room (D-56 item 6): confirmed, not assumed.
/// <see cref="Pending"/> are the PCs that have not confirmed it yet; <see cref="CannotHold"/>
/// are the PCs whose agent predates M5 and drops an <c>instance:</c> entry (they confirmed
/// the leaf serial issued in the same withdrawal but never this one) — those need an agent
/// update, not patience, and the entry is never "complete" while they exist.
/// </summary>
public sealed record RevocationDelivery(string Serial, int Delivered, int Total, IReadOnlyList<MachineRecord> Pending, IReadOnlyList<MachineRecord> CannotHold)
{
    public bool IsComplete => Pending.Count == 0 && CannotHold.Count == 0;
}

/// <summary>
/// The running lab on this teacher machine: the identity it serves with, the machine list
/// it caches, the PCs linked to it right now, the jobs in flight, the events, the beacon
/// going out and the beacons coming in. Everything the UI shows comes from here and
/// everything the gRPC services do goes through here; neither knows the other.
/// </summary>
public sealed class LabSession : IAsyncDisposable
{
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, AgentConnection> _linked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OtherConsole> _others = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingWake> _waking = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When each other teacher machine was last reported as having a wrong clock (D-58).</summary>
    private readonly Dictionary<string, DateTimeOffset> _skewReported = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly JobJournal _journal;
    private static long _generations;
    private int _closed;

    private ConsoleServer? _server;
    private BeaconBroadcaster? _broadcaster;
    private BroadcastHub? _broadcast;
    private readonly HashSet<string> _broadcastTargets = new(StringComparer.OrdinalIgnoreCase);
    private BeaconListener? _listener;
    private Task? _housekeeping;

    /// <summary>An administrator session: the identity comes from the vault's key document.</summary>
    public LabSession(
        ConsoleOptions options,
        LabStore store,
        LabKeyVault vault,
        ConsoleInstance instance,
        InstanceDocument instanceDocument,
        ILoggerFactory loggers,
        Func<DateTimeOffset>? clock = null,
        IReadOnlyList<SeedScript>? seedScripts = null)
        : this(options, store, LabIdentity.Of(vault.Document), vault, instance, instanceDocument, loggers, clock, seedScripts)
    {
    }

    /// <summary>
    /// A session with or without the lab key (M5, D-56 item 5): a teacher profile has no
    /// <c>lab-key.lck</c>, so <paramref name="vault"/> is <c>null</c> and every CA operation
    /// answers that administrator access is needed.
    /// </summary>
    public LabSession(
        ConsoleOptions options,
        LabStore store,
        LabIdentity identity,
        LabKeyVault? vault,
        ConsoleInstance instance,
        InstanceDocument instanceDocument,
        ILoggerFactory loggers,
        Func<DateTimeOffset>? clock = null,
        IReadOnlyList<SeedScript>? seedScripts = null)
    {
        Options = options;
        Store = store;
        Identity = identity;
        Vault = vault;
        Instance = instance;
        InstanceDocument = instanceDocument;
        if (vault is not null)
        {
            // This computer's memory of the key (D-82), in the same store as this instance's own key.
            vault.Memory ??= new LabKeyMemory(store, instance.InstanceId, () => SecretProtector.For(instanceDocument.PrivateKey));
        }
        _loggers = loggers;
        _log = loggers.CreateLogger<LabSession>();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        Authority = X509CertificateLoader.LoadCertificate(identity.Authority);
        Trust = new LabTrust(Authority, identity.LabId);
        Registry = new LabRegistry(store.LoadLab(identity.LabId, identity.LabName), Authority);
        Enrollment = new EnrollmentAuthority(store.LoadEnrollment(identity.LabId));
        Jobs = new JobQueue(identity.LabId, instance.InstanceId);
        Files = new FileOffers();
        Screens = new ScreenStore(_clock);
        Screens.KeyframeNeeded += screen => RequestKeyframe(screen.AgentId);
        Screens.VideoBroken += OnFullVideoBroken;
        Events = new EventLog(store.LogsDirectory, _clock);
        _journal = new JobJournal(store.LogsDirectory);
        BatchLogs = new JobBatchLogs(store.LogsDirectory, Jobs, id => Registry.FindByAgentId(id)?.Number ?? 0, _clock);
        Scripts = new ScriptLibrary(store, identity.LabId, seedScripts ?? [], _clock, loggers.CreateLogger<ScriptLibrary>());
        InternetPresets = new InternetPresetLibrary(store, identity.LabId, _clock, loggers.CreateLogger<InternetPresetLibrary>());
        ExamPresets = new ExamPresetLibrary(store, identity.LabId, _clock, InternetPresets, loggers.CreateLogger<ExamPresetLibrary>());
        Packages = new PackageCatalog(store, instance.InstanceName, _clock, loggers.CreateLogger<PackageCatalog>());
        PackageStatus = new PackageStatusBook(store, identity.LabId, Jobs, loggers.CreateLogger<PackageStatusBook>());
        Packages.Changed += DropStaleInstalls;

        Registry.Changed += () => SaveLabSoon();
        Registry.Replaced += OnMachineReplaced;
        Jobs.Updated += OnJobUpdated;
        Collections = new WorkCollections(
            (ids, request) => CreateJobs(ids, Job.Types.Kind.CollectFiles, request.ToArgs(), Defaults.CollectJobTimeout, JobDelivery.OnlineOnly),
            id => Registry.FindByAgentId(id)?.Number ?? 0, Events, Uploads, _clock);
        Jobs.Updated += Collections.OnJobUpdated;
        RestoreInFlightJobs();

        var self = Registry.RecordInstance(instance.InstanceId, instance.InstanceName, instance.CertificateSerial, _clock(), isThisMachine: true);
        if (self.Access == ProfileAccess.Unknown)
        {
            self.Access = Access;
        }
    }

    public ConsoleOptions Options { get; }

    public LabStore Store { get; }

    /// <summary>Lab id, name and public CA — what every session has, key or no key.</summary>
    public LabIdentity Identity { get; }

    /// <summary>The lab key, or <c>null</c> on a teacher profile (M5, D-56 item 5): then nothing here can sign.</summary>
    public LabKeyVault? Vault { get; }

    /// <summary>What this session may do: the key makes an administrator; without it, a teacher.</summary>
    public ProfileAccess Access => Vault is null ? ProfileAccess.Teacher : ProfileAccess.Administrator;

    public bool IsAdministrator => Vault is not null;

    public ConsoleInstance Instance { get; }

    public InstanceDocument InstanceDocument { get; }

    /// <summary>The public authority, for validating peers and beacons.</summary>
    public X509Certificate2 Authority { get; }

    public LabTrust Trust { get; }

    public LabRegistry Registry { get; }

    public EnrollmentAuthority Enrollment { get; }

    public JobQueue Jobs { get; }

    public JobBatchLogs BatchLogs { get; }

    /// <summary>What agents may pull through <c>PullFile</c> (D-31): scripts now, packages and bundles in M4.</summary>
    public FileOffers Files { get; }

    public FileUploads Uploads { get; } = new();

    /// <summary>Collected work (M6 portion 5, D-77): standalone collections and the exams that collect at their end.</summary>
    public WorkCollections Collections { get; }

    /// <summary>Every PC's screen as last seen (M3): thumbnails for the mosaic, the full picture for the single-PC view.</summary>
    public ScreenStore Screens { get; }

    /// <summary>The script library (D-31 item 4), seeded on the first run.</summary>
    public ScriptLibrary Scripts { get; }

    /// <summary>The teacher's named whitelist presets (M6 portion 3, D-73), beside <c>lab.json</c> and in the backup.</summary>
    public InternetPresetLibrary InternetPresets { get; }

    /// <summary>The teacher's named exam presets (M6 portion 4, D-76), beside <c>lab.json</c> and in the backup — never in a lab file.</summary>
    public ExamPresetLibrary ExamPresets { get; }

    /// <summary>The package catalog (M7 portion 3, D-88): <c>packages/</c> beside <c>lab.json</c>, its YAML in the backup, its installers on this computer only.</summary>
    public PackageCatalog Packages { get; }

    /// <summary>What each PC last said about each package (M7 portion 4, D-89), in <c>package-status.json</c>.</summary>
    public PackageStatusBook PackageStatus { get; }

    public EventLog Events { get; }

    public string LabId => Identity.LabId;

    public string LabName => Identity.LabName;

    /// <summary>The port the server actually listens on — differs from the option only when it was 0.</summary>
    public int Port => _server?.Port ?? Options.Port;

    public DateTimeOffset Now => _clock();

    /// <summary>
    /// Distinguishes this session from every other one this process has built (M5, D-57): a
    /// callback posted by one session checks it before touching the UI of the next.
    /// </summary>
    public long Generation { get; } = Interlocked.Increment(ref _generations);

    /// <summary>True once <see cref="CloseAsync"/> has run: nothing here serves, beacons or holds a key any more.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Names a lab saved on this device by id, or <c>null</c> (M5, D-57 item 2): when a PC of
    /// another saved lab reaches this session, the refusal says which lab it belongs to
    /// instead of only "not this one". Set by <see cref="ConsoleBootstrap"/>; never a network call.
    /// </summary>
    public Func<string, string?>? LabNameResolver { get; set; }

    // ------------------------------------------------------------------ notifications

    /// <summary>A PC linked, unlinked, changed status or was added/replaced; the lab view refreshes.</summary>
    public event Action? MachinesChanged;

    public event Action<AgentConnection>? AgentLinked;

    public event Action<AgentConnection, string>? AgentUnlinked;

    /// <summary>Another teacher machine appeared, disappeared or took over.</summary>
    public event Action? OtherConsolesChanged;

    /// <summary>The session has released everything (M5, D-57): the room, the port, the key.</summary>
    public event Action<LabSession>? Disposed;

    // ------------------------------------------------------------------ lifecycle

    public async Task StartAsync()
    {
        Store.EnsureDirectories();

        _server = await ConsoleServer.StartAsync(this, _loggers);
        _log.LogInformation("Console '{Name}' ({Instance}) serving lab '{Lab}' on {Bind}:{Port}",
            Instance.InstanceName, Instance.InstanceId, LabName, Options.BindAddress, Port);

        _broadcaster = new BeaconBroadcaster(Instance, Port, Options.BindAddress, _clock, Options.BeaconPort);
        _broadcaster.Failed += message => Events.Warning("beacon.send_failed", message);
        _broadcaster.Start();

        _listener = new BeaconListener(Options.BeaconPort);
        _listener.Received += OnBeaconReceived;
        _listener.Failed += message => Events.Warning("beacon.listen_failed", message);
        _listener.Start();

        _housekeeping = Task.Run(() => HousekeepingAsync(_stopping.Token));
    }

    public ValueTask DisposeAsync() => CloseAsync("the console is closing");

    /// <summary>
    /// Releases the lab in the fixed order of D-57 item 2, so that the room is let go before
    /// anything else and the port is free for the next session: cancel; beacons stop first
    /// (no PC hears this instance any more); every link is closed with
    /// <paramref name="reason"/>; the beacon listener stops; the server stops (2 s budget) and
    /// is disposed; housekeeping joins; <c>lab.json</c> is saved; screens are dropped; the
    /// vault locks the CA key; the instance key is released. Idempotent.
    /// </summary>
    public async ValueTask CloseAsync(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        // Every step runs whatever the previous one did: a failure is logged and the next
        // step still runs, and steps 6–9 — the ones that hold the key and the pictures — sit
        // in a finally, so no exception on the way can leave the CA unlocked or the
        // instance key open. IsDisposed and Disposed are set there too, once, always.
        try
        {
            Step(LabCloseStep.Cancel, () => _stopping.Cancel());

            // 0. The broadcast ends before anything else (M6, D-71): every PC is told while
            // the links are still up, and the streams end with the hub. A lock does not end
            // here — it has its own limit (D-70).
            await StepAsync(LabCloseStep.Broadcast, () => StopBroadcastAsync(BroadcastEnd.Departure)).ConfigureAwait(false);

            // 1. Beacons first: a PC that hears nothing from this instance stops considering it.
            Step(LabCloseStep.Beacons, () => _broadcaster?.Dispose());

            // 2. Every link, with the reason the PC's event log will show.
            Step(LabCloseStep.Links, () =>
            {
                AgentConnection[] connections;
                lock (_gate)
                {
                    connections = _linked.Values.ToArray();
                }

                foreach (var connection in connections)
                {
                    connection.Close(reason);
                }
            });

            // 3. Stop hearing other consoles.
            Step(LabCloseStep.Listener, () => _listener?.Dispose());

            // 4. The server: stop, then dispose, so the port is free for the next session.
            await StepAsync(LabCloseStep.Server, async () =>
            {
                if (_server is not null)
                {
                    var server = _server;
                    _server = null;
                    await server.DisposeAsync().ConfigureAwait(false);
                }

                // No PC can upload any more: collections in progress give back their grants and staging files (D-77).
                Collections.Close();
            }).ConfigureAwait(false);

            // 5. Housekeeping joins.
            await StepAsync(LabCloseStep.Housekeeping, async () =>
            {
                if (_housekeeping is not null)
                {
                    try
                    {
                        await _housekeeping.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                    {
                    }
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            // 6–9. Persist, drop the pictures, lock the CA key, release the instance key.
            Step(LabCloseStep.Save, () =>
            {
                SaveLab();
                SaveInFlightJobs();
            });
            Step(LabCloseStep.Screens, Screens.Dispose);
            Step(LabCloseStep.Vault, () => Vault?.Dispose());
            Step(LabCloseStep.Instance, () =>
            {
                Instance.Dispose();
                Authority.Dispose();
            });
            Step(LabCloseStep.Cancel, _stopping.Dispose);

            IsDisposed = true;
            _log.LogInformation("Lab '{Lab}' released: {Reason}", LabName, reason);
            Disposed?.Invoke(this);
        }
    }

    /// <summary>
    /// A hook run before each step of <see cref="CloseAsync"/>, for diagnostics and for the
    /// tests that make one step fail and check the rest still ran. An exception thrown here
    /// counts as that step failing. <c>null</c> in production.
    /// </summary>
    public Action<LabCloseStep>? BeforeCloseStep { get; set; }

    /// <summary>
    /// A hook that may rewrite the <c>Welcome</c> before it goes out, for the test that makes a
    /// console claim an instance id its certificate does not carry (D-57 item 4). <c>null</c>
    /// in production.
    /// </summary>
    public Action<Welcome>? BeforeWelcome { get; set; }

    private void Step(LabCloseStep step, Action action)
    {
        try
        {
            BeforeCloseStep?.Invoke(step);
            action();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Releasing lab '{Lab}': step {Step} failed; continuing", LabName, step);
        }
    }

    private async Task StepAsync(LabCloseStep step, Func<Task> action)
    {
        try
        {
            BeforeCloseStep?.Invoke(step);
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Releasing lab '{Lab}': step {Step} failed; continuing", LabName, step);
        }
    }

    // ------------------------------------------------------------------ linked PCs

    public IReadOnlyList<AgentConnection> Linked
    {
        get
        {
            lock (_gate)
            {
                return _linked.Values.OrderBy(c => c.Number).ToArray();
            }
        }
    }

    public AgentConnection? FindLinked(string agentId)
    {
        lock (_gate)
        {
            return _linked.GetValueOrDefault(agentId);
        }
    }

    public bool IsLinked(string agentId)
    {
        lock (_gate)
        {
            return _linked.ContainsKey(agentId);
        }
    }

    // ------------------------------------------------------------------ other consoles

    public IReadOnlyList<OtherConsole> OtherConsoles
    {
        get
        {
            lock (_gate)
            {
                return _others.Values.OrderBy(o => o.Name, StringComparer.CurrentCulture).ToArray();
            }
        }
    }

    /// <summary>
    /// Who holds one PC, as far as this console has actually learned (M5 §4.6, D-58). It is
    /// linked here, or positively observed with a teacher machine that is still beaconing,
    /// or — when nothing was observed — offline if no other console is live and
    /// <see cref="OwnershipKind.Unknown"/> if one is. Not being linked here is never by
    /// itself a reason to say another console has it.
    /// </summary>
    public MachineOwnership Ownership(MachineRecord machine)
    {
        var now = _clock();
        var lastSeen = machine.LastSeenUnix == 0 ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(machine.LastSeenUnix);

        OtherConsole[] live;
        lock (_gate)
        {
            if (_linked.ContainsKey(machine.AgentId))
            {
                return new MachineOwnership(OwnershipKind.LinkedHere, LastSeen: now);
            }

            live = _others.Values.Where(o => now - o.LastSeen <= Defaults.OtherConsoleTimeout).ToArray();
        }

        // A machine whose access was withdrawn is nobody's holder, even if its beacon was
        // heard a moment before the withdrawal landed (D-56 item 6).
        live = live.Where(o => !Registry.Revocations.IsRevoked(LabCertificates.InstanceSerial(o.InstanceId))).ToArray();

        // The instance and the moment behind a claim are read together, under the registry's
        // own lock, so no tile can ever show one console's name with another's timestamp.
        var holder = live.FirstOrDefault(o => Registry.IsObservedWith(machine, o.InstanceId, now));
        if (holder is not null)
        {
            return new MachineOwnership(OwnershipKind.ObservedElsewhere, holder,
                DateTimeOffset.FromUnixTimeSeconds(machine.LastInstanceObservedUnix), lastSeen);
        }

        return new MachineOwnership(live.Length > 0 ? OwnershipKind.Unknown : OwnershipKind.Offline, LastSeen: lastSeen);
    }

    /// <summary>
    /// The PCs this console positively knows <paramref name="instanceId"/> holds: observed
    /// leaving for it during its own signed take-over, still within the observation
    /// lifetime, and not linked here since. The §3.7.2 banner counts these and says
    /// <i>at least</i>, because the PCs it knows nothing about may be anywhere.
    /// </summary>
    public IReadOnlyList<MachineRecord> ObservedElsewhere(string instanceId)
    {
        var now = _clock();
        var observed = Registry.ObservedElsewhere(instanceId, now);
        lock (_gate)
        {
            return observed.Where(m => !_linked.ContainsKey(m.AgentId)).ToArray();
        }
    }

    /// <summary>Adds <c>take</c> to the beacon for 30 s; every PC on another console re-homes here (§3.7.2).</summary>
    public void TakeOver()
    {
        _broadcaster?.TakeOver();

        // Whatever another machine did before this moment is history: the banner must stop
        // saying "… took over the lab at 10:32" the instant this console takes it back
        // (D-58). The observations that press produced stand until the PCs come back here
        // and supersede them by saying Hello.
        var changed = false;
        lock (_gate)
        {
            foreach (var (id, other) in _others.ToArray())
            {
                if (other.TookOverAt is not null || other.TookOverSeenAt is not null)
                {
                    _others[id] = other with { TookOverAt = null, TookOverSeenAt = null };
                    changed = true;
                }
            }
        }

        Events.Info("console.take_over", $"{Instance.InstanceName} is taking over the lab.");
        if (changed)
        {
            OtherConsolesChanged?.Invoke();
        }
    }

    public DateTimeOffset? TakingOverSince => _broadcaster?.TakeOverAt;

    /// <param name="receivedAt">
    /// When the socket produced the datagram. The console records beacons on its own clock and
    /// nothing it decides turns on sub-second arrival order — that rule belongs to the agent
    /// (D-58) — so this is not used here; it is part of the listener's contract for the PCs.
    /// </param>
    private void OnBeaconReceived(ReadOnlyMemory<byte> datagram, IPEndPoint from, DateTimeOffset receivedAt)
    {
        if (!Beacon.TryParse(datagram.Span, out var beacon) ||
            string.Equals(beacon.InstanceId, Instance.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = _clock();
        if (!beacon.TryVerify(Authority, LabId, now, out var failure))
        {
            if (failure != BeaconFailure.WrongLab)
            {
                _log.LogDebug("Dropped a beacon from {From}: {Failure}", from, failure);
            }

            // A stale timestamp is reached only after the endorsement and the signature have
            // verified, so this really is another teacher machine of this lab — one whose
            // clock is far enough out that neither side will hear the other at all. Silence
            // would leave two teachers wondering why nothing works, so it is said once in a
            // while, in the teacher's own words (D-58).
            if (failure == BeaconFailure.StaleTimestamp)
            {
                NoteClockSkew(beacon, now);
            }

            return;
        }

        // A withdrawn device is not another teacher machine any more (D-56 item 6): it is not
        // listed, cannot offer a Take over banner and can never be named as holding a PC. The
        // agents refuse it too; this is the console's own half of the same rule.
        if (Registry.Revocations.IsRevoked(LabCertificates.InstanceSerial(beacon.InstanceId)))
        {
            _log.LogDebug("Dropped a beacon from {From}: the access of {Instance} was withdrawn", from, beacon.InstanceId);
            ForgetOtherConsole(beacon.InstanceId);
            return;
        }

        Registry.RecordBeacon(beacon, now);

        var changed = false;
        lock (_gate)
        {
            var previous = _others.GetValueOrDefault(beacon.InstanceId);
            DateTimeOffset? tookOver = previous?.TookOverAt;
            var tookOverSeen = previous?.TookOverSeenAt;

            if (beacon.TakeAtUnix > 0)
            {
                // A console only carries `take` for TakeOverWindow after the press, so any
                // beacon that has it describes a press that is happening now. When it was
                // received is this console's own clock and is what decides, later, whether
                // a PC leaving is that take-over (D-58); the taker's timestamp is only shown.
                var at = DateTimeOffset.FromUnixTimeSeconds(beacon.TakeAtUnix);
                tookOverSeen = now;
                if (tookOver is null || at > tookOver)
                {
                    tookOver = at;
                    Events.Warning("console.taken_over",
                        $"{beacon.InstanceName} took over the lab at {at.ToLocalTime():HH:mm}.");
                    changed = true;
                }
            }

            if (previous is null)
            {
                Events.Info("console.other_seen", $"{beacon.InstanceName} is also running this lab (at {beacon.Endpoint}).");
                changed = true;
            }

            _others[beacon.InstanceId] = new OtherConsole(beacon.InstanceId, beacon.InstanceName, beacon.Endpoint, now, tookOver, tookOverSeen);
        }

        if (changed)
        {
            OtherConsolesChanged?.Invoke();
        }
    }

    /// <summary>
    /// A teacher machine's display name, from the beacons heard now or the instances
    /// <c>lab.json</c> remembers; the bare id when this console has never met it.
    /// </summary>
    /// <summary>A console instance's name as this session knows it — live, from the registry, or the id itself.</summary>
    public string NameOfInstance(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        lock (_gate)
        {
            if (_others.TryGetValue(instanceId, out var other) && other.Name.Length > 0)
            {
                return other.Name;
            }
        }

        var record = Registry.OtherTeacherMachines(Instance.InstanceId).FirstOrDefault(
            i => string.Equals(i.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        return record is { Name.Length: > 0 } ? record.Name : instanceId;
    }

    /// <summary>
    /// Drops a teacher machine from the live list — used when its access is withdrawn, so the
    /// banner, the <i>Take over</i> button it carries and any holder it could be named as all
    /// go away at once rather than at the next beacon timeout.
    /// </summary>
    private void ForgetOtherConsole(string instanceId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _others.Remove(instanceId);
        }

        if (removed)
        {
            OtherConsolesChanged?.Invoke();
        }
    }

    /// <summary>
    /// Another teacher machine of this lab whose clock is too far from ours for either side to
    /// hear the other (M5, D-58). Said at most once every
    /// <see cref="Defaults.OwnershipObservationLifetime"/> per machine, because the beacon
    /// repeats every two seconds and the answer is the same every time.
    /// </summary>
    private void NoteClockSkew(Beacon beacon, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_skewReported.TryGetValue(beacon.InstanceId, out var said) &&
                now - said < Defaults.OwnershipObservationLifetime)
            {
                return;
            }

            _skewReported[beacon.InstanceId] = now;
        }

        var skew = now - DateTimeOffset.FromUnixTimeSeconds(beacon.SentAtUnix);
        var name = beacon.InstanceName.Length > 0 ? beacon.InstanceName : beacon.InstanceId;
        Events.Warning("console.clock_skew",
            $"{name} is running this lab with a clock {Math.Abs(skew.TotalSeconds):0} s " +
            $"{(skew > TimeSpan.Zero ? "behind" : "ahead of")} this one. More than " +
            $"{Defaults.BeaconMaxSkew.TotalSeconds:0} s apart the two consoles cannot see each other at all, " +
            "and neither can take the lab from the other; set both clocks and they will find each other again.");
    }

    // ------------------------------------------------------------------ enrolment (gRPC entry point)

    public EnrollResponse Enroll(EnrollRequest request, IPAddress from)
    {
        var now = _clock();
        var who = string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, request.Number);

        if (Vault is null)
        {
            // Closed, naming the administrator (D-56 item 5): a teacher console has no key to issue with.
            _log.LogInformation("{Pc} at {From} asked to enrol; this console has teacher access", who, from);
            throw new RpcException(new Status(StatusCode.Unavailable, Strings.Get("Access.EnrolNeedsAdministrator")));
        }

        var lab = Vault.Peek();
        var result = Enrollment.Redeem(lab, request, now, Options.DevelopmentAgentCertificateLifetime);

        switch (result.Outcome)
        {
            case EnrollmentOutcome.Issued:
                SaveEnrollment();
                using (var certificate = result.Certificate!)
                {
                    var machine = Registry.RecordEnrollment(request, LabCertificates.SerialOf(certificate), now);
                    machine.CertificateNotAfterUnix = new DateTimeOffset(certificate.NotAfter.ToUniversalTime()).ToUnixTimeSeconds();
                    machine.LastIp = from.ToString();
                    Events.Info("enroll.issued", $"{who} enrolled from {from}.", request.AgentId, request.Number);
                    MachinesChanged?.Invoke();

                    return new EnrollResponse
                    {
                        AgentCertificate = Google.Protobuf.ByteString.CopyFrom(certificate.Export(X509ContentType.Cert)),
                        CaCertificate = Google.Protobuf.ByteString.CopyFrom(Authority.Export(X509ContentType.Cert)),
                        LabId = LabId,
                        ServerTimeUnix = now.ToUnixTimeSeconds(),
                    };
                }

            case EnrollmentOutcome.Closed:
                // Not an event: fourteen PCs asking before the teacher unlocks the key is normal.
                _log.LogInformation("{Pc} at {From} asked to enrol; the lab key is locked", who, from);
                throw new RpcException(new Status(StatusCode.Unavailable, result.Message));

            case EnrollmentOutcome.BurnedCode:
                Events.Warning("enroll.burned_code", $"{result.Message} (from {from})", request.AgentId, request.Number);
                throw new RpcException(new Status(StatusCode.PermissionDenied, result.Message));

            case EnrollmentOutcome.VoidedCode:
                Events.Warning("enroll.voided_code", $"{result.Message} (from {from})", request.AgentId, request.Number);
                throw new RpcException(new Status(StatusCode.PermissionDenied, result.Message));

            case EnrollmentOutcome.DormantCode:
                // Refused, and said once per attempt: the remedy is a Settings click (D-60).
                Events.Warning("enroll.dormant_code", $"{result.Message} (from {from})", request.AgentId, request.Number);
                throw new RpcException(new Status(StatusCode.Unavailable, result.Message));

            default:
                Events.Warning("enroll.refused", $"{result.Message} (from {from})", request.AgentId, request.Number);
                throw new RpcException(new Status(StatusCode.InvalidArgument, result.Message));
        }
    }

    /// <summary>
    /// Writes the USB payload's trust part (INSTALLER.md): <c>ca.crt</c> and <c>setup.json</c>
    /// with fresh single-use codes — one per PC plus spares. With <paramref name="voidEarlier"/>
    /// the unused codes of every earlier stick are voided first: a new stick replaces the old
    /// one (D-28). The teacher switches that off when PCs installed from an earlier stick are
    /// still waiting to enrol. The agent binaries are added by <c>tools/build-usb.sh</c> in M4;
    /// <c>FakeAgent</c> needs only this.
    /// </summary>
    public string WritePayload(string directory, int pcCount, bool voidEarlier = true)
    {
        if (Vault is null)
        {
            throw new InvalidOperationException(Strings.Get("Access.AdministratorNeeded"));
        }

        var now = _clock();
        var target = Path.Combine(directory, Defaults.PayloadDirectoryName);
        Directory.CreateDirectory(target);

        var voided = voidEarlier ? Enrollment.Supersede(now) : 0;
        var codes = Enrollment.Generate(pcCount + Defaults.SpareEnrollmentCodes, $"{Instance.InstanceName} {now:yyyy-MM-dd HH:mm}", now, Instance.InstanceId, Instance.InstanceName);
        SaveEnrollment();

        File.WriteAllBytes(Path.Combine(target, Defaults.CaCertificateFileName), Authority.Export(X509ContentType.Cert));

        var setup = new SetupPayloadDocument
        {
            LabId = LabId,
            LabName = LabName,
            ConsolePort = Port,
            NextNumber = 1,
            EnrollmentCodes = codes.ToList(),
            WrittenBy = Instance.InstanceName,
            WrittenAtUnix = now.ToUnixTimeSeconds(),
        };
        JsonStore.Save(Path.Combine(target, Defaults.SetupFileName), setup, SetupPayloadDocument.Migrations);

        Events.Info("enroll.payload_written", voided > 0
            ? $"USB payload with {codes.Count} enrollment codes written to {target}; {voided} unused code(s) from earlier sticks voided."
            : $"USB payload with {codes.Count} enrollment codes written to {target}.");
        return target;
    }

    // ------------------------------------------------------------------ renewal (gRPC entry point)

    public RenewResponse Renew(X509Certificate2? peer, RenewRequest request)
    {
        var now = _clock();
        var name = RequireAgent(peer);
        // A renewed certificate always gets the full lifetime, even with the development
        // switch that shortens enrolment certificates (D-27): otherwise a PC with a 30-day
        // certificate would renew, get another 30-day one, and renew again for ever.
        var response = new RenewResponse { ServerTimeUnix = now.ToUnixTimeSeconds() };

        if (Vault is null)
        {
            // Closed, naming the administrator (D-56 item 5); the PC asks the next administrator console.
            _log.LogInformation("{Pc} asked to renew its certificate; this console has teacher access", string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, name.Number));
            response.Refusal = Strings.Get("Access.RenewNeedsAdministrator");
            return response;
        }

        var result = CertificateRenewal.Renew(Vault.Peek(), name, request.Csr.ToByteArray(), now);

        if (result.Ok)
        {
            using var certificate = result.Certificate!;
            var machine = Registry.RecordRenewal(name.Id, LabCertificates.SerialOf(certificate), now);
            if (machine is not null)
            {
                machine.CertificateNotAfterUnix = new DateTimeOffset(certificate.NotAfter.ToUniversalTime()).ToUnixTimeSeconds();
            }

            Events.Info("renew.issued", result.Message, name.Id, name.Number);
            response.AgentCertificate = Google.Protobuf.ByteString.CopyFrom(certificate.Export(X509ContentType.Cert));
            MachinesChanged?.Invoke();
            return response;
        }

        if (result.Outcome == RenewalOutcome.Closed)
        {
            _log.LogInformation("{Message}", result.Message);
        }
        else
        {
            Events.Warning("renew.refused", result.Message, name.Id, name.Number);
        }

        response.Refusal = result.Message;
        return response;
    }

    /// <summary>Every PC of the lab, by number — a copy of the list, safe to read on any thread.</summary>
    public IReadOnlyList<MachineRecord> Machines()
    {
        lock (_gate)
        {
            return Registry.Document.Machines.OrderBy(m => m.Number).ToArray();
        }
    }

    /// <summary>The PCs whose certificates are inside the renewal lead time (D-25), for the banner.</summary>
    public IReadOnlyList<MachineRecord> MachinesNeedingRenewal()
    {
        var now = _clock();
        lock (_gate)
        {
            return Registry.Document.Machines
                .Where(m => m.CertificateNotAfterUnix > 0 &&
                            DateTimeOffset.FromUnixTimeSeconds(m.CertificateNotAfterUnix) - now < Defaults.CertificateRenewalLeadTime)
                .OrderBy(m => m.Number)
                .ToArray();
        }
    }

    // ------------------------------------------------------------------ the link (gRPC entry point)

    private LabName RequireAgent(X509Certificate2? peer)
    {
        if (Trust.TryValidate(peer, LabRole.Agent, Registry.Revocations, out var name, out var failure, _clock()))
        {
            return name;
        }

        // A PC of another lab saved on this device is the everyday case since M5: the teacher
        // opened lab B while lab A's PCs still dial. Say which lab, not only "not this one".
        var description = failure == TrustFailure.WrongLab && name is not null && LabNameResolver?.Invoke(name.LabId) is { Length: > 0 } otherLab
            ? $"it belongs to lab \"{otherLab}\", which is not the active lab on this console"
            : LabTrust.Describe(failure, name);
        var reason = $"this PC's certificate was refused: {description}";
        Events.Warning("link.refused", reason, name?.Id, name?.Number ?? 0);
        throw new RpcException(new Status(StatusCode.PermissionDenied, reason));
    }

    public async Task LinkAsync(
        X509Certificate2? peer,
        IPAddress from,
        IAsyncStreamReader<AgentMessage> incoming,
        IServerStreamWriter<ConsoleMessage> outgoing,
        CancellationToken callCancelled,
        Action? abort = null)
    {
        var name = RequireAgent(peer);

        if (!await incoming.MoveNext(callCancelled) || incoming.Current.PayloadCase != AgentMessage.PayloadOneofCase.Hello)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "the first message on a link must be Hello"));
        }

        var hello = incoming.Current.Hello;
        var who = string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, hello.Number);

        // The certificate proved who this is; Hello must not claim to be anyone else.
        if (!string.Equals(hello.AgentId, name.Id, StringComparison.OrdinalIgnoreCase) || hello.Number != name.Number)
        {
            var reason = $"{who} said it is agent {hello.AgentId} number {hello.Number}, but its certificate says {name.Id} number {name.Number}";
            Events.Warning("link.identity_mismatch", reason, name.Id, name.Number);
            throw new RpcException(new Status(StatusCode.PermissionDenied, reason));
        }

        if (!string.Equals(hello.LabId, LabId, StringComparison.OrdinalIgnoreCase))
        {
            var reason = $"{who} belongs to lab {hello.LabId}, not this one";
            Events.Warning("link.wrong_lab", reason, name.Id, name.Number);
            throw new RpcException(new Status(StatusCode.PermissionDenied, reason));
        }

        var now = _clock();
        var serial = LabCertificates.SerialOf(peer!);
        var machine = Registry.RecordHello(hello, serial, Instance.InstanceId, now, out var isNew);
        machine.CertificateNotAfterUnix = new DateTimeOffset(peer!.NotAfter.ToUniversalTime()).ToUnixTimeSeconds();
        machine.LastIp = from.ToString();
        NoteArrival(hello, who, now);

        var connection = new AgentConnection(hello, machine, serial, from, now, abort);

        AgentConnection? replaced;
        lock (_gate)
        {
            _linked.Remove(hello.AgentId, out replaced);
            _linked[hello.AgentId] = connection;
        }

        replaced?.Close("the PC opened a new link");

        if (isNew)
        {
            Events.Info("link.new_machine", $"{who} connected with a certificate from this lab and was added to the list.", hello.AgentId, hello.Number);
        }

        if (connection.IsOutdated)
        {
            Events.Warning("link.outdated",
                $"{who} runs protocol version {hello.ProtocolVersion}; this console needs {Defaults.MinimumProtocolVersion}. It stays connected and can be updated.",
                hello.AgentId, hello.Number);
        }
        else if (connection.IsNewer)
        {
            Events.Warning("link.newer_agent",
                $"{who} runs protocol version {hello.ProtocolVersion}, newer than this console ({Defaults.ProtocolVersion}); update the console.",
                hello.AgentId, hello.Number);
        }
        else if (!connection.SupportsClassroomControl)
        {
            Events.Warning("link.no_classroom_control",
                Localization.Strings.Format("Event.NoClassroomControl", who, hello.ProtocolVersion, Defaults.ClassroomControlProtocolVersion),
                hello.AgentId, hello.Number);
        }

        _log.LogInformation("{Pc} linked from {From} (agent {Agent}, serial {Serial})", who, from, hello.AgentVersion, serial);

        var welcome = new Welcome
        {
            ServerTimeUnix = now.ToUnixTimeSeconds(),
            InstanceId = Instance.InstanceId,
            InstanceName = Instance.InstanceName,

            // Informational only (M5, D-58): it is read off this console's own leaf, the very
            // certificate the agent has just validated, so it can never claim more than the
            // OU does — and the agent refuses jobs from the certificate, not from this field.
            ConsoleAccess = AnnouncedAccess(Instance.Access),

            // M7 (D-87): the PC writes what it sends on this link in the teacher's language.
            Language = Language.Code,
        };
        welcome.RevokedSerials.AddRange(Registry.Revocations.Serials);
        BeforeWelcome?.Invoke(welcome);
        connection.TrySend(new ConsoleMessage { Welcome = welcome });

        NoteWoke(hello.AgentId, who, now);
        DeliverJobs(connection, resendInFlight: true, hello.BootTimeUnix);

        // A lab moved to a new key (D-82 item 4): each PC that links here is sent its move.
        if (MoveTarget is { } moveTarget && !connection.IsOutdated)
        {
            MoveToNewKey([hello.AgentId], moveTarget);
        }

        // Screens (M3): every linked PC streams a thumbnail; the full view asks for more.
        // An M2-era agent answers with session.not_in_this_build once and is otherwise unhurt.
        if (!connection.IsOutdated)
        {
            var screen = Screens.Get(hello.AgentId);
            var wanted = screen.RequestedMode == VideoMode.Full ? FullControlFor(screen) : VideoSettings.ThumbnailControl();
            screen.RequestedMode = wanted.Mode;
            connection.RequestedVideo = wanted.Mode;
            connection.TrySend(new ConsoleMessage { VideoControl = wanted });

            // A PC the teacher is showing the screen to, back from a dropped link, joins the
            // broadcast again (D-71): it ended on the PC with the link's stream.
            if (BroadcastStartFor(hello.AgentId) is { } start)
            {
                connection.TrySend(new ConsoleMessage { Overlay = start });
            }
        }

        AgentLinked?.Invoke(connection);
        MachinesChanged?.Invoke();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callCancelled, connection.Closing);
        var token = linked.Token;
        var writer = WriteLoopAsync(connection, outgoing, token);
        var endedBy = "the PC closed the link";

        try
        {
            while (await incoming.MoveNext(token))
            {
                HandleIncoming(connection, incoming.Current);
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            endedBy = connection.ClosedBecause ?? (callCancelled.IsCancellationRequested ? "the PC closed the link" : ex.Message);
        }
        catch (Exception ex) when (ex is RpcException or IOException)
        {
            endedBy = $"the link failed: {ex.Message}";
        }
        finally
        {
            connection.Close(endedBy);
            await SwallowAsync(writer);

            bool wasCurrent;
            lock (_gate)
            {
                wasCurrent = _linked.TryGetValue(hello.AgentId, out var current) && ReferenceEquals(current, connection);
                if (wasCurrent)
                {
                    _linked.Remove(hello.AgentId);
                }
            }

            if (wasCurrent)
            {
                var wentOffline = _clock();
                NoteDeparture(connection, who, wentOffline);

                var dropped = Jobs.AgentWentOffline(hello.AgentId, wentOffline);
                foreach (var job in dropped)
                {
                    Events.Info("job.not_delivered", $"{who}: {job.Kind} was not delivered — the PC went offline first.", hello.AgentId, hello.Number);
                }
            }

            _log.LogInformation("{Pc} unlinked: {Reason}", who, endedBy);
            AgentUnlinked?.Invoke(connection, endedBy);
            MachinesChanged?.Invoke();
        }
    }

    /// <summary>This console's own leaf access in the <c>Welcome</c> field's terms (M5, D-58).</summary>
    private static Welcome.Types.ConsoleAccess AnnouncedAccess(ConsoleAccess access) => access switch
    {
        ConsoleAccess.Administrator => Welcome.Types.ConsoleAccess.Administrator,
        ConsoleAccess.Teacher => Welcome.Types.ConsoleAccess.Teacher,
        _ => Welcome.Types.ConsoleAccess.Unspecified,
    };

    /// <summary>
    /// The other half of what a console can positively learn (M5 §4.6, D-58): a PC's
    /// <c>Hello</c> naming the console it has just left, while that machine is beaconing
    /// here too. It is a fact about the past — this console holds the PC as of this
    /// <c>Hello</c>, which is why it supersedes any earlier ownership rather than becoming
    /// one — so it is recorded where it belongs, in the log the teacher reads.
    /// </summary>
    private void NoteArrival(Hello hello, string who, DateTimeOffset now)
    {
        if (hello.PreviousInstanceId.Length == 0 ||
            string.Equals(hello.PreviousInstanceId, Instance.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        OtherConsole? previous;
        lock (_gate)
        {
            previous = _others.GetValueOrDefault(hello.PreviousInstanceId);
            if (previous is not null && now - previous.LastSeen > Defaults.OtherConsoleTimeout)
            {
                previous = null;
            }
        }

        if (previous is not null)
        {
            Events.Info("link.arrived_from", $"{who} came to this console from {previous.Name}.", hello.AgentId, hello.Number);
        }
    }

    /// <summary>
    /// The only way this console learns that another teacher machine holds a PC (M5 §4.6,
    /// D-58): <b>the PC said so on its way out</b>. It has just honoured that machine's signed
    /// <c>take</c> beacon, so it names the taker in a <c>link.taken_over</c> event before it
    /// half-closes the stream, and that report — not the end of the stream — is what this
    /// console attributes on. A stream ending for any other reason (the PC switched off, a
    /// dropped cable or Wi-Fi, a crash, an agent restart, an agent too old to say anything)
    /// leaves no observation behind, and the tile then says offline or "not seen", never
    /// "held by". Naming a machine is still only a claim, so it must be one this console has
    /// itself heard beaconing and has not withdrawn.
    /// </summary>
    private void NoteDeparture(AgentConnection connection, string who, DateTimeOffset now)
    {
        var machine = connection.Machine;
        if (connection.LeavingForInstanceId is not { Length: > 0 } takerId)
        {
            return;
        }

        OtherConsole? taker;
        lock (_gate)
        {
            taker = _others.GetValueOrDefault(takerId);
        }

        if (taker is null || Registry.Revocations.IsRevoked(LabCertificates.InstanceSerial(taker.InstanceId)))
        {
            _log.LogDebug("{Pc} said it left for {Instance}, which this console has not heard beaconing; nobody is credited with it", who, takerId);
            return;
        }

        if (Registry.RecordObservedElsewhere(machine.AgentId, taker.InstanceId, now) is null)
        {
            return;
        }

        Events.Info("link.left_for_taker", $"{who} left this console for {taker.Name}, which is taking over the lab.", machine.AgentId, machine.Number);
    }

    private void HandleIncoming(AgentConnection connection, AgentMessage message)
    {
        var now = _clock();
        connection.Touch(now);
        var who = string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, connection.Number);

        switch (message.PayloadCase)
        {
            case AgentMessage.PayloadOneofCase.Heartbeat:
            case AgentMessage.PayloadOneofCase.Pong:
                connection.Machine.LastSeenUnix = now.ToUnixTimeSeconds();
                break;

            case AgentMessage.PayloadOneofCase.RevocationState:
                var offered = message.RevocationState.Entries;
                var added = Registry.Merge(offered);
                foreach (var entry in added)
                {
                    Events.Warning("revocation.learned",
                        $"{who} brought a revocation this console had not seen: serial {entry.Serial} ({entry.Reason}).",
                        connection.AgentId, connection.Number);
                }

                var rejected = offered.Count - offered.Count(e => Registry.Revocations.IsRevoked(e.Serial));
                if (rejected > 0)
                {
                    Events.Warning("revocation.forged",
                        $"{who} offered {rejected} revocation entr{(rejected == 1 ? "y" : "ies")} the lab key never signed; ignored.",
                        connection.AgentId, connection.Number);
                }

                // What this PC confirmed holding (D-56 item 6): the delivery list, never a claim.
                var seen = offered.Select(e => LabCertificates.NormalizeSerial(e.Serial)).Where(Registry.Revocations.IsRevoked).Distinct(StringComparer.Ordinal).ToArray();
                if (!seen.SequenceEqual(connection.Machine.RevocationSerialsSeen, StringComparer.Ordinal))
                {
                    connection.Machine.RevocationSerialsSeen = seen;
                    SaveLabSoon();
                    RevocationDeliveryChanged?.Invoke();
                }

                foreach (var entry in added)
                {
                    NoteRevocationOfSelf(entry);
                }

                // Learned here, so news to the other linked PCs too — and a PC linked with the
                // very certificate this entry revokes is cut now, not at its next link. A console
                // restored from a backup older than the revocation accepted it a moment ago
                // (M7 portion 5): the first honest PC to arrive is what tells it otherwise.
                if (added.Count > 0)
                {
                    Push(added);
                }

                var missing = Registry.Revocations.Except(offered.Select(e => e.Serial));
                if (missing.Count > 0)
                {
                    var revocation = new Revocation();
                    revocation.Entries.AddRange(missing);
                    connection.TrySend(new ConsoleMessage { Revocation = revocation });
                }

                break;

            case AgentMessage.PayloadOneofCase.Inventory:
                var inventory = message.Inventory;
                if (inventory.Hostname.Length > 0)
                {
                    connection.Machine.Hostname = inventory.Hostname;
                }

                connection.Machine.LoggedOnUser = inventory.LoggedOnUser;
                SaveLabSoon();
                MachinesChanged?.Invoke();
                break;

            case AgentMessage.PayloadOneofCase.SessionState:
                var state = message.SessionState;
                var previousHelper = connection.HelperAlive;
                connection.Machine.LoggedOnUser = state.Kind is SessionState.Types.Kind.Logoff || state.User.Length == 0 ? null : state.User;
                connection.ApplySessionState(state);

                switch (state.Kind)
                {
                    case SessionState.Types.Kind.Logon:
                        Events.Info("session.logon", $"{who}: {state.User} logged on.", connection.AgentId, connection.Number);
                        break;
                    case SessionState.Types.Kind.Logoff:
                        Events.Info("session.logoff", $"{who}: logged off.", connection.AgentId, connection.Number);
                        break;
                    case SessionState.Types.Kind.Lock:
                        Events.Info("session.lock", $"{who}: screen locked.", connection.AgentId, connection.Number);
                        break;
                    case SessionState.Types.Kind.Unlock:
                        Events.Info("session.unlock", $"{who}: screen unlocked.", connection.AgentId, connection.Number);
                        break;
                }

                // At logon and logoff Windows ends the helper and the service re-spawns it by
                // design; only an unexplained loss is worth a warning.
                if (previousHelper is true && !state.HelperAlive && state.Kind is not (SessionState.Types.Kind.Logon or SessionState.Types.Kind.Logoff))
                {
                    Events.Warning("session.helper_down", $"{who}: the session helper is not running; screens, control and lock are unavailable there until it is back.", connection.AgentId, connection.Number);
                }

                // The logged-on user is part of the persisted record, so a restarted console
                // does not show yesterday's student until the next change.
                SaveLabSoon();
                MachinesChanged?.Invoke();
                break;

            case AgentMessage.PayloadOneofCase.OverlayState:
                ApplyOverlayState(connection, message.OverlayState, who);
                break;

            case AgentMessage.PayloadOneofCase.InternetState:
                ApplyInternetState(connection, message.InternetState, who);
                break;

            case AgentMessage.PayloadOneofCase.ExamState:
                ApplyExamState(connection, message.ExamState, who);
                break;

            case AgentMessage.PayloadOneofCase.JobProgress:
                Jobs.Progress(message.JobProgress, now);
                break;

            case AgentMessage.PayloadOneofCase.JobResult:
                Jobs.Complete(message.JobResult, now);
                break;

            case AgentMessage.PayloadOneofCase.Event:
                var reported = message.Event;

                // The PC is on its way out because another teacher machine took it over, and
                // says which one (D-58). It is remembered on the connection and read when the
                // stream ends: nothing else this console can see tells a take-over apart from
                // a PC that was simply switched off.
                if (DepartureNotice.TakerOf(reported) is { } takerInstance)
                {
                    connection.NoteLeavingFor(takerInstance);
                }
                if (reported.Code == SetupReadiness.EventCode)
                {
                    // The agent repeats its readiness snapshot on every reconnect. A valid,
                    // unchanged snapshot is not news: no event line, nothing to save (D-66).
                    // An unreadable one is still logged below, and never clears the cache.
                    var codes = ReadinessPresentation.TryParse(reported.Message);
                    if (codes is not null && (connection.Machine.SetupReadinessCodes ?? []).SequenceEqual(codes)) break;
                    Events.Add(codes is null || SetupReadiness.AnyNeedsAttention(codes) ? EventSeverity.Warning : EventSeverity.Info,
                        reported.Code, $"{who}: {ReadinessPresentation.EventText(reported.Message)}", connection.AgentId, connection.Number);
                    if (connection.ApplyEvent(reported))
                    {
                        SaveLabSoon();
                        MachinesChanged?.Invoke();
                    }
                    break;
                }
                var eventText = reported.Code == SetupReadiness.EventCode
                    ? ReadinessPresentation.EventText(reported.Message)
                    : reported.Code == UpdateTerminalReport.RolledBackCode
                        ? Localization.Strings.Get("Update.RollbackReported") + (InstallLayout.IsValidVersion(reported.Message)
                            ? " " + Localization.Strings.Format("Tile.UpdateRolledBack", reported.Message) : "")
                        : reported.Code == UpdateTerminalReport.StableCode
                            ? Localization.Strings.Get("Tile.UpdateStable")
                            : reported.Code == DepartureNotice.Code
                                ? Localization.Strings.Format("Event.TakenOver", NameOfInstance(reported.Message))
                                : reported.Message;
                Events.Add(reported.Severity switch
                {
                    Event.Types.Severity.Error => EventSeverity.Error,
                    Event.Types.Severity.Warning => EventSeverity.Warning,
                    _ => EventSeverity.Info,
                }, reported.Code.Length > 0 ? reported.Code : "agent.event", $"{who}: {eventText}", connection.AgentId, connection.Number);

                // A capture or input problem is state the tile and the single-PC window show
                // (M3 portion 3), not only a line in the log.
                if (connection.ApplyEvent(reported)) MachinesChanged?.Invoke();

                break;

            default:
                _log.LogDebug("{Pc} sent {Kind}, which this console does not handle", who, message.PayloadCase);
                break;
        }
    }

    private static async Task WriteLoopAsync(AgentConnection connection, IServerStreamWriter<ConsoleMessage> outgoing, CancellationToken token)
    {
        await foreach (var message in connection.Outgoing.ReadAllAsync(token))
        {
            await outgoing.WriteAsync(message, token);
        }
    }

    // ------------------------------------------------------------------ jobs

    /// <summary>
    /// One toolbar click: a job per selected PC sharing a batch id, delivered at once to
    /// the PCs that are linked and queued for the rest (or closed on the spot for
    /// <c>online_only</c>, D-25).
    /// </summary>
    public IReadOnlyList<JobRecord> CreateJobs(
        IEnumerable<string> agentIds,
        Job.Types.Kind kind,
        IReadOnlyDictionary<string, string>? args = null,
        TimeSpan? timeout = null,
        JobDelivery? delivery = null)
    {
        var now = _clock();
        var batch = Guid.NewGuid().ToString("d");
        var created = new List<JobRecord>();

        foreach (var agentId in agentIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var connection = FindLinked(agentId);
            var job = Jobs.Create(agentId, kind, now, connection is not null, args, timeout, delivery, batch);
            created.Add(job);
        }

        TryWriteBatchLogs(batch, register: true);
        foreach (var job in created)
        {
            if (FindLinked(job.AgentId) is { } connection)
            {
                DeliverJobs(connection, resendInFlight: false);
            }
        }

        return created;
    }

    /// <summary>One selection of handouts: all names/files validated before any jobs are dispatched.</summary>
    public IReadOnlyList<JobRecord> SendFiles(IEnumerable<string> agentIds, IEnumerable<string> paths, bool open)
    {
        var targets = agentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var files = paths.ToArray();
        if (targets.Length == 0 || files.Length == 0) return [];
        var names = files.Select(Path.GetFileName).ToArray();
        if (names.Any(n => n is null || !SendFileRequest.IsValidName(n))
            || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ArgumentException("Choose files with distinct Windows-compatible names.");
        var offers = files.Select(Files.OfferFile).ToArray();
        var batch = Guid.NewGuid().ToString("d");
        var created = new List<JobRecord>();
        var now = _clock();
        for (var i = 0; i < offers.Length; i++)
        {
            var offer = offers[i];
            var request = new SendFileRequest(offer.Reference, offer.Sha256, names[i]!, open);
            foreach (var target in targets)
                created.Add(Jobs.Create(target, Job.Types.Kind.SendFile, now, FindLinked(target) is not null,
                    request.ToArgs(), Defaults.FileChunkTimeout + Defaults.JobTimeoutGrace, JobDelivery.Queued, batch));
        }
        TryWriteBatchLogs(batch, register: true);
        foreach (var target in targets)
            if (FindLinked(target) is { } connection) DeliverJobs(connection, resendInFlight: false);
        return created;
    }

    /// <summary>
    /// The console's inactivity budget for an <c>install_package</c> job: the PC reports while it
    /// waits for another install, while it receives and every <see cref="Defaults.PackageInstallHeartbeat"/>
    /// while the installer runs, and ends a stuck installer itself (D-89).
    /// </summary>
    public static TimeSpan PackageJobTimeout =>
        (Defaults.FileChunkTimeout > Defaults.PackageInstallHeartbeat * 2 ? Defaults.FileChunkTimeout : Defaults.PackageInstallHeartbeat * 2) + Defaults.JobTimeoutGrace;

    /// <summary>
    /// <c>install_package</c> for <paramref name="entry"/> on <paramref name="agentIds"/> (D-89): the
    /// installer is offered from the catalog under the hash it was added with, one job per PC in one
    /// batch, queued for a PC that is off (owner decision 2026-10-27). <see cref="InstallMode.Check"/>
    /// is for PCs online now only — a check is about the present.
    /// </summary>
    public IReadOnlyList<JobRecord> InstallPackage(IEnumerable<string> agentIds, PackageEntry entry, InstallMode mode)
    {
        var targets = agentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) return [];
        if (mode != InstallMode.Check)
        {
            if (Packages.StateOf(entry) != InstallerState.Present)
            {
                throw new InvalidOperationException(Localization.Strings.Get("Packages.State." + Packages.StateOf(entry)));
            }

            Files.OfferKnownFile(Packages.InstallerPath(entry), entry.Sha256);
        }

        var now = _clock();
        var batch = Guid.NewGuid().ToString("d");
        var args = InstallPackageRequest.For(entry, mode).ToArgs();
        var delivery = mode == InstallMode.Check ? JobDelivery.OnlineOnly : JobDelivery.Queued;
        var created = targets.Select(target => Jobs.Create(target, Job.Types.Kind.InstallPackage, now, FindLinked(target) is not null,
            new Dictionary<string, string>(args), PackageJobTimeout, delivery, batch)).ToList();
        TryWriteBatchLogs(batch, register: true);
        foreach (var target in targets)
            if (FindLinked(target) is { } connection) DeliverJobs(connection, resendInFlight: false);
        return created;
    }

    /// <summary>
    /// <i>Install on all missing</i> (D-89): every PC of the lab except those known to hold this
    /// installer and those already busy with it. A PC known to hold an older version gets a plain
    /// install (its rule would find the old program and skip); every other PC an install that the
    /// PC itself skips when its rule finds the program.
    /// </summary>
    public IReadOnlyList<JobRecord> InstallPackageOnAllMissing(PackageEntry entry)
    {
        var machines = Machines();
        var upgrade = new List<string>();
        var missing = new List<string>();
        foreach (var machine in machines)
        {
            var state = PackageStatus.StateOf(entry, machine.AgentId);
            if (state is PcPackageState.Installed or PcPackageState.RebootRequired or PcPackageState.Busy) continue;
            var last = PackageStatus.Last(entry.Id, machine.AgentId);
            var olderVersion = last is not null && PackageOutcomes.MeansInstalled(last.Outcome) && last.Outcome != PackageOutcomes.Present
                && !string.Equals(last.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase);
            (olderVersion ? upgrade : missing).Add(machine.AgentId);
        }

        return [.. InstallPackage(upgrade, entry, InstallMode.Install), .. InstallPackage(missing, entry, InstallMode.Missing)];
    }

    /// <summary>
    /// An <c>install_package</c> queued for a PC that is off names its installer by hash; once
    /// the entry is replaced by a new version or deleted, that installer is gone from the catalog
    /// and the job could only fail — or install last week's version — when the PC returns (M7
    /// portion 5). Such a job is closed as not delivered with the reason, and the PC is again
    /// one <i>Install on all missing</i> reaches.
    /// </summary>
    private void DropStaleInstalls()
    {
        Dictionary<string, string> present;
        try
        {
            present = Packages.List()
                .Where(item => item.Entry is not null && item.State == InstallerState.Present)
                .ToDictionary(item => item.Id, item => item.Entry!.Sha256, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("package catalog unreadable; queued installs kept: {Message}", ex.Message);
            return;
        }

        var dropped = Jobs.DropPending(job => job.Kind == Job.Types.Kind.InstallPackage
                && job.Args.TryGetValue("package", out var package)
                && !(present.TryGetValue(package, out var sha256) && string.Equals(sha256, job.Args.GetValueOrDefault("sha256"), StringComparison.OrdinalIgnoreCase)),
            _clock(), Localization.Strings.Get("Packages.QueuedInstallReplaced"));
        foreach (var job in dropped)
        {
            var number = Registry.FindByAgentId(job.AgentId)?.Number ?? 0;
            Events.Info("job.not_delivered", $"{string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, number)}: {job.Message}", job.AgentId, number);
        }
    }

    /// <summary><i>Check PCs</i>: every linked PC evaluates the entry's rule and says whether the program is there.</summary>
    public IReadOnlyList<JobRecord> CheckPackage(PackageEntry entry) =>
        InstallPackage(Linked.Select(c => c.AgentId), entry, InstallMode.Check);

    /// <summary>
    /// <i>Collect files…</i> outside an exam (owner decision 2026-10-23): <paramref name="folder"/>
    /// from every PC in <paramref name="agentIds"/> into a new <c>&lt;name date&gt;</c> folder under
    /// <paramref name="destinationRoot"/>; a PC that is not linked is reported missing.
    /// </summary>
    public WorkCollection CollectFiles(IEnumerable<string> agentIds, string name, string destinationRoot, string folder) =>
        Collections.Start(name, destinationRoot, folder, agentIds);

    /// <summary>
    /// <i>Reset profile…</i> (D-78): a <c>reset_profile</c> job per PC, online only — a reset is
    /// meant for now, never for a PC switched on tomorrow. The PC refuses during an exam and on
    /// an installation without the managed student.
    /// </summary>
    public IReadOnlyList<JobRecord> ResetProfiles(IEnumerable<string> agentIds, ResetMode mode) =>
        CreateJobs(agentIds, Job.Types.Kind.ResetProfile, new ResetProfileRequest(mode).ToArgs(),
            Defaults.ProfileUnloadTimeout + Defaults.ProfileWipeMaxDuration + Defaults.JobTimeoutGrace, JobDelivery.OnlineOnly);

    /// <summary>
    /// Runs a library script on the given PCs (D-31): the text is offered through
    /// <c>PullFile</c> under its hash and a <c>run_script</c> job goes to each PC with the
    /// script's shell, run-as and timeout. The console's inactivity timeout is the script's
    /// plus a grace, so the agent's own "killed" result always arrives first (D-32). The
    /// record need not be saved — unsaved text runs once, as typed.
    /// </summary>
    public IReadOnlyList<JobRecord> RunScript(IEnumerable<string> agentIds, ScriptRecord script)
    {
        var name = RunScriptRequest.SafeName(script.Name);
        if (name.Length == 0)
        {
            name = "script";
        }

        var offer = Files.OfferText(script.Text, name);
        var request = new RunScriptRequest(offer.Reference, offer.Sha256, script.ShellKind, script.RunAsKind, script.Timeout, name);
        return CreateJobs(agentIds, Job.Types.Kind.RunScript, request.ToArgs(), script.Timeout + Defaults.JobTimeoutGrace);
    }

    /// <summary>
    /// Signs the build manifest with the unlocked lab key, offers its files through
    /// <c>PullFile</c> and sends a
    /// <c>self_update</c> job per PC. The result comes from the <b>new</b> version after the
    /// service restart, so the job's inactivity timeout is generous.
    /// </summary>
    public IReadOnlyList<JobRecord> PushAgentBuild(IEnumerable<string> agentIds, AgentBuild build)
    {
        if (Vault is null)
        {
            throw new InvalidOperationException(Strings.Get("Access.AdministratorNeeded"));
        }

        if (!Vault.Use(key => UpdateManifestSignature.Sign(key, build.Manifest), out var signature))
        {
            throw new InvalidOperationException("Unlock the lab key before signing an agent update.");
        }

        foreach (var file in build.Files)
        {
            Files.OfferFile(file.Path);
        }

        var manifest = Files.OfferBytes(build.Manifest, $"manifest-{build.Version}");
        var request = new SelfUpdateRequest(build.Version, manifest.Reference, manifest.Sha256, signature);
        _log.LogInformation("pushing agent build {Version} from {Folder} ({Bytes} bytes)", build.Version, build.Folder, build.TotalBytes);

        // The latest push wins for a PC that is off (M7 portion 5): two builds queued for it
        // would both be delivered on its return, the second only to be refused while the first
        // is on probation (D-83), leaving it on the older build with a failed row.
        var targets = agentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var wanted = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        var superseded = Jobs.DropPending(job => job.Kind == Job.Types.Kind.SelfUpdate && wanted.Contains(job.AgentId),
            _clock(), Localization.Strings.Format("Update.QueuedSuperseded", build.Version));
        foreach (var job in superseded)
        {
            var number = Registry.FindByAgentId(job.AgentId)?.Number ?? 0;
            Events.Info("job.not_delivered", $"{string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, number)}: {job.Message}", job.AgentId, number);
        }

        return CreateJobs(targets, Job.Types.Kind.SelfUpdate, request.ToArgs(), Defaults.SelfUpdateJobTimeout);
    }

    private void DeliverJobs(AgentConnection connection, bool resendInFlight, long bootTimeUnix = 0)
    {
        var now = _clock();

        if (resendInFlight)
        {
            foreach (var job in Jobs.InFlight(connection.AgentId))
            {
                // A row this session brought back from disk (D-57 item 4) is only worth sending
                // again while the agent's in-memory ledger can still answer it. The PC's own
                // boot time says whether that ledger survived; if it did not, the re-sent copy
                // would run a second time, so the row is closed as outcome unknown instead.
                if (job.RestoredFromDisk && InFlightJobPolicy.RebootedSinceDelivery(job.DeliveredAtUnix, bootTimeUnix))
                {
                    if (Jobs.CloseAsOutcomeUnknown(job.Id, now, "the PC has restarted since, so it no longer remembers this job") is { } closed)
                    {
                        Events.Warning("job.outcome_unknown",
                            $"{string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, connection.Number)}: {closed.Kind} — {closed.Message}",
                            connection.AgentId, connection.Number);
                    }

                    continue;
                }

                connection.TrySend(new ConsoleMessage { Job = job.ToMessage() });
            }
        }

        foreach (var job in Jobs.TakePending(connection.AgentId, now))
        {
            connection.TrySend(new ConsoleMessage { Job = job.ToMessage() });
        }
    }

    private void OnJobUpdated(JobRecord job)
    {
        if (job.IsFinished)
        {
            _journal.Record(job);
            TryWriteBatchLogs(job.BatchId);
        }

        // The durable in-flight set follows deliveries and closures; progress lines are
        // saved with the session's close, not on every line from thirty PCs. This runs on
        // whichever gRPC handler thread reported the change, so the write is coalesced the
        // way lab.json's is — thirty PCs finishing one batch must not race on one file.
        if (job.State == JobState.Delivered || job.IsFinished)
        {
            SaveInFlightJobsSoon();
        }
    }

    /// <summary>
    /// What the previous session of this lab, on this same instance, left running on the PCs
    /// (D-57 item 4). A row comes back live only when sending it a second time is harmless and
    /// this session can still serve what it needs — in practice a <c>run_script</c> whose text
    /// is still in the library (<see cref="InFlightJobPolicy"/>). Every other row comes back as
    /// a closed <i>outcome unknown</i> line, because silence would be a lie. The file is
    /// consumed here and a restored row is never written back, so it cannot outlive two
    /// sessions. Nothing in here may stop the lab from opening.
    /// </summary>
    private void RestoreInFlightJobs()
    {
        var kept = InFlightJobs.Load(Store, LabId, Instance.InstanceId, out var problem);
        if (problem is not null)
        {
            Events.Warning("jobs.inflight_unreadable",
                $"{Defaults.InFlightJobsFileName} was ignored and the jobs it listed will not be sent again: {problem}");
        }

        if (kept.Jobs.Count == 0)
        {
            return;
        }

        var now = _clock();
        var restored = Jobs.Restore(kept.Jobs, now, job => RefuseRestore(job, kept.SavedAt, now));
        foreach (var batch in restored.Resent.Concat(restored.Unknown)
                     .Select(j => j.BatchId).Where(b => b.Length > 0).Distinct(StringComparer.Ordinal))
        {
            TryWriteBatchLogs(batch, restore: true);
        }

        _log.LogInformation("{Lab}: {Resent} in-flight job(s) restored for re-sending, {Unknown} closed as outcome unknown, {Foreign} row(s) of another lab or instance dropped",
            LabName, restored.Resent.Count, restored.Unknown.Count, restored.Foreign);

        if (restored.Resent.Count > 0)
        {
            Events.Info("jobs.restored", $"{restored.Resent.Count} job(s) were still running on PCs when this console last left the lab; their results arrive as the PCs link.");
        }

        foreach (var job in restored.Unknown)
        {
            Events.Warning("job.outcome_unknown", $"{job.Kind} on agent {job.AgentId}: {job.Message}", job.AgentId);
        }

        if (restored.Foreign > 0)
        {
            Events.Warning("jobs.inflight_foreign",
                $"{restored.Foreign} saved job row(s) were not this console's to take up again — another lab, another console instance, or already finished — and were dropped.");
        }

        SaveInFlightJobs();
    }

    /// <summary>
    /// Why one saved row must not be sent to its PC again, or <c>null</c> when it may be. On
    /// top of <see cref="InFlightJobPolicy"/>'s kind and age rules this session has to be able
    /// to serve the payload: a script travels through <c>PullFile</c>, and the offer died with
    /// the session that made it, so the text is looked up in the library by the reference the
    /// job carries — the reference is the content's SHA-256 (D-31) — and offered again.
    /// </summary>
    private string? RefuseRestore(InFlightJob job, DateTimeOffset savedAt, DateTimeOffset now)
    {
        if (InFlightJobPolicy.RefuseReason(job, savedAt, now) is { } refusal)
        {
            return refusal;
        }

        var message = new Job { Id = job.Id, Kind = job.Kind, TimeoutSeconds = job.TimeoutSeconds };
        message.Args.Add(job.Args);
        if (!RunScriptRequest.TryParse(message, out var request, out var error))
        {
            return $"its arguments cannot be read ({error})";
        }

        if (Files.Find(request.Reference) is not null)
        {
            return null;
        }

        var script = Scripts.Scripts.FirstOrDefault(s =>
            string.Equals(FileHash.Sha256Hex(FileOffers.TextBytes(s.Text)), request.Sha256, StringComparison.OrdinalIgnoreCase));
        if (script is null)
        {
            return "the script it was running is no longer in this console's library, so the PC could not fetch it again";
        }

        Files.OfferText(script.Text, request.Name);
        return null;
    }

    private int _inFlightSavePending;
    private readonly Lock _inFlightSaveGate = new();

    /// <summary>
    /// Coalesces the in-flight save the way <see cref="SaveLabSoon"/> coalesces <c>lab.json</c>:
    /// thirty PCs closing one batch at once produce one write, not thirty racing ones.
    /// </summary>
    private void SaveInFlightJobsSoon()
    {
        if (IsDisposed || Interlocked.Exchange(ref _inFlightSavePending, 1) == 1)
        {
            return;
        }

        CancellationToken stopping;
        try
        {
            // A change reported by a link that is only now finishing, after the close: the
            // close already wrote the file, so there is nothing left to coalesce.
            stopping = _stopping.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, stopping);
            }
            catch (OperationCanceledException)
            {
            }

            Interlocked.Exchange(ref _inFlightSavePending, 0);
            if (!IsDisposed)
            {
                SaveInFlightJobs();
            }
        });
    }

    private void SaveInFlightJobs()
    {
        try
        {
            Interlocked.Exchange(ref _inFlightSavePending, 0);
            lock (_inFlightSaveGate)
            {
                InFlightJobs.Save(Store, Jobs, _clock());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Events.Warning("jobs.inflight_save_failed", $"Could not save {Defaults.InFlightJobsFileName}: {ex.Message}");
        }
    }

    private void TryWriteBatchLogs(string batchId, bool register = false, bool restore = false)
    {
        try
        {
            if (restore)
            {
                // A batch whose rows this session brought back already has a file with the
                // results of the PCs that finished before the console left; registering it
                // again must add to that document, never replace it (D-43, D-57 item 4).
                BatchLogs.Restore(batchId);
            }
            else if (register)
            {
                BatchLogs.Register(batchId);
            }
            else
            {
                BatchLogs.Record(batchId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException or SchemaVersionException)
        {
            // A batch id that is not a GUID, or a log a newer build wrote, is a log problem:
            // it is reported and the lab carries on.
            Events.Warning("jobs.log_failed", "Could not save the batch job logs: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ files (gRPC entry point)

    /// <summary>
    /// Serves one offered file to an agent (PROTOCOL, <i>Files</i>): the peer must be this
    /// lab's agent, the reference must be offered, and — in this minimal form — the pull
    /// starts at offset 0. The last chunk carries the hash and the total.
    /// </summary>
    // ------------------------------------------------------------------ screens (M3)

    /// <summary>
    /// The agent's <c>PushVideo</c> stream (PROTOCOL "Video"): every frame lands in
    /// <see cref="Screens"/>. The peer certificate says whose screen it is; a frame that
    /// claims another agent is dropped and the stream ended.
    /// </summary>
    public async Task<VideoAck> ReceiveVideoAsync(X509Certificate2? peer, IAsyncStreamReader<VideoFrame> incoming, CancellationToken token)
    {
        var name = RequireAgent(peer);
        var who = string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, name.Number);
        ulong last = 0;

        try
        {
            while (await incoming.MoveNext(token))
            {
                var frame = incoming.Current;
                if (frame.AgentId.Length > 0 && !string.Equals(frame.AgentId, name.Id, StringComparison.OrdinalIgnoreCase))
                {
                    Events.Warning("video.identity_mismatch", $"{who} sent a screen frame labelled as agent {frame.AgentId}; the stream was closed.", name.Id, name.Number);
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "the frame names another agent"));
                }

                if (frame.Jpeg.Length > Defaults.VideoFrameMaxBytes)
                {
                    Events.Warning("video.frame_too_large", $"{who} sent a {frame.Jpeg.Length / 1024} KiB frame, above the {Defaults.VideoFrameMaxBytes / 1024} KiB limit; dropped.", name.Id, name.Number);
                    continue;
                }

                last = frame.Seq;
                var outcome = Screens.Apply(name.Id, frame);
                if (outcome is FrameOutcome.Undecodable or FrameOutcome.Malformed)
                {
                    _log.LogDebug("{Pc}: frame {Seq} {Outcome}", who, frame.Seq, outcome);
                }
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested || ex is IOException)
        {
            // The PC dropped the stream or the link ended; the picture stays as it was.
        }

        return new VideoAck { LastSeq = last };
    }

    /// <summary>
    /// Switches a linked PC between the mosaic thumbnail and the native-resolution stream
    /// for the single-PC view (ARCHITECTURE §8). Remembered per PC, so a PC that relinks
    /// while its full view is open comes back in full.
    /// </summary>
    public void SetScreenMode(string agentId, VideoMode mode, int? quality = null, bool? video = null)
    {
        var screen = Screens.Get(agentId);
        screen.RequestedMode = mode == VideoMode.Full ? VideoMode.Full : VideoMode.Thumbnail;
        if (quality is { } chosen)
        {
            screen.RequestedQuality = Math.Clamp(chosen, Defaults.VideoQualityAuto, 100);
        }

        if (video is { } wanted)
        {
            // A choice made again (or a window opened again) gives H.264 another chance.
            screen.RequestedVideo = wanted;
            screen.VideoBroken = false;
            screen.FullDecoder.Reset();
        }

        var connection = FindLinked(agentId);
        if (connection is null || connection.IsOutdated)
        {
            return;
        }

        connection.RequestedVideo = screen.RequestedMode;
        connection.TrySend(new ConsoleMessage
        {
            VideoControl = screen.RequestedMode == VideoMode.Full
                ? FullControlFor(screen)
                : VideoSettings.ThumbnailControl(),
        });
    }

    /// <summary>
    /// The full-view control for this PC: *Text* is JPEG at the chosen quality, *Video* asks for
    /// H.264 (D-91) — only when this console can decode it, so a Linux console never asks.
    /// </summary>
    private static VideoControl FullControlFor(AgentScreen screen, bool requestKeyframe = true) =>
        VideoSettings.FullControl(requestKeyframe, screen.RequestedQuality, video: screen.RequestedVideo && !screen.VideoBroken && H264Codecs.CanDecode);

    /// <summary>
    /// This console could not decode a PC's H.264 three times in a row: the full view goes on in
    /// JPEG and the teacher is told (review of D-91). The window's status line already says the
    /// PC is sending Text.
    /// </summary>
    private void OnFullVideoBroken(AgentScreen screen)
    {
        var number = Registry.Document.Machines.FirstOrDefault(m => string.Equals(m.AgentId, screen.AgentId, StringComparison.OrdinalIgnoreCase))?.Number ?? 0;
        Events.Warning("video.console_decode_failed",
            Localization.Strings.Format("Event.VideoDecodeFailed", string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, number), screen.FullDecoder.Problem ?? string.Empty),
            screen.AgentId, number);
        if (screen.RequestedMode == VideoMode.Full && FindLinked(screen.AgentId) is { IsOutdated: false } connection)
        {
            connection.TrySend(new ConsoleMessage { VideoControl = FullControlFor(screen) });
        }
    }

    /// <summary>
    /// The teacher's mouse and keyboard for the PC in the single-PC window (PROTOCOL
    /// "Input", D-36): queued on the link as it is. <c>false</c> when the PC is not linked
    /// or too old to know the message — the window greys its control toggle out first, so
    /// this is the race, not the rule.
    /// </summary>
    public bool SendInput(string agentId, Input input)
    {
        var connection = FindLinked(agentId);
        if (connection is null || connection.IsOutdated)
        {
            return false;
        }

        return connection.TrySend(new ConsoleMessage { Input = input });
    }

    // ------------------------------------------------------------------ lock (M6 portion 1)

    /// <summary>
    /// Locks the screens of the given PCs (PROTOCOL <i>Overlay</i>, D-70): one <c>Overlay</c>
    /// per linked PC with one fresh <c>session_id</c> for the whole action,
    /// <c>until_seconds</c> from <paramref name="duration"/> (<c>null</c> = until lifted) and
    /// a proposed hard limit of <see cref="Defaults.LockHardLimit"/> — both <b>durations</b>,
    /// never this console's clock: the lab has no time source, so each PC stamps them onto
    /// its own clock at arrival and clamps the limit there (D-70 item 6). A PC that is not
    /// linked, or too old to know the message, is skipped — a lock is a link message like
    /// <c>Input</c>, not a job, and is never queued for later. Returns how many PCs were
    /// asked; what each one <i>shows</i> arrives as <c>OverlayState</c> and is what the tile
    /// follows.
    /// </summary>
    public int Lock(IEnumerable<string> agentIds, string message, TimeSpan? duration)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ArgumentNullException.ThrowIfNull(message);
        var overlay = new Overlay
        {
            Kind = Overlay.Types.Kind.Lock,
            Message = message,
            SessionId = Guid.NewGuid().ToString("n"),
            UntilSeconds = duration is { } d && d > TimeSpan.Zero ? (long)Math.Ceiling(d.TotalSeconds) : 0,
            HardLimitSeconds = (long)Math.Ceiling(LockLimit.TotalSeconds),
            Language = Language.Code,
        };

        var (sent, skipped) = SendOverlay(agentIds, overlay, setting: true);
        Events.Info("overlay.lock_sent", Localization.Strings.Format("Event.LockSent", sent, skipped, message));
        return sent;
    }

    /// <summary>Lifts the lock on the given PCs; any console of the same lab may end a lock it can see (owner decision 4).</summary>
    public int Unlock(IEnumerable<string> agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        var ids = agentIds.ToArray();
        RememberUnreached(ids, _heldLock, _pendingUnlock);
        var (sent, skipped) = SendOverlay(ids, new Overlay { Kind = Overlay.Types.Kind.Unlock }, setting: false);
        Events.Info("overlay.unlock_sent", Localization.Strings.Format("Event.UnlockSent", sent, skipped));
        return sent;
    }

    /// <summary>
    /// <paramref name="setting"/>: the message starts something, and goes only to a PC that
    /// enforces classroom control; an end goes to any linked PC (D-79 item 4).
    /// </summary>
    private (int Sent, int Skipped) SendOverlay(IEnumerable<string> agentIds, Overlay overlay, bool setting)
    {
        var sent = 0;
        var skipped = 0;
        foreach (var agentId in agentIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var connection = FindLinked(agentId);
            if (connection is null || !MaySend(connection, setting) || !connection.TrySend(new ConsoleMessage { Overlay = overlay.Clone() }))
            {
                skipped++;
                continue;
            }

            sent++;
        }

        return (sent, skipped);
    }

    // ------------------------------------------------------------------ ends for PCs that were off (lab, 2026-10-26)

    // The lock and the standalone internet session each PC was last seen holding, and the ends the
    // teacher asked for while it was not linked. In memory only: they live as long as this session.
    private readonly Dictionary<string, string> _heldLock = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _heldInternet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingUnlock = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingOpen = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsExamInternet(InternetState state) =>
        state.SessionId.EndsWith(LabControl.Shared.Policy.PolicyJournal.ExamInternetSuffix, StringComparison.Ordinal);

    /// <summary>The standalone internet session the PC reports holding, or <c>null</c> (open, or only an exam's switch).</summary>
    private static string? StandaloneSessionOf(AgentConnection connection) =>
        HoldsStandaloneInternet(connection) ? connection.InternetState!.SessionId : null;

    /// <summary>
    /// <i>Unlock</i> / <i>Open</i> reached only the linked PCs (owner decision 2026-10-26): a PC
    /// that is not linked and was last seen holding a session is remembered with that session, and
    /// when it comes back still holding <b>the same one</b> the end is sent then. A session set
    /// afterwards — by this console or another — is never ended this way.
    /// </summary>
    private void RememberUnreached(IEnumerable<string> agentIds, Dictionary<string, string> held, Dictionary<string, string> pending)
    {
        lock (held)
        {
            foreach (var id in agentIds)
            {
                if (FindLinked(id) is null && held.TryGetValue(id, out var session))
                {
                    pending[id] = session;
                }
                else
                {
                    pending.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// Keeps <paramref name="held"/> current from what the PC just reported, and sends a remembered
    /// end when the PC came back with the session it was meant for. <paramref name="conclusive"/>
    /// is false for a state that says nothing about the standalone session (a refusal, or an exam's
    /// internet switch in front of it); the pending end then waits for the next state.
    /// </summary>
    private void FollowHeld(Dictionary<string, string> held, Dictionary<string, string> pending, AgentConnection connection, string? session,
        bool conclusive, Func<bool> sendEnd, string code)
    {
        if (!conclusive)
        {
            return;
        }

        bool send;
        lock (held)
        {
            if (session is null)
            {
                held.Remove(connection.AgentId);
            }
            else
            {
                held[connection.AgentId] = session;
            }

            // Only the very session it was meant for is ended; any other state leaves the request
            // waiting, since the first word after a restart may come before the journal re-applied.
            send = session is not null && pending.TryGetValue(connection.AgentId, out var meant) && string.Equals(meant, session, StringComparison.Ordinal)
                && pending.Remove(connection.AgentId);
        }

        if (send && sendEnd())
        {
            Events.Info(code, Localization.Strings.Format("Event.EndOnReturn",
                string.Format(System.Globalization.CultureInfo.InvariantCulture, Defaults.MachineNameFormat, connection.Number)), connection.AgentId, connection.Number);
        }
    }

    /// <summary>
    /// Whether a classroom-control message may go to <paramref name="connection"/>: one that
    /// <i>sets</i> something needs an agent that enforces it, one that ends something goes to any
    /// PC that is not outdated — an M6 build from before <see cref="Defaults.ClassroomControlProtocolVersion"/>
    /// may hold a policy, and an older agent ignores the message (D-79 item 4).
    /// </summary>
    private static bool MaySend(AgentConnection connection, bool setting) =>
        setting ? connection.SupportsClassroomControl : !connection.IsOutdated;

    // ------------------------------------------------------------------ moving to a new key (D-82)

    private readonly HashSet<string> _moveSent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set while this lab is being moved to a new key (D-82 item 4): every PC that links is sent
    /// its move once per session. <c>null</c> for an ordinary lab.
    /// </summary>
    public LabMoveTarget? MoveTarget { get; set; }

    /// <summary>PCs of this lab sent a move in this session, linked or not since.</summary>
    public IReadOnlyCollection<string> MovesSent
    {
        get
        {
            lock (_moveSent)
            {
                return _moveSent.ToArray();
            }
        }
    }

    /// <summary>
    /// Moves the given linked PCs to <paramref name="target"/> (D-82 item 3): for each, one
    /// single-use enrolment code minted in the target lab, a payload with that code and the
    /// target's CA certificate, and a SYSTEM <c>run_script</c> whose text carries both and
    /// schedules the installed <c>Setup.exe --rekey</c>. A PC that is not linked is skipped — it
    /// is moved when it links, while <see cref="MoveTarget"/> is set — and one already sent a
    /// move in this session is not sent another. Needs no key of this lab: only the target's
    /// codes, which need no key either. Returns the jobs created.
    /// </summary>
    public IReadOnlyList<JobRecord> MoveToNewKey(IEnumerable<string> agentIds, LabMoveTarget target)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ArgumentNullException.ThrowIfNull(target);
        if (Vault is null)
        {
            throw new InvalidOperationException(Strings.Get("Access.AdministratorNeeded"));
        }

        var jobs = new List<JobRecord>();
        foreach (var agentId in agentIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (FindLinked(agentId) is not { } connection || connection.IsOutdated)
            {
                continue;
            }

            lock (_moveSent)
            {
                if (!_moveSent.Add(agentId))
                {
                    continue;
                }
            }

            var now = _clock();
            var codes = new EnrollmentAuthority(target.Store.LoadEnrollment(target.LabId));
            var code = codes.Generate(1, $"move from {LabName} {now:yyyy-MM-dd HH:mm}", now, Instance.InstanceId, Instance.InstanceName)[0];
            codes.Persist(target.Store.SaveEnrollment);

            var setup = new SetupPayloadDocument
            {
                LabId = target.LabId,
                LabName = target.LabName,

                // A console bound to one address is found there; one serving every address is
                // found by its beacon, as a stick written by WritePayload is (the rekey's rule).
                ConsoleHost = Options.BindAddress is { } bound && !bound.Equals(System.Net.IPAddress.Any) && !bound.Equals(System.Net.IPAddress.IPv6Any)
                    ? bound.ToString()
                    : null,
                ConsolePort = Port,
                NextNumber = connection.Number,
                EnrollmentCodes = [code],
                WrittenBy = Instance.InstanceName,
                WrittenAtUnix = now.ToUnixTimeSeconds(),
            };
            var text = LabMoveScript.Build(Guid.NewGuid().ToString("N"),
                System.Text.Encoding.UTF8.GetBytes(JsonStore.Serialize(setup, SetupPayloadDocument.Migrations)), target.Authority);
            jobs.AddRange(RunScript([agentId], new ScriptRecord
            {
                Name = Localization.Strings.Get("Move.ScriptName"),
                Shell = RunScriptRequest.PowerShellValue,
                RunAs = RunScriptRequest.SystemValue,
                TimeoutSeconds = (int)Defaults.LabMoveScriptTimeout.TotalSeconds,
                Text = text,
            }));
            Events.Info("lab.move_sent", Localization.Strings.Format("Event.MoveSent", string.Format(System.Globalization.CultureInfo.InvariantCulture, Defaults.MachineNameFormat, connection.Number), target.LabName), agentId, connection.Number);
        }

        return jobs;
    }

    // ------------------------------------------------------------------ internet (M6 portion 3)

    /// <summary>
    /// Sets a standalone internet policy on the given PCs (PROTOCOL <i>Internet control</i>,
    /// D-73): one <c>InternetPolicy</c> per linked PC with one fresh <c>session_id</c>, the host
    /// patterns for a whitelist, <c>until_seconds</c> from <paramref name="duration"/>
    /// (<c>null</c> = until lifted) and a proposed hard limit of
    /// <see cref="Defaults.InternetPolicyHardLimit"/> — durations each PC stamps onto its own
    /// clock. Like a lock it is a link message, never queued for a PC that is not linked.
    /// Returns how many PCs were asked; the tiles follow what each reports in <c>InternetState</c>.
    /// </summary>
    public int SetInternet(IEnumerable<string> agentIds, InternetPolicy.Types.Mode mode, IReadOnlyList<string> hosts, string presetName, TimeSpan? duration)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ArgumentNullException.ThrowIfNull(hosts);
        if (mode is not (InternetPolicy.Types.Mode.Whitelist or InternetPolicy.Types.Mode.Blocked))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "only a whitelist or a block is set; Open is LiftInternet");
        }

        var policy = new InternetPolicy
        {
            Mode = mode,
            SessionId = Guid.NewGuid().ToString("n"),
            PresetName = presetName ?? string.Empty,
            UntilSeconds = duration is { } d && d > TimeSpan.Zero ? (long)Math.Ceiling(d.TotalSeconds) : 0,
            HardLimitSeconds = (long)Math.Ceiling(InternetLimit.TotalSeconds),
        };
        if (mode == InternetPolicy.Types.Mode.Whitelist)
        {
            policy.AllowedHosts.AddRange(hosts);
        }

        var (sent, skipped) = SendInternet(agentIds, _ => policy.Clone(), setting: true);
        Events.Info("internet.set_sent", Localization.Strings.Format(
            mode == InternetPolicy.Types.Mode.Blocked ? "Event.InternetBlockSent" : "Event.InternetWhitelistSent", sent, skipped, policy.PresetName));
        return sent;
    }

    /// <summary>Lifts the standalone internet policy on the given PCs; any console of the lab may lift one it can see (owner decision 4).</summary>
    public int LiftInternet(IEnumerable<string> agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        var ids = agentIds.ToArray();
        RememberUnreached(ids, _heldInternet, _pendingOpen);
        var (sent, skipped) = SendInternet(ids, _ => new InternetPolicy { Mode = InternetPolicy.Types.Mode.Open }, setting: false);
        Events.Info("internet.lift_sent", Localization.Strings.Format("Event.InternetLiftSent", sent, skipped));
        return sent;
    }

    /// <summary>
    /// The teacher's <i>Allow</i> on a refused name (owner decision 2026-10-17, D-74): every
    /// linked PC whose whitelist refused it — and every other PC under a whitelist that does not
    /// cover it yet — gets its own host list plus <paramref name="name"/>, as an
    /// <c>amend_hosts</c> on the session it holds, so the timing of each PC's policy is kept.
    /// Returns how many PCs were asked.
    /// </summary>
    public int AllowRefusedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!LabControl.Shared.Internet.HostPattern.TryNormalize(name, out var pattern, out _))
        {
            return 0;
        }

        var targets = Linked
            .Where(c => c.InternetState is { Mode: InternetPolicy.Types.Mode.Whitelist } state
                && !LabControl.Shared.Internet.HostPattern.AnyMatches(state.AllowedHosts, pattern))
            .ToDictionary(c => c.AgentId, c => c.InternetState!, StringComparer.OrdinalIgnoreCase);
        var (sent, skipped) = SendInternet(targets.Keys, agentId =>
        {
            var state = targets[agentId];
            var amend = new InternetPolicy { Mode = InternetPolicy.Types.Mode.Whitelist, SessionId = state.SessionId, AmendHosts = true, PresetName = state.PresetName };
            amend.AllowedHosts.AddRange(state.AllowedHosts);
            amend.AllowedHosts.Add(pattern);
            return amend;
        }, setting: false);
        Events.Info("internet.allow_sent", Localization.Strings.Format("Event.InternetAllowSent", pattern, sent, skipped));
        return sent;
    }

    /// <summary>
    /// The names the linked PCs' whitelists refused lately, merged (D-74): each with how many PCs
    /// refused it, most widespread first. What the <i>Internet</i> dialog lists with an
    /// <i>Allow</i> button.
    /// </summary>
    public IReadOnlyList<RefusedName> RefusedNames()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var connection in Linked)
        {
            if (connection.InternetState is not { Mode: InternetPolicy.Types.Mode.Whitelist } state)
            {
                continue;
            }

            foreach (var name in state.RefusedRecently.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (LabControl.Shared.Internet.HostPattern.AnyMatches(state.AllowedHosts, name))
                {
                    // Allowed since: the PC's list still names it until its window passes.
                    continue;
                }

                if (counts.TryAdd(name, 1))
                {
                    order.Add(name);
                }
                else
                {
                    counts[name]++;
                }
            }
        }

        return order.Select((n, i) => (Name: n, Index: i))
            .OrderByDescending(x => counts[x.Name]).ThenBy(x => x.Index)
            .Select(x => new RefusedName(x.Name, counts[x.Name]))
            .ToArray();
    }

    private (int Sent, int Skipped) SendInternet(IEnumerable<string> agentIds, Func<string, InternetPolicy> policyFor, bool setting)
    {
        var sent = 0;
        var skipped = 0;
        foreach (var agentId in agentIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var connection = FindLinked(agentId);
            if (connection is null || !MaySend(connection, setting) || !connection.TrySend(new ConsoleMessage { InternetPolicy = policyFor(agentId) }))
            {
                skipped++;
                continue;
            }

            sent++;
        }

        return (sent, skipped);
    }

    private void ApplyInternetState(AgentConnection connection, InternetState state, string who)
    {
        var modeBefore = connection.InternetMode;
        var problemBefore = connection.InternetProblem;
        var changed = connection.ApplyInternetState(state);
        FollowHeld(_heldInternet, _pendingOpen, connection, StandaloneSessionOf(connection), state.Problem.Length == 0 && !IsExamInternet(state),
            () => connection.TrySend(new ConsoleMessage { InternetPolicy = new InternetPolicy { Mode = InternetPolicy.Types.Mode.Open } }), "internet.lift_on_return");

        if (connection.InternetProblem is { } problem && problem != problemBefore)
        {
            Events.Warning("internet.refused", Localization.Strings.Format("Event.InternetRefused", who, problem), connection.AgentId, connection.Number);
        }

        if (modeBefore != connection.InternetMode)
        {
            if (connection.InternetMode == InternetPolicy.Types.Mode.Open)
            {
                Events.Info("internet.opened", Localization.Strings.Format("Event.InternetOpened", who), connection.AgentId, connection.Number);
            }
            else
            {
                var by = string.Equals(state.SetByInstance, Instance.InstanceId, StringComparison.OrdinalIgnoreCase)
                    ? Localization.Strings.Get("Lock.ByThisConsole")
                    : NameOfInstance(state.SetByInstance);
                Events.Info("internet.restricted", Localization.Strings.Format(
                    connection.InternetMode == InternetPolicy.Types.Mode.Blocked ? "Event.InternetBlocked" : "Event.InternetWhitelisted", who, by), connection.AgentId, connection.Number);
            }
        }

        if (changed)
        {
            MachinesChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ exam (M6 portion 4)

    /// <summary>
    /// The exam this console started for the whole lab, kept in memory so a PC that links before
    /// its end joins it with what is left (owner decision 2026-10-23). Forgotten when it ends, is
    /// ended, or this session closes; a console restart forgets it too (D-76).
    /// </summary>
    private sealed record LabExam(ExamMode Template, DateTimeOffset? Deadline, DateTimeOffset HardDeadline);

    private LabExam? _labExam;
    private readonly Lock _examGate = new();

    /// <summary>Joining late is not worth it with less than this left: the PC would end the exam again at once.</summary>
    private static readonly TimeSpan LateJoinMargin = TimeSpan.FromSeconds(5);

    /// <summary>The session id of the lab-wide exam this console remembers, or <c>null</c>.</summary>
    public string? LabExamSessionId
    {
        get
        {
            lock (_examGate)
            {
                return _labExam is { } exam && !ExamOver(exam, _clock()) ? exam.Template.SessionId : null;
            }
        }
    }

    /// <summary>
    /// Starts an exam on the given PCs (PROTOCOL <i>Exam mode</i>, D-75): one <c>ExamMode</c> per
    /// linked PC with one fresh <c>session_id</c>, the timer as <c>until_seconds</c>
    /// (<c>null</c> = no timer) and the hard limit left to each PC's own ceiling — durations
    /// each PC stamps onto its own clock. <paramref name="wholeLab"/> remembers the exam, so a
    /// PC that links before its end joins it. Returns the session id and how many PCs were asked.
    /// </summary>
    public (string SessionId, int Sent) StartExam(IEnumerable<string> agentIds, ExamRequest request, bool wholeLab)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ArgumentNullException.ThrowIfNull(request);
        var now = _clock();
        var template = new ExamMode
        {
            SessionId = Guid.NewGuid().ToString("n"),
            Active = true,
            Message = request.Message ?? string.Empty,
            PresetName = request.PresetName ?? string.Empty,
            RestrictPrograms = request.RestrictPrograms,
            UntilSeconds = request.Timer is { } timer && timer > TimeSpan.Zero ? (long)Math.Ceiling(timer.TotalSeconds) : 0,
            HardLimitSeconds = (long)Math.Ceiling(ExamLimit.TotalSeconds),
            Language = Language.Code,
        };
        template.AllowedPrograms.AddRange(LabControl.Shared.Exam.ProgramNames.NormalizeAll(request.AllowedPrograms));
        string? collectFolder = null;
        if (request.CollectFolder is { } folder)
        {
            if (!CollectRequest.TryNormalize(folder, out var normalized))
            {
                throw new ArgumentException(Localization.Strings.Get("Collect.FolderInvalid"));
            }

            collectFolder = normalized;
            template.Collect = new CollectSpec { Folder = normalized, OnEnd = true };
        }
        if (request.InternetMode is InternetPolicy.Types.Mode.Whitelist or InternetPolicy.Types.Mode.Blocked)
        {
            var internet = new InternetPolicy { Mode = request.InternetMode, PresetName = request.InternetPresetName ?? string.Empty };
            if (request.InternetMode == InternetPolicy.Types.Mode.Whitelist)
            {
                internet.AllowedHosts.AddRange(request.InternetHosts);
            }

            template.Internet = internet;
        }

        lock (_examGate)
        {
            _labExam = wholeLab
                ? new LabExam(template.Clone(), template.UntilSeconds > 0 ? now.AddSeconds(template.UntilSeconds) : null, now + ExamLimit)
                : _labExam;
        }

        var targets = agentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (collectFolder is not null)
        {
            // Remembered before the first PC can answer, so its very first state is seen.
            Collections.RememberExam(template.SessionId, template.PresetName.Length > 0 ? template.PresetName : Localization.Strings.Get("Collect.ExamName"),
                request.CollectDestination ?? WorkCollections.DefaultDestination, collectFolder, targets,
                template.UntilSeconds > 0 ? now.AddSeconds(template.UntilSeconds) : null);
        }

        var (sent, skipped) = SendExam(targets, connection =>
        {
            connection.ExamSessionsSeen.Add(template.SessionId);
            return template.Clone();
        }, setting: true);
        Events.Info("exam.start_sent", Localization.Strings.Format("Event.ExamStartSent", sent, skipped, template.PresetName));
        return (template.SessionId, sent);
    }

    /// <summary>
    /// Gives every PC that holds an exam among <paramref name="agentIds"/> <paramref name="more"/>
    /// time: the same session re-stated with what the PC has left plus <paramref name="more"/>,
    /// counted from the PC's own <c>remaining_seconds</c>, never its clock. A lab-wide exam's
    /// remembered deadline moves with it. Returns how many PCs were asked.
    /// </summary>
    public int ExtendExam(IEnumerable<string> agentIds, TimeSpan more)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        var extra = (long)Math.Ceiling(Math.Max(0, more.TotalSeconds));
        var (sent, skipped) = SendExam(agentIds, connection =>
        {
            if (connection.ExamState is not { Active: true, UntilUnix: > 0 } state)
            {
                // No exam, or one without a timer: nothing to extend.
                return null;
            }

            // remaining_seconds is as of sending; the PC re-sends only on change, so age it on
            // this console's clock (VM drill 2026-10-23: an unaged value gave +43 s for +30 s).
            var mode = FromState(state);
            mode.Language = Language.Code;
            var aged = (long)Math.Floor((_clock() - connection.ExamStateAt).TotalSeconds);
            mode.UntilSeconds = Math.Max(1, state.RemainingSeconds - Math.Max(0, aged)) + extra;
            return mode;
        }, setting: false);

        lock (_examGate)
        {
            if (_labExam is { Deadline: { } deadline } exam)
            {
                var template = exam.Template.Clone();
                var left = Math.Max(0, (long)Math.Ceiling((deadline - _clock()).TotalSeconds)) + extra;
                template.UntilSeconds = left;
                _labExam = exam with { Template = template, Deadline = deadline.AddSeconds(extra) };
                Collections.MoveExamDeadline(template.SessionId, deadline.AddSeconds(extra));
            }
        }

        Events.Info("exam.extend_sent", Localization.Strings.Format("Event.ExamExtendSent", (int)more.TotalMinutes, sent, skipped));
        return sent;
    }

    /// <summary>
    /// Ends the exam on the given PCs — any console of the lab may end one it can see (owner
    /// decision 4): <c>active</c> false with the session the PC reported, so a PC that meanwhile
    /// holds another exam keeps it. Forgets the lab-wide exam. Returns how many PCs were asked.
    /// </summary>
    public int EndExam(IEnumerable<string> agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        var ids = agentIds.ToArray();
        lock (_examGate)
        {
            _labExam = null;
        }

        Collections.ExamEndedByTeacher(ids);
        var (sent, skipped) = SendExam(ids, connection =>
            connection.ExamState is { Active: true } state ? new ExamMode { Active = false, SessionId = state.SessionId } : null, setting: false);
        Events.Info("exam.end_sent", Localization.Strings.Format("Event.ExamEndSent", sent, skipped));
        return sent;
    }

    /// <summary>The ExamMode that re-states what a PC reported holding, settings and all; the timing is the caller's.</summary>
    private static ExamMode FromState(ExamState state)
    {
        var mode = new ExamMode
        {
            SessionId = state.SessionId,
            Active = true,
            Message = state.Message,
            PresetName = state.PresetName,
            RestrictPrograms = state.RestrictPrograms,

            // A re-stated exam gets the PC's ceiling from now: the teacher is here to end it.
            HardLimitSeconds = 0,
        };
        mode.AllowedPrograms.AddRange(state.AllowedPrograms);
        return mode;
    }

    private (int Sent, int Skipped) SendExam(IEnumerable<string> agentIds, Func<AgentConnection, ExamMode?> modeFor, bool setting)
    {
        var sent = 0;
        var skipped = 0;
        foreach (var agentId in agentIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var connection = FindLinked(agentId);
            if (connection is null || !MaySend(connection, setting) || modeFor(connection) is not { } mode || !connection.TrySend(new ConsoleMessage { ExamMode = mode }))
            {
                skipped++;
                continue;
            }

            sent++;
        }

        return (sent, skipped);
    }

    private static bool ExamOver(LabExam exam, DateTimeOffset now) =>
        now + LateJoinMargin >= exam.HardDeadline || exam.Deadline is { } deadline && now + LateJoinMargin >= deadline;

    /// <summary>
    /// A PC of the lab that holds no part of the lab-wide exam and has never been sent it on this
    /// link joins it with what is left (owner decision 2026-10-23). Judged on the PC's first
    /// word after linking, so a PC that rebooted inside the exam — and re-applied it from its own
    /// journal — is never sent it again.
    /// </summary>
    private void JoinLabExamIfLate(AgentConnection connection)
    {
        ExamMode? mode = null;
        lock (_examGate)
        {
            if (_labExam is not { } exam)
            {
                return;
            }

            var now = _clock();
            if (ExamOver(exam, now))
            {
                _labExam = null;
                return;
            }

            var sessionId = exam.Template.SessionId;
            if (connection.ExamSessionsSeen.Contains(sessionId) || connection.ExamState is { Active: true } || !connection.SupportsClassroomControl)
            {
                // Already in it, or in another exam that is not this console's to replace.
                connection.ExamSessionsSeen.Add(sessionId);
                return;
            }

            connection.ExamSessionsSeen.Add(sessionId);
            mode = exam.Template.Clone();
            mode.UntilSeconds = exam.Deadline is { } deadline ? Math.Max(1, (long)Math.Ceiling((deadline - now).TotalSeconds)) : 0;
            mode.HardLimitSeconds = Math.Max(1, (long)Math.Ceiling((exam.HardDeadline - now).TotalSeconds));
        }

        if (connection.TrySend(new ConsoleMessage { ExamMode = mode }))
        {
            Collections.AddToExam(mode!.SessionId, connection.AgentId);
            Events.Info("exam.late_join", Localization.Strings.Format("Event.ExamLateJoin", string.Format(System.Globalization.CultureInfo.InvariantCulture, Defaults.MachineNameFormat, connection.Number)), connection.AgentId, connection.Number);
        }
    }

    private void ApplyExamState(AgentConnection connection, ExamState state, string who)
    {
        var activeBefore = connection.InExam;
        var problemBefore = connection.ExamProblem;
        var changed = connection.ApplyExamState(state, _clock());

        if (connection.ExamProblem is { } problem && problem != problemBefore)
        {
            Events.Warning("exam.refused", Localization.Strings.Format("Event.ExamRefused", who, problem), connection.AgentId, connection.Number);
        }

        if (activeBefore != connection.InExam)
        {
            if (connection.InExam)
            {
                var by = string.Equals(state.SetByInstance, Instance.InstanceId, StringComparison.OrdinalIgnoreCase)
                    ? Localization.Strings.Get("Lock.ByThisConsole")
                    : NameOfInstance(state.SetByInstance);
                Events.Info("exam.started", Localization.Strings.Format("Event.ExamStarted", who, by), connection.AgentId, connection.Number);
            }
            else
            {
                Events.Info("exam.ended", Localization.Strings.Format("Event.ExamEnded", who), connection.AgentId, connection.Number);
            }
        }

        if (state.Problem.Length == 0)
        {
            Collections.ExamStateOf(connection.AgentId, state.Active, state.SessionId);
            JoinLabExamIfLate(connection);
        }

        if (changed)
        {
            MachinesChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ broadcast (M6 portion 2)

    /// <summary>
    /// Opens the console's own screen for a broadcast; <see cref="ConsoleScreen.Open"/> unless
    /// a test draws one. Called on the producer's thread whenever the capture (re)opens.
    /// </summary>
    public Func<IScreenSource> BroadcastSource { get; set; } = () => ConsoleScreen.Open(Defaults.BroadcastWidth);

    /// <summary><see cref="Defaults.BroadcastHardLimit"/> unless a test wants it sooner.</summary>
    public TimeSpan BroadcastLimit { get; set; } = Defaults.BroadcastHardLimit;

    /// <summary>
    /// The language this console speaks to its PCs (M7, D-86 item 5, D-87): sent in every
    /// <c>Welcome</c> for what the PC reports, and on every lock and exam for the stock words the
    /// student sees. The running console language unless a test says otherwise.
    /// </summary>
    public ProductLanguage Language { get; set; } = ConsoleLanguage.Running;

    /// <summary>
    /// The hard limit a lock proposes: <see cref="Defaults.LockHardLimit"/> unless a test or the
    /// fail-safe drill wants it sooner (D-79 item 5). The PC clamps whatever it is sent to its
    /// own ceiling, so this can only shorten a lock's life, never lengthen it.
    /// </summary>
    public TimeSpan LockLimit { get; set; } = Defaults.LockHardLimit;

    /// <summary>The hard limit a standalone internet policy proposes: <see cref="Defaults.InternetPolicyHardLimit"/> unless the drill wants it sooner.</summary>
    public TimeSpan InternetLimit { get; set; } = Defaults.InternetPolicyHardLimit;

    /// <summary>The hard limit an exam proposes — its internet switch's too: <see cref="Defaults.ExamHardLimit"/> unless the drill wants it sooner.</summary>
    public TimeSpan ExamLimit { get; set; } = Defaults.ExamHardLimit;

    /// <summary>The broadcast in progress, or <c>null</c>.</summary>
    public BroadcastHub? Broadcast
    {
        get
        {
            lock (_gate)
            {
                return _broadcast;
            }
        }
    }

    /// <summary>The PCs the teacher chose to show the screen to, linked or not.</summary>
    public IReadOnlyCollection<string> BroadcastTargets
    {
        get
        {
            lock (_gate)
            {
                return _broadcastTargets.ToArray();
            }
        }
    }

    /// <summary>A broadcast started, ended or changed what it shows; the lab view refreshes its status line.</summary>
    public event Action? BroadcastChanged;

    /// <summary>
    /// Shows this console's screen on the given PCs (PROTOCOL <i>Broadcast</i>, D-71): one
    /// <see cref="BroadcastHub"/> captures and encodes it once, and each linked PC is sent an
    /// <c>Overlay BROADCAST_START</c> naming the session; the PC dials <c>PullBroadcast</c>
    /// for the picture and takes it full-screen with input blocked, for as long as the stream
    /// lasts and never past its own <see cref="Defaults.BroadcastHardLimit"/>. Called while a
    /// broadcast is running, it adds PCs to the same one. A PC that is not linked now joins
    /// when it links; one too old to know the message is skipped. Returns how many PCs were
    /// asked; the tiles follow what each PC reports in <c>OverlayState</c>.
    /// </summary>
    /// <param name="video">The teacher's *Video* (D-91): H.264 for the PCs that can show it; ignored by a running broadcast, which <see cref="BroadcastHub.SetVideo"/> switches.</param>
    public int StartBroadcast(IEnumerable<string> agentIds, bool video = false)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        var ids = agentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        BroadcastHub? started = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed == 1, this);
            if (_broadcast is null)
            {
                started = new BroadcastHub(Guid.NewGuid().ToString("n"), BroadcastSource, _loggers.CreateLogger<BroadcastHub>(), _clock,
                    (severity, code, message) => Events.Add(severity switch
                    {
                        Event.Types.Severity.Error => EventSeverity.Error,
                        Event.Types.Severity.Warning => EventSeverity.Warning,
                        _ => EventSeverity.Info,
                    }, code, message), BroadcastLimit, video);
                started.Expired += OnBroadcastExpired;
                started.Changed += () => BroadcastChanged?.Invoke();
                _broadcast = started;
            }

            foreach (var id in ids)
            {
                _broadcastTargets.Add(id);
            }
        }

        started?.Start();

        var sent = 0;
        var skipped = 0;
        foreach (var id in ids)
        {
            var connection = FindLinked(id);
            if (connection is null || !connection.SupportsClassroomControl || BroadcastStartFor(id) is not { } start
                || !connection.TrySend(new ConsoleMessage { Overlay = start }))
            {
                skipped++;
                continue;
            }

            sent++;
        }

        Events.Info("broadcast.started", Localization.Strings.Format(started is null ? "Event.BroadcastAdded" : "Event.BroadcastStarted", sent, skipped));
        BroadcastChanged?.Invoke();
        return sent;
    }

    /// <summary>Why a broadcast ended, for the event the teacher sees.</summary>
    public enum BroadcastEnd
    {
        Teacher = 0,
        Limit = 1,
        Departure = 2,
    }

    /// <summary>
    /// Ends the broadcast: every linked PC that was shown it is sent <c>BROADCAST_STOP</c>, and
    /// the hub ends every stream, which ends the overlay on a PC that missed the message. A
    /// no-op without a broadcast.
    /// </summary>
    public async Task StopBroadcastAsync(BroadcastEnd why = BroadcastEnd.Teacher)
    {
        BroadcastHub? hub;
        string[] targets;
        lock (_gate)
        {
            hub = _broadcast;
            _broadcast = null;
            targets = _broadcastTargets.ToArray();
            _broadcastTargets.Clear();
        }

        if (hub is null)
        {
            return;
        }

        hub.Expired -= OnBroadcastExpired;
        var stop = new Overlay { Kind = Overlay.Types.Kind.BroadcastStop, SessionId = hub.SessionId };
        foreach (var id in targets)
        {
            if (FindLinked(id) is { IsOutdated: false } connection)
            {
                connection.TrySend(new ConsoleMessage { Overlay = stop.Clone() });
            }
        }

        await hub.DisposeAsync().ConfigureAwait(false);
        var key = why switch
        {
            BroadcastEnd.Limit => "Event.BroadcastExpired",
            BroadcastEnd.Departure => "Event.BroadcastDeparture",
            _ => "Event.BroadcastStopped",
        };
        Events.Info(why == BroadcastEnd.Limit ? "broadcast.expired" : "broadcast.stopped",
            Localization.Strings.Format(key, (int)Math.Round(hub.Limit.TotalMinutes)));
        BroadcastChanged?.Invoke();
    }

    /// <summary>The <c>Overlay</c> that tells one PC to join the running broadcast, or <c>null</c> when it is not shown it.</summary>
    private Overlay? BroadcastStartFor(string agentId)
    {
        lock (_gate)
        {
            if (_broadcast is not { } hub || !_broadcastTargets.Contains(agentId))
            {
                return null;
            }

            // A duration, like a lock's (D-70 item 6): what is left of this console's limit,
            // which the PC clamps again to its own ceiling on its own clock.
            var left = (long)Math.Ceiling((hub.Deadline - _clock()).TotalSeconds);
            return left <= 0
                ? null
                : new Overlay
                {
                    Kind = Overlay.Types.Kind.BroadcastStart,
                    SessionId = hub.SessionId,
                    Message = Instance.InstanceName,
                    HardLimitSeconds = left,
                };
        }
    }

    private void OnBroadcastExpired(BroadcastHub hub)
    {
        if (ReferenceEquals(Broadcast, hub))
        {
            _ = StopBroadcastAsync(BroadcastEnd.Limit);
        }
    }

    /// <summary>
    /// A PC's <c>PullBroadcast</c> (PROTOCOL <i>Broadcast</i>): the peer certificate says which
    /// PC it is; it must be one the teacher is showing the screen to, asking for the session
    /// that is running. Served until the broadcast ends or the PC hangs up.
    /// </summary>
    public async Task ServeBroadcastAsync(X509Certificate2? peer, BroadcastRequest request, IServerStreamWriter<VideoFrame> outgoing, CancellationToken token)
    {
        var name = RequireAgent(peer);
        BroadcastHub? hub;
        bool target;
        lock (_gate)
        {
            hub = _broadcast;
            target = _broadcastTargets.Contains(name.Id);
        }

        if (hub is null || !string.Equals(hub.SessionId, request.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "this console is not broadcasting that session"));
        }

        if (!target)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "this PC is not one the broadcast is shown to"));
        }

        await hub.ServeAsync(name.Id, request.Codecs, outgoing, token).ConfigureAwait(false);
    }

    /// <summary>
    /// The PC's word on what it shows (D-70): merged into the connection, logged when the
    /// lock appears, ends or is refused, and pushed to the tiles. Never the console's request.
    /// </summary>
    private void ApplyOverlayState(AgentConnection connection, OverlayState state, string who)
    {
        var lockedBefore = connection.OverlayLocked;
        var problemBefore = connection.OverlayProblem;
        var changed = connection.ApplyOverlayState(state);
        FollowHeld(_heldLock, _pendingUnlock, connection, connection.OverlayLocked ? connection.OverlayState!.SessionId : null, state.Problem.Length == 0,
            () => connection.TrySend(new ConsoleMessage { Overlay = new Overlay { Kind = Overlay.Types.Kind.Unlock } }), "overlay.unlock_on_return");

        if (connection.OverlayProblem is { } problem && problem != problemBefore)
        {
            Events.Warning("overlay.refused", Localization.Strings.Format("Event.OverlayRefused", who, problem), connection.AgentId, connection.Number);
        }

        if (!lockedBefore && connection.OverlayLocked)
        {
            var by = string.Equals(state.SetByInstance, Instance.InstanceId, StringComparison.OrdinalIgnoreCase)
                ? Localization.Strings.Get("Lock.ByThisConsole")
                : NameOfInstance(state.SetByInstance);
            Events.Info("overlay.locked", Localization.Strings.Format("Event.Locked", who, by), connection.AgentId, connection.Number);
        }
        else if (lockedBefore && !connection.OverlayLocked)
        {
            Events.Info("overlay.unlocked", Localization.Strings.Format("Event.Unlocked", who), connection.AgentId, connection.Number);
        }

        if (changed)
        {
            MachinesChanged?.Invoke();
        }
    }

    /// <summary>Asks a PC in full mode for a whole picture: the console has nothing to patch a delta onto.</summary>
    public void RequestKeyframe(string agentId)
    {
        var connection = FindLinked(agentId);
        if (connection is null || connection.RequestedVideo != VideoMode.Full)
        {
            return;
        }

        // The same quality as the standing control, or the PC would see a new control and restart its budget.
        connection.TrySend(new ConsoleMessage { VideoControl = FullControlFor(Screens.Get(agentId)) });
    }

    public Task<FileAck> ReceiveFileAsync(X509Certificate2? peer, IAsyncStreamReader<FileChunk> incoming, CancellationToken token) =>
        Uploads.ReceiveAsync(RequireAgent(peer).Id, incoming, token);

    public Task<FileAck> UploadStatusAsync(X509Certificate2? peer, FileRequest request, CancellationToken token) =>
        Uploads.StatusAsync(RequireAgent(peer).Id, request.Reference, token);

    /// <summary>A PC asks for the grant of an upload its job produced (D-77); only collected work asks today.</summary>
    public Task<UploadGrant> RequestUploadAsync(X509Certificate2? peer, UploadRequest request) =>
        Task.FromResult(Collections.RequestUpload(RequireAgent(peer).Id, request));

    public async Task ServeFileAsync(X509Certificate2? peer, FileRequest request, IServerStreamWriter<FileChunk> outgoing, CancellationToken token)
    {
        var name = RequireAgent(peer);
        var who = string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, name.Number);

        var offer = Files.Find(request.Reference);
        if (offer is null)
        {
            Events.Warning("file.unknown", $"{who} asked for a file this console is not offering ({request.Reference}).", name.Id, name.Number);
            throw new RpcException(new Status(StatusCode.NotFound, $"this console is not offering '{request.Reference}'"));
        }

        if (request.Offset < 0 || request.Offset > offer.Size)
        {
            throw new RpcException(new Status(StatusCode.OutOfRange, "the requested offset is outside the offered file"));
        }

        _log.LogInformation("{Pc} pulls {Name} ({Bytes} bytes, {Reference})", who, offer.Name, offer.Size, offer.Reference);

        await using var stream = offer.Open();
        if (stream.Length != offer.Size)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "the offered file changed; offer it again"));
        }

        stream.Position = request.Offset;
        var buffer = new byte[Defaults.FileChunkBytes];
        var offset = request.Offset;

        while (true)
        {
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: token);
            var last = offset + read == offer.Size;
            if (offset + read > offer.Size || (read == 0 && !last))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "the offered file changed during transfer"));
            }

            var chunk = new FileChunk
            {
                Reference = offer.Reference,
                Offset = offset,
                Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read),
                Last = last,
            };

            if (last)
            {
                chunk.Sha256 = offer.Sha256;
                chunk.TotalBytes = offset + read;
            }

            await outgoing.WriteAsync(chunk, token);
            offset += read;

            if (last)
            {
                break;
            }
        }
    }

    // ------------------------------------------------------------------ Wake-on-LAN

    /// <summary>
    /// Sends magic packets to every selected PC that is not linked (ARCHITECTURE §6, PROTOCOL
    /// <c>Job</c>: not a job — the result is the PC linking within
    /// <see cref="Defaults.WakeTimeout"/>, reported per PC as an event). A PC already online
    /// is skipped; one without a MAC cannot be woken and says so.
    /// </summary>
    public async Task<IReadOnlyList<PendingWake>> WakeAsync(IEnumerable<string> agentIds)
    {
        var started = new List<PendingWake>();

        foreach (var agentId in agentIds)
        {
            var machine = Registry.FindByAgentId(agentId);
            if (machine is null || IsLinked(agentId))
            {
                continue;
            }

            var who = string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, machine.Number);
            if (!WakeOnLan.TryParseMac(machine.Mac, out _))
            {
                Events.Warning("wake.no_mac", $"{who} cannot be woken: the console has no MAC address for it. It is learned at enrolment and from Hello.", agentId, machine.Number);
                continue;
            }

            var now = _clock();
            var pending = new PendingWake(agentId, machine.Number, now, now + Defaults.WakeTimeout);
            lock (_gate)
            {
                _waking[agentId] = pending;
            }

            started.Add(pending);
            MachinesChanged?.Invoke();

            var failures = new List<string>();
            IReadOnlyList<System.Net.IPEndPoint> reached;
            try
            {
                reached = await WakeOnLan.SendAsync(machine.Mac, machine.LastIp, failures.Add, _stopping.Token);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or FormatException)
            {
                reached = [];
                failures.Add(ex.Message);
            }

            if (reached.Count == 0)
            {
                lock (_gate)
                {
                    _waking.Remove(agentId);
                }

                Events.Error("wake.send_failed", $"{who}: could not send a magic packet at all ({string.Join("; ", failures)}).", agentId, machine.Number);
                MachinesChanged?.Invoke();
                continue;
            }

            Events.Info("wake.sent",
                $"{who}: magic packet sent to {machine.Mac} via {string.Join(", ", reached.Select(r => r.Address))}; waiting up to {Defaults.WakeTimeout.TotalSeconds:0} s for it to link." +
                (failures.Count > 0 ? $" Not sent via: {string.Join("; ", failures)}." : string.Empty),
                agentId, machine.Number);
        }

        return started;
    }

    /// <summary>The wake in progress for this PC, if any — the tile shows <i>waking…</i>.</summary>
    public PendingWake? Waking(string agentId)
    {
        lock (_gate)
        {
            return _waking.GetValueOrDefault(agentId);
        }
    }

    private void NoteWoke(string agentId, string who, DateTimeOffset now)
    {
        PendingWake? pending;
        lock (_gate)
        {
            _waking.Remove(agentId, out pending);
        }

        if (pending is not null)
        {
            Events.Info("wake.woke", $"{who} woke: linked {(now - pending.StartedAt).TotalSeconds:0} s after the magic packet.", agentId, pending.Number);
        }
    }

    private void TimeOutWakes(DateTimeOffset now)
    {
        List<PendingWake> expired;
        lock (_gate)
        {
            expired = _waking.Values.Where(w => now >= w.Deadline).ToList();
            foreach (var wake in expired)
            {
                _waking.Remove(wake.AgentId);
            }
        }

        foreach (var wake in expired)
        {
            var who = string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, wake.Number);
            Events.Warning("wake.failed",
                $"{who} did not wake within {Defaults.WakeTimeout.TotalSeconds:0} s. Check on the PC: Wake-on-LAN in the BIOS/UEFI, Fast Startup off, the NIC's \"Wake on Magic Packet\" property, and that it is plugged in and cabled (INSTALLER.md step 7).",
                wake.AgentId, wake.Number);
        }

        if (expired.Count > 0)
        {
            MachinesChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ machines and revocation

    private void OnMachineReplaced(MachineRecord old, MachineRecord replacement)
    {
        var who = string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, replacement.Number);
        Events.Warning("machine.replaced",
            $"{who} was reinstalled: agent {replacement.AgentId} replaces {old.AgentId} (old certificate serial {old.CertificateSerial}).",
            replacement.AgentId, replacement.Number);

        var stale = FindLinked(old.AgentId);
        stale?.Close("this PC was reinstalled under the same number");
    }

    /// <summary>Drops a machine the teacher does not recognise. Its certificate stays valid unless revoked.</summary>
    public bool ForgetMachine(string agentId)
    {
        FindLinked(agentId)?.Close("the teacher removed this PC from the list");
        Screens.Remove(agentId);
        var forgotten = Registry.Forget(agentId);
        if (forgotten)
        {
            MachinesChanged?.Invoke();
        }

        return forgotten;
    }

    /// <summary>
    /// Revokes a certificate — a stolen teacher machine or a PC that must never connect
    /// again (ARCHITECTURE §3.7.3). Needs the lab key; pushes the entry to every linked PC.
    /// </summary>
    public bool TryRevoke(string certificateSerial, string reason, out string message)
    {
        if (Vault is null)
        {
            message = Strings.Get("Access.AdministratorNeeded");
            return false;
        }

        if (!Vault.Use(lab => Registry.Revoke(lab, certificateSerial, reason, _clock()), out var entry))
        {
            message = "The lab key is locked; unlock it to revoke.";
            return false;
        }

        Push([entry]);
        Events.Warning("revocation.issued", $"Certificate {entry.Serial} revoked: {reason}");
        message = $"Certificate {entry.Serial} is revoked.";
        return true;
    }

    /// <summary>
    /// Withdraws a teacher device (D-56 item 6): two signed entries — its current leaf serial
    /// and the <c>instance:&lt;id&gt;</c> pseudo-serial — so a leaf renewed later is refused
    /// too. Pushed to every linked PC at once; delivery is shown, never assumed.
    /// </summary>
    public bool TryWithdrawDevice(string instanceId, string reason, out string message)
    {
        if (Vault is null)
        {
            message = Strings.Get("Access.AdministratorNeeded");
            return false;
        }

        var record = Registry.Document.Instances.FirstOrDefault(i => string.Equals(i.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            message = $"Device {instanceId} is not in this lab's list.";
            return false;
        }

        if (string.Equals(instanceId, Instance.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            message = "This console cannot withdraw its own access.";
            return false;
        }

        var now = _clock();
        if (!Vault.Use(lab =>
            {
                // The instance entry, then every leaf serial the device was ever recorded with
                // (D-56 item 6): a leaf minted for an earlier request must not outlive the withdrawal.
                var entries = new List<RevocationEntry> { Registry.Revoke(lab, LabCertificates.InstanceSerial(instanceId), reason, now) };
                var serials = new HashSet<string>(record.CertificateSerials, StringComparer.Ordinal);
                if (record.CertificateSerial.Length > 0)
                {
                    serials.Add(record.CertificateSerial);
                }

                foreach (var serial in serials.Where(serial => serial.Length > 0 && !Registry.Revocations.IsRevoked(serial)))
                {
                    entries.Add(Registry.Revoke(lab, serial, reason, now));
                }

                return entries;
            }, out var issued))
        {
            message = "The lab key is locked; unlock it to withdraw access.";
            return false;
        }

        Registry.Persist(_ => record.RevokedAtUnix = now.ToUnixTimeSeconds());
        Push(issued);
        SaveLabSoon();

        var name = record.Name.Length > 0 ? record.Name : instanceId;
        Events.Warning("device.withdrawn", $"Access of {name} withdrawn: {reason}. Delivered to the linked PCs now; the others learn it when they link.");

        // It stops being one of the other teacher machines here and now: no banner, no
        // Take over button of its own, and it can never be named as holding a PC (D-56 item 6).
        ForgetOtherConsole(instanceId);
        OtherConsolesChanged?.Invoke();
        message = $"Access of {name} is withdrawn.";
        return true;
    }

    /// <summary>Raised when a PC confirms holding a revocation it did not have before (D-56 item 6).</summary>
    public event Action? RevocationDeliveryChanged;

    /// <summary>
    /// How far one revocation entry has travelled: which PCs confirmed holding it and which
    /// have not, with when those were last seen. "Delivered to 11 of 14; pending on PC-03".
    /// </summary>
    public RevocationDelivery DeliveryOf(string serial)
    {
        var normalized = LabCertificates.NormalizeSerial(serial);
        MachineRecord[] machines;
        lock (_gate)
        {
            machines = Registry.Document.Machines.ToArray();
        }

        // An agent older than M5 cannot hold an instance: entry (its serial normalisation
        // breaks the signature, so it drops it and is re-pushed on every link). It shows
        // itself by confirming a sibling entry — a leaf serial signed in the same withdrawal,
        // same second and same reason — while never confirming this one.
        var siblings = Array.Empty<string>();
        if (normalized.StartsWith(Defaults.InstanceRevocationPrefix, StringComparison.Ordinal)
            && Registry.Revocations.Entries.FirstOrDefault(e => string.Equals(e.Serial, normalized, StringComparison.Ordinal)) is { } entry)
        {
            siblings = Registry.Revocations.Entries
                .Where(e => !e.Serial.StartsWith(Defaults.InstanceRevocationPrefix, StringComparison.Ordinal)
                            && e.RevokedAtUnix == entry.RevokedAtUnix && string.Equals(e.Reason, entry.Reason, StringComparison.Ordinal))
                .Select(e => e.Serial)
                .ToArray();
        }

        var missing = machines.Where(m => !m.RevocationSerialsSeen.Contains(normalized, StringComparer.Ordinal)).OrderBy(m => m.Number).ToArray();
        var cannotHold = missing.Where(m => siblings.Length > 0 && m.RevocationSerialsSeen.Any(seen => siblings.Contains(seen, StringComparer.Ordinal))).ToArray();
        var pending = missing.Except(cannotHold).ToArray();
        return new RevocationDelivery(normalized, machines.Length - missing.Length, machines.Length, pending, cannotHold);
    }

    private void Push(IReadOnlyList<RevocationEntry> entries)
    {
        var revocation = new Revocation();
        revocation.Entries.AddRange(entries);
        var push = new ConsoleMessage { Revocation = revocation };
        var serials = entries.Select(e => LabCertificates.NormalizeSerial(e.Serial)).ToHashSet(StringComparer.Ordinal);

        foreach (var connection in Linked)
        {
            connection.TrySend(push);
            if (serials.Contains(LabCertificates.NormalizeSerial(connection.CertificateSerial)))
            {
                connection.Close("this PC's certificate was revoked");
                Events.Warning("link.revoked", Localization.Strings.Format("Event.RevokedLinkClosed",
                    string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, connection.Number)), connection.AgentId, connection.Number);
            }
        }
    }

    /// <summary>A revocation that names this very console (learned from a PC or a file): said loudly, once.</summary>
    private void NoteRevocationOfSelf(RevocationEntry entry)
    {
        if (string.Equals(entry.Serial, LabCertificates.InstanceSerial(Instance.InstanceId), StringComparison.Ordinal)
            || string.Equals(entry.Serial, Instance.CertificateSerial, StringComparison.Ordinal))
        {
            Events.Error("access.withdrawn", $"This console's access to the lab was withdrawn ({entry.Reason}); PCs that hold the entry refuse it from now on.");
        }
    }

    /// <summary>
    /// Applies a lab file or grant snapshot to the running lab (D-56 item 3): the merge that
    /// cannot go backwards, through the registry so linked PCs and the mosaic see it.
    /// </summary>
    public LabFileMergeReport ApplySnapshot(LabFilePayload snapshot)
    {
        LabFileMergeReport report = null!;
        var known = Registry.Revocations.Serials;
        Registry.Persist(document => report = LabFile.Merge(document, snapshot, Authority, Registry.Revocations, _clock()));
        var learned = Registry.Revocations.Except(known);
        if (learned.Count > 0)
        {
            foreach (var entry in learned)
            {
                NoteRevocationOfSelf(entry);
            }

            Push(learned);
        }

        SaveLab();
        MachinesChanged?.Invoke();
        return report;
    }

    // ------------------------------------------------------------------ departure (M5, D-57)

    /// <summary>
    /// What leaving this lab right now would interrupt (D-57 item 3): jobs delivered and
    /// still running, grouped by kind with what happens to each; jobs waiting for an offline
    /// PC; uploads in progress; PCs trying a new agent version; wakes being watched. Read
    /// only — nothing is cancelled here.
    /// </summary>
    public DepartureReport DescribeDeparture()
    {
        var jobs = Jobs.All();
        var running = jobs
            .Where(j => j.State is JobState.Delivered or JobState.Running)
            .GroupBy(j => j.Kind)
            .Select(g => new DepartureJobGroup(g.Key, g.Count(), DepartureReport.ConsequenceOf(g.Key)))
            .OrderBy(g => g.Kind)
            .ToArray();
        var queued = jobs.Count(j => j.State == JobState.Pending);

        int[] probation;
        int[] wakes;
        lock (_gate)
        {
            probation = _linked.Values
                .Where(c => c.UpdateState.Phase == UpdateState.Types.Phase.OnProbation)
                .Select(c => c.Number).Order().ToArray();
            wakes = _waking.Values.Select(w => w.Number).Order().ToArray();
        }

        var now = _clock();
        var linked = Linked;
        var broadcast = BroadcastTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uncollected = Collections.AwaitingExamWork()
            .Select(id => Registry.FindByAgentId(id)?.Number ?? 0).Where(n => n > 0).Distinct().Order().ToArray();
        return new DepartureReport(running, queued, Uploads.InProgressCount, probation, wakes)
        {
            BroadcastPcs = Broadcast is null ? [] : linked.Where(c => broadcast.Contains(c.AgentId)).Select(c => c.Number).Order().ToArray(),
            Locks = PolicyOf(linked.Where(c => c.OverlayLocked), c => now.AddSeconds(Math.Max(0, c.OverlayState!.RemainingSeconds))),
            Internet = PolicyOf(linked.Where(HoldsStandaloneInternet), c => now.AddSeconds(Math.Max(0, c.InternetState!.RemainingSeconds))),
            Exams = PolicyOf(linked.Where(c => c.InExam), c => c.ExamStateAt.AddSeconds(Math.Max(0, c.ExamState!.RemainingSeconds))),
            UncollectedExamPcs = uncollected,
        };
    }

    /// <summary>
    /// The PCs holding one kind of policy and the latest moment one lifts it by itself. The lock's
    /// and the internet's <c>remaining_seconds</c> are as of their last send and are not aged, so
    /// the moment is never earlier than the truth; the exam's are aged from their arrival.
    /// </summary>
    private static DeparturePolicy PolicyOf(IEnumerable<AgentConnection> holding, Func<AgentConnection, DateTimeOffset> endsAt)
    {
        var pcs = holding.ToArray();
        return pcs.Length == 0
            ? DeparturePolicy.None
            : new DeparturePolicy(pcs.Select(c => c.Number).Distinct().Order().ToArray(), pcs.Max(endsAt));
    }

    /// <summary>
    /// The PC holds a standalone internet policy, not only an exam's internet switch — the exam's
    /// lives and dies with the exam, and <i>Open</i> lifts the standalone one alone.
    /// </summary>
    private static bool HoldsStandaloneInternet(AgentConnection connection) =>
        connection.InternetMode != InternetPolicy.Types.Mode.Open
        && !connection.InternetState!.SessionId.EndsWith(LabControl.Shared.Policy.PolicyJournal.ExamInternetSuffix, StringComparison.Ordinal);

    /// <summary>
    /// The linked PCs among <paramref name="agentIds"/> whose exam switches the internet: there the
    /// exam's setting wins until it ends, so <i>Open</i> or a new policy only takes effect after it
    /// (a lab, 2026-10-26).
    /// </summary>
    public IReadOnlyList<int> ExamInternetPcs(IEnumerable<string> agentIds)
    {
        var ids = agentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Linked
            .Where(c => ids.Contains(c.AgentId) && c.InternetMode != InternetPolicy.Types.Mode.Open
                && c.InternetState!.SessionId.EndsWith(LabControl.Shared.Policy.PolicyJournal.ExamInternetSuffix, StringComparison.Ordinal))
            .Select(c => c.Number).Distinct().Order().ToArray();
    }

    /// <summary>
    /// <i>Unlock, open the internet and leave</i> (owner decision 2026-10-26, D-79 item 1): every
    /// linked PC holding a lock is unlocked and every one holding a standalone internet policy has
    /// it lifted — whoever set them, as any console of the lab may (owner decision 4); an exam is
    /// left to its timer, its internet switch with it. Waits, at most <paramref name="budget"/>,
    /// for the PCs' own word that they are clear, because the departure closes every link and a
    /// queued message would die with it. Returns the PC numbers still holding one when the wait
    /// ended; they keep it to its own limit.
    /// </summary>
    public async Task<IReadOnlyList<int>> EndLocksAndInternetAsync(TimeSpan budget)
    {
        var locked = Linked.Where(c => c.OverlayLocked).Select(c => c.AgentId).ToArray();
        var internet = Linked.Where(HoldsStandaloneInternet).Select(c => c.AgentId).ToArray();
        if (locked.Length > 0)
        {
            Unlock(locked);
        }

        if (internet.Length > 0)
        {
            LiftInternet(internet);
        }

        int[] Holding() => Linked
            .Where(c => locked.Contains(c.AgentId, StringComparer.OrdinalIgnoreCase) && c.OverlayLocked
                || internet.Contains(c.AgentId, StringComparer.OrdinalIgnoreCase) && HoldsStandaloneInternet(c))
            .Select(c => c.Number).Distinct().Order().ToArray();

        var deadline = DateTimeOffset.UtcNow + budget;
        var holding = Holding();
        while (holding.Length > 0 && DateTimeOffset.UtcNow < deadline && !_stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            holding = Holding();
        }

        if (holding.Length > 0)
        {
            Events.Warning("departure.policies_held", Localization.Strings.Format("Event.DeparturePoliciesHeld",
                string.Join(", ", holding.Select(n => string.Format(System.Globalization.CultureInfo.InvariantCulture, Defaults.MachineNameFormat, n)))));
        }

        return holding;
    }

    // ------------------------------------------------------------------ persistence and housekeeping

    private int _savePending;

    private void SaveLabSoon()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, _stopping.Token);
            }
            catch (OperationCanceledException)
            {
            }

            Interlocked.Exchange(ref _savePending, 0);
            SaveLab();
        });
    }

    public void SaveLab()
    {
        try
        {
            Registry.Persist(Store.SaveLab);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Events.Error("store.save_failed", $"Could not save {Defaults.LabFileName}: {ex.Message}");
        }
    }

    public void SaveEnrollment()
    {
        try
        {
            Enrollment.Persist(Store.SaveEnrollment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Events.Error("store.save_failed", $"Could not save {Defaults.EnrollmentFileName}: {ex.Message}");
        }
    }

    private async Task HousekeepingAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var now = _clock();

                foreach (var connection in Linked)
                {
                    if (now - connection.LastHeartbeat > Defaults.HeartbeatTimeout)
                    {
                        connection.Close($"no heartbeat for {Defaults.HeartbeatTimeout.TotalSeconds:0} s");
                    }
                }

                foreach (var job in Jobs.TimeOutStale(now))
                {
                    Events.Warning("job.timed_out", $"{job.Kind} on agent {job.AgentId}: {job.Message}", job.AgentId);
                }

                TimeOutWakes(now);
                Collections.Tick();

                var gone = false;
                lock (_gate)
                {
                    foreach (var (id, other) in _others.ToArray())
                    {
                        if (now - other.LastSeen > Defaults.OtherConsoleTimeout)
                        {
                            _others.Remove(id);
                            Events.Info("console.other_gone", $"{other.Name} is no longer beaconing.");
                            gone = true;
                        }
                    }
                }

                if (gone)
                {
                    OtherConsolesChanged?.Invoke();
                }

                Vault?.Tick();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or RpcException or IOException or InvalidOperationException)
        {
        }
    }
}
