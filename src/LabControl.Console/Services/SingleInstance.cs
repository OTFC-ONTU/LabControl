using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabControl.Shared;
using LabControl.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabControl.Console.Services;

/// <summary>What the forwarder got back: whether a running console took the files, and which ones it kept.</summary>
public sealed record ForwardOutcome(bool Delivered, IReadOnlyList<string> Accepted, IReadOnlyList<string> Rejected, string Error)
{
    public static ForwardOutcome NotDelivered(string error) => new(false, [], [], error);
}

/// <summary>
/// One console per data directory, and every later launch hands its documents to the first
/// (M5, D-59 item 5). The process holding <see cref="ConsoleLock"/> listens on an endpoint
/// named after the directory — a Windows named pipe <c>labcontrol-console-&lt;hash&gt;</c>
/// restricted to the current user, or a Unix socket of that name (mode 0600) under
/// <c>$XDG_RUNTIME_DIR</c> or the user's temp directory — and a launch that cannot take the
/// lock connects there instead, sends one UTF-8 JSON line
/// <c>{"schema_version":1,"open":["/abs/path", …]}</c>, waits for the acknowledgement line
/// and exits. The hash is the first 16 hex digits of SHA-256 over the directory, so two
/// <c>--data</c> directories have two endpoints and never see each other.
/// <para>
/// The lock decides who serves: a stale socket file left by a crashed console cannot belong
/// to anyone once the lock has been taken, so <see cref="Listen"/> removes it. A forwarder
/// that finds no listener (the lock is held but the socket is gone, or the console is still
/// starting) retries until its timeout and then reports that nothing was delivered.
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

    private readonly Action<Action> _post;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Socket? _socket;
    private readonly string? _socketPath;
    private readonly Task _loop;
    private int _disposed;

    private SingleInstance(string name, string endpoint, Socket? socket, string? socketPath, Action<Action> post, ILogger log)
    {
        Name = name;
        Endpoint = endpoint;
        _socket = socket;
        _socketPath = socketPath;
        _post = post;
        _log = log;
        _loop = OperatingSystem.IsWindows() ? ServePipesAsync() : ServeSocketAsync();
    }

    /// <summary><c>labcontrol-console-&lt;hash&gt;</c>: the pipe name, or the socket file's stem.</summary>
    public string Name { get; }

    /// <summary>The pipe name on Windows; the socket file's full path elsewhere.</summary>
    public string Endpoint { get; }

    /// <summary>
    /// Documents another launch asked this console to open, validated (existing files with a
    /// known extension, as full paths) and raised through the poster given to
    /// <see cref="Listen"/> — the UI thread in the app. An empty list is a bare second launch
    /// with nothing to open: the console should just come to the front.
    /// </summary>
    public event Action<IReadOnlyList<string>>? FilesArrived;

    public static string NameFor(string dataDirectory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        if (OperatingSystem.IsWindows())
        {
            // Windows paths name the same directory in any case; the socket name must agree.
            normalized = normalized.ToLowerInvariant();
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Defaults.SingleInstanceNamePrefix + hash[..Defaults.SingleInstanceNameHashLength];
    }

    /// <summary>Where the endpoint lives for <paramref name="dataDirectory"/>: the pipe name, or the socket path.</summary>
    public static string EndpointFor(string dataDirectory) =>
        OperatingSystem.IsWindows()
            ? NameFor(dataDirectory)
            : Path.Combine(SocketDirectory(), NameFor(dataDirectory) + Defaults.SingleInstanceSocketExtension);

    /// <summary><c>$XDG_RUNTIME_DIR</c> when it is set and exists (a per-user tmpfs on Linux), else the user's temp directory.</summary>
    public static string SocketDirectory()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime))
        {
            return runtime;
        }

        return Path.GetTempPath();
    }

    /// <summary>
    /// Starts serving for <paramref name="dataDirectory"/>. The caller holds that directory's
    /// <see cref="ConsoleLock"/>, which is what makes a socket file already there stale rather
    /// than someone else's. <paramref name="post"/> runs <see cref="FilesArrived"/> where the
    /// app wants it (the UI thread).
    /// </summary>
    /// <exception cref="IOException">The endpoint could not be created (an unwritable socket directory, a name in use by a different user).</exception>
    public static SingleInstance Listen(string dataDirectory, Action<Action> post, ILogger? log = null)
    {
        log ??= NullLogger.Instance;
        var name = NameFor(dataDirectory);
        var endpoint = EndpointFor(dataDirectory);
        if (OperatingSystem.IsWindows())
        {
            return new SingleInstance(name, endpoint, null, null, post, log);
        }

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
                socket.Bind(new UnixDomainSocketEndPoint(endpoint));
                File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                socket.Listen(8);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            return new SingleInstance(name, endpoint, socket, endpoint, post, log);
        }
        catch (SocketException ex)
        {
            throw new IOException($"The single-instance socket {endpoint} could not be opened: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Hands <paramref name="files"/> to the console serving <paramref name="dataDirectory"/>.
    /// Connecting is retried until <paramref name="timeout"/> — the other console may still be
    /// between taking its lock and opening its endpoint. Nothing here throws: a missing or
    /// unanswering server is a <see cref="ForwardOutcome"/> with <c>Delivered == false</c>.
    /// </summary>
    public static async Task<ForwardOutcome> ForwardAsync(string dataDirectory, IReadOnlyList<string> files, TimeSpan timeout, CancellationToken cancellation = default)
    {
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

                var request = JsonSerializer.Serialize(new OpenRequest { SchemaVersion = SchemaVersion, Open = files.ToList() }, Wire) + "\n";
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
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or TimeoutException or UnauthorizedAccessException)
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

        while (true)
        {
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
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellation);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                // The file is there but nobody accepts: a console still starting, or a
                // stale file the lock holder is about to replace. Try again shortly.
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
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (_stop.IsCancellationRequested)
                {
                    return;
                }

                _log.LogWarning(ex, "The single-instance socket stopped accepting");
                return;
            }

            _ = HandleAsync(new NetworkStream(client, ownsSocket: true));
        }
    }

    private async Task ServePipesAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(Endpoint, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "The single-instance pipe {Name} could not be created", Endpoint);
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "The single-instance pipe {Name} stopped accepting", Endpoint);
                await pipe.DisposeAsync();
                return;
            }

            _ = HandleAsync(pipe);
        }
    }

    /// <summary>One client: a request line in, validation, the event, an acknowledgement line out.</summary>
    private async Task HandleAsync(Stream stream)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        budget.CancelAfter(ClientBudget);
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

        var accepted = new List<string>();
        var rejected = new List<string>();
        foreach (var path in request.Open ?? [])
        {
            if (!Path.IsPathRooted(path))
            {
                // The forwarder resolved its paths already; a relative one would resolve
                // against this process's directory, which is not what anyone meant.
                rejected.Add($"'{path}' is not an absolute path");
                continue;
            }

            if (ConsoleOptions.TryValidateFile(path, out var full, out var error))
            {
                accepted.Add(full);
            }
            else
            {
                rejected.Add(error);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
            try
            {
                File.Delete(_socketPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _stop.Dispose();
    }

    private sealed class OpenRequest
    {
        public int SchemaVersion { get; set; }

        public List<string>? Open { get; set; }
    }

    private sealed class OpenAcknowledgement
    {
        public int SchemaVersion { get; set; } = SingleInstance.SchemaVersion;

        public List<string> Accepted { get; set; } = [];

        public List<string> Rejected { get; set; } = [];

        public string Error { get; set; } = string.Empty;
    }
}
