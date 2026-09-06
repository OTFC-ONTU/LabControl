using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.ServiceProcess;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using LabControl.Shared.Session;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent;

/// <summary>
/// Keeps exactly one <c>session.exe</c> alive in the interactive session and tells the
/// console what that session is doing (ROADMAP M2 portion 2, D-30). The service owns the
/// named pipe; the helper connects to it, says hello, and reports its status every
/// <see cref="Defaults.HelperStatusInterval"/>. The supervisor re-reads the session every
/// <see cref="Defaults.SessionPollInterval"/> — sooner when the service control manager
/// reports a change — and respawns the helper when it exits, hangs, or the session it lives
/// in is no longer the one the student sees. Nothing here may throw out of the loop.
/// </summary>
internal sealed class SessionSupervisor : IAsyncDisposable
{
    private readonly AgentLink _link;
    private readonly SessionChangeSource _changes;
    private readonly ILogger _log;
    private readonly string _helperPath;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _recentExits = new();

    private Task? _loop;
    private Helper? _helper;
    private SessionSnapshot _snapshot;
    private bool _published;
    private bool _helperAlivePublished;

    private SessionState.Types.Kind _pendingKind;
    private uint? _pendingKindSession;
    private string? _restartRequested;

    private DateTimeOffset _nextSpawnAt = DateTimeOffset.MinValue;
    private bool _crashLoopReported;
    private string? _lastSpawnFailure;
    private bool _pipeFailureReported;
    private NamedPipeServerStream? _server;

    public SessionSupervisor(AgentLink link, SessionChangeSource changes, ILogger log, string helperPath)
    {
        _link = link;
        _changes = changes;
        _log = log;
        _helperPath = helperPath;
    }

    /// <summary>The helper is connected and has spoken recently.</summary>
    public bool HelperAlive
    {
        get
        {
            var helper = _helper;
            return helper is not null && helper.Alive(DateTimeOffset.UtcNow);
        }
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _changes.Changed += OnSessionChange;
        _loop = Task.Run(() => LoopAsync(_stopping.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        _changes.Changed -= OnSessionChange;
        _stopping.Cancel();
        Wake();

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is System.TimeoutException or OperationCanceledException)
            {
            }
        }

        _stopping.Dispose();
        _wake.Dispose();
    }

    // ------------------------------------------------------------------ notifications

