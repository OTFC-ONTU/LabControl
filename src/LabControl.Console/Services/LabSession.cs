using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
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

/// <summary>Another teacher machine heard beaconing (ARCHITECTURE §3.7.2).</summary>
public sealed record OtherConsole(string InstanceId, string Name, string Endpoint, DateTimeOffset LastSeen, DateTimeOffset? TookOverAt);

/// <summary>A Wake-on-LAN in progress: the packets went out, the PC has until <see cref="Deadline"/> to link.</summary>
public sealed record PendingWake(string AgentId, int Number, DateTimeOffset StartedAt, DateTimeOffset Deadline);

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
        Func<DateTimeOffset>? clock = null,
        IReadOnlyList<SeedScript>? seedScripts = null)
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
        Files = new FileOffers();
        Screens = new ScreenStore(_clock);
        Screens.KeyframeNeeded += screen => RequestKeyframe(screen.AgentId);
        Events = new EventLog(store.LogsDirectory, _clock);
        _journal = new JobJournal(store.LogsDirectory);
        BatchLogs = new JobBatchLogs(store.LogsDirectory, Jobs, id => Registry.FindByAgentId(id)?.Number ?? 0, _clock);
        Scripts = new ScriptLibrary(store, vault.LabId, seedScripts ?? [], _clock, loggers.CreateLogger<ScriptLibrary>());

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

    public JobBatchLogs BatchLogs { get; }

    /// <summary>What agents may pull through <c>PullFile</c> (D-31): scripts now, packages and bundles in M4.</summary>
    public FileOffers Files { get; }

    public FileUploads Uploads { get; } = new();

    /// <summary>Every PC's screen as last seen (M3): thumbnails for the mosaic, the full picture for the single-PC view.</summary>
    public ScreenStore Screens { get; }

    /// <summary>The script library (D-31 item 4), seeded on the first run.</summary>
    public ScriptLibrary Scripts { get; }

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

        _broadcaster = new BeaconBroadcaster(Instance, Port, Options.BindAddress, _clock, Options.BeaconPort);
        _broadcaster.Failed += message => Events.Warning("beacon.send_failed", message);
        _broadcaster.Start();

        _listener = new BeaconListener(Options.BeaconPort);
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
        Screens.Dispose();
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

        NoteWoke(hello.AgentId, who, now);
        DeliverJobs(connection, resendInFlight: true);

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
                if (connection.ApplyEvent(reported))
                {
                    if (reported.Code == LabControl.Shared.Setup.SetupReadiness.EventCode) SaveLabSoon();
                    MachinesChanged?.Invoke();
                }

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
            TryWriteBatchLogs(job.BatchId);
        }
    }

    private void TryWriteBatchLogs(string batchId, bool register = false)
    {
        try
        {
            if (register)
            {
                BatchLogs.Register(batchId);
            }
            else
            {
                BatchLogs.Record(batchId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
