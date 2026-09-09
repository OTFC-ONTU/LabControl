using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using LabControl.Shared.Discovery;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using Microsoft.Extensions.Logging;

namespace LabControl.Shared.Link;

/// <summary>Where the PC stands, for its own log and for a simulator's display.</summary>
public enum LinkState
{
    Stopped = 0,

    /// <summary>Listening for a beacon (or retrying the pinned host); nothing dialled yet.</summary>
    Searching = 1,

    /// <summary>Dialled; enrolling or opening the stream.</summary>
    Connecting = 2,

    Linked = 3,
}

/// <summary>Tunables a host may override; a real PC uses the defaults.</summary>
public sealed class AgentLinkOptions
{
    /// <summary>What the PC claims in <c>Hello</c>. A simulator lowers it to play an outdated agent.</summary>
    public int ProtocolVersion { get; init; } = Defaults.ProtocolVersion;

    /// <summary>A clock a test can move; the PC's own clock otherwise.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Send a forged revocation entry in <c>RevocationState</c>, to prove the console drops it.</summary>
    public Func<IEnumerable<RevocationEntry>>? ExtraRevocations { get; init; }

    /// <summary>
    /// How many finished job results this PC keeps for consoles that have not come back
    /// (D-57 item 4); the oldest are dropped, with an event, past it. Lowered in tests.
    /// </summary>
    public int MaxPendingResults { get; init; } = Defaults.MaxPendingJobResults;
}

/// <summary>
/// One PC's whole relationship with the console, shared by the real agent and
/// <c>FakeAgent</c> (ROADMAP M1). It hears beacons, dials, enrols if it has no
/// certificate yet, holds the <c>Link</c> stream, heartbeats, runs jobs exactly once,
/// renews its certificate when due, follows a <i>Take over</i>, and reconnects with
/// backoff when any of that ends. Everything it needs from the outside is behind
/// <see cref="IAgentStore"/> and <see cref="IAgentBehaviour"/>.
/// </summary>
public sealed partial class AgentLink : IAsyncDisposable
{
    private readonly IAgentStore _store;
    private readonly IAgentBehaviour _behaviour;
    private readonly AgentLinkOptions _options;
    private readonly ILogger _log;
    private readonly BeaconGate _gate;
    private readonly JobLedger _ledger = new();
    private readonly RevocationSet _revocations = new();
    private readonly ReconnectBackoff _pinnedBackoff = new();
    private readonly Channel<string> _dials = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gateLock = new();

    private readonly Queue<Event> _pendingEvents = new();
    private readonly List<JobLedgerEntry> _pendingResults = [];
    private ChannelWriter<AgentMessage>? _outgoing;
    private AgentService.AgentServiceClient? _client;
    private SessionState? _latestSessionState;
    private VideoUplink? _video;
    private VideoControl? _videoControl;

    private Task? _loop;
    private CancellationTokenSource? _session;
    private string? _pendingEndpoint;
    private string? _pendingReason;
    private string? _previousInstanceId;
    private string _lastRefusal = string.Empty;

    public AgentLink(IAgentStore store, IAgentBehaviour behaviour, ILogger log, AgentLinkOptions? options = null)
    {
        _store = store;
        _behaviour = behaviour;
        _log = log;
        _options = options ?? new AgentLinkOptions();

        // The gate reads the same live set the link merges into (D-56 item 6): a console
        // withdrawn during the lesson stops being followed from the very next beacon, without
        // waiting for a handshake this PC would only reach after giving up the room.
        _gate = new BeaconGate(store.Authority, store.Config.LabId, _revocations);

        foreach (var record in store.Config.Revocations.ToArray())
        {
            if (!_revocations.TryAdd(store.Authority, record.ToEntry()))
            {
                store.Config.Revocations.Remove(record);
            }
        }
    }

    public LinkState State { get; private set; }

    public string? LinkedInstanceId { get; private set; }

    public string? LinkedInstanceName { get; private set; }

    public string? LinkedEndpoint { get; private set; }

    /// <summary>
    /// What the linked console may do, from the OU of the leaf it presented (M5, D-56 item 5):
    /// a teacher console drives the room but is refused <c>self_update</c> and <c>rekey</c>,
    /// and this PC does not ask it for a renewal. <see cref="ConsoleAccess.Unknown"/> while unlinked.
    /// </summary>
    public ConsoleAccess LinkedConsoleAccess { get; private set; }

    /// <summary>
    /// What the console <i>said</i> about its own access in <c>Welcome.console_access</c>
    /// (M5, D-58). Informational: it is here so an event can name the access level, and it
    /// is never used to decide anything — <see cref="LinkedConsoleAccess"/>, read off the
    /// validated leaf, is what refuses a job. <see cref="ConsoleAccess.Unknown"/> while
    /// unlinked and from a console that predates the field.
    /// </summary>
    public ConsoleAccess AnnouncedConsoleAccess { get; private set; }

    /// <summary>The serial of the linked console's leaf, from the TLS handshake; <c>null</c> while unlinked.</summary>
    private string? _linkedConsoleSerial;

    /// <summary>
    /// The instance id every job on this link is bound to (M5, D-57 item 4): the id in the
    /// validated console leaf, so a result can only ever go back to the console that delivered
    /// the job — never to the next console that links. <c>null</c> while unlinked.
    /// </summary>
    private string? _boundInstanceId;


    /// <summary>Console clock minus PC clock, learned from <c>Welcome</c>; deadlines are corrected by it.</summary>
    public TimeSpan ClockSkew { get; private set; }

    public bool IsEnrolled => _store.Certificate is not null;

    public bool IsLinked => State == LinkState.Linked;

    /// <summary>Jobs the behaviour is running right now, whether or not a link is up.</summary>
    public int RunningJobs => _ledger.RunningCount;