    /// <summary>From the SCM, on its thread: remember what happened and wake the loop.</summary>
    private void OnSessionChange(SessionChangeReason reason, int sessionId)
    {
        lock (_lock)
        {
            var current = _snapshot.SessionId;
            var concernsUs = current is null || (uint)sessionId == current || reason is SessionChangeReason.ConsoleConnect;
            if (!concernsUs)
            {
                _log.LogDebug("session change {Reason} for session {Session} ignored (console session is {Console})", reason, sessionId, current);
                return;
            }

            switch (reason)
            {
                case SessionChangeReason.SessionLock:
                    _pendingKind = SessionState.Types.Kind.Lock;
                    _pendingKindSession = (uint)sessionId;
                    break;
                case SessionChangeReason.SessionUnlock:
                    _pendingKind = SessionState.Types.Kind.Unlock;
                    _pendingKindSession = (uint)sessionId;
                    break;
                case SessionChangeReason.SessionLogon:
                    _pendingKind = SessionState.Types.Kind.Logon;
                    _pendingKindSession = (uint)sessionId;
                    _restartRequested = "a user logged on";
                    break;
                case SessionChangeReason.SessionLogoff:
                    _pendingKind = SessionState.Types.Kind.Logoff;
                    _pendingKindSession = (uint)sessionId;
                    _restartRequested = "the user logged off";
                    break;
                case SessionChangeReason.ConsoleConnect:
                case SessionChangeReason.ConsoleDisconnect:
                case SessionChangeReason.RemoteConnect:
                case SessionChangeReason.RemoteDisconnect:
                    _restartRequested = $"the console session changed ({reason})";
                    break;
            }
        }

        _log.LogDebug("session change: {Reason} in session {Session}", reason, sessionId);
        Wake();
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    // ------------------------------------------------------------------ the loop

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await TickAsync(DateTimeOffset.UtcNow, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must outlive any single failure: log it and look again next tick.
                _log.LogError(ex, "session supervision failed this round: {Message}", ex.Message);
            }

            try
            {
                await _wake.WaitAsync(Defaults.SessionPollInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await StopHelperAsync("the service is stopping", CancellationToken.None);
        _server?.Dispose();
        _server = null;
    }

    private async Task TickAsync(DateTimeOffset now, CancellationToken token)
    {
        var snapshot = InteractiveSession.Snapshot();

        SessionState.Types.Kind kind;
        string? restart;
        lock (_lock)
        {
            kind = _pendingKindSession is null || _pendingKindSession == snapshot.SessionId ? _pendingKind : SessionState.Types.Kind.Unspecified;
            _pendingKind = SessionState.Types.Kind.Unspecified;
            _pendingKindSession = null;
            restart = _restartRequested;
            _restartRequested = null;
        }

        // The SCM knows about a lock before WTS shows it; trust the notification for this tick.
        // A session nobody is logged on to is "locked" to WTS and the SCM alike; that is the
        // logon screen, not a student locking the PC, so it is neither reported nor shown.
        if (kind is SessionState.Types.Kind.Lock or SessionState.Types.Kind.Unlock)
        {
            snapshot = snapshot with { Locked = snapshot.HasUser ? kind == SessionState.Types.Kind.Lock : null };
            if (!snapshot.HasUser)
            {
                kind = SessionState.Types.Kind.Unspecified;
            }
        }

        var changed = !_published || snapshot != _snapshot;
        if (changed)
        {
            if (kind == SessionState.Types.Kind.Unspecified)
            {
                kind = Derive(_snapshot, snapshot, _published);
            }

            if (_published && (snapshot.SessionId != _snapshot.SessionId || snapshot.User != _snapshot.User))
            {
                restart ??= snapshot.User != _snapshot.User ? "the logged-on user changed" : "the interactive session changed";
            }

            _log.LogInformation("interactive session: {Snapshot}", snapshot);
        }

        // ---- supervise the helper

        var helper = _helper;
        if (helper is not null)
        {
            string? stop = null;
            var crashed = false;

            // Order matters: at logoff Windows ends the helper before this tick sees the
            // change, so an exit that coincides with a planned restart is the plan, not a crash.
            var planned = !snapshot.HasSession
                ? "there is no interactive session"
                : helper.SessionId != snapshot.SessionId
                    ? $"the console session moved from {helper.SessionId} to {snapshot.SessionId}"
                    : restart;

            if (planned is not null)
            {
                stop = planned;
            }
            else if (helper.TryGetExitCode(out var exitCode))
            {
                // Hold the verdict: a logoff notification that arrives within the grace turns
                // this exit into a planned restart instead of a reported crash.
                helper.ExitNoticedAt ??= now;
                if (now - helper.ExitNoticedAt.Value >= Defaults.HelperExitGrace)
                {
                    stop = $"session.exe exited with code {exitCode}";
                    crashed = true;
                }
            }
            else if (helper.Connected && now - helper.LastHeard > Defaults.HelperSilenceTimeout)
            {
                stop = $"session.exe has been silent for {(now - helper.LastHeard).TotalSeconds:0} s";
                crashed = true;
            }
            else if (!helper.Connected && now - helper.StartedAt > Defaults.HelperConnectTimeout)
            {
                stop = $"session.exe did not connect within {Defaults.HelperConnectTimeout.TotalSeconds:0} s";
                crashed = true;
            }

            if (stop is not null)
            {
                await StopHelperAsync(stop, token);
                helper = null;

                if (crashed)
                {
                    NoteCrash(now, stop);
                }
                else
                {
                    _nextSpawnAt = now + Defaults.HelperRestartDelay;
                }
            }
        }

        if (helper is null && snapshot.HasSession && now >= _nextSpawnAt && EnsureServer())
        {
            helper = Spawn(snapshot.SessionId!.Value, now);
        }

        // ---- tell the console

        var alive = helper is not null && (helper.Alive(now) || helper.InExitGrace(now));
        if (changed || alive != _helperAlivePublished || kind != SessionState.Types.Kind.Unspecified)
        {
            _link.PublishSessionState(new SessionState
            {
                Kind = kind,
                User = snapshot.User,
                SessionId = snapshot.SessionId ?? 0,
                HelperAlive = alive,
                Locked = snapshot.Locked ?? false,
            });

            _snapshot = snapshot;
            _published = true;
            _helperAlivePublished = alive;
        }
    }

    private static SessionState.Types.Kind Derive(SessionSnapshot before, SessionSnapshot after, bool hadBefore)
    {
        if (!hadBefore)
        {
            return SessionState.Types.Kind.Unspecified;
        }

        if (before.HasUser && !after.HasUser)
        {
            return SessionState.Types.Kind.Logoff;
        }

        if (!before.HasUser && after.HasUser)
        {
            return SessionState.Types.Kind.Logon;
        }

        if (before.Locked != after.Locked && after.Locked is { } locked)
        {
            return locked ? SessionState.Types.Kind.Lock : SessionState.Types.Kind.Unlock;
        }

        return SessionState.Types.Kind.Unspecified;
    }

    // ------------------------------------------------------------------ the helper

    /// <summary>
    /// A helper that dies repeatedly is reported once and retried slowly; one that dies once
    /// is back within <see cref="Defaults.HelperRestartDelay"/> (ROADMAP M2: "within 5 s").
    /// </summary>
    private void NoteCrash(DateTimeOffset now, string what)
    {
        _recentExits.Enqueue(now);
        while (_recentExits.Count > 0 && now - _recentExits.Peek() > Defaults.HelperCrashLoopWindow)
        {
            _recentExits.Dequeue();
        }

        if (_recentExits.Count >= Defaults.HelperCrashLoopThreshold)
        {
            _nextSpawnAt = now + Defaults.HelperCrashLoopBackoff;
            if (!_crashLoopReported)
            {
                _crashLoopReported = true;
                var message = $"session.exe keeps dying ({_recentExits.Count} times in {Defaults.HelperCrashLoopWindow.TotalSeconds:0} s; last: {what}); retrying every {Defaults.HelperCrashLoopBackoff.TotalSeconds:0} s. Screens, control and lock will not work on this PC until it stays up.";
                _log.LogError("{Message}", message);
                _link.Report(Event.Types.Severity.Error, "session.helper_crash_loop", message);
            }
        }
        else
        {
            _nextSpawnAt = now + Defaults.HelperRestartDelay;
            _log.LogWarning("{What}; restarting in {Seconds:0.#} s", what, Defaults.HelperRestartDelay.TotalSeconds);
            _link.Report(Event.Types.Severity.Warning, "session.helper_exited", $"{what}; restarting it.");
        }
    }

    private Helper? Spawn(uint sessionId, DateTimeOffset now)
    {
        try
        {
            var process = SessionLauncher.Start(_helperPath, string.Empty, sessionId);
            var helper = new Helper(process, sessionId, now);
            _helper = helper;
            helper.Pump = Task.Run(() => PumpAsync(helper, _server!, helper.Cancel.Token));
            _log.LogInformation("started session.exe (pid {Pid}) in session {Session}", process.Id, sessionId);
            _lastSpawnFailure = null;
            return helper;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            var message = $"Could not start {Defaults.SessionExecutableName} in session {sessionId}: {ex.Message}";
            _log.LogError("{Message}", message);
            if (_lastSpawnFailure != message)
            {
                _lastSpawnFailure = message;
                _link.Report(Event.Types.Severity.Error, "session.helper_spawn_failed", message);
            }

            NoteCrash(now, message);
            return null;
        }
    }

    private async Task StopHelperAsync(string why, CancellationToken token)
    {
        var helper = _helper;
        if (helper is null)
        {
            return;
        }

        _helper = null;
        _log.LogInformation("stopping session.exe (pid {Pid}): {Why}", helper.ProcessId, why);
        helper.Cancel.Cancel();

        try
        {
            if (!helper.Process.HasExited)
            {
                helper.Process.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _log.LogDebug(ex, "killing session.exe: {Message}", ex.Message);
        }

        if (helper.Pump is { } pump)
        {
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
            catch (Exception ex) when (ex is System.TimeoutException or OperationCanceledException)
            {
            }
        }

        if (_crashLoopReported && _recentExits.Count > 0 && DateTimeOffset.UtcNow - _recentExits.Peek() > Defaults.HelperCrashLoopWindow)
        {
            _crashLoopReported = false;
        }

        helper.Dispose();
        ResetServer();
    }

    // ------------------------------------------------------------------ the pipe

    /// <summary>
    /// One server instance for the life of the service, created before any helper runs so
    /// nobody else can claim the name (<c>CurrentUserOnly</c> also limits connections to
    /// SYSTEM, and the helper checks the server the same way). Reported once when the name
    /// is taken; retried every tick.
    /// </summary>
    private bool EnsureServer()
    {
        if (_server is not null)
        {
            return true;
        }

        try
        {
            _server = new NamedPipeServerStream(
                Defaults.SessionPipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            _pipeFailureReported = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_pipeFailureReported)
            {
                _pipeFailureReported = true;
                var message = $@"Cannot open the pipe \\.\pipe\{Defaults.SessionPipeName} for the session helper: {ex.Message}. Another process holds the name (a second agent instance?); the helper will not run until it is freed.";
                _log.LogError("{Message}", message);
                _link.Report(Event.Types.Severity.Error, "session.pipe_unavailable", message);
            }

            return false;
        }
    }

    /// <summary>Back to listening after a helper went away; a server that cannot be reused is replaced.</summary>
    private void ResetServer()
    {
        if (_server is null)
        {
            return;
        }

        try
        {
            if (_server.IsConnected)
            {
                _server.Disconnect();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _log.LogDebug(ex, "replacing the pipe server: {Message}", ex.Message);
            _server.Dispose();
            _server = null;
        }
    }

    private async Task PumpAsync(Helper helper, NamedPipeServerStream server, CancellationToken token)
    {
        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connectTimeout.CancelAfter(Defaults.HelperConnectTimeout);
                await server.WaitForConnectionAsync(connectTimeout.Token);
            }

            var hello = await PipeFraming.ReadAsync(server, HelperMessage.Parser, token);
            if (hello?.PayloadCase != HelperMessage.PayloadOneofCase.Hello)
            {
                _log.LogWarning("the first message on the pipe was {What}, not a hello; dropping the connection", hello?.PayloadCase.ToString() ?? "nothing");
                return;
            }

            if (hello.Hello.ProcessId != (uint)helper.ProcessId)
            {
                // A stale helper from a previous service instance, or an impostor: not ours.
                _log.LogWarning("pid {Pid} connected to the pipe but the helper we started is pid {Ours}; dropping it", hello.Hello.ProcessId, helper.ProcessId);
                return;
            }

            helper.Connected = true;
            helper.LastHeard = DateTimeOffset.UtcNow;
            helper.Version = hello.Hello.Version;
            _log.LogInformation("session.exe {Version} (pid {Pid}) connected from session {Session}", hello.Hello.Version, hello.Hello.ProcessId, hello.Hello.SessionId);

            await PipeFraming.WriteAsync(server, new ServiceMessage { Ping = new Ping { SentAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() } }, token);

            var announced = false;
            while (await PipeFraming.ReadAsync(server, HelperMessage.Parser, token) is { } message)
            {
                helper.LastHeard = DateTimeOffset.UtcNow;
                switch (message.PayloadCase)
                {
                    case HelperMessage.PayloadOneofCase.Status:
                        var status = message.Status;
                        var changedDesktop = helper.Status?.InputDesktop != status.InputDesktop;
                        helper.Status = status;
                        if (!announced)
                        {
                            announced = true;
                            _link.Report(Event.Types.Severity.Info, "session.helper_ready",
                                $"Session helper {helper.Version} is up in session {helper.SessionId} ({status.ScreenWidth}×{status.ScreenHeight}, desktop {status.InputDesktop}).");
                            Wake();
                        }
                        else if (changedDesktop)
                        {
                            _log.LogInformation("input desktop is now {Desktop}", status.InputDesktop);
                        }

                        break;

                    case HelperMessage.PayloadOneofCase.Event:
                        var relayed = message.Event;
                        _link.Report(relayed.Severity, relayed.Code.Length > 0 ? relayed.Code : "session.event", relayed.Message);
                        break;

                    default:
                        _log.LogDebug("session.exe sent {What}, which this build does not handle", message.PayloadCase);
                        break;
                }
            }

            _log.LogInformation("session.exe (pid {Pid}) closed the pipe", helper.ProcessId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or InvalidOperationException)
        {
            _log.LogWarning("the pipe to session.exe (pid {Pid}) ended: {Message}", helper.ProcessId, ex.Message);
        }
        finally
        {
            helper.Connected = false;
            Wake();
        }
    }

    /// <summary>One running instance of session.exe and what it has told us.</summary>
    private sealed class Helper : IDisposable
    {
        public Helper(Process process, uint sessionId, DateTimeOffset startedAt)
        {
            Process = process;
            ProcessId = process.Id;
            SessionId = sessionId;
            StartedAt = startedAt;
            LastHeard = startedAt;
        }

        public Process Process { get; }

        public int ProcessId { get; }

        public uint SessionId { get; }

        public DateTimeOffset StartedAt { get; }

        public CancellationTokenSource Cancel { get; } = new();

        public Task? Pump { get; set; }

        public volatile bool Connected;

        public DateTimeOffset LastHeard { get; set; }

        public string Version { get; set; } = string.Empty;

        public HelperStatus? Status { get; set; }

        public bool Alive(DateTimeOffset now) => Connected && now - LastHeard <= Defaults.HelperSilenceTimeout && !TryGetExitCode(out _);

        /// <summary>When the supervisor first saw the process gone; <c>null</c> while it runs.</summary>
        public DateTimeOffset? ExitNoticedAt { get; set; }

        /// <summary>Exited, but the verdict is still held for a planned reason to arrive.</summary>
        public bool InExitGrace(DateTimeOffset now) => ExitNoticedAt is { } at && now - at < Defaults.HelperExitGrace;

        public bool TryGetExitCode(out int exitCode)
        {
            try
            {
                if (Process.HasExited)
                {
                    exitCode = Process.ExitCode;
                    return true;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                exitCode = -1;
                return true;
            }

            exitCode = 0;
            return false;
        }

        public void Dispose()
        {
            Cancel.Dispose();
            Process.Dispose();
        }
    }
}
