using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace LabControl.Shared.Internet;

/// <summary>What a <see cref="LoopbackResolver"/> listens on, asks, allows and tells.</summary>
/// <param name="Listen">Where to answer: production is <see cref="ProductionListen"/>, tests use <c>127.0.0.1:0</c>.</param>
/// <param name="Upstream">The DNS servers the NIC used before the whitelist, asked in order.</param>
/// <param name="AllowedPatterns">The whitelist, already normalized by <see cref="HostPattern.TryNormalize"/>.</param>
/// <param name="Admit">
/// Called with the A/AAAA addresses of an allowed answer <b>before</b> the reply goes out — the
/// Windows side opens them in the firewall there and then, so the client's first connection
/// already finds them open. <c>false</c> (or an exception) and the client gets SERVFAIL.
/// </param>
/// <param name="Logger">Where the resolver reports its own trouble; query names only at debug level.</param>
public sealed record LoopbackResolverOptions(
    IReadOnlyList<IPEndPoint> Listen,
    IReadOnlyList<IPEndPoint> Upstream,
    IReadOnlyList<string> AllowedPatterns,
    Func<IReadOnlyList<IPAddress>, bool> Admit,
    ILogger Logger)
{
    /// <summary>The time refusals are stamped with; the wall clock when <c>null</c>.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }

    /// <summary>How long one upstream server gets; <see cref="Defaults.WhitelistUpstreamTimeout"/> when <c>null</c>.</summary>
    public TimeSpan? UpstreamTimeout { get; init; }

    /// <summary><c>127.0.0.1:53</c> and <c>[::1]:53</c> (<see cref="Defaults.WhitelistResolverAddress"/>, <see cref="Defaults.WhitelistResolverAddressV6"/>).</summary>
    public static IReadOnlyList<IPEndPoint> ProductionListen { get; } =
    [
        new(IPAddress.Parse(Defaults.WhitelistResolverAddress), Defaults.WhitelistResolverPort),
        new(IPAddress.Parse(Defaults.WhitelistResolverAddressV6), Defaults.WhitelistResolverPort),
    ];
}

/// <summary>
/// The whitelist's resolver on the loopback interface (M6 portion 3, ARCHITECTURE §6.2, D-74).
/// While a whitelist is active the NIC's DNS points here; for an allowed name the query goes
/// to the previous upstream servers, the answer's A/AAAA addresses are handed to
/// <see cref="LoopbackResolverOptions.Admit"/> (the firewall's allow rule) and only then is the
/// answer relayed; any other name is answered REFUSED and remembered in <see cref="Refused"/>
/// for the console. HTTPS/SVCB queries for an allowed name get an empty answer, so a browser
/// falls back to A/AAAA and never connects to a hint address the firewall has not seen.
/// Reverse lookups pass through untouched. Pure managed sockets, so it runs and is tested on
/// any platform; nothing that arrives on a socket can throw out of a receive loop.
/// </summary>
public sealed class LoopbackResolver : IAsyncDisposable
{
    /// <summary>Stable reasons <see cref="TryStart"/> fails.</summary>
    public static class Problems
    {
        /// <summary>Something else already answers on a listen port (on Windows also an exclusive bind's access denial).</summary>
        public const string PortInUse = "resolver.port_in_use";

        /// <summary>A listen endpoint could not be bound for another reason (no such address, no IPv6, …).</summary>
        public const string BindFailed = "resolver.bind_failed";

        /// <summary>No listen endpoint was given.</summary>
        public const string NoListen = "resolver.no_listen";

        /// <summary><see cref="TryStart"/> was called on a resolver that is already running.</summary>
        public const string AlreadyStarted = "resolver.already_started";

        /// <summary>The resolver was disposed.</summary>
        public const string Disposed = "resolver.disposed";
    }

    /// <summary>A TCP client that sends nothing for this long is hung up on.</summary>
    private static readonly TimeSpan TcpIdleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long <see cref="DisposeAsync"/> waits for the loops and queries to finish.</summary>
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(3);

    private const int TcpBacklog = 64;
    private const int EphemeralBindAttempts = 8;

