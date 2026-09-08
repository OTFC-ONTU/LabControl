using System.Diagnostics;
using LabControl.Shared.Identity;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>Where the console stands with respect to a lab (M5, D-57 item 1).</summary>
public enum ActivationState
{
    Idle = 0,
    Activating = 1,
    Active = 2,
    Deactivating = 3,
    Failed = 4,
}

/// <summary>The controller's state, the lab it concerns and — for <see cref="ActivationState.Failed"/> — why.</summary>
public sealed record ActivationStatus(ActivationState State, string? LabId, string? Error);

/// <summary>What one <see cref="ActiveLabController.ActivateAsync"/> call ended with.</summary>
/// <param name="Ok">The lab is active (or already was).</param>
/// <param name="Superseded">A later request replaced this one before it started; nothing was touched for it.</param>
public sealed record ActivationResult(string LabId, bool Ok, bool Superseded, string? Error)
{
    public static ActivationResult Success(string labId) => new(labId, true, false, null);

    public static ActivationResult Replaced(string labId) => new(labId, false, true, null);

    public static ActivationResult Failure(string labId, string error) => new(labId, false, false, error);
}

/// <summary>
/// How long one switch took (M5, D-57 item 3; design §4.4): departure of the old session,
/// the new server up, the first PC linked, every PC the lab already knew linked. Logged as
/// the switch progresses; the tests assert the 2 s cached-mosaic target on the first two.
/// </summary>
public sealed class SwitchTimings
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public SwitchTimings(string? fromLabId, string toLabId)
    {
        FromLabId = fromLabId;
        ToLabId = toLabId;
    }

    public string? FromLabId { get; }

    public string ToLabId { get; }

    /// <summary>Milliseconds until the previous session had released everything (0 when there was none).</summary>
    public double DepartureMs { get; internal set; }

    /// <summary>Milliseconds spent opening the profile: documents, the instance key from the OS keystore (a Keychain prompt lands here).</summary>
    public double OpenMs { get; internal set; }

    /// <summary>Milliseconds spent building the session: registry, scripts, logs, screens.</summary>
    public double BuildMs { get; internal set; }

    /// <summary>Milliseconds spent starting the server and the beacons.</summary>
    public double StartMs { get; internal set; }

    /// <summary>
    /// Milliseconds from the request until the new session existed with its saved roster
    /// (<c>lab.json</c>) — the moment the main window can show the cached mosaic, before the
    /// server is up (D-57 item 1, the 2 s target).
    /// </summary>
    public double MosaicReadyMs { get; internal set; }

    /// <summary>Milliseconds from the request until the new session served and beaconed.</summary>
    public double ServerUpMs { get; internal set; }

    /// <summary>Milliseconds from the request until the first PC linked; <c>null</c> until then.</summary>
    public double? FirstAgentMs { get; internal set; }

    /// <summary>Milliseconds until every PC in the lab's saved roster was linked; <c>null</c> until then, and for a lab without PCs.</summary>
    public double? AllKnownAgentsMs { get; internal set; }

    /// <summary>The PC count in <c>lab.json</c> at activation — the mosaic that was shown from the cache.</summary>
    public int KnownAgents { get; internal set; }

    internal double Elapsed => _clock.Elapsed.TotalMilliseconds;

    public override string ToString() =>
        $"switch {(FromLabId is null ? "(idle)" : FromLabId)} -> {ToLabId}: departure {DepartureMs:0} ms, mosaic {MosaicReadyMs:0} ms, server up {ServerUpMs:0} ms " +
        $"(open {OpenMs:0}, build {BuildMs:0}, start {StartMs:0}), " +
        $"first PC {(FirstAgentMs is { } f ? $"{f:0} ms" : "not yet")}, all {KnownAgents} known PCs {(AllKnownAgentsMs is { } a ? $"{a:0} ms" : "not yet")}";
}

