using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabControl.Shared;
using LabControl.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabControl.Console.Services;

/// <summary>
/// The endpoint cannot be used at all — a path longer than <c>sun_path</c>, a socket
/// directory that is not this user's private one. Retrying will not help, so a forwarder
/// reports it instead of waiting out its budget.
/// </summary>
public sealed class EndpointUnusableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>What the forwarder got back: whether a running console took the files, and which ones it kept.</summary>
public sealed record ForwardOutcome(bool Delivered, IReadOnlyList<string> Accepted, IReadOnlyList<string> Rejected, string Error)
{
    public static ForwardOutcome NotDelivered(string error) => new(false, [], [], error);
}

/// <summary>
/// One console per data directory, and every later launch hands its documents to the first
/// (M5, D-59 item 5). The process holding <see cref="ConsoleLock"/> listens on an endpoint
/// named after the directory — a Windows named pipe <c>labcontrol-console-&lt;hash&gt;</c>
/// restricted to the current user, or a Unix socket <c>console-&lt;hash&gt;.sock</c> (mode 0600)
/// inside this user's private socket directory — and a launch that cannot take the lock
/// connects there instead, sends one UTF-8 JSON line
/// <c>{"schema_version":1,"open":["/abs/path", …]}</c>, waits for the acknowledgement line
/// and exits. The hash is the first 16 hex digits of SHA-256 over the directory, so two
/// <c>--data</c> directories have two endpoints and never see each other.
/// <para>
/// The lock decides who serves: a stale socket left by a crashed console cannot belong to
/// anyone once the lock has been taken, so <see cref="Listen"/> — which takes that lock as an
/// argument rather than assuming it — removes it. A forwarder that finds no listener (the
/// lock is held but the socket is gone, or the console is still starting) retries until its
/// timeout and then reports that nothing was delivered.
/// </para>
/// <para>
/// Where the socket lives is a security decision, not a convenience: it sits in
/// <c>labcontrol-&lt;uid&gt;</c> under <c>$XDG_RUNTIME_DIR</c> or the temp directory, a
/// directory created with mode 0700 and re-checked (real directory, not a symbolic link,
/// still 0700) by the server before it binds and by every client before it connects. A
/// world-writable <c>/tmp</c> is then not enough to pre-create the path, to read the
/// teacher's document names, or to make the lock holder's <c>unlink</c> fail on the sticky
/// bit. The bind-to-chmod window on the socket itself is closed by that directory, and each
/// accepted connection is additionally checked to come from this user
/// (<c>LOCAL_PEERCRED</c>/<c>SO_PEERCRED</c>).
/// </para>
/// </summary>
public sealed class SingleInstance : IAsyncDisposable
{
    public const int SchemaVersion = 1;

    /// <summary>A request line longer than this is not a console talking: the connection is dropped.</summary>
    private const int MaxLineBytes = 64 * 1024;

    /// <summary>The wire format: snake_case like every document, but on one line — a newline ends a message.</summary>
    private static readonly JsonSerializerOptions Wire = new(JsonStore.Options) { WriteIndented = false };

    private static readonly TimeSpan ClientBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectRetry = TimeSpan.FromMilliseconds(200);

    /// <summary>Mode 0700: the socket directory belongs to this user and to nobody else.</summary>
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Two paths name the same file case-insensitively on Windows and case-sensitively elsewhere.</summary>
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Action<Action> _post;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Socket? _socket;
    private readonly string? _socketPath;
    private readonly Task _loop;
    private volatile bool _stopping;
    private int _disposed;

    private SingleInstance(string name, string endpoint, Socket? socket, NamedPipeServerStream? pipe, string? socketPath, Action<Action> post, ILogger log)
    {
        Name = name;
        Endpoint = endpoint;
        _socket = socket;
        _socketPath = socketPath;
        _post = post;
        _log = log;
        _loop = pipe is not null ? ServePipesAsync(pipe) : ServeSocketAsync();
    }

    /// <summary><c>labcontrol-console-&lt;hash&gt;</c>: the pipe name, or the endpoint's identity in a log line.</summary>
    public string Name { get; }

    /// <summary>The pipe name on Windows; the socket file's full path elsewhere.</summary>
    public string Endpoint { get; }

