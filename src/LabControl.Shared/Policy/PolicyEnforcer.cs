using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Shared.Policy;

/// <summary>
/// The platform-neutral half of policy enforcement on a PC (M6 portion 1, D-69): owns the
/// <see cref="PolicyJournal"/> over <c>policy.json</c> in the agent's data directory, the
/// kernel mutex every pass holds, the clock timer that ends a session at its deadline, and
/// the wiring to an <see cref="AgentLink"/> — <c>Overlay</c> in, <c>OverlayState</c> and
/// events out. The Windows service, the simulator and <c>agent.exe --restore-policy</c> all
/// drive this one class over their own <see cref="IMachineState"/>; only what the machine
/// <i>does</i> with a session is platform code.
/// <para>
/// <b>Nothing here throws, ever.</b> Every public entry point — <see cref="Start"/>,
/// <see cref="Apply"/>, <see cref="ReassertOverlay"/>, <see cref="NoteEnforcement"/>, the
/// timer tick and <see cref="DisposeAsync"/> — runs inside <see cref="Safe"/>: an exception
/// anywhere in it is logged, reported as <see cref="PolicyEvents.EnforcerFailed"/> and the
/// method returns, because the service dials the link right after <see cref="Start"/> and a
/// corrupt <c>policy.json</c> must cost a warning, not an offline PC. A mutex that is held
/// elsewhere for the whole wait is reported as <see cref="PolicyEvents.Busy"/> and the pass
/// is skipped (the timer tries again shortly), and the link's read thread is never blocked —
/// an <c>Overlay</c> is handed to a worker at once.
/// </para>
/// </summary>
public sealed class PolicyEnforcer : IAsyncDisposable
{
    private readonly AgentLink _link;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string? _mutexName;
    private readonly TimeSpan _mutexWait;
    private readonly Timer _timer;
    private readonly Lock _lock = new();
    private readonly Action<Overlay, string> _onOverlay;
    private bool _started;
    private bool _stopped;
    private bool _mutexUnavailableLogged;

    /// <param name="link">The link the enforcer answers on; it subscribes to <see cref="AgentLink.OverlayReceived"/> on <see cref="Start"/>.</param>
    /// <param name="machine">What the PC does with a session — Windows, or the simulator.</param>
    /// <param name="dataDirectory">Where <c>policy.json</c> lives (<see cref="Defaults.PolicyFileName"/>).</param>
    /// <param name="labId">This PC's lab; a document naming another lab is not this PC's.</param>
    /// <param name="mutexName">
    /// The kernel mutex name; <see cref="Defaults.PolicyMutexName"/> when <c>null</c>. A
    /// simulator passes a name of its own so thirty simulated PCs in one process, and the
    /// tests, never queue behind one another or a real agent.
    /// </param>
    /// <param name="mutexWait">How long a pass waits for the mutex; <see cref="Defaults.PolicyMutexWait"/> when <c>null</c>.</param>
    public PolicyEnforcer(AgentLink link, IMachineState machine, string dataDirectory, string labId, ILogger log,
        Func<DateTimeOffset>? clock = null, string? mutexName = null, TimeSpan? mutexWait = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(labId);
        ArgumentNullException.ThrowIfNull(log);
        _link = link;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _mutexName = mutexName;
        _mutexWait = mutexWait ?? Defaults.PolicyMutexWait;
        DocumentPath = Path.Combine(dataDirectory, Defaults.PolicyFileName);
        Journal = new PolicyJournal(machine, labId, PolicyJournal.FileReader(DocumentPath), PolicyJournal.FileWriter(DocumentPath));
        Journal.Reported += OnReported;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        _onOverlay = OnOverlay;
    }

    /// <summary>The journal, for a caller that needs its snapshot (the simulator's status line, the tests).</summary>
    public PolicyJournal Journal { get; }

    /// <summary><c>policy.json</c>'s full path.</summary>
    public string DocumentPath { get; }

    /// <summary>Restore passes and sessions lifted by the clock, for the tests; raised after the state was published.</summary>
    public event Action<IReadOnlyList<PolicySession>>? Lifted;

    /// <summary>
    /// The service-start pass (D-69 steps 0–6) under the mutex, then the link subscription
    /// and the deadline timer. Called <b>before</b> the link dials, so the first
    /// <c>OverlayState</c> after <c>Welcome</c> already says whether a lock survived the
    /// restart. Returns what the pass made of the machine, or <c>null</c> when the mutex was
    /// held elsewhere for the whole wait (reported as <see cref="PolicyEvents.Busy"/>; the
    /// enforcer still subscribes, and the next timer pass restores nothing — the document is
    /// left to whoever holds the mutex), when it was already started, or when the pass
    /// itself failed (reported as <see cref="PolicyEvents.EnforcerFailed"/>). Never throws.
    /// </summary>
    public PolicyRestoreOutcome? Start()
    {
        lock (_lock)
        {
            if (_started)
            {
                return null;
            }

            _started = true;
        }

        PolicyRestoreOutcome? outcome = null;
        Safe("start", () =>
        {
            Guarded("start", () =>
            {
                outcome = Journal.Start(_clock().ToUnixTimeSeconds());
                _log.LogInformation("policy restore at start: machine {Open}, {Reapplied} re-applied, {Dropped} dropped",
                    outcome.MachineOpen ? "open" : "NOT proven open", outcome.Reapplied.Count, outcome.Dropped.Count);
            });
        });

        // Subscribed and published whatever the pass did: a link that never hears the enforcer
        // would leave the console waiting on every Overlay.
        Safe("start", () => _link.OverlayReceived += _onOverlay);
        Safe("start", () => Publish(Journal.OverlayStateOf(_clock().ToUnixTimeSeconds())));
        Rearm();
        return outcome;
    }