    public string Name => string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, _store.Config.Number);

    /// <summary>The link came up: the console instance id and its name.</summary>
    public event Action<string, string>? Linked;

    /// <summary>The link ended, with the reason in plain language.</summary>
    public event Action<string>? Unlinked;

    /// <summary>A refusal the teacher should see on the PC's side — enrolment closed, certificate revoked.</summary>
    public event Action<string>? Refused;

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        State = LinkState.Searching;
        _loop = Task.Run(() => RunAsync(_stopping.Token));

        if (!string.IsNullOrWhiteSpace(_store.Config.ConsoleHost))
        {
            _ = Task.Run(() => RetryPinnedHostAsync(_stopping.Token));
        }
    }

    /// <summary>
    /// Hands the PC a beacon datagram, from whatever socket the host listens on.
    /// <paramref name="receivedAt"/> is when the socket produced it (<see cref="Discovery.BeaconListener"/>
    /// stamps every datagram in its own receive loop): the take-over rule turns on whether a
    /// beacon arrived before or after the link came up, and stamping it here instead — after
    /// a fan-out to thirty listeners, or after the link was recorded on another thread —
    /// would make a datagram that was already in flight look like a fresh press (D-58).
    /// The wait since that stamp is measured on the real clock and taken off this PC's own
    /// clock, so a host with a shifted clock still compares like with like.
    /// </summary>
    public void OfferBeacon(ReadOnlyMemory<byte> datagram, DateTimeOffset? receivedAt = null)
    {
        if (State == LinkState.Stopped || _stopping.IsCancellationRequested)
        {
            return;
        }

        var now = _options.Clock();
        if (receivedAt is { } arrived)
        {
            var waited = DateTimeOffset.UtcNow - arrived;
            if (waited > TimeSpan.Zero)
            {
                now -= waited;
            }
        }

        var verdict = _gate.Consider(datagram.Span, now);

        switch (verdict.Action)
        {
            case BeaconAction.Dial:
                _log.LogDebug("{Pc}: {Reason}", Name, verdict.Reason);
                _dials.Writer.TryWrite(verdict.Beacon!.Endpoint);
                break;

            case BeaconAction.TakeOver:
                _log.LogInformation("{Pc}: {Reason}; leaving {Current}", Name, verdict.Reason, LinkedInstanceName);
                _ = LeaveForTakerAsync(verdict.Beacon!);
                break;

            default:
                _log.LogTrace("{Pc}: beacon ignored — {Reason}", Name, verdict.Reason);
                break;
        }
    }

    /// <summary>
    /// Leaves the current console for the one that took over — <b>after telling it so</b>
    /// (D-58). The console credits another teacher machine with this PC only on this report,
    /// because every other way a stream can end (the PC switched off, the network dropped,
    /// the service restarted) looks identical from its side. The notice is written first, the
    /// request stream is then closed gracefully so everything queued is delivered in order,
    /// and the link is cut anyway after <see cref="Defaults.DepartureNoticeGrace"/> — a
    /// console that does not answer must never keep this PC from following the taker.
    /// </summary>
    private async Task LeaveForTakerAsync(Beacon taker)
    {
        const string reason = "another console took over the lab";

        ChannelWriter<AgentMessage>? outgoing;
        CancellationTokenSource? session;
        lock (_gateLock)
        {
            outgoing = _outgoing;
            session = _session;
            _pendingEndpoint = taker.Endpoint;
            _pendingReason = reason;
        }

        if (outgoing is not null && session is not null)
        {
            // Straight onto this link, never into the queue of events kept for the next one:
            // a notice that missed this stream is not worth telling the taker about itself.
            outgoing.TryWrite(new AgentMessage { Event = DepartureNotice.Create(taker.InstanceId, _options.Clock()) });

            // A half-close, not a cancellation: a reset stream can lose the notice that was
            // the whole point of sending it.
            outgoing.TryComplete();

            var deadline = _options.Clock() + Defaults.DepartureNoticeGrace;
            while (IsCurrent(session) && _options.Clock() < deadline && !_stopping.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), _stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        lock (_gateLock)
        {
            // Only ever the link this take-over was about: by now the PC may already be
            // linked to the taker, and cutting *that* would be this method undoing its
            // own work.
            if (session is null || ReferenceEquals(_session, session))
            {
                _pendingEndpoint ??= taker.Endpoint;
                _pendingReason ??= reason;
                session?.Cancel();
            }
        }
    }

    /// <summary>Whether <paramref name="session"/> is still the link this PC is running.</summary>
    private bool IsCurrent(CancellationTokenSource session)
    {
        lock (_gateLock)
        {
            return ReferenceEquals(_session, session);
        }
    }

    /// <summary>
    /// Sends an event the teacher should see — a failed Win32 call, a helper that died, an
    /// ACL that is wrong. Sent at once while linked; otherwise kept (the last
    /// <see cref="MaxPendingEvents"/>) and delivered right after the next <c>Welcome</c>, so a
    /// failure during a reconnect is not lost.
    /// </summary>
    public void Report(Event.Types.Severity severity, string code, string message)
    {
        var report = new Event
        {
            Severity = severity,
            Code = code,
            Message = message,
            AtUnix = _options.Clock().ToUnixTimeSeconds(),
        };

        lock (_gateLock)
        {
            if (_outgoing is { } outgoing && outgoing.TryWrite(new AgentMessage { Event = report }))
            {
                return;
            }

            _pendingEvents.Enqueue(report);
            while (_pendingEvents.Count > MaxPendingEvents)
            {
                _pendingEvents.Dequeue();
            }
        }
    }

    /// <summary>Events kept for the next link while there is none; older ones are dropped.</summary>
    public const int MaxPendingEvents = 100;

    /// <summary>
    /// Tells the console who is in the interactive session, whether it is locked and whether
    /// the helper is up (M2). Sent at once while linked; the latest state is also re-sent
    /// right after every <c>Welcome</c>, so a console that (re)connects sees the current
    /// state without waiting for the next change.
    /// </summary>
    public void PublishSessionState(SessionState state)
    {
        lock (_gateLock)
        {
            _latestSessionState = state;
            _outgoing?.TryWrite(new AgentMessage { SessionState = state });
        }
    }

    /// <summary>The last state given to <see cref="PublishSessionState"/>, for a simulator's display.</summary>
    public SessionState? LatestSessionState
    {
        get
        {
            lock (_gateLock)
            {
                return _latestSessionState;
            }
        }
    }

    // ------------------------------------------------------------------ video (M3)

    /// <summary>
    /// What the console last asked for on this link — <c>null</c> when it asked for nothing
    /// or there is no link — so a producer that starts late still knows what to do.
    /// </summary>
    public VideoControl? VideoControl
    {
        get
        {
            lock (_gateLock)
            {
                return _videoControl;
            }
        }
    }

    /// <summary>
    /// Raised with every <c>VideoControl</c> from the console, and with <c>null</c> when the
    /// link ends, so the producer stops. On a thread of the link; return quickly.
    /// </summary>
    public event Action<VideoControl?>? VideoControlChanged;

    /// <summary>
    /// Raised with every <c>Input</c> from the console (PROTOCOL "Input", M3 portion 3), on
    /// the link's read thread: the handler must hand it on without waiting — a real agent
    /// writes it down the helper's pipe fire-and-forget, the simulator draws it.
    /// </summary>
    public event Action<Input>? InputReceived;

    /// <summary>Video frames and bytes sent on the current link, for a simulator's display; zeros without a link.</summary>
    public (long Frames, long Bytes, long Dropped) VideoStats
    {
        get
        {
            lock (_gateLock)
            {
                return _video is { } video ? (video.FramesSent, video.BytesSent, video.FramesDropped) : (0, 0, 0);
            }
        }
    }

    /// <summary>
    /// Offers one frame to the <c>PushVideo</c> stream (PROTOCOL "Video"). Returns <c>false</c>
    /// when it was not taken — no link, video not active, or the previous frame still on the
    /// wire — and the producer should keep what the frame carried for the next try.
    /// </summary>
    public bool TryPushVideo(VideoFrame frame)
    {
        VideoUplink? video;
        lock (_gateLock)
        {
            if (_videoControl is not { Active: true })
            {
                return false;
            }

            video = _video;
        }

        return video is not null && video.TryOffer(frame, _options.Clock());
    }

    /// <summary>Drops the current link, if any; the loop reconnects on the next beacon.</summary>
    public void Disconnect(string reason)
    {
        lock (_gateLock)
        {
            _log.LogInformation("{Pc}: disconnecting ({Reason})", Name, reason);
            _pendingReason = reason;
            _session?.Cancel();
        }
    }

    /// <summary>Ends the current link and dials <paramref name="endpoint"/> next, skipping the beacon wait.</summary>
    private void Redial(string endpoint, string reason)
    {
        lock (_gateLock)
        {
            _pendingEndpoint = endpoint;
            _pendingReason = reason;
            _session?.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        _stopping.Cancel();
        lock (_gateLock)
        {
            _session?.Cancel();
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        State = LinkState.Stopped;
    }

    // ------------------------------------------------------------------ main loop

    private async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            string endpoint;
            try
            {
                endpoint = await NextEndpointAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            State = LinkState.Connecting;
            var succeeded = await RunSessionAsync(endpoint, stopping);

            if (!succeeded && !stopping.IsCancellationRequested)
            {
                var delay = _gate.DialFailed(endpoint, _options.Clock());
                _log.LogDebug("{Pc}: next attempt at {Endpoint} in {Delay:0.#} s", Name, endpoint, delay.TotalSeconds);

                // Beacons that arrived while we were busy point at the same place; the gate's
                // backoff applies to the next one, not to a dial queued before the failure.
                while (_dials.Reader.TryRead(out _))
                {
                }
            }

            State = LinkState.Searching;
        }
    }

    private async Task<string> NextEndpointAsync(CancellationToken stopping)
    {
        lock (_gateLock)
        {
            if (_pendingEndpoint is { } next)
            {
                _pendingEndpoint = null;
                return next;
            }
        }

        return await _dials.Reader.ReadAsync(stopping);
    }

    private async Task RetryPinnedHostAsync(CancellationToken stopping)
    {
        var endpoint = $"{_store.Config.ConsoleHost}:{_store.Config.ConsolePort.ToString(CultureInfo.InvariantCulture)}";

        while (!stopping.IsCancellationRequested)
        {
            var searching = State == LinkState.Searching;
            if (searching)
            {
                _dials.Writer.TryWrite(endpoint);
            }

            try
            {
                // While linked or dialling, look again every second so a dropped link is
                // followed by a dial without waiting out a whole backoff step.
                await Task.Delay(searching ? _pinnedBackoff.Next() : TimeSpan.FromSeconds(1), stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------------ one session

    /// <summary>Returns <c>true</c> if a link was established, whatever ended it.</summary>
    private async Task<bool> RunSessionAsync(string endpoint, CancellationToken stopping)
    {
        if (!TrySplit(endpoint, out var host, out var port))
        {
            _log.LogWarning("{Pc}: '{Endpoint}' is not a host:port", Name, endpoint);
            return false;
        }

        if (_store.Certificate is null && !await EnrollAsync(host, port, stopping))
        {
            return false;
        }

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        lock (_gateLock)
        {
            _session = session;
        }

        try
        {
            return await LinkAsync(host, port, endpoint, session.Token);
        }
        finally
        {
            lock (_gateLock)
            {
                _session = null;
            }
        }
    }

    private async Task<bool> EnrollAsync(string host, int port, CancellationToken token)
    {
        var config = _store.Config;
        if (string.IsNullOrWhiteSpace(config.EnrollmentCode))
        {
            Report("this PC has no enrollment code and no certificate — run Setup again");
            return false;
        }

        using var channel = ConsoleChannel.Open(host, port, _store.Authority, config.LabId, null, _revocations);
        var client = new EnrollmentService.EnrollmentServiceClient(channel.Channel);

        var request = new EnrollRequest
        {
            LabId = config.LabId,
            AgentId = config.AgentId,
            Number = config.Number,
            Hostname = config.Hostname,
            Mac = config.Mac,
            EnrollmentCode = config.EnrollmentCode,
            Csr = ByteString.CopyFrom(LabCertificates.CreateSigningRequest(_store.Key, Name)),
            ProtocolVersion = _options.ProtocolVersion,
        };

        try
        {
            var response = await client.EnrollAsync(request, deadline: DateTime.UtcNow + Defaults.HeartbeatTimeout, cancellationToken: token);

            using var issued = X509CertificateLoader.LoadCertificate(response.AgentCertificate.ToByteArray());
            var trust = new LabTrust(_store.Authority, config.LabId);
            if (!trust.TryValidate(issued, LabRole.Agent, null, out var name, out var failure) ||
                !string.Equals(name.Id, config.AgentId, StringComparison.OrdinalIgnoreCase) ||
                name.Number != config.Number)
            {
                Report($"the console issued a certificate this PC cannot use ({LabTrust.Describe(failure, name)})");
                return false;
            }

            // The key stays; only the certificate is new. Install re-reads both.
            _store.InstallCertificate(_store.Key, issued);
            config.EnrollmentCode = null;
            _store.SaveConfig();
            ClockSkew = DateTimeOffset.FromUnixTimeSeconds(response.ServerTimeUnix) - _options.Clock();

            _lastRefusal = string.Empty;
            _log.LogInformation("{Pc}: enrolled; certificate serial {Serial}", Name, LabCertificates.SerialOf(issued));
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable && channel.Refusal is null)
        {
            // The lab key is locked on the console (D-24): not an error, try again later.
            Report(ex.Status.Detail.Length > 0 ? ex.Status.Detail : "the console is not accepting enrolments right now");
            return false;
        }
        catch (RpcException ex)
        {
            Report(channel.Refusal ?? $"enrolment refused: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or CryptographicException)
        {
            Report(channel.Refusal ?? $"could not reach {host}:{port}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> LinkAsync(string host, int port, string endpoint, CancellationToken token)
    {
        var config = _store.Config;
        var certificate = _store.Certificate!;

        using var channel = ConsoleChannel.Open(host, port, _store.Authority, config.LabId, CertificateWithKey(certificate), _revocations);
        var client = new AgentService.AgentServiceClient(channel.Channel);
        var linked = false;
        var endedBy = "the console closed the link";

        try
        {
            using var call = client.Link(cancellationToken: token);

            var hello = new Hello
            {
                AgentId = config.AgentId,
                LabId = config.LabId,
                Number = config.Number,
                ProtocolVersion = _options.ProtocolVersion,
                CertificateSerial = LabCertificates.SerialOf(certificate),
                Mac = config.Mac,
                PreviousInstanceId = _previousInstanceId ?? string.Empty,
            };
            _behaviour.Describe(hello);

            await call.RequestStream.WriteAsync(new AgentMessage { Hello = hello }, token);

            var state = new RevocationState();
            state.Entries.AddRange(_revocations.Entries);
            if (_options.ExtraRevocations is not null)
            {
                state.Entries.AddRange(_options.ExtraRevocations());
            }

            await call.RequestStream.WriteAsync(new AgentMessage { RevocationState = state }, token);

            if (_behaviour.DescribeInventory() is { } inventory)
            {
                await call.RequestStream.WriteAsync(new AgentMessage { Inventory = inventory }, token);
            }

            // The first message back is Welcome; without it the console did not accept us.
            if (!await call.ResponseStream.MoveNext(token) || call.ResponseStream.Current.PayloadCase != ConsoleMessage.PayloadOneofCase.Welcome)
            {
                endedBy = "the console closed the link before welcoming this PC";
                return false;
            }

            var welcome = call.ResponseStream.Current.Welcome;
            if (BindInstance(channel.PeerName, welcome) is not { } boundInstanceId)
            {
                endedBy = "the console presented no validated console identity";
                return false;
            }

            LinkedConsoleAccess = channel.PeerName?.Access ?? ConsoleAccess.Unknown;
            _linkedConsoleSerial = channel.PeerSerial;
            OnWelcome(welcome, boundInstanceId, endpoint);
            linked = true;

            var outgoing = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions { SingleReader = true });
            var writer = WriteLoopAsync(call.RequestStream, outgoing.Reader, token);
            var heartbeats = HeartbeatLoopAsync(outgoing.Writer, token);

            // Only an administrator console can sign a renewal (D-56 item 5): asking a teacher
            // console would only fill its event log with refusals, and a leaf whose OU this build
            // does not know has no authority at all. The PC asks the next administrator instead.
            var renewal = LinkedConsoleAccess != ConsoleAccess.Administrator ? Task.CompletedTask : RenewalLoopAsync(client, certificate, token);

            var video = new VideoUplink(client, config.AgentId, _log, token,
                (code, message) => Report(Event.Types.Severity.Warning, code, message));

            lock (_gateLock)
            {
                _outgoing = outgoing.Writer;
                _client = client;
                _video = video;
                if (_latestSessionState is { } sessionState)
                {
                    outgoing.Writer.TryWrite(new AgentMessage { SessionState = sessionState });
                }

                while (_pendingEvents.TryDequeue(out var pending))
                {
                    outgoing.Writer.TryWrite(new AgentMessage { Event = pending });
                }

                // Results of jobs that finished while there was no link (D-32): the console
                // re-sends its in-flight jobs after Welcome and the ledger answers those from
                // the cache too, so a result is never lost and never runs twice. Only the
                // results this console's own instance is owed go out (D-57 item 4); the rest
                // stay here for the console that delivered them.
                var kept = new List<JobLedgerEntry>();
                foreach (var finished in _pendingResults)
                {
                    if (JobLedger.SameInstance(finished.InstanceId, boundInstanceId))
                    {
                        outgoing.Writer.TryWrite(new AgentMessage { JobResult = finished.Result });
                    }
                    else
                    {
                        kept.Add(finished);
                    }
                }

                _pendingResults.Clear();
                _pendingResults.AddRange(kept);
                if (kept.Count > 0)
                {
                    _log.LogInformation("{Pc}: {Count} finished job result(s) belong to another console and wait for it", Name, kept.Count);
                }
            }

            try
            {
                while (await call.ResponseStream.MoveNext(token))
                {
                    await HandleAsync(call.ResponseStream.Current, outgoing.Writer, token);
                }
            }
            finally
            {
                var hadVideo = false;
                lock (_gateLock)
                {
                    _outgoing = null;
                    _client = null;
                    _video = null;
                    hadVideo = _videoControl is not null;
                    _videoControl = null;
                }

                outgoing.Writer.TryComplete();
                await Task.WhenAll(SwallowAsync(writer), SwallowAsync(heartbeats), SwallowAsync(renewal), SwallowAsync(video.DisposeAsync().AsTask()));

                // The producer must not keep capturing for a console that is gone.
                if (hadVideo)
                {
                    VideoControlChanged?.Invoke(null);
                }
            }

            return true;
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is RpcException or OperationCanceledException or IOException)
        {
            lock (_gateLock)
            {
                endedBy = _pendingReason ?? "this PC closed the link";
                _pendingReason = null;
            }

            return linked;
        }
        catch (RpcException ex)
        {
            endedBy = channel.Refusal ?? (ex.Status.Detail.Length > 0 ? ex.Status.Detail : ex.StatusCode.ToString());
            if (!linked)
            {
                Report(endedBy);
            }

            return linked;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            endedBy = channel.Refusal ?? $"link to {endpoint} failed: {ex.Message}";
            if (!linked)
            {
                Report(endedBy);
            }

            return linked;
        }
        finally
        {
            if (linked)
            {
                _previousInstanceId = LinkedInstanceId;
                LinkedInstanceId = null;
                LinkedInstanceName = null;
                LinkedEndpoint = null;
                LinkedConsoleAccess = ConsoleAccess.Unknown;
                AnnouncedConsoleAccess = ConsoleAccess.Unknown;
                _linkedConsoleSerial = null;
                lock (_gateLock)
                {
                    _boundInstanceId = null;
                }

                _gate.Unlinked();
                _log.LogInformation("{Pc}: unlinked — {Reason}", Name, endedBy);
                Unlinked?.Invoke(endedBy);
            }
        }
    }

    /// <summary>
    /// The identity everything on this link is bound to: the instance id in the SAN URI of the
    /// console leaf the TLS handshake validated (<c>labcontrol://&lt;lab&gt;/console/&lt;instance&gt;</c>).
    /// <c>Welcome.instance_id</c> is only what the console <i>says</i> about itself, and a peer
    /// can say anything, so it is never used: not for job ownership, not for the take-over gate,
    /// not for <c>LastInstanceId</c>. Returns <c>null</c> when the validated identity is missing
    /// — then this PC has no console to be bound to and the link is not kept.
    /// </summary>
    private string? BindInstance(LabName? peer, Welcome welcome)
    {
        var certified = peer?.Role == LabRole.Console && peer.Id.Length > 0 ? peer.Id : null;
        if (certified is null)
        {
            _log.LogWarning("{Pc}: the console presented no validated console identity; the link is refused", Name);
            Report("the console presented no validated console identity");
            return null;
        }

        if (welcome.InstanceId.Length > 0 && !JobLedger.SameInstance(certified, welcome.InstanceId))
        {
            _log.LogWarning("{Pc}: the console's certificate names instance {Certified} but its Welcome says {Claimed}; the certificate is the one that counts", Name, certified, welcome.InstanceId);
            Report(Event.Types.Severity.Warning, "console.instance_mismatch",
                $"The console's certificate names instance {certified} but its Welcome says {welcome.InstanceId}; the certificate is the one this PC uses.");
        }

        lock (_gateLock)
        {
            _boundInstanceId = certified;
        }

        return certified;
    }

    /// <param name="instanceId">
    /// The instance id from the validated console certificate (D-57 item 4). Everything that
    /// outlives the message — the take-over gate, <c>LastInstanceId</c>, the
    /// <c>previous_instance_id</c> of the next <c>Hello</c> — uses this, never the claim in
    /// <c>Welcome</c>. The name is cosmetic and may come from the message.
    /// </param>
    private void OnWelcome(Welcome welcome, string instanceId, string endpoint)
    {
        var now = _options.Clock();
        ClockSkew = DateTimeOffset.FromUnixTimeSeconds(welcome.ServerTimeUnix) - now;

        LinkedInstanceId = instanceId;
        LinkedInstanceName = welcome.InstanceName;
        LinkedEndpoint = endpoint;
        AnnouncedConsoleAccess = FromWelcome(welcome.ConsoleAccess);
        State = LinkState.Linked;

        // The field is informational (D-58): the certificate has already decided what this
        // console may do, and a disagreement is worth a line in the log and nothing more.
        if (AnnouncedConsoleAccess != ConsoleAccess.Unknown && AnnouncedConsoleAccess != LinkedConsoleAccess)
        {
            _log.LogWarning("{Pc}: {Console} announces {Announced} access but its certificate says {Certified}; the certificate is the one that counts",
                Name, welcome.InstanceName, AnnouncedConsoleAccess, LinkedConsoleAccess);
        }
        _lastRefusal = string.Empty;
        _pinnedBackoff.Reset();
        _gate.Linked(instanceId, endpoint, now);

        if (!string.Equals(_store.Config.LastInstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
        {
            _store.Config.LastInstanceId = instanceId;
            _store.SaveConfig();
        }

        _log.LogInformation("{Pc}: linked to {Console} ({Instance}) at {Endpoint}", Name, welcome.InstanceName, instanceId, endpoint);
        Linked?.Invoke(instanceId, welcome.InstanceName);
    }

    /// <summary>The informational <c>Welcome</c> field in this build's terms; anything unknown stays unknown.</summary>
    private static ConsoleAccess FromWelcome(Welcome.Types.ConsoleAccess announced) => announced switch
    {
        Welcome.Types.ConsoleAccess.Administrator => ConsoleAccess.Administrator,
        Welcome.Types.ConsoleAccess.Teacher => ConsoleAccess.Teacher,
        _ => ConsoleAccess.Unknown,
    };

    private async Task HandleAsync(ConsoleMessage message, ChannelWriter<AgentMessage> outgoing, CancellationToken token)
    {
        switch (message.PayloadCase)
        {
            case ConsoleMessage.PayloadOneofCase.Ping:
                await outgoing.WriteAsync(new AgentMessage
                {
                    Pong = new Pong { PingSentAtUnix = message.Ping.SentAtUnix, SentAtUnix = _options.Clock().ToUnixTimeSeconds() },
                }, token);
                break;

            case ConsoleMessage.PayloadOneofCase.Job:
                await AdmitJobAsync(message.Job, outgoing, token);
                break;

            case ConsoleMessage.PayloadOneofCase.Revocation:
                MergeRevocations(message.Revocation.Entries);
                // Delivery is confirmed, never assumed (D-56 item 6): every Revocation is
                // answered with what this PC holds now, so the console can show a pending list.
                var held = new RevocationState();
                held.Entries.AddRange(_revocations.Entries);
                await outgoing.WriteAsync(new AgentMessage { RevocationState = held }, token);
                LeaveIfConsoleRevoked();
                break;

            case ConsoleMessage.PayloadOneofCase.Welcome:
                break;

            case ConsoleMessage.PayloadOneofCase.VideoControl:
                await ApplyVideoControlAsync(message.VideoControl);
                break;

            case ConsoleMessage.PayloadOneofCase.Input:
                DeliverInput(message.Input);
                break;

            default:
                // A message this build does not know is logged and ignored, never fatal
                // (PROTOCOL, "Versioning").
                _log.LogDebug("{Pc}: ignoring {Kind} — not handled by this agent", Name, message.PayloadCase);
                break;
        }
    }

    /// <summary>
    /// The console's word on video (PROTOCOL "Video"): remembered, so a producer can read it
    /// at any time; a stop closes the current call; every change is announced.
    /// </summary>
    private async Task ApplyVideoControlAsync(VideoControl control)
    {
        VideoUplink? video;
        lock (_gateLock)
        {
            _videoControl = control;
            video = _video;
        }

        _log.LogDebug("{Pc}: video {State} {Mode} {Fps} fps q{Quality}{Keyframe}", Name,
            control.Active ? "on" : "off", control.Mode, control.FramesPerSecond, control.Quality, control.RequestKeyframe ? " keyframe" : string.Empty);

        if (!control.Active && video is not null)
        {
            await video.StopAsync();
        }

        try
        {
            VideoControlChanged?.Invoke(control);
        }
        catch (Exception ex)
        {
            // A producer's failure to react is its own problem; the link stays up.
            _log.LogWarning(ex, "{Pc}: a video producer failed on a control change: {Message}", Name, ex.Message);
        }
    }

    /// <summary>Input is fire-and-forget: a handler's failure is logged, never the link's problem.</summary>
    private void DeliverInput(Input input)
    {
        var handler = InputReceived;
        if (handler is null)
        {
            _log.LogDebug("{Pc}: input {Kind} arrived but nothing here injects input", Name, input.Kind);
            return;
        }

        try
        {
            handler(input);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Pc}: the input handler failed on {Kind}: {Message}", Name, input.Kind, ex.Message);
        }
    }

    private void MergeRevocations(IEnumerable<RevocationEntry> entries)
    {
        var added = _revocations.Merge(_store.Authority, entries);
        if (added.Count == 0)
        {
            return;
        }

        foreach (var entry in added)
        {
            _store.Config.Revocations.Add(RevocationRecord.From(entry));
            _log.LogInformation("{Pc}: learned that certificate {Serial} is revoked ({Reason})", Name, entry.Serial, entry.Reason);
        }

        _store.SaveConfig();
    }

    /// <summary>
    /// A revocation just merged may name the very console this PC is linked to — its leaf
    /// serial, or its <c>instance:</c> id (D-56 item 6), pushed by that console itself after a
    /// lab file taught it, or carried in from another console. A withdrawn console keeps no
    /// live link: the PC leaves at once and refuses the next dial like any other agent would.
    /// </summary>
    private void LeaveIfConsoleRevoked()
    {
        var instanceId = LinkedInstanceId;
        var serial = _linkedConsoleSerial;
        if (instanceId is null)
        {
            return;
        }

        string? why = null;
        if (serial is not null && _revocations.IsRevoked(serial))
        {
            why = $"the linked console's certificate {serial} is revoked";
        }
        else if (_revocations.IsRevoked(LabCertificates.InstanceSerial(instanceId)))
        {
            why = $"the linked console's access ({LinkedInstanceName ?? instanceId}) was withdrawn";
        }

        if (why is null)
        {
            return;
        }

        Report(Event.Types.Severity.Warning, "console.revoked", $"Leaving {LinkedInstanceName ?? instanceId}: {why}.");
        Disconnect(why);
    }

    // ------------------------------------------------------------------ jobs

    private async Task AdmitJobAsync(Job job, ChannelWriter<AgentMessage> outgoing, CancellationToken token)
    {
        if (job.Kind is Job.Types.Kind.SelfUpdate or Job.Types.Kind.Rekey && LinkedConsoleAccess != ConsoleAccess.Administrator)
        {
            // Refused by role before any manifest is pulled (D-56 item 5, PROTOCOL "M5 additions"
            // item 4): only OU=LabControl Console — every leaf minted before M5 carries it — may
            // sign an update or re-key; a teacher leaf and an unknown OU may not. Not through
            // the ledger: an administrator console re-sending the same job id later must be
            // admitted, not answered from a cache of this refusal.
            var refusal = LinkedConsoleAccess == ConsoleAccess.Teacher
                ? "refused: this console has teacher access"
                : "refused: this console's certificate carries no known access level";
            _log.LogWarning("{Pc}: job {Job} ({Kind}) refused — {Reason}", Name, job.Id, job.Kind, refusal);
            Report(Event.Types.Severity.Warning, "job.refused_by_role", $"{job.Kind} job {job.Id} {refusal}; only an administrator console can send it.");
            await outgoing.WriteAsync(new AgentMessage { JobResult = new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = refusal } }, token);
            return;
        }

        string? instanceId;
        lock (_gateLock)
        {
            instanceId = _boundInstanceId;
        }

        if (instanceId is null)
        {
            // The link is already coming down; the console will re-send after the next Welcome.
            _log.LogDebug("{Pc}: job {Job} arrived on a link that is no longer bound to a console; ignored", Name, job.Id);
            return;
        }

        var admission = _ledger.Admit(job, instanceId);

        if (admission.BelongsToAnotherInstance)
        {
            // Another console delivered this id (D-57 item 4). Its result — finished or still
            // to come — is that console's alone: not revealed, not run a second time, and not
            // silently swallowed either, so this console's row closes with the reason.
            const string refusal = "refused: this job was delivered by another console; its result is kept for that console";
            _log.LogWarning("{Pc}: job {Job} was delivered by instance {Other}, not {This}; refused", Name, job.Id, admission.OtherInstanceId, instanceId);
            Report(Event.Types.Severity.Warning, "job.other_instance", $"Job {job.Id} was delivered by another console; its result is kept for that console.");
            await outgoing.WriteAsync(new AgentMessage { JobResult = new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = refusal } }, token);
            return;
        }

        if (admission.CachedResult is { } cached)
        {
            _log.LogInformation("{Pc}: job {Job} was re-sent; answering with the cached result", Name, job.Id);
            await outgoing.WriteAsync(new AgentMessage { JobResult = cached }, token);
            return;
        }

        if (admission.DuplicateOfRunning)
        {
            _log.LogDebug("{Pc}: job {Job} is already running", Name, job.Id);
            return;
        }

        // The job runs on the PC, not on the link: a cable pulled mid-script must not kill
        // the script, and its result must reach the console when the link is back (D-32).
        // Progress lines go through whatever link is up at the moment, or nowhere; the
        // result is remembered by the ledger and queued for the next Welcome.
        var stopping = _stopping.Token;
        _ = Task.Run(async () =>
        {
            JobResult result;
            try
            {
                result = await _behaviour.RunJobAsync(job, progress => SendProgressAsync(progress, instanceId), stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = $"The agent failed to run this job: {ex.Message}" };
            }
            catch (OperationCanceledException)
            {
                // The agent is stopping; the console will re-send after the restart, and the
                // re-sent copy must be admitted, not taken for a duplicate of this one (D-33).
                _ledger.Forget(job.Id);
                return;
            }

            result.JobId = job.Id;
            SendResult(_ledger.Complete(result, instanceId));
        }, CancellationToken.None);
    }

    /// <summary>
    /// A line of a job's output goes to the console that sent the job, or nowhere (D-57 item 4).
    /// Progress is not durable — while the delivering console is away the lines are dropped,
    /// and only the <see cref="JobResult"/> survives to be handed over when it returns — but a
    /// teacher who has taken the room over must not see the previous teacher's output either.
    /// </summary>
    private Task SendProgressAsync(JobProgress progress, string instanceId)
    {
        lock (_gateLock)
        {
            if (JobLedger.SameInstance(_boundInstanceId, instanceId))
            {
                _outgoing?.TryWrite(new AgentMessage { JobProgress = progress });
            }
        }

        return Task.CompletedTask;
    }

    private void SendResult(JobLedgerEntry finished)
    {
        var evicted = 0;
        lock (_gateLock)
        {
            // Only the console that delivered the job may receive its result (D-57 item 4):
            // a link to any other instance is treated like no link at all.
            if (_outgoing is { } outgoing && JobLedger.SameInstance(_boundInstanceId, finished.InstanceId)
                && outgoing.TryWrite(new AgentMessage { JobResult = finished.Result }))
            {
                return;
            }

            _pendingResults.Add(finished);
            // The wait is bounded and lives only in this process: a console that never comes
            // back is never told, and nothing here survives an agent restart (D-57 item 4).
            // Dropping the oldest silently would be the one loss nobody could explain, so it
            // is an event on the very next link.
            while (_pendingResults.Count > _options.MaxPendingResults)
            {
                _pendingResults.RemoveAt(0);
                evicted++;
            }

            _log.LogInformation("{Pc}: job {Job} finished while not linked to the console that sent it; its result waits for that console", Name, finished.Result.JobId);
        }

        if (evicted > 0)
        {
            _log.LogWarning("{Pc}: {Count} oldest job result(s) were dropped; {Max} results are kept for consoles that have not returned", Name, evicted, _options.MaxPendingResults);
            Report(Event.Types.Severity.Warning, "job.result_dropped",
                $"This PC keeps at most {_options.MaxPendingResults} finished job results for consoles that have not come back; {evicted} of the oldest were dropped and cannot be shown any more.");
        }
    }

    /// <summary>The instance id a job was delivered by, as the ledger remembers it (tests and diagnostics).</summary>
    public string? DeliveringInstanceOf(string jobId) => _ledger.DeliveringInstanceOf(jobId);

    /// <summary>Finished results still waiting for the console that delivered their jobs.</summary>
    public int PendingResults
    {
        get
        {
            lock (_gateLock)
            {
                return _pendingResults.Count;
            }
        }
    }

    // ------------------------------------------------------------------ files

    /// <summary>
    /// Pulls and hashes an offered file, retaining verified chunk boundaries across link
    /// reconnects (D-41). The destination remains open and must be discarded on failure.
    /// Resume lasts only for this call; it does not persist partial files across restarts.
    /// </summary>
    public async Task<long> PullFileAsync(string reference, string expectedSha256, Stream destination, CancellationToken token, Func<long, Task>? progress = null)
    {
        lock (_gateLock)
        {
            if (_client is null)
            {
                throw new FilePullException($"cannot pull '{reference}': this PC is not linked to a console right now");
            }
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        inactivity.CancelAfter(Defaults.FileChunkTimeout);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        var sawLast = false;
        string? declaredHash = null;

        try
        {
            while (!sawLast)
            {
                inactivity.Token.ThrowIfCancellationRequested();
                AgentService.AgentServiceClient? client;
                CancellationToken sessionToken;
                lock (_gateLock)
                {
                    client = _client;
                    sessionToken = _session?.Token ?? new CancellationToken(canceled: true);
                }

                if (client is null || sessionToken.IsCancellationRequested)
                {
                    await Task.Delay(Defaults.FileRetryDelay, inactivity.Token);
                    continue;
                }

                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(inactivity.Token, sessionToken);
                try
                {
                    using var call = client.PullFile(new FileRequest { Reference = reference, Offset = received }, cancellationToken: attempt.Token);
                    while (await call.ResponseStream.MoveNext(attempt.Token))
                    {
                        var chunk = call.ResponseStream.Current;
                        if (!string.Equals(chunk.Reference, reference, StringComparison.OrdinalIgnoreCase) || chunk.Offset != received)
                        {
                            throw new FilePullException($"the console sent '{reference}' with the wrong reference or offset (expected {received}, got {chunk.Offset})");
                        }

                        var data = chunk.Data.Memory;
                        if (data.Length > Defaults.FileChunkBytes || (data.Length == 0 && !chunk.Last))
                        {
                            throw new FilePullException($"the console sent an invalid chunk for '{reference}'");
                        }

                        // A link ending must not cancel a local write halfway through a chunk.
                        // Only a completed write advances the resume offset and incremental hash.
                        await destination.WriteAsync(data, lifetime.Token);
                        hasher.AppendData(data.Span);
                        received += data.Length;
                        if (data.Length > 0)
                        {
                            inactivity.CancelAfter(Defaults.FileChunkTimeout);
                            if (progress is not null) await progress(received);
                        }

                        if (chunk.Last)
                        {
                            if (chunk.TotalBytes != received || !Files.FileHash.LooksLikeSha256(chunk.Sha256))
                            {
                                throw new FilePullException($"the console sent invalid final size or hash for '{reference}' after {received} bytes");
                            }

                            declaredHash = chunk.Sha256;
                            sawLast = true;
                            break;
                        }
                    }
                    // A stream ending without the terminal chunk is also resumable.
                }
                catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.Cancelled or StatusCode.DeadlineExceeded)
                {
                    // The shared inactivity deadline bounds all attempts, including reconnect waits.
                }
                catch (OperationCanceledException) when (sessionToken.IsCancellationRequested && !inactivity.IsCancellationRequested)
                {
                }
                catch (ObjectDisposedException) when (sessionToken.IsCancellationRequested)
                {
                    // The channel can be disposed between taking the client snapshot and opening the call.
                }
                catch (RpcException ex)
                {
                    throw new FilePullException($"pulling '{reference}' failed: {(ex.Status.Detail.Length > 0 ? ex.Status.Detail : ex.StatusCode.ToString())}");
                }

                if (!sawLast)
                {
                    await Task.Delay(Defaults.FileRetryDelay, inactivity.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            throw new FilePullException($"the console stopped sending '{reference}' after {received} bytes (no progress for {Defaults.FileChunkTimeout.TotalSeconds:0} s, including reconnects)");
        }

        lifetime.Token.ThrowIfCancellationRequested();
        await destination.FlushAsync(lifetime.Token);
        var actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
        if (!Files.FileHash.Matches(expectedSha256, actual) || !Files.FileHash.Matches(declaredHash!, actual))
        {
            throw new FilePullException($"'{reference}' failed its hash check. The file was not used.");
        }

        return received;
    }

    // ------------------------------------------------------------------ loops

    private static async Task WriteLoopAsync(IClientStreamWriter<AgentMessage> stream, ChannelReader<AgentMessage> reader, CancellationToken token)
    {
        await foreach (var message in reader.ReadAllAsync(token))
        {
            await stream.WriteAsync(message, token);
        }

        // Reached only when the channel was completed deliberately — the PC leaving for a
        // console that took over (D-58). Half-closing tells the console this is the end of
        // what the PC had to say, so the departure notice ahead of it is delivered instead
        // of being lost with a reset stream.
        await stream.CompleteAsync();
    }

    private async Task HeartbeatLoopAsync(ChannelWriter<AgentMessage> outgoing, CancellationToken token)
    {
        using var timer = new PeriodicTimer(Defaults.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(token))
        {
            await outgoing.WriteAsync(new AgentMessage
            {
                Heartbeat = new Heartbeat { SentAtUnix = _options.Clock().ToUnixTimeSeconds() },
            }, token);
        }
    }

    /// <summary>
    /// Renewal (D-25): once the certificate is inside its lead time, ask after every
    /// <c>Hello</c> and keep asking on the reconnect backoff while the lab key is locked. A
    /// new certificate is installed and the link re-opened with it, so the console sees the
    /// new serial straight away.
    /// </summary>
    private async Task RenewalLoopAsync(AgentService.AgentServiceClient client, X509Certificate2 current, CancellationToken token)
    {
        var backoff = new ReconnectBackoff(Defaults.ReconnectDelayMin, Defaults.RenewalRetryMax);
        var refusedBefore = false;

        while (!token.IsCancellationRequested && LabCertificates.NeedsRenewal(current, _options.Clock()))
        {
            var key = LabCertificates.CreateKey();
            try
            {
                var request = new RenewRequest { Csr = ByteString.CopyFrom(LabCertificates.CreateSigningRequest(key, Name)) };
                var response = await client.RenewAsync(request, deadline: DateTime.UtcNow + Defaults.HeartbeatTimeout, cancellationToken: token);

                if (response.AgentCertificate.Length == 0)
                {
                    if (!refusedBefore)
                    {
                        _log.LogInformation("{Pc}: renewal refused for now — {Reason}", Name, response.Refusal);
                        refusedBefore = true;
                    }

                    key.Dispose();
                    await Task.Delay(backoff.Next(), token);
                    continue;
                }

                using var issued = X509CertificateLoader.LoadCertificate(response.AgentCertificate.ToByteArray());
                _store.InstallCertificate(key, issued);
                _log.LogInformation("{Pc}: certificate renewed; new serial {Serial}, reconnecting with it", Name, LabCertificates.SerialOf(issued));

                // Reconnect at once with the new certificate, to the same console.
                Redial(LinkedEndpoint!, "reconnecting with the renewed certificate");
                return;
            }
            catch (RpcException ex) when (!token.IsCancellationRequested)
            {
                key.Dispose();
                _log.LogWarning("{Pc}: renewal call failed: {Detail}", Name, ex.Status.Detail);
                try
                {
                    await Task.Delay(backoff.Next(), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                key.Dispose();
                return;
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private X509Certificate2 CertificateWithKey(X509Certificate2 certificate) =>
        certificate.HasPrivateKey ? certificate : certificate.CopyWithPrivateKey(_store.Key);

    private void Report(string refusal)
    {
        if (string.Equals(refusal, _lastRefusal, StringComparison.Ordinal))
        {
            _log.LogDebug("{Pc}: still refused — {Reason}", Name, refusal);
            return;
        }

        _lastRefusal = refusal;
        _log.LogWarning("{Pc}: {Reason}", Name, refusal);
        Refused?.Invoke(refusal);
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or RpcException or IOException or ChannelClosedException or InvalidOperationException)
        {
        }
    }

    private static bool TrySplit(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port))
        {
            return false;
        }

        host = endpoint[..colon].Trim('[', ']');
        return IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }
}
