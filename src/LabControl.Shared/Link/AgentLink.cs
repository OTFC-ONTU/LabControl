using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
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
}

/// <summary>
/// One PC's whole relationship with the console, shared by the real agent and
/// <c>FakeAgent</c> (ROADMAP M1). It hears beacons, dials, enrols if it has no
/// certificate yet, holds the <c>Link</c> stream, heartbeats, runs jobs exactly once,
/// renews its certificate when due, follows a <i>Take over</i>, and reconnects with
/// backoff when any of that ends. Everything it needs from the outside is behind
/// <see cref="IAgentStore"/> and <see cref="IAgentBehaviour"/>.
/// </summary>
public sealed class AgentLink : IAsyncDisposable
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
    private readonly Queue<JobResult> _pendingResults = new();
    private ChannelWriter<AgentMessage>? _outgoing;
    private AgentService.AgentServiceClient? _client;
    private SessionState? _latestSessionState;

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
        _gate = new BeaconGate(store.Authority, store.Config.LabId);

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

    /// <summary>Hands the PC a beacon datagram, from whatever socket the host listens on.</summary>
    public void OfferBeacon(ReadOnlyMemory<byte> datagram)
    {
        if (State == LinkState.Stopped || _stopping.IsCancellationRequested)
        {
            return;
        }

        var verdict = _gate.Consider(datagram.Span, _options.Clock());

        switch (verdict.Action)
        {
            case BeaconAction.Dial:
                _log.LogDebug("{Pc}: {Reason}", Name, verdict.Reason);
                _dials.Writer.TryWrite(verdict.Beacon!.Endpoint);
                break;

            case BeaconAction.TakeOver:
                _log.LogInformation("{Pc}: {Reason}; leaving {Current}", Name, verdict.Reason, LinkedInstanceName);
                Redial(verdict.Beacon!.Endpoint, "another console took over the lab");
                break;

            default:
                _log.LogTrace("{Pc}: beacon ignored — {Reason}", Name, verdict.Reason);
                break;
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
            OnWelcome(welcome, endpoint);
            linked = true;

            var outgoing = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions { SingleReader = true });
            var writer = WriteLoopAsync(call.RequestStream, outgoing.Reader, token);
            var heartbeats = HeartbeatLoopAsync(outgoing.Writer, token);
            var renewal = RenewalLoopAsync(client, certificate, token);

            lock (_gateLock)
            {
                _outgoing = outgoing.Writer;
                _client = client;
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
                // the cache too, so a result is never lost and never runs twice.
                while (_pendingResults.TryDequeue(out var finished))
                {
                    outgoing.Writer.TryWrite(new AgentMessage { JobResult = finished });
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
                lock (_gateLock)
                {
                    _outgoing = null;
                    _client = null;
                }

                outgoing.Writer.TryComplete();
                await Task.WhenAll(SwallowAsync(writer), SwallowAsync(heartbeats), SwallowAsync(renewal));
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
                _gate.Unlinked();
                _log.LogInformation("{Pc}: unlinked — {Reason}", Name, endedBy);
                Unlinked?.Invoke(endedBy);
            }
        }
    }

    private void OnWelcome(Welcome welcome, string endpoint)
    {
        var now = _options.Clock();
        ClockSkew = DateTimeOffset.FromUnixTimeSeconds(welcome.ServerTimeUnix) - now;

        LinkedInstanceId = welcome.InstanceId;
        LinkedInstanceName = welcome.InstanceName;
        LinkedEndpoint = endpoint;
        State = LinkState.Linked;
        _lastRefusal = string.Empty;
        _pinnedBackoff.Reset();
        _gate.Linked(welcome.InstanceId, endpoint, now);

        if (!string.Equals(_store.Config.LastInstanceId, welcome.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            _store.Config.LastInstanceId = welcome.InstanceId;
            _store.SaveConfig();
        }

        _log.LogInformation("{Pc}: linked to {Console} ({Instance}) at {Endpoint}", Name, welcome.InstanceName, welcome.InstanceId, endpoint);
        Linked?.Invoke(welcome.InstanceId, welcome.InstanceName);
    }

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
                break;

            case ConsoleMessage.PayloadOneofCase.Welcome:
                break;

            default:
                // A message this build does not know is logged and ignored, never fatal
                // (PROTOCOL, "Versioning").
                _log.LogDebug("{Pc}: ignoring {Kind} — not handled by this agent", Name, message.PayloadCase);
                break;
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

    // ------------------------------------------------------------------ jobs

    private async Task AdmitJobAsync(Job job, ChannelWriter<AgentMessage> outgoing, CancellationToken token)
    {
        var admission = _ledger.Admit(job);

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
                result = await _behaviour.RunJobAsync(job, progress => SendProgressAsync(progress), stopping);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = $"The agent failed to run this job: {ex.Message}" };
            }
            catch (OperationCanceledException)
            {
                // The agent is stopping; the console will re-send after the restart.
                return;
            }

            result.JobId = job.Id;
            _ledger.Complete(result);
            SendResult(result);
        }, CancellationToken.None);
    }

    private Task SendProgressAsync(JobProgress progress)
    {
        lock (_gateLock)
        {
            _outgoing?.TryWrite(new AgentMessage { JobProgress = progress });
        }

        return Task.CompletedTask;
    }

    private void SendResult(JobResult result)
    {
        lock (_gateLock)
        {
            if (_outgoing is { } outgoing && outgoing.TryWrite(new AgentMessage { JobResult = result }))
            {
                return;
            }

            _pendingResults.Enqueue(result);
            _log.LogInformation("{Pc}: job {Job} finished while unlinked; its result waits for the next link", Name, result.JobId);
        }
    }

    // ------------------------------------------------------------------ files

    /// <summary>
    /// Pulls a file the console offered (PROTOCOL, <i>Files</i>; D-31) into
    /// <paramref name="destination"/> and verifies its SHA-256 against
    /// <paramref name="expectedSha256"/>. The minimal M2 form: no resume — a pull that
    /// breaks starts again from the beginning. Throws <see cref="FilePullException"/> with
    /// the reason in plain language; the caller turns that into a failed job result.
    /// </summary>
    public async Task<long> PullFileAsync(string reference, string expectedSha256, Stream destination, CancellationToken token)
    {
        AgentService.AgentServiceClient? client;
        lock (_gateLock)
        {
            client = _client;
        }

        if (client is null)
        {
            throw new FilePullException($"cannot pull '{reference}': this PC is not linked to a console right now");
        }

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        var sawLast = false;
        string? declaredHash = null;

        try
        {
            using var call = client.PullFile(new FileRequest { Reference = reference, Offset = 0 }, cancellationToken: token);
            while (true)
            {
                bool more;
                using (var chunkTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    chunkTimeout.CancelAfter(Defaults.FileChunkTimeout);
                    try
                    {
                        more = await call.ResponseStream.MoveNext(chunkTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new FilePullException($"the console stopped sending '{reference}' after {received} bytes (no chunk for {Defaults.FileChunkTimeout.TotalSeconds:0} s)");
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && !token.IsCancellationRequested)
                    {
                        throw new FilePullException($"the console stopped sending '{reference}' after {received} bytes (no chunk for {Defaults.FileChunkTimeout.TotalSeconds:0} s)");
                    }
                }

                if (!more)
                {
                    break;
                }

                var chunk = call.ResponseStream.Current;
                if (chunk.Offset != received)
                {
                    throw new FilePullException($"the console sent '{reference}' out of order (expected offset {received}, got {chunk.Offset})");
                }

                var data = chunk.Data.Memory;
                await destination.WriteAsync(data, token);
                hasher.AppendData(data.Span);
                received += data.Length;

                if (chunk.Last)
                {
                    sawLast = true;
                    declaredHash = chunk.Sha256;
                    if (chunk.TotalBytes > 0 && chunk.TotalBytes != received)
                    {
                        throw new FilePullException($"'{reference}' is {chunk.TotalBytes} bytes but only {received} arrived");
                    }

                    break;
                }
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            throw new FilePullException($"the console has no file '{reference}' to offer: {ex.Status.Detail}");
        }
        catch (RpcException ex) when (!token.IsCancellationRequested)
        {
            throw new FilePullException($"pulling '{reference}' failed: {(ex.Status.Detail.Length > 0 ? ex.Status.Detail : ex.StatusCode.ToString())}");
        }

        if (!sawLast)
        {
            throw new FilePullException($"the console closed the stream for '{reference}' after {received} bytes without finishing it");
        }

        await destination.FlushAsync(token);
        var actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
        if (!Files.FileHash.Matches(expectedSha256, actual))
        {
            throw new FilePullException($"'{reference}' failed its hash check: expected {expectedSha256}, got {actual}. The file was not used.");
        }

        if (declaredHash is { Length: > 0 } && !Files.FileHash.Matches(declaredHash, actual))
        {
            throw new FilePullException($"the console's own hash for '{reference}' ({declaredHash}) does not match what it sent ({actual}). The file was not used.");
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
