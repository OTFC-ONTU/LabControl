using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using LabControl.Console.Server;
using LabControl.Shared;
using LabControl.Shared.Discovery;
using LabControl.Shared.Identity;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>Another teacher machine heard beaconing (ARCHITECTURE §3.7.2).</summary>
public sealed record OtherConsole(string InstanceId, string Name, string Endpoint, DateTimeOffset LastSeen, DateTimeOffset? TookOverAt);

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
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly JobJournal _journal;

    private ConsoleServer? _server;
    private BeaconBroadcaster? _broadcaster;
    private BeaconListener? _listener;
    private Task? _housekeeping;

    public LabSession(
        ConsoleOptions options,
        LabStore store,
        LabKeyVault vault,
        ConsoleInstance instance,
        InstanceDocument instanceDocument,
        ILoggerFactory loggers,
        Func<DateTimeOffset>? clock = null)
    {
        Options = options;
        Store = store;
        Vault = vault;
        Instance = instance;
        InstanceDocument = instanceDocument;
        _loggers = loggers;
        _log = loggers.CreateLogger<LabSession>();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        Authority = X509CertificateLoader.LoadCertificate(vault.Document.Authority);
        Trust = new LabTrust(Authority, vault.LabId);
        Registry = new LabRegistry(store.LoadLab(vault.LabId, vault.LabName), Authority);
        Enrollment = new EnrollmentAuthority(store.LoadEnrollment(vault.LabId));
        Jobs = new JobQueue();
        Events = new EventLog(store.LogsDirectory, _clock);
        _journal = new JobJournal(store.LogsDirectory);

        Registry.Changed += () => SaveLabSoon();
        Registry.Replaced += OnMachineReplaced;
        Jobs.Updated += OnJobUpdated;

        Registry.RecordInstance(instance.InstanceId, instance.InstanceName, instance.CertificateSerial, _clock(), isThisMachine: true);
    }

    public ConsoleOptions Options { get; }

    public LabStore Store { get; }

    public LabKeyVault Vault { get; }

    public ConsoleInstance Instance { get; }

    public InstanceDocument InstanceDocument { get; }

    /// <summary>The public authority, for validating peers and beacons.</summary>
    public X509Certificate2 Authority { get; }

    public LabTrust Trust { get; }

    public LabRegistry Registry { get; }

    public EnrollmentAuthority Enrollment { get; }

    public JobQueue Jobs { get; }

    public EventLog Events { get; }

    public string LabId => Vault.LabId;

    public string LabName => Vault.LabName;

    /// <summary>The port the server actually listens on — differs from the option only when it was 0.</summary>
    public int Port => _server?.Port ?? Options.Port;

    public DateTimeOffset Now => _clock();

    // ------------------------------------------------------------------ notifications

    /// <summary>A PC linked, unlinked, changed status or was added/replaced; the lab view refreshes.</summary>
    public event Action? MachinesChanged;

    public event Action<AgentConnection>? AgentLinked;

    public event Action<AgentConnection, string>? AgentUnlinked;

    /// <summary>Another teacher machine appeared, disappeared or took over.</summary>
    public event Action? OtherConsolesChanged;

    // ------------------------------------------------------------------ lifecycle

    public async Task StartAsync()
    {
        Store.EnsureDirectories();

        _server = await ConsoleServer.StartAsync(this, _loggers);
        _log.LogInformation("Console '{Name}' ({Instance}) serving lab '{Lab}' on {Bind}:{Port}",
            Instance.InstanceName, Instance.InstanceId, LabName, Options.BindAddress, Port);

        _broadcaster = new BeaconBroadcaster(Instance, Port, Options.BindAddress, _clock);
        _broadcaster.Failed += message => Events.Warning("beacon.send_failed", message);
        _broadcaster.Start();

        _listener = new BeaconListener();
        _listener.Received += OnBeaconReceived;
        _listener.Failed += message => Events.Warning("beacon.listen_failed", message);
        _listener.Start();

        _housekeeping = Task.Run(() => HousekeepingAsync(_stopping.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();

        AgentConnection[] connections;
        lock (_gate)
        {
            connections = _linked.Values.ToArray();
        }

        foreach (var connection in connections)
        {
            connection.Close("the console is closing");
        }

        _broadcaster?.Dispose();
        _listener?.Dispose();

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        if (_housekeeping is not null)
        {
            try
            {
                await _housekeeping.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        SaveLab();
        Vault.Dispose();
        Instance.Dispose();
        Authority.Dispose();
        _stopping.Dispose();
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
    /// The PCs this console knows but does not hold while another console is live: the
    /// number the §3.7.2 banner shows. Another console cannot tell us its list, so every
    /// known PC that is not linked here is presumed to be with it.
    /// </summary>
    public IReadOnlyList<MachineRecord> HeldElsewhere()
    {
        lock (_gate)
        {
            return Registry.Document.Machines.Where(m => !_linked.ContainsKey(m.AgentId)).OrderBy(m => m.Number).ToArray();
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

            if (beacon.TakeAtUnix > 0)
            {
                var at = DateTimeOffset.FromUnixTimeSeconds(beacon.TakeAtUnix);
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

            _others[beacon.InstanceId] = new OtherConsole(beacon.InstanceId, beacon.InstanceName, beacon.Endpoint, now, tookOver);
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
        var lab = Vault.Peek();
        var result = Enrollment.Redeem(lab, request, now, Options.DevelopmentAgentCertificateLifetime);
        var who = string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, request.Number);

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
        var now = _clock();
        var target = Path.Combine(directory, Defaults.PayloadDirectoryName);
        Directory.CreateDirectory(target);

        var voided = voidEarlier ? Enrollment.Supersede(now) : 0;
        var codes = Enrollment.Generate(pcCount + Defaults.SpareEnrollmentCodes, $"{Instance.InstanceName} {now:yyyy-MM-dd HH:mm}", now);
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
        var result = CertificateRenewal.Renew(Vault.Peek(), name, request.Csr.ToByteArray(), now);

        var response = new RenewResponse { ServerTimeUnix = now.ToUnixTimeSeconds() };

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

        var reason = $"this PC's certificate was refused: {LabTrust.Describe(failure, name)}";
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
        };
        welcome.RevokedSerials.AddRange(Registry.Revocations.Serials);
        connection.TrySend(new ConsoleMessage { Welcome = welcome });

        DeliverJobs(connection, resendInFlight: true);
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
                var dropped = Jobs.AgentWentOffline(hello.AgentId, _clock());
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

                if (previousHelper is true && !state.HelperAlive)
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
                Events.Add(reported.Severity switch
                {
                    Event.Types.Severity.Error => EventSeverity.Error,
                    Event.Types.Severity.Warning => EventSeverity.Warning,
                    _ => EventSeverity.Info,
                }, reported.Code.Length > 0 ? reported.Code : "agent.event", $"{who}: {reported.Message}", connection.AgentId, connection.Number);
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

        foreach (var agentId in agentIds)
        {
            var connection = FindLinked(agentId);
            var job = Jobs.Create(agentId, kind, now, connection is not null, args, timeout, delivery, batch);
            created.Add(job);

            if (connection is not null)
            {
                DeliverJobs(connection, resendInFlight: false);
            }
        }

        return created;
    }

    private void DeliverJobs(AgentConnection connection, bool resendInFlight)
    {
        var now = _clock();

        if (resendInFlight)
        {
            foreach (var job in Jobs.InFlight(connection.AgentId))
            {
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
        if (!Vault.Use(lab => Registry.Revoke(lab, certificateSerial, reason, _clock()), out var entry))
        {
            message = "The lab key is locked; unlock it to revoke.";
            return false;
        }

        var revocation = new Revocation();
        revocation.Entries.Add(entry);
        var push = new ConsoleMessage { Revocation = revocation };

        foreach (var connection in Linked)
        {
            connection.TrySend(push);
            if (string.Equals(connection.CertificateSerial, entry.Serial, StringComparison.Ordinal))
            {
                connection.Close("this PC's certificate was revoked");
            }
        }

        Events.Warning("revocation.issued", $"Certificate {entry.Serial} revoked: {reason}");
        message = $"Certificate {entry.Serial} is revoked.";
        return true;
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

                Vault.Tick();
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