    // Windows' SIO_UDP_CONNRESET: without it, an ICMP "port unreachable" for an earlier reply
    // fails the next ReceiveFrom on the listening socket with WSAECONNRESET.
    private const int SioUdpConnReset = -1744830452;

    // Noise a Windows PC asks on its own; refused, but not worth a line in the console's list.
    private static readonly string[] NoisePatterns = ["*.local", "*.lan", "*.home.arpa", "*.msftconnecttest.com", "*.msftncsi.com"];
    private static readonly string[] ReversePatterns = ["*.in-addr.arpa", "*.ip6.arpa"];

    private readonly object _gate = new();
    private readonly LoopbackResolverOptions _options;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _inFlight = new(Defaults.WhitelistMaxInFlight, Defaults.WhitelistMaxInFlight);
    private readonly SemaphoreSlim _connections = new(TcpConnectionLimit, TcpConnectionLimit);
    private readonly List<Socket> _sockets = [];
    private readonly List<Task> _loops = [];
    private readonly ConcurrentDictionary<long, Task> _work = new();
    private IReadOnlyList<IPEndPoint> _bound = [];
    private string[] _allowed;
    private IPEndPoint[] _upstream;
    private long _nextWork;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>TCP connections held open at once: a quarter of the query limit, so TCP clients cannot starve UDP.</summary>
    private static int TcpConnectionLimit => Math.Max(1, Defaults.WhitelistMaxInFlight / 4);

    public LoopbackResolver(LoopbackResolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Listen);
        ArgumentNullException.ThrowIfNull(options.Admit);
        ArgumentNullException.ThrowIfNull(options.Logger);