    /// <summary>
    /// Documents another launch asked this console to open, validated (existing regular files
    /// with a known extension and a sane size, as full paths) and raised through the poster
    /// given to <see cref="Listen"/> — the UI thread in the app. An empty list is a bare
    /// second launch with nothing to open: the console should just come to the front.
    /// </summary>
    public event Action<IReadOnlyList<string>>? FilesArrived;

    public static string NameFor(string dataDirectory) => Defaults.SingleInstanceNamePrefix + HashOf(dataDirectory);

    /// <summary>Where the endpoint lives for <paramref name="dataDirectory"/>: the pipe name, or the socket path.</summary>
    public static string EndpointFor(string dataDirectory) =>
        OperatingSystem.IsWindows()
            ? NameFor(dataDirectory)
            : Path.Combine(SocketDirectory(), Defaults.SingleInstanceSocketPrefix + HashOf(dataDirectory) + Defaults.SingleInstanceSocketExtension);

    /// <summary>
    /// This user's private socket directory: <c>labcontrol-&lt;uid&gt;</c> under
    /// <c>$XDG_RUNTIME_DIR</c> when it is set and exists (a per-user tmpfs on Linux), else
    /// under the user's temp directory. The uid is in the name because that temp directory
    /// may be a shared, world-writable <c>/tmp</c>.
    /// </summary>
    public static string SocketDirectory()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var root = !string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime) ? runtime : Path.GetTempPath();
        return Path.Combine(root, Defaults.SingleInstanceDirectoryPrefix + (OperatingSystem.IsWindows() ? "0" : CurrentUserId().ToString()));
    }

    /// <summary>
    /// Starts serving for <paramref name="dataDirectory"/>. <paramref name="heldLock"/> is
    /// that directory's <see cref="ConsoleLock"/>, held by the caller: it is what makes a
    /// socket already at the path stale rather than someone else's, and it is passed rather
    /// than assumed so the rule cannot be forgotten. <paramref name="post"/> runs
    /// <see cref="FilesArrived"/> where the app wants it (the UI thread).
    /// </summary>
    /// <exception cref="IOException">The endpoint could not be created (a socket directory that is not private, a path longer than <c>sun_path</c>, a name already in use).</exception>
    public static SingleInstance Listen(string dataDirectory, ConsoleLock heldLock, Action<Action> post, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(heldLock);
        log ??= NullLogger.Instance;
        var name = NameFor(dataDirectory);
        if (OperatingSystem.IsWindows())
        {
            // The first instance is created here, not inside the loop: a squatted name or a
            // refused ACL is this call's IOException, not a faulted background task.
            return new SingleInstance(name, name, null, CreatePipe(name), null, post, log);
        }

        var endpoint = EndpointFor(dataDirectory);
        EnsurePrivateSocketDirectory(Path.GetDirectoryName(endpoint)!, log);
        var address = UnixEndPointFor(endpoint);
        try
        {
            if (File.Exists(endpoint))
            {
                log.LogInformation("Removing the stale single-instance socket {Path}", endpoint);
                File.Delete(endpoint);
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Bind(address);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            try
            {
                // The directory is 0700, so nothing else can reach the socket between bind and
                // chmod; the mode is belt and braces. A chmod that fails leaves no usable
                // socket behind for the next launch to connect to.
                File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                socket.Listen(8);
            }
            catch
            {
                socket.Dispose();
                TryDelete(endpoint);
                throw;
            }

            return new SingleInstance(name, endpoint, socket, null, endpoint, post, log);
        }
        catch (Exception ex) when (ex is SocketException or UnauthorizedAccessException)
        {
            throw new IOException($"The single-instance socket {endpoint} could not be opened: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The endpoint answers nothing from now on: a launch that reaches it is told the console
    /// is going away and starts its own instead of having its files acknowledged and dropped.
    /// The app calls this — and disposes the endpoint — before anything else in its shutdown.
    /// </summary>
    public void BeginStopping() => _stopping = true;

    /// <summary>
    /// Hands <paramref name="files"/> to the console serving <paramref name="dataDirectory"/>.
    /// Connecting is retried until <paramref name="timeout"/> — the other console may still be
    /// between taking its lock and opening its endpoint. Nothing here throws: a missing,
    /// unanswering or departing server is a <see cref="ForwardOutcome"/> with
    /// <c>Delivered == false</c>, and the caller starts a console of its own.
    /// </summary>
    public static async Task<ForwardOutcome> ForwardAsync(string dataDirectory, IReadOnlyList<string> files, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (files.Count > Defaults.SingleInstanceMaxOpenPaths)
        {
            return ForwardOutcome.NotDelivered($"a launch may hand over at most {Defaults.SingleInstanceMaxOpenPaths} files at a time, not {files.Count}");
        }

        var endpoint = EndpointFor(dataDirectory);
        var deadline = DateTime.UtcNow + timeout;
        string lastError;
        do
        {
            try
            {
                await using var stream = await ConnectAsync(endpoint, deadline, cancellation);
                if (stream is null)
                {
                    return ForwardOutcome.NotDelivered($"no console is serving {endpoint}");
                }

                var request = JsonSerializer.Serialize(new OpenRequest { SchemaVersion = SchemaVersion, Open = [.. files] }, Wire) + "\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(request), cancellation);
                await stream.FlushAsync(cancellation);

                var line = await ReadLineAsync(stream, cancellation);
                if (line is null)
                {
                    return ForwardOutcome.NotDelivered("the console closed the connection without answering");
                }

                var ack = JsonSerializer.Deserialize<OpenAcknowledgement>(line, Wire);
                if (ack is null || ack.SchemaVersion > SchemaVersion)
                {
                    return ForwardOutcome.NotDelivered("the console answered in a format this launcher does not understand");
                }

                return new ForwardOutcome(ack.Error.Length == 0, ack.Accepted, ack.Rejected, ack.Error);
            }
            catch (OperationCanceledException)
            {
                return ForwardOutcome.NotDelivered("cancelled");
            }
            catch (EndpointUnusableException ex)
            {
                return ForwardOutcome.NotDelivered(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or TimeoutException or UnauthorizedAccessException or ArgumentException)
            {
                lastError = ex.Message;
            }

            try
            {
                await Task.Delay(ConnectRetry, cancellation);
            }
            catch (OperationCanceledException)
            {
                return ForwardOutcome.NotDelivered("cancelled");
            }
        }
        while (DateTime.UtcNow < deadline);

        return ForwardOutcome.NotDelivered(lastError);
    }

    /// <summary>A connected stream, or <c>null</c> once the deadline passes without a listener.</summary>
    private static async Task<Stream?> ConnectAsync(string endpoint, DateTime deadline, CancellationToken cancellation)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                var remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                await pipe.ConnectAsync(remaining, cancellation);
                return pipe;
            }
            catch (TimeoutException)
            {
                await pipe.DisposeAsync();
                return null;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        var directory = Path.GetDirectoryName(endpoint)!;
        var address = UnixEndPointFor(endpoint);
        while (true)
        {
            // Somebody else's directory at our path is not a console that is still starting:
            // it is a trap, and nothing of the teacher's is written into it.
            if (Directory.Exists(directory) && !SocketDirectoryIsPrivate(directory, out var why))
            {
                throw new EndpointUnusableException(why);
            }

            if (!File.Exists(endpoint))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    return null;
                }

                await Task.Delay(ConnectRetry, cancellation);
                continue;
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(address, cancellation);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                // The file is there but nobody accepts: a console still starting, or a
                // stale socket the lock holder is about to replace. Try again shortly.
                socket.Dispose();
                await Task.Delay(ConnectRetry, cancellation);
            }
            catch (SocketException)
            {
                socket.Dispose();
                return null;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    // ------------------------------------------------------------------ the endpoint's home

    private static string HashOf(string dataDirectory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        if (OperatingSystem.IsWindows())
        {
            // Windows paths name the same directory in any case; the endpoint name must agree.
            normalized = normalized.ToLowerInvariant();
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return hash[..Defaults.SingleInstanceNameHashLength];
    }

    /// <summary>
    /// Creates the socket directory with mode 0700 and proves it is ours before anything binds
    /// in it. There is no portable way to read a directory's owner from .NET, so ownership is
    /// established by what only an owner can do: a directory that is 0700 and that we may
    /// still chmod and write into belongs to this user (or to root, whose cooperation is
    /// assumed by every other part of the system anyway).
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static void EnsurePrivateSocketDirectory(string directory, ILogger log)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory, PrivateDirectory);
                log.LogInformation("Created the private single-instance socket directory {Path}", directory);
            }

            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                throw new EndpointUnusableException($"{directory} is a symbolic link; the single-instance socket needs a real private directory");
            }

            if (File.GetUnixFileMode(directory) != PrivateDirectory)
            {
                // Only the owner may chmod, so this both tightens a loose directory of ours
                // and refuses one somebody else created at the same path.
                File.SetUnixFileMode(directory, PrivateDirectory);
            }

            if (!SocketDirectoryIsPrivate(directory, out var why))
            {
                throw new EndpointUnusableException(why);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new EndpointUnusableException($"The single-instance socket directory {directory} is not usable: {ex.Message}", ex);
        }
    }

    /// <summary>A real directory, not a link, with mode 0700 — checked by the server before binding and by every client before connecting.</summary>
    [UnsupportedOSPlatform("windows")]
    private static bool SocketDirectoryIsPrivate(string directory, out string error)
    {
        error = string.Empty;
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists)
            {
                error = $"{directory} does not exist";
                return false;
            }

            if (info.LinkTarget is not null)
            {
                error = $"{directory} is a symbolic link, not this user's private socket directory";
                return false;
            }

            var mode = File.GetUnixFileMode(directory);
            if (mode != PrivateDirectory)
            {
                error = $"{directory} is {mode} and not this user's private socket directory (0700 expected)";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{directory} could not be checked: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// The address for <paramref name="path"/>, refusing a path longer than the platform's
    /// <c>sun_path</c> (104 bytes on macOS, 108 on Linux, both including the terminator)
    /// before <see cref="UnixDomainSocketEndPoint"/> throws an argument exception at it.
    /// </summary>
    private static UnixDomainSocketEndPoint UnixEndPointFor(string path)
    {
        // sun_path holds 104 bytes on macOS and 108 on Linux, the terminator included.
        var limit = OperatingSystem.IsMacOS() ? 103 : 107;
        var length = Encoding.UTF8.GetByteCount(path);
        if (length > limit)
        {
            throw new EndpointUnusableException($"The single-instance socket path {path} is {length} bytes long; this system allows {limit}");
        }

        try
        {
            return new UnixDomainSocketEndPoint(path);
        }
        catch (Exception ex) when (ex is ArgumentException or SocketException)
        {
            throw new EndpointUnusableException($"The single-instance socket path {path} cannot be used: {ex.Message}", ex);
        }
    }

    private static NamedPipeServerStream CreatePipe(string name)
    {
        try
        {
            return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new IOException($"The single-instance pipe {name} could not be created: {ex.Message}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ the server side

    private async Task ServeSocketAsync()
    {
        var listener = _socket!;
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_stop.IsCancellationRequested)
                {
                    return;
                }

                _log.LogWarning(ex, "The single-instance socket stopped accepting; a second launch will be refused instead of forwarded");
                return;
            }

            if (!PeerIsThisUser(client, out var peer))
            {
                _log.LogWarning("A single-instance connection from another user was dropped ({Peer})", peer);
                client.Dispose();
                continue;
            }

            _ = HandleAsync(new NetworkStream(client, ownsSocket: true));
        }
    }

    private async Task ServePipesAsync(NamedPipeServerStream first)
    {
        var pipe = first;
        while (true)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "The single-instance pipe {Name} stopped accepting", Endpoint);
                await pipe.DisposeAsync();
                return;
            }

            // The pipe instance is handed to the client; the next one waits for the next launch.
            _ = HandleAsync(pipe);
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            try
            {
                pipe = CreatePipe(Endpoint);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "The single-instance pipe {Name} could not be re-created; a second launch will be refused instead of forwarded", Endpoint);
                return;
            }
        }
    }

    /// <summary>One client: a request line in, validation, the event, an acknowledgement line out.</summary>
    private async Task HandleAsync(Stream stream)
    {
        // The budget is deliberately not linked to _stop: a client whose request is already in
        // flight when the console starts shutting down is answered with the refusal below
        // rather than left to guess from a closed connection.
        using var budget = new CancellationTokenSource(ClientBudget);
        try
        {
            await using (stream)
            {
                var line = await ReadLineAsync(stream, budget.Token);
                if (line is null)
                {
                    return;
                }

                var ack = Consider(line);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ack, Wire) + "\n");
                await stream.WriteAsync(bytes, budget.Token);
                await stream.FlushAsync(budget.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _log.LogDebug(ex, "A single-instance client went away");
        }
    }

    /// <summary>Validates one request line the way the command line is validated, and raises <see cref="FilesArrived"/> for what passed.</summary>
    private OpenAcknowledgement Consider(string line)
    {
        if (_stopping)
        {
            // Nothing can be imported any more: say so, so the launcher starts its own console
            // instead of exiting 0 on files this process is about to drop.
            return new OpenAcknowledgement { Error = "the console is shutting down" };
        }

        OpenRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<OpenRequest>(line, Wire);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return new OpenAcknowledgement { Error = "not a request this console understands" };
        }

        if (request.SchemaVersion > SchemaVersion)
        {
            return new OpenAcknowledgement { Error = $"schema_version {request.SchemaVersion} is newer than this console's {SchemaVersion}" };
        }

        var open = request.Open ?? [];
        if (open.Count > Defaults.SingleInstanceMaxOpenPaths)
        {
            return new OpenAcknowledgement { Error = $"a launch may hand over at most {Defaults.SingleInstanceMaxOpenPaths} files at a time, not {open.Count}" };
        }

        var accepted = new List<string>();
        var rejected = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var path in open)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                rejected.Add("an empty file name");
                continue;
            }

            if (!Path.IsPathRooted(path))
            {
                // The forwarder resolved its paths already; a relative one would resolve
                // against this process's directory, which is not what anyone meant.
                rejected.Add($"'{path}' is not an absolute path");
                continue;
            }

            if (!ConsoleOptions.TryValidateFile(path, out var full, out var error))
            {
                rejected.Add(error);
                continue;
            }

            if (seen.Add(full))
            {
                accepted.Add(full);
            }
        }

        _log.LogInformation("Another launch handed over {Accepted} file(s); {Rejected} rejected", accepted.Count, rejected.Count);
        var files = accepted.AsReadOnly();
        _post(() => FilesArrived?.Invoke(files));
        return new OpenAcknowledgement { Accepted = accepted, Rejected = rejected };
    }

    /// <summary>Reads up to the first newline, without the newline; <c>null</c> at end of stream or past the size cap.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellation)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellation);
            if (read == 0)
            {
                return null;
            }

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            buffer.Write(chunk, 0, newline < 0 ? read : newline);
            if (buffer.Length > MaxLineBytes)
            {
                return null;
            }

            if (newline >= 0)
            {
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
        }
    }

    // ------------------------------------------------------------------ who is on the other end

    /// <summary>
    /// The connected peer runs as this user. macOS answers <c>LOCAL_PEERCRED</c> with a
    /// <c>struct xucred</c> and Linux answers <c>SO_PEERCRED</c> with a <c>struct ucred</c>;
    /// the two disagree about everything except the uid, which both put at offset 4. A kernel
    /// that answers neither leaves the 0700 directory as the guard and the connection is
    /// allowed — the check is defence in depth, not the only door.
    /// </summary>
    private bool PeerIsThisUser(Socket client, out string peer)
    {
        peer = "unknown";
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const int SolLocalOnMac = 0;
        const int LocalPeerCred = 1;
        const int SolSocketOnLinux = 1;
        const int SoPeerCred = 17;
        try
        {
            Span<byte> answer = stackalloc byte[96];
            var level = OperatingSystem.IsMacOS() ? SolLocalOnMac : SolSocketOnLinux;
            var option = OperatingSystem.IsMacOS() ? LocalPeerCred : SoPeerCred;
            var length = client.GetRawSocketOption(level, option, answer);
            if (length < 8)
            {
                return true;
            }

            var uid = BitConverter.ToUInt32(answer[4..8]);
            peer = $"uid {uid}";
            return uid == CurrentUserId();
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or ObjectDisposedException)
        {
            _log.LogDebug(ex, "The peer credentials of a single-instance client could not be read; the 0700 socket directory is the guard");
            return true;
        }
    }

    private static uint CurrentUserId() => OperatingSystem.IsWindows() ? 0 : GetEuid();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        BeginStopping();
        await _stop.CancelAsync();
        _socket?.Dispose();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }

        if (_socketPath is not null)
        {
            TryDelete(_socketPath);
        }

        _stop.Dispose();
    }

    private sealed class OpenRequest
    {
        public int SchemaVersion { get; set; }

        /// <summary>A hostile or careless client may send nulls inside the list; the element type says so.</summary>
        public List<string?>? Open { get; set; }
    }

    private sealed class OpenAcknowledgement
    {
        public int SchemaVersion { get; set; } = SingleInstance.SchemaVersion;

        public List<string> Accepted { get; set; } = [];

        public List<string> Rejected { get; set; } = [];

        public string Error { get; set; } = string.Empty;
    }
}