/// <summary>
/// The one place that owns the running <see cref="LabSession"/> (M5, D-53 item 2, D-57).
/// Never two: activating a lab releases the current one first — beacons, links, port, key —
/// and only then opens the next. Requests are serialised; a burst of selections ends with
/// exactly one activation, of the last request, and the skipped ones never half-start.
/// <see cref="StatusChanged"/> is raised on whichever thread finished the step; the UI posts.
/// </summary>
public sealed class ActiveLabController : IAsyncDisposable
{
    private readonly ConsoleBootstrap _bootstrap;
    private readonly ILogger _log;
    private readonly Func<OpenedLab, CancellationToken, Task<bool>>? _remintPrompt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private long _generation;
    private Request _requested = new(null, 0);
    private LabSession? _active;
    private ActivationStatus _status = new(ActivationState.Idle, null, null);

    /// <summary>The latest wish: which lab (or none, for a deactivation) and its sequence number.</summary>
    private sealed record Request(string? LabId, long Generation);

    /// <param name="bootstrap">Opens profiles and builds sessions; owns the index.</param>
    /// <param name="loggers">Log sink.</param>
    /// <param name="remintPrompt">
    /// Asked when an administrator profile's console leaf is due for re-minting (ARCHITECTURE
    /// §3.8): it must unlock the vault of the opened lab and return <c>true</c>, or return
    /// <c>false</c> to keep the current leaf. <c>null</c> keeps the leaf and logs a warning.
    /// The token is cancelled when the console shuts down while the prompt is up: the
    /// dialog must close and answer <c>false</c>.
    /// </param>
    public ActiveLabController(ConsoleBootstrap bootstrap, ILoggerFactory loggers, Func<OpenedLab, CancellationToken, Task<bool>>? remintPrompt = null)
    {
        _bootstrap = bootstrap;
        _log = loggers.CreateLogger<ActiveLabController>();
        _remintPrompt = remintPrompt;
    }

    /// <summary>The running session, or <c>null</c>. At most one, ever.</summary>
    public LabSession? Active => Volatile.Read(ref _active);

    public ActivationStatus Status => Volatile.Read(ref _status);

    /// <summary>Raised on a background thread (or the caller's); the UI posts to its own.</summary>
    public event Action<ActivationStatus>? StatusChanged;

    /// <summary>
    /// A session exists with its saved roster and is about to start serving (D-57 item 1):
    /// the app shows the main window on it straight away — every tile offline, the toolbar
    /// disabled, "Connecting…" in the lab chip — so the cached mosaic is on screen before
    /// the server binds. Raised on a background thread. <see cref="Active"/> is still
    /// <c>null</c>; a failure after this ends in <see cref="ActivationState.Failed"/> and
    /// the window goes with it.
    /// </summary>
    public event Action<LabSession>? SessionBuilt;

    /// <summary>A session started serving. Tests use it to check that nothing leaks; the UI does not need it.</summary>
    public event Action<LabSession>? SessionStarted;

    /// <summary>The timings of the latest switch, for the log line, the tests and the drill.</summary>
    public SwitchTimings? LastSwitch { get; private set; }

    /// <summary>How many sessions were started so far: a burst of requests must add exactly one.</summary>
    public int Activations { get; private set; }

    /// <summary>What leaving now would interrupt; empty when nothing is active.</summary>
    public Task<DepartureReport> DescribeDepartureAsync() =>
        Task.FromResult(Active is { IsDisposed: false } session ? session.DescribeDeparture() : DepartureReport.Empty);