        _options = options;
        _log = options.Logger;
        _clock = options.Clock ?? (() => DateTimeOffset.UtcNow);
        _timeout = options.UpstreamTimeout ?? Defaults.WhitelistUpstreamTimeout;
        _allowed = [.. options.AllowedPatterns ?? []];
        _upstream = [.. options.Upstream ?? []];
    }

    /// <summary>The names refused lately, for the console.</summary>
    public RefusedNames Refused { get; } = new();

    /// <summary>Whether the resolver is bound and answering.</summary>
    public bool Running => _running;

    /// <summary>The endpoints actually bound (with the real port when <c>0</c> was asked), one per listen endpoint.</summary>
    public IReadOnlyList<IPEndPoint> BoundEndpoints
    {
        get
        {
            lock (_gate)
            {
                return _bound;
            }
        }
    }

    /// <summary>
    /// Binds UDP and TCP on every listen endpoint and starts answering. If any bind fails,
    /// everything already bound is closed again and <paramref name="problemCode"/> says why.
    /// </summary>
    public bool TryStart(out string problemCode)
    {
        problemCode = string.Empty;
        lock (_gate)
        {
            if (_disposed)
            {
                problemCode = Problems.Disposed;
                return false;
            }

            if (_running)
            {
                problemCode = Problems.AlreadyStarted;
                return false;
            }

            if (_options.Listen.Count == 0)
            {
                problemCode = Problems.NoListen;
                return false;
            }

            var pairs = new List<(Socket Udp, Socket Tcp)>();
            var bound = new List<IPEndPoint>();
            try
            {
                foreach (var endpoint in _options.Listen)
                {
                    var pair = BindPair(endpoint);
                    pairs.Add(pair);
                    bound.Add((IPEndPoint)pair.Udp.LocalEndPoint!);
                }
            }
            catch (Exception ex)
            {
                foreach (var (udp, tcp) in pairs)
                {
                    udp.Dispose();
                    tcp.Dispose();
                }

                problemCode = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
                    ? Problems.PortInUse
                    : Problems.BindFailed;
                _log.LogWarning("the whitelist resolver could not bind: {Problem} ({Message})", problemCode, ex.Message);
                return false;
            }

            var token = _stop.Token;
            foreach (var (udp, tcp) in pairs)
            {
                _sockets.Add(udp);
                _sockets.Add(tcp);
                _loops.Add(Task.Run(() => UdpLoopAsync(udp, token), CancellationToken.None));
                _loops.Add(Task.Run(() => AcceptLoopAsync(tcp, token), CancellationToken.None));
            }

            _bound = bound;
            _running = true;
            _log.LogInformation("the whitelist resolver answers on {Endpoints}", string.Join(", ", bound));
            return true;
        }
    }

    /// <summary>Replaces the whitelist; queries already being answered keep the list they started with.</summary>
    public void SetAllowed(IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        Volatile.Write(ref _allowed, [.. patterns]);
    }

    /// <summary>Replaces the upstream servers, as when the NIC's previous DNS is learned again.</summary>
    public void SetUpstream(IReadOnlyList<IPEndPoint> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        Volatile.Write(ref _upstream, [.. upstream]);
    }

    /// <summary>Closes the listeners and waits, briefly, for the loops and the queries in hand.</summary>
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            _stop.Cancel();
            foreach (var socket in _sockets)
            {
                socket.Dispose();
            }

            _sockets.Clear();
            pending = [.. _loops, .. _work.Values];
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(DisposeWait).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "the whitelist resolver did not wind down in time: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Answers one query: the reply to send, or <c>null</c> to send nothing. A malformed query
    /// is dropped rather than answered FORMERR — nothing on a Windows PC sends one, and a reply
    /// to garbage could only feed a reflection loop.
    /// </summary>
    private async Task<byte[]?> AnswerAsync(byte[] message, bool overTcp, CancellationToken ct)
    {
        if (!DnsMessage.TryParseQuery(message, out var query))
        {
            return null;
        }

        var name = HostPattern.NormalizeName(query.Question.Name);
        if (name is not null && HostPattern.AnyMatches(ReversePatterns, name))
        {
            var reverse = await ForwardAsync(message, query, ct).ConfigureAwait(false);
            return reverse is null ? DnsMessage.BuildServerFailure(message, query) : Fit(reverse.Value.Bytes, query, overTcp);
        }

        if (name is null || !HostPattern.AnyMatches(Volatile.Read(ref _allowed), name))
        {
            if (name is not null && !IsNoise(name))
            {
                Refused.Note(name, _clock());
                _log.LogDebug("refused {Name}", name);
            }

            return DnsMessage.BuildRefused(message, query);
        }

        if (query.Question.Type is DnsMessage.Types.Https or DnsMessage.Types.Svcb)
        {
            return DnsMessage.BuildNoData(message, query);
        }

        var answer = await ForwardAsync(message, query, ct).ConfigureAwait(false);
        if (answer is null)
        {
            return DnsMessage.BuildServerFailure(message, query);
        }

        var addresses = answer.Value.Response.Addresses.Select(a => a.Address).Distinct().ToArray();
        if (addresses.Length > 0 && !TryAdmit(addresses))
        {
            return DnsMessage.BuildServerFailure(message, query);
        }

        return Fit(answer.Value.Bytes, query, overTcp);
    }

    private bool TryAdmit(IReadOnlyList<IPAddress> addresses)
    {
        try
        {
            return _options.Admit(addresses);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "admitting {Count} addresses failed: {Message}", addresses.Count, ex.Message);
            return false;
        }
    }

    /// <summary>An answer as it may go to the client: over UDP, one too big for it becomes "ask again over TCP".</summary>
    private static byte[] Fit(byte[] answer, DnsQuery query, bool overTcp) =>
        !overTcp && answer.Length > query.UdpPayloadLimit ? DnsMessage.BuildTruncated(answer, query) : answer;

    private static bool IsNoise(string name) =>
        !name.Contains('.')
        || name.StartsWith("wpad.", StringComparison.Ordinal)
        || HostPattern.AnyMatches(NoisePatterns, name);

    /// <summary>
    /// Asks the upstream servers in order and returns the first usable answer — NOERROR or
    /// NXDOMAIN — with the client's id written back; a timeout, a network error or any other
    /// RCODE moves on to the next server, and a server that is this resolver itself is skipped.
    /// <c>null</c> when none answered.
    /// </summary>
    private async Task<(byte[] Bytes, DnsResponse Response)?> ForwardAsync(byte[] message, DnsQuery query, CancellationToken ct)
    {
        var bound = BoundEndpoints;
        foreach (var server in Volatile.Read(ref _upstream))
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (bound.Contains(server))
            {
                // Ourselves: the NIC's DNS already points here, so asking would only loop.
                continue;
            }

            var answer = await AskAsync(server, message, query, ct).ConfigureAwait(false);
            if (answer is { Response.ResponseCode: DnsMessage.ResponseCodes.NoError or DnsMessage.ResponseCodes.NameError })
            {
                return answer;
            }
        }

        return null;
    }

    /// <summary>
    /// One server: over UDP with an id of our own (so an off-path guess of the client's id
    /// cannot be relayed), again over TCP when the answer came back truncated.
    /// </summary>
    private async Task<(byte[] Bytes, DnsResponse Response)?> AskAsync(IPEndPoint server, byte[] message, DnsQuery query, CancellationToken ct)
    {
        var outgoing = message.ToArray();
        var upstreamId = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
        DnsMessage.WriteId(outgoing, upstreamId);

        try
        {
            var answer = await AskUdpAsync(server, outgoing, upstreamId, query, ct).ConfigureAwait(false);
            if (answer is { Response.Truncated: true })
            {
                answer = await AskTcpAsync(server, outgoing, upstreamId, query, ct).ConfigureAwait(false);
            }

            if (answer is null)
            {
                return null;
            }

            DnsMessage.WriteId(answer.Value.Bytes, query.Id);
            return answer;
        }
        catch (OperationCanceledException)
        {
            // This server's time ran out, or the resolver is stopping.
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            _log.LogDebug("upstream {Server} failed: {Message}", server, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "upstream {Server} failed unexpectedly: {Message}", server, ex.Message);
            return null;
        }
    }

    private async Task<(byte[] Bytes, DnsResponse Response)?> AskUdpAsync(IPEndPoint server, byte[] outgoing, ushort upstreamId, DnsQuery query, CancellationToken ct)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        IgnoreConnectionReset(socket);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);

        await socket.ConnectAsync(server, deadline.Token).ConfigureAwait(false);
        await socket.SendAsync(outgoing, SocketFlags.None, deadline.Token).ConfigureAwait(false);

        var buffer = new byte[DnsMessage.MaxMessageLength];
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, SocketFlags.None, deadline.Token).ConfigureAwait(false);
            var bytes = buffer.AsSpan(0, received).ToArray();
            if (IsAnswerTo(bytes, upstreamId, query, out var response))
            {
                return (bytes, response);
            }
        }
    }

    private async Task<(byte[] Bytes, DnsResponse Response)?> AskTcpAsync(IPEndPoint server, byte[] outgoing, ushort upstreamId, DnsQuery query, CancellationToken ct)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);

        await socket.ConnectAsync(server, deadline.Token).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(Frame(outgoing), deadline.Token).ConfigureAwait(false);

        var bytes = await ReadFrameAsync(stream, deadline.Token).ConfigureAwait(false);
        return bytes is not null && IsAnswerTo(bytes, upstreamId, query, out var response) ? (bytes, response) : null;
    }

    /// <summary>Whether <paramref name="bytes"/> answer our query: our id, and the same question unless truncated before it.</summary>
    private static bool IsAnswerTo(byte[] bytes, ushort upstreamId, DnsQuery query, out DnsResponse response)
    {
        if (!DnsMessage.TryParseResponse(bytes, out response) || response.Id != upstreamId)
        {
            return false;
        }

        if (response.Question is not { } question)
        {
            return response.Truncated;
        }

        return question.Type == query.Question.Type
            && question.Class == query.Question.Class
            && string.Equals(question.Name, query.Question.Name, StringComparison.OrdinalIgnoreCase);
    }

    private async Task UdpLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[DnsMessage.MaxMessageLength];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "the whitelist resolver's UDP receive failed: {Message}", ex.Message);
                await PauseAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (!_inFlight.Wait(0))
            {
                // Over the limit: dropped, the client asks again.
                continue;
            }

            var message = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
            var client = received.RemoteEndPoint;
            Run(async () =>
            {
                try
                {
                    var reply = await AnswerAsync(message, overTcp: false, ct).ConfigureAwait(false);
                    if (reply is not null)
                    {
                        await socket.SendToAsync(reply, SocketFlags.None, client, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _inFlight.Release();
                }
            });
        }
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "the whitelist resolver's TCP accept failed: {Message}", ex.Message);
                await PauseAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (!_connections.Wait(0))
            {
                client.Dispose();
                continue;
            }

            Run(async () =>
            {
                try
                {
                    await ServeTcpAsync(client, ct).ConfigureAwait(false);
                }
                finally
                {
                    client.Dispose();
                    _connections.Release();
                }
            });
        }
    }

    /// <summary>Length-prefixed queries, one after another, until the client goes quiet or away.</summary>
    private async Task ServeTcpAsync(Socket client, CancellationToken ct)
    {
        await using var stream = new NetworkStream(client, ownsSocket: false);
        while (!ct.IsCancellationRequested)
        {
            byte[]? message;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(TcpIdleTimeout);
                message = await ReadFrameAsync(stream, idle.Token).ConfigureAwait(false);
            }

            if (message is null || !_inFlight.Wait(0))
            {
                return;
            }

            byte[]? reply;
            try
            {
                reply = await AnswerAsync(message, overTcp: true, ct).ConfigureAwait(false);
            }
            finally
            {
                _inFlight.Release();
            }

            if (reply is null)
            {
                continue;
            }

            using var write = CancellationTokenSource.CreateLinkedTokenSource(ct);
            write.CancelAfter(TcpIdleTimeout);
            await stream.WriteAsync(Frame(reply), write.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Starts tracked background work; the task is registered before it can finish, and nothing escapes it.</summary>
    private void Run(Func<Task> work)
    {
        var id = Interlocked.Increment(ref _nextWork);
        var outer = new Task<Task>(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
                // A client that went away, or the resolver stopping.
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "the whitelist resolver failed answering: {Message}", ex.Message);
            }
            finally
            {
                _work.TryRemove(id, out _);
            }
        });

        _work[id] = outer.Unwrap();
        outer.Start(TaskScheduler.Default);
    }

    private (Socket Udp, Socket Tcp) BindPair(IPEndPoint endpoint)
    {
        var attempts = endpoint.Port == 0 ? EphemeralBindAttempts : 1;
        for (var attempt = 1; ; attempt++)
        {
            var udp = NewSocket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            Socket? tcp = null;
            try
            {
                udp.Bind(endpoint);
                IgnoreConnectionReset(udp);

                // TCP takes the port UDP got, so an ephemeral test endpoint is one port for both.
                tcp = NewSocket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                tcp.Bind((IPEndPoint)udp.LocalEndPoint!);
                tcp.Listen(TcpBacklog);
                return (udp, tcp);
            }
            catch (SocketException) when (tcp is not null && attempt < attempts)
            {
                udp.Dispose();
                tcp.Dispose();
            }
            catch
            {
                udp.Dispose();
                tcp?.Dispose();
                throw;
            }
        }
    }

    private static Socket NewSocket(AddressFamily family, SocketType type, ProtocolType protocol)
    {
        var socket = new Socket(family, type, protocol);
        try
        {
            if (family == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = false;
            }

            if (OperatingSystem.IsWindows())
            {
                // Nobody may bind the same port next to us and take the answers.
                socket.ExclusiveAddressUse = true;
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return socket;
    }

    private static void IgnoreConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
        {
            // The receive loop tolerates WSAECONNRESET anyway.
        }
    }

    private static byte[] Frame(byte[] message)
    {
        var framed = new byte[message.Length + 2];
        framed[0] = (byte)(message.Length >> 8);
        framed[1] = (byte)message.Length;
        message.CopyTo(framed, 2);
        return framed;
    }

    /// <summary>One length-prefixed message, or <c>null</c> at the end of the stream, a too-short frame or cancellation.</summary>
    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            var prefix = new byte[2];
            if (await stream.ReadAtLeastAsync(prefix, 2, throwOnEndOfStream: false, ct).ConfigureAwait(false) < 2)
            {
                return null;
            }

            var length = (prefix[0] << 8) | prefix[1];
            if (length < DnsMessage.HeaderLength)
            {
                return null;
            }

            var message = new byte[length];
            return await stream.ReadAtLeastAsync(message, length, throwOnEndOfStream: false, ct).ConfigureAwait(false) < length
                ? null
                : message;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task PauseAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