    /// <summary>
    /// Re-asserts the current lock to a machine that lost it — a helper restarted at logon
    /// or logoff (D-30, D-70 item 4). A no-op without a lock; publishes the state either way
    /// so the console sees the helper gap close. Never throws.
    /// </summary>
    public void ReassertOverlay()
    {
        Safe("reassert", () => Guarded("reassert", () => Publish(Journal.ReassertOverlay(_clock().ToUnixTimeSeconds()))));
    }

    /// <summary>
    /// What the helper says about its own enforcement (<c>HelperMessage.overlay_state</c>,
    /// with every status): merged into the next <c>OverlayState</c>, and published only when
    /// the word differs from what the console last heard — thirty PCs saying "still fine"
    /// every two seconds is not news. The comparison is against the state last published,
    /// not a remembered pair, so whatever changed the published state in between is
    /// followed by the helper's next report, never by a stale one. The word itself is
    /// reset where a helper's departure is known — the supervisor says
    /// <see cref="OverlayReasons.HelperDown"/> when one leaves, and the next helper reports on
    /// every ping. <paramref name="reason"/> is <see cref="OverlayReasons"/> codes, not words.
    /// Never throws.
    /// </summary>
    public void NoteEnforcement(bool inputBlocked, string reason)
    {
        Safe("enforcement", () =>
        {
            reason ??= string.Empty;
            lock (_lock)
            {
                if (_stopped)
                {
                    return;
                }
            }

            var state = Journal.NoteEnforcement(inputBlocked, reason, _clock().ToUnixTimeSeconds());
            var last = _link.LatestOverlayState;
            if (last is not null && last.Kind == state.Kind && last.InputBlocked == state.InputBlocked
                && string.Equals(last.Reason, state.Reason, StringComparison.Ordinal) && last.Problem.Length == 0)
            {
                return;
            }

            Publish(state);
        });
    }

    /// <summary>
    /// The <c>OnStop</c> restore (D-69 item 5) under the mutex; the document is not cleared.
    /// Safe to call twice. Never throws.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }

        Safe("stop", () => _link.OverlayReceived -= _onOverlay);
        try
        {
            await _timer.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "disposing the policy timer failed: {Message}", ex.Message);
        }

        if (_started)
        {
            Safe("stop", () => Guarded("stop", () =>
            {
                var outcome = Journal.Stop(_clock().ToUnixTimeSeconds());
                _log.LogInformation("policy restore at stop: machine {Open}", outcome.MachineOpen ? "open" : "NOT proven open");
            }));
        }

        Journal.Reported -= OnReported;
    }

    // ------------------------------------------------------------------ the pieces

    /// <summary>From the link's read thread: handed to a worker at once, never waited for.</summary>
    private void OnOverlay(Overlay overlay, string instanceId) => _ = Task.Run(() => Apply(overlay, instanceId));

    /// <summary>
    /// An <c>Overlay</c> already past the ownership gate, applied under the mutex and answered
    /// with the state the PC shows afterwards; the deadline timer follows. Synchronous, for a
    /// host that has its own worker (the link's handler runs it on the pool). Never throws:
    /// a pass that fails, like one that finds the mutex busy, answers the console with a
    /// <c>problem</c> rather than leaving it waiting for a state that never comes.
    /// </summary>
    public void Apply(Overlay overlay, string setByInstance)
    {
        Safe("overlay", () =>
        {
            if (overlay is null || setByInstance is null)
            {
                _log.LogWarning("an overlay with no {What} was handed to the enforcer; ignored", overlay is null ? "message" : "instance");
                return;
            }

            var now = _clock().ToUnixTimeSeconds();
            var outcome = Guarded("overlay", () => Publish(Journal.SetOverlay(overlay, setByInstance, now)));
            if (outcome != PassOutcome.Ran)
            {
                // The console must hear that nothing happened rather than wait for a state
                // that never comes. The enforcer's own latest state is untouched.
                Publish(new OverlayState
                {
                    Kind = Overlay.Types.Kind.Unspecified,
                    Problem = outcome == PassOutcome.Busy
                        ? PolicyEvents.Text("Busy", (long)_mutexWait.TotalSeconds)
                        : PolicyEvents.Text("EnforcerFailed", "overlay", _lastFailure),
                }, remember: false);
            }

            Rearm();
        });
    }

    private void Tick()
    {
        Safe("tick", () =>
        {
            IReadOnlyList<PolicySession> sessions = [];
            Guarded("tick", () =>
            {
                var now = _clock().ToUnixTimeSeconds();
                sessions = Journal.Tick(now);
                if (sessions.Count > 0)
                {
                    Publish(Journal.OverlayStateOf(now));
                }
            });

            if (sessions.Count > 0)
            {
                try
                {
                    Lifted?.Invoke(sessions);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "a lifted-session listener failed: {Message}", ex.Message);
                }
            }

            Rearm(retryIfNothingLifted: true);
        });
    }

    /// <summary>
    /// Arms the timer for the earliest deadline the journal holds, or stops it. A tick whose
    /// mutex was busy, or whose lift failed, is retried in a few seconds rather than never.
    /// Never throws.
    /// </summary>
    private void Rearm(bool retryIfNothingLifted = false)
    {
        Safe("rearm", () =>
        {
            lock (_lock)
            {
                if (_stopped)
                {
                    return;
                }

                try
                {
                    var deadline = Journal.NextDeadline;
                    if (deadline is null)
                    {
                        _timer.Change(Timeout.Infinite, Timeout.Infinite);
                        return;
                    }

                    var now = _clock().ToUnixTimeSeconds();
                    var due = deadline.Value <= now
                        ? (retryIfNothingLifted ? RetryDelay : TimeSpan.Zero)
                        : TimeSpan.FromSeconds(Math.Min(deadline.Value - now, MaxTimerSeconds));
                    if (due < TimeSpan.Zero)
                    {
                        due = TimeSpan.Zero;
                    }

                    _timer.Change(due, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        });
    }

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>A timer cannot be set more than ~24 days out; a session that far off is re-checked then.</summary>
    private const long MaxTimerSeconds = 20L * 24 * 3600;

    /// <summary>
    /// Hands a state to the link. A state that describes the screen is remembered as the
    /// latest (re-sent after every <c>Welcome</c>); a bare problem answer is not, so a busy
    /// or failed pass never overwrites the truth the console last heard.
    /// </summary>
    private void Publish(OverlayState state, bool remember = true)
    {
        try
        {
            if (remember)
            {
                _link.PublishOverlayState(state);
            }
            else
            {
                _link.AnswerOverlayProblem(state);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "could not publish the overlay state: {Message}", ex.Message);
        }
    }

    private enum PassOutcome
    {
        Ran,
        Busy,
        Failed,
    }

    private string _lastFailure = string.Empty;

    /// <summary>
    /// One pass under the mutex. <see cref="PassOutcome.Busy"/> when the mutex was held
    /// elsewhere for the whole wait (reported as <see cref="PolicyEvents.Busy"/>, the pass
    /// skipped); <see cref="PassOutcome.Failed"/> when the pass threw (logged and reported,
    /// never thrown); <see cref="PassOutcome.Ran"/> otherwise.
    /// </summary>
    private PassOutcome Guarded(string what, Action pass)
    {
        using var lease = PolicyMutex.TryAcquire(_mutexWait, _mutexName);
        switch (lease.Outcome)
        {
            case PolicyMutexOutcome.Busy:
                var message = PolicyEvents.Text("Busy", (long)_mutexWait.TotalSeconds);
                _log.LogWarning("policy {What}: {Message}", what, message);
                Report(Event.Types.Severity.Warning, PolicyEvents.Busy, message);
                return PassOutcome.Busy;

            case PolicyMutexOutcome.Unavailable:
                // A mutex that cannot be opened at all (an odd platform, a DACL) must not stop
                // the pass: a pass that is not serialized is still better than a PC that never
                // restores. Said once, not on every tick.
                if (!_mutexUnavailableLogged)
                {
                    _mutexUnavailableLogged = true;
                    _log.LogWarning("the policy mutex is unavailable ({Problem}); running {What} without it", lease.Problem, what);
                }

                break;
        }

        try
        {
            pass();
            return PassOutcome.Ran;
        }
        catch (Exception ex)
        {
            _lastFailure = ex.Message;
            Failed(what, ex);
            return PassOutcome.Failed;
        }
    }

    /// <summary>
    /// The outermost net: whatever escapes a public entry point is logged, reported and
    /// dropped here. The service loop, the link's read thread and the timer never see it.
    /// </summary>
    private void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _lastFailure = ex.Message;
            Failed(what, ex);
        }
    }

    private void Failed(string what, Exception ex)
    {
        try
        {
            _log.LogError(ex, "policy {What} failed unexpectedly: {Message}", what, ex.Message);
            Report(Event.Types.Severity.Error, PolicyEvents.EnforcerFailed, PolicyEvents.Text("EnforcerFailed", what, ex.Message));
        }
        catch
        {
            // Logging and reporting are the last resort; if they fail too there is nothing left to do.
        }
    }

    private void Report(Event.Types.Severity severity, string code, string message)
    {
        try
        {
            _link.Report(severity, code, message);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "could not report {Code}: {Message}", code, ex.Message);
        }
    }

    private void OnReported(PolicyEvent reported) => Report(reported.Severity, reported.Code, reported.Message);
}