    /// <summary>
    /// Makes <paramref name="labId"/> the active lab: a no-op when it already is; otherwise
    /// <see cref="ActivationState.Activating"/>, release the current session, open the profile,
    /// start the new session, mark the profile used, <see cref="ActivationState.Active"/>. Any
    /// failure disposes what was built and ends in <see cref="ActivationState.Failed"/> with
    /// <see cref="Active"/> <c>null</c>. A request overtaken by a later one returns
    /// <see cref="ActivationResult.Superseded"/> without touching anything.
    /// </summary>
    public async Task<ActivationResult> ActivateAsync(string labId, CancellationToken cancellationToken = default)
    {
        var request = new Request(labId, Interlocked.Increment(ref _generation));
        Volatile.Write(ref _requested, request);

        // Let a burst of selections all register before the first one takes the gate, so
        // A, B, C in one go is one activation of C and nothing is half-started for A.
        await Task.Yield();

        // Shutting the console down cancels whatever activation is in flight, including a
        // prompt it is waiting on; the caller's own token still counts.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linked.Token;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref _requested), request))
            {
                // A later selection is queued behind us; it will do the work. Skipping means
                // the room is never taken for a lab the teacher has already moved on from.
                _log.LogDebug("Activation of {LabId} skipped: a later request supersedes it", labId);
                return ActivationResult.Replaced(labId);
            }

            var current = _active;
            if (current is { IsDisposed: false } && string.Equals(current.LabId, labId, StringComparison.OrdinalIgnoreCase))
            {
                Set(new ActivationStatus(ActivationState.Active, labId, null));
                return ActivationResult.Success(labId);
            }

            var timings = new SwitchTimings(current?.LabId, labId);
            OpenedLab? opened = null;
            ConsoleInstance? instance = null;
            LabSession? session = null;
            try
            {
                Set(new ActivationStatus(ActivationState.Activating, labId, null));

                // Release before acquire (D-53 item 2): the old room is let go completely —
                // beacons, links, port, key — before the new lab's server binds the same port.
                // Inside the try: whatever the release throws lands in Failed with a retry,
                // never in a controller stuck on Activating. Off the caller's thread: the
                // server's 2 s stop budget and the housekeeping join must not freeze the UI.
                await Task.Run(() => CloseActiveAsync("the console left this lab"), CancellationToken.None).ConfigureAwait(false);
                timings.DepartureMs = timings.Elapsed;

                token.ThrowIfCancellationRequested();
                var phase = Stopwatch.StartNew();
                opened = await Task.Run(() => _bootstrap.OpenExisting(labId), token).ConfigureAwait(false);
                instance = opened.Instance;
                timings.OpenMs = phase.Elapsed.TotalMilliseconds;

                if (opened.NeedsRemint)
                {
                    if (_remintPrompt is not null && await _remintPrompt(opened, token).ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested();
                        var reminted = _bootstrap.Remint(opened.Vault, opened.Document);
                        instance.Dispose();
                        instance = reminted;
                        _log.LogInformation("Console leaf of lab {LabId} re-minted; new serial {Serial}", labId, instance.CertificateSerial);
                    }
                    else
                    {
                        _log.LogWarning("Console leaf of lab {LabId} expires on {When} and was not re-minted", labId, instance.ExpiresAt);
                    }
                }

                token.ThrowIfCancellationRequested();
                phase.Restart();
                var built = opened;
                var withInstance = instance;
                session = await Task.Run(() => _bootstrap.Build(built, withInstance), token).ConfigureAwait(false);
                timings.BuildMs = phase.Elapsed.TotalMilliseconds;
                timings.KnownAgents = session.Registry.Document.Machines.Count;
                timings.MosaicReadyMs = timings.Elapsed;
                Watch(session, timings);

                // The cached mosaic first (D-57 item 1): the app puts the roster on screen now.
                SessionBuilt?.Invoke(session);

                token.ThrowIfCancellationRequested();
                phase.Restart();
                var starting = session;
                await Task.Run(() => starting.StartAsync(), token).ConfigureAwait(false);
                timings.StartMs = phase.Elapsed.TotalMilliseconds;
                timings.ServerUpMs = timings.Elapsed;

                _bootstrap.Profiles.Touch(labId, session.Now, timings.KnownAgents);

                Volatile.Write(ref _active, session);
                Activations++;
                LastSwitch = timings;
                _log.LogInformation("{Timings}", timings);
                SessionStarted?.Invoke(session);
                Set(new ActivationStatus(ActivationState.Active, labId, null));
                return ActivationResult.Success(labId);
            }
            catch (Exception ex)
            {
                // Whatever went wrong — a corrupt document, a port still busy, a keystore that
                // refused, a caller that gave up — the rule is one thing: never a half-active
                // lab. Everything built so far is released; a failure leaves Failed with a retry
                // in the chooser, a cancellation leaves Idle.
                var cancelled = ex is OperationCanceledException && token.IsCancellationRequested;
                if (cancelled)
                {
                    _log.LogInformation("Activation of lab {LabId} was cancelled", labId);
                }
                else
                {
                    _log.LogError(ex, "Lab {LabId} could not be activated", labId);
                }

                if (session is not null)
                {
                    await CloseQuietlyAsync(session, "the lab could not be opened").ConfigureAwait(false);
                }
                else
                {
                    opened?.Vault.Dispose();
                    instance?.Dispose();
                }

                Volatile.Write(ref _active, null);
                LastSwitch = timings;
                if (cancelled)
                {
                    Set(new ActivationStatus(ActivationState.Idle, null, null));
                    throw;
                }

                Set(new ActivationStatus(ActivationState.Failed, labId, ex.Message));
                return ActivationResult.Failure(labId, ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases the active lab (D-57 item 2) and ends <see cref="ActivationState.Idle"/>. A
    /// selection made after this request wins over it, exactly as a later selection wins over
    /// an earlier one.
    /// </summary>
    public async Task DeactivateAsync(string reason)
    {
        var request = new Request(null, Interlocked.Increment(ref _generation));
        Volatile.Write(ref _requested, request);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref _requested), request))
            {
                return;
            }

            var current = _active;
            if (current is null)
            {
                Set(new ActivationStatus(ActivationState.Idle, null, null));
                return;
            }

            Set(new ActivationStatus(ActivationState.Deactivating, current.LabId, null));
            await Task.Run(() => CloseActiveAsync(reason), CancellationToken.None).ConfigureAwait(false);
            Set(new ActivationStatus(ActivationState.Idle, null, null));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Cancels an activation in flight — and the prompt it may be waiting on — then releases
    /// the active lab. Never blocks the caller's thread on the release itself.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_shutdown.IsCancellationRequested)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }

        await DeactivateAsync("the console is closing").ConfigureAwait(false);
        _gate.Dispose();
        _shutdown.Dispose();
    }

