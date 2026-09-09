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
using LabControl.Shared.Persistence;
using LabControl.Shared.Power;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
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
    Beacons = 1,
    Links = 2,
    Listener = 3,
    Server = 4,
    Housekeeping = 5,
    Save = 6,
    Screens = 7,
    Vault = 8,
    Instance = 9,
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
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly JobJournal _journal;
    private static long _generations;
    private int _closed;

    private ConsoleServer? _server;
    private BeaconBroadcaster? _broadcaster;
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
        Events = new EventLog(store.LogsDirectory, _clock);
        _journal = new JobJournal(store.LogsDirectory);
        BatchLogs = new JobBatchLogs(store.LogsDirectory, Jobs, id => Registry.FindByAgentId(id)?.Number ?? 0, _clock);
        Scripts = new ScriptLibrary(store, identity.LabId, seedScripts ?? [], _clock, loggers.CreateLogger<ScriptLibrary>());

        Registry.Changed += () => SaveLabSoon();
        Registry.Replaced += OnMachineReplaced;
        Jobs.Updated += OnJobUpdated;
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

    /// <summary>Every PC's screen as last seen (M3): thumbnails for the mosaic, the full picture for the single-PC view.</summary>
    public ScreenStore Screens { get; }

    /// <summary>The script library (D-31 item 4), seeded on the first run.</summary>
    public ScriptLibrary Scripts { get; }

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

        lock (_gate)
        {
            if (_linked.ContainsKey(machine.AgentId))
            {
                return new MachineOwnership(OwnershipKind.LinkedHere, LastSeen: now);
            }

            var live = _others.Values.Where(o => now - o.LastSeen <= Defaults.OtherConsoleTimeout).ToArray();
            var holder = live.FirstOrDefault(o => LabRegistry.IsObservedWith(machine, o.InstanceId, now));
            if (holder is not null)
            {
                return new MachineOwnership(OwnershipKind.ObservedElsewhere, holder,
                    DateTimeOffset.FromUnixTimeSeconds(machine.LastInstanceObservedUnix), lastSeen);
            }

            return new MachineOwnership(live.Length > 0 ? OwnershipKind.Unknown : OwnershipKind.Offline, LastSeen: lastSeen);
        }
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
        Events.Info("console.take_over", $"{Instance.InstanceName} is taking over the lab.");
    }

    public DateTimeOffset? TakingOverSince => _broadcaster?.TakeOverAt;

    private void OnBeaconReceived(ReadOnlyMemory<byte> datagram, IPEndPoint from)
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
        };
        welcome.RevokedSerials.AddRange(Registry.Revocations.Serials);
        BeforeWelcome?.Invoke(welcome);
        connection.TrySend(new ConsoleMessage { Welcome = welcome });

        NoteWoke(hello.AgentId, who, now);
        DeliverJobs(connection, resendInFlight: true, hello.BootTimeUnix);

        // Screens (M3): every linked PC streams a thumbnail; the full view asks for more.
        // An M2-era agent answers with session.not_in_this_build once and is otherwise unhurt.
        if (!connection.IsOutdated)
        {
            var screen = Screens.Get(hello.AgentId);
            var wanted = screen.RequestedMode == VideoMode.Full ? VideoSettings.FullControl(quality: screen.RequestedQuality) : VideoSettings.ThumbnailControl();
            screen.RequestedMode = wanted.Mode;
            connection.RequestedVideo = wanted.Mode;
            connection.TrySend(new ConsoleMessage { VideoControl = wanted });
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
                NoteDeparture(connection.Machine, who, wentOffline);

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
    /// D-58): the PC left while a take-over by that machine was live here — its signed
    /// <c>take</c> beacon arrived within the window, measured on this console's own clock.
    /// A PC that leaves for any other reason leaves no observation behind, and its tile then
    /// says offline or "not seen", never "held by".
    /// </summary>
    private void NoteDeparture(MachineRecord machine, string who, DateTimeOffset now)
    {
        OtherConsole? taker = null;
        lock (_gate)
        {
            foreach (var other in _others.Values)
            {
                if (other.TookOverSeenAt is not { } seen || now - seen > Defaults.TakeOverWindow)
                {
                    continue;
                }

                if (taker is null || seen > taker.TookOverSeenAt)
                {
                    taker = other;
                }
            }
        }

        if (taker is null || Registry.RecordObservedElsewhere(machine.AgentId, taker.InstanceId, now) is null)
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

            case AgentMessage.PayloadOneofCase.JobProgress:
                Jobs.Progress(message.JobProgress, now);
                break;

            case AgentMessage.PayloadOneofCase.JobResult:
                Jobs.Complete(message.JobResult, now);
                break;

            case AgentMessage.PayloadOneofCase.Event:
                var reported = message.Event;
                var eventText = reported.Code == SetupReadiness.EventCode
                    ? ReadinessPresentation.EventText(reported.Message)
                    : reported.Code == UpdateTerminalReport.RolledBackCode
                        ? Localization.Strings.Get("Update.RollbackReported") + (InstallLayout.IsValidVersion(reported.Message)
                            ? " " + Localization.Strings.Format("Tile.UpdateRolledBack", reported.Message) : "")
                        : reported.Code == UpdateTerminalReport.StableCode ? Localization.Strings.Get("Tile.UpdateStable") : reported.Message;
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
        return CreateJobs(agentIds, Job.Types.Kind.SelfUpdate, request.ToArgs(), Defaults.SelfUpdateJobTimeout);
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
    public void SetScreenMode(string agentId, VideoMode mode, int? quality = null)
    {
        var screen = Screens.Get(agentId);
        screen.RequestedMode = mode == VideoMode.Full ? VideoMode.Full : VideoMode.Thumbnail;
        if (quality is { } chosen)
        {
            screen.RequestedQuality = Math.Clamp(chosen, Defaults.VideoQualityAuto, 100);
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
                ? VideoSettings.FullControl(quality: screen.RequestedQuality)
                : VideoSettings.ThumbnailControl(),
        });
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

    /// <summary>Asks a PC in full mode for a whole picture: the console has nothing to patch a delta onto.</summary>
    public void RequestKeyframe(string agentId)
    {
        var connection = FindLinked(agentId);
        if (connection is null || connection.RequestedVideo != VideoMode.Full)
        {
            return;
        }

        // The same quality as the standing control, or the PC would see a new control and restart its budget.
        connection.TrySend(new ConsoleMessage { VideoControl = VideoSettings.FullControl(requestKeyframe: true, Screens.Get(agentId).RequestedQuality) });
    }

    public Task<FileAck> ReceiveFileAsync(X509Certificate2? peer, IAsyncStreamReader<FileChunk> incoming, CancellationToken token) =>
        Uploads.ReceiveAsync(RequireAgent(peer).Id, incoming, token);

    public Task<FileAck> UploadStatusAsync(X509Certificate2? peer, FileRequest request, CancellationToken token) =>
        Uploads.StatusAsync(RequireAgent(peer).Id, request.Reference, token);

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
        var serials = entries.Select(e => e.Serial).ToHashSet(StringComparer.Ordinal);

        foreach (var connection in Linked)
        {
            connection.TrySend(push);
            if (serials.Contains(connection.CertificateSerial))
            {
                connection.Close("this PC's certificate was revoked");
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

        return new DepartureReport(running, queued, Uploads.InProgressCount, probation, wakes);
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