    private async Task CloseActiveAsync(string reason)
    {
        var session = Interlocked.Exchange(ref _active, null);
        if (session is not null)
        {
            await CloseQuietlyAsync(session, reason).ConfigureAwait(false);
        }
    }

    private async Task CloseQuietlyAsync(LabSession session, string reason)
    {
        try
        {
            await session.CloseAsync(reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Releasing must not stop a switch: the session is gone as far as the controller
            // is concerned (its own close guards every step and always locks the key), and
            // whatever escaped — a Disposed subscriber, say — is only worth a line in the log.
            _log.LogWarning(ex, "Releasing lab '{Lab}' reported an error", session.LabName);
        }
    }

    /// <summary>Fills the link timings as PCs arrive; nothing here references the controller's state.</summary>
    private void Watch(LabSession session, SwitchTimings timings)
    {
        session.AgentLinked += _ =>
        {
            var changed = false;
            if (timings.FirstAgentMs is null)
            {
                timings.FirstAgentMs = timings.Elapsed;
                changed = true;
            }

            if (timings.AllKnownAgentsMs is null && timings.KnownAgents > 0 && session.Linked.Count >= timings.KnownAgents)
            {
                timings.AllKnownAgentsMs = timings.Elapsed;
                changed = true;
            }

            if (changed)
            {
                _log.LogInformation("{Timings}", timings);
            }
        };
    }

    private void Set(ActivationStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(status);
    }
}
