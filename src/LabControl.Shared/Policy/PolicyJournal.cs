using System.Globalization;
using System.Text.Json;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Policy;

/// <summary>What one restore pass (service start, <c>--restore-policy</c>, <c>OnStop</c>) made of the machine.</summary>
/// <param name="MachineOpen">
/// True only when the unconditional restore, the refine and the read-back all succeeded and
/// no lift failed: the PC is <i>proven</i> as open as Windows defaults. False means "as open
/// as possible", which is not the same thing and is never reported as such (D-69 step 4).
/// </param>
/// <param name="Reapplied">Sessions that were still valid and are back in force.</param>
/// <param name="Dropped">Sessions the document held that are not in force: expired, or refused by the machine.</param>
public sealed record PolicyRestoreOutcome(bool MachineOpen, IReadOnlyList<PolicySession> Reapplied, IReadOnlyList<PolicySession> Dropped);

/// <summary>
/// The one policy journal on a PC (M6, D-69): the sessions in force, the settings they
/// displaced, and the state machine that restores the machine first and re-applies second.
/// It is pure over its three collaborators — an <see cref="IMachineState"/>, a document
/// reader and a document writer — so the whole of §0 runs on the Mac against a fake machine,
/// and the Windows service, the simulator and the watchdog task all drive the same code.
/// <para>
/// Nothing here throws. Every machine call, every read and every write is wrapped; a
/// failure is a <see cref="PolicyEvent"/> on <see cref="Reported"/> and a false
/// <see cref="MachineOpen"/>, never an exception out of the service loop. The caller holds
/// <see cref="PolicyMutex"/> around the passes; the journal serializes its own operations.
/// </para>
/// </summary>
public sealed class PolicyJournal
{
    private readonly IMachineState _machine;
    private readonly string _labId;
    private readonly Func<string?> _read;
    private readonly Action<PolicyDocument> _write;
    private readonly Lock _lock = new();

    private ClassroomPolicy _policy = new() { Displaced = new DisplacedSettings() };
    private bool _machineOpen;
    private bool _stopped;
    private bool _inputBlocked;
    private string _overlayReason = string.Empty;

    /// <param name="machine">The machine this journal drives.</param>
    /// <param name="labId">This PC's lab; a document naming another lab is not this PC's.</param>
    /// <param name="readDocument">
    /// Returns the text of <c>policy.json</c>, or <c>null</c> when there is none. A delegate,
    /// so a disk error (it may throw) takes exactly the path a corrupt file takes.
    /// </param>
    /// <param name="writeDocument">Saves the document; may throw, which is reported as <see cref="PolicyEvents.SaveFailed"/>.</param>
    public PolicyJournal(IMachineState machine, string labId, Func<string?> readDocument, Action<PolicyDocument> writeDocument)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(labId);
        ArgumentNullException.ThrowIfNull(readDocument);
        ArgumentNullException.ThrowIfNull(writeDocument);
        _machine = machine;
        _labId = labId;
        _read = readDocument;
        _write = writeDocument;
    }

    /// <summary>A reader over a file: the text, or <c>null</c> when the file does not exist.</summary>
    public static Func<string?> FileReader(string path) => () => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>A writer over a file, atomic like every other document (<see cref="JsonStore.Save{T}"/>).</summary>
    public static Action<PolicyDocument> FileWriter(string path) => document => JsonStore.Save(path, document, PolicyDocument.Migrations);

    /// <summary>Every event the teacher should see, in the order it happened; raised synchronously, handlers must not throw.</summary>
    public event Action<PolicyEvent>? Reported;

    /// <summary>
    /// True only after a restore pass that fully succeeded and while no lift has failed since.
    /// Never true on a failed lift, whatever else succeeded (D-69 step 4).
    /// </summary>
    public bool MachineOpen
    {
        get
        {
            lock (_lock)
            {
                return _machineOpen;
            }
        }
    }

    /// <summary>A copy of what is held right now; safe to read on any thread.</summary>
    public ClassroomPolicy Snapshot()
    {
        lock (_lock)
        {
            return Copy(_policy);
        }
    }

    /// <summary>The earliest moment a held session must be lifted, or <c>null</c> when nothing is held; the service's timer.</summary>
    public long? NextDeadline
    {
        get
        {
            lock (_lock)
            {
                long? next = null;
                foreach (var session in _policy.Sessions)
                {
                    next = next is null ? session.DeadlineUnix : Math.Min(next.Value, session.DeadlineUnix);
                }

                return next;
            }
        }
    }

    // ------------------------------------------------------------------ restore passes

    /// <summary>
    /// Service start and <c>agent.exe --restore-policy</c>: D-69 steps 0–6. The machine is
    /// restored <b>before</b> the document is opened, whatever the document says or fails to
    /// say; the document only decides what is re-applied afterwards; and the document is
    /// saved at the end whatever happened.
    /// </summary>
    public PolicyRestoreOutcome Start(long nowUnix)
    {
        lock (_lock)
        {
            _stopped = false;

            // Steps 0–1: Windows defaults, document-independent. A stale allow is harmless and
            // a stale block is a brick, so nothing that follows may depend on what is read.
            var open = Call(nameof(IMachineState.RestoreDefaults), _machine.RestoreDefaults, PolicyEvents.RestoreFailed, "RestoreFailed");

            // Step 2: only now the document.
            var loaded = ReadDocument();
            var reapplied = new List<PolicySession>();
            var dropped = new List<PolicySession>();

            // The in-memory policy starts empty and is filled with what survives; the
            // displaced settings are carried over until a restore confirms them gone.
            var policy = new ClassroomPolicy { Displaced = new DisplacedSettings() };

            // Step 3: refine from the document. Step 4: read-back. The originals are dropped
            // only once a restore is *confirmed* — a refine that ran and a read-back that
            // failed is not a confirmed restore, and the teacher's static DNS is not lost for
            // a netsh hiccup (D-69 item 2, M2 of the foundation review).
            var displaced = loaded?.Displaced is { IsEmpty: false } originals ? originals : null;
            var refined = displaced is null || Call(nameof(IMachineState.RefineRestore), () => _machine.RefineRestore(displaced), PolicyEvents.RestoreFailed, "RefineFailed");
            var confirmed = Call(nameof(IMachineState.Confirm), _machine.Confirm, PolicyEvents.RestoreFailed, "ConfirmFailed");
            open &= refined && confirmed;
            if (displaced is not null && !(refined && confirmed))
            {
                policy.Displaced = displaced;
            }

            // Step 5: only now the clock. Sessions are re-applied whether or not the restore
            // was confirmed: an unconfirmed restore is "as open as possible", and a policy the
            // teacher set is still the teacher's — MachineOpen stays false and says so.
            if (loaded is not null)
            {
                if (loaded.Exam is { } exam)
                {
                    Reapply(exam, Defaults.ExamHardLimit, nowUnix, nameof(IMachineState.ApplyExam), _machine.ApplyExam, () => policy.Exam = exam, reapplied, dropped);

                    if (loaded.ExamInternet is { } examInternet)
                    {
                        // The exam's internet switch lives and dies with the exam (D-76).
                        if (policy.Exam is null)
                        {
                            DropWithExam(examInternet, dropped);
                        }
                        else
                        {
                            var ceiling = TimeSpan.FromSeconds(Math.Max(0, Math.Min((long)Defaults.ExamHardLimit.TotalSeconds, exam.HardLimitUnix - nowUnix)));
                            Reapply(examInternet, ceiling, nowUnix, nameof(IMachineState.ApplyInternet),
                                s => _machine.ApplyInternet(s, policy.Displaced!, () => SaveOrThrow(policy, nowUnix)), () => policy.ExamInternet = examInternet, reapplied, dropped,
                                undo: () => open &= UndoInternet(policy));
                        }
                    }
                }
                else if (loaded.ExamInternet is { } orphan)
                {
                    DropWithExam(orphan, dropped);
                }

                // The standalone policy after the exam's: the exam's wins (D-22), and while it is in
                // force the standalone one only waits underneath it, re-applied when it ends.
                if (loaded.Internet is { } internet)
                {
                    if (policy.ExamInternet is not null && !internet.Ended(nowUnix))
                    {
                        internet.HardLimitUnix = ClassroomPolicy.Clamp(internet.HardLimitUnix, Defaults.InternetPolicyHardLimit, nowUnix);
                        policy.Internet = internet;
                    }
                    else
                    {
                        Reapply(internet, Defaults.InternetPolicyHardLimit, nowUnix, nameof(IMachineState.ApplyInternet),
                            s => _machine.ApplyInternet(s, policy.Displaced!, () => SaveOrThrow(policy, nowUnix)), () => policy.Internet = internet, reapplied, dropped,
                            undo: () => open &= UndoInternet(policy));
                    }
                }

                if (loaded.Overlay is { } overlay)
                {
                    if (overlay.Kind == Overlay.Types.Kind.Lock)
                    {
                        Reapply(overlay, Defaults.LockHardLimit, nowUnix, nameof(IMachineState.ApplyOverlay), _machine.ApplyOverlay, () => policy.Overlay = overlay, reapplied, dropped);
                    }
                    else
                    {
                        // Only a lock is a session in this build (D-71: a broadcast dies with its
                        // stream and never reaches the document); an edited kind is not re-applied.
                        dropped.Add(overlay);
                        Report(Event.Types.Severity.Warning, PolicyEvents.ApplyFailed,
                            PolicyEvents.Text("ApplyFailed", PolicyEvents.KindName(overlay), overlay.SessionId, PolicyEvents.RefusedUnknownKind));
                    }
                }
            }

            _policy = policy;
            _machineOpen = open;

            // Step 6: save, whatever happened.
            Save(nowUnix);

            return new PolicyRestoreOutcome(open, reapplied, dropped);
        }
    }

    /// <summary>
    /// The watchdog task's pass when the service is not running (D-73 item 7): D-69 steps 0–4 —
    /// the unconditional restore, the refine from the document's displaced block, the read-back —
    /// and <b>nothing re-applied</b>. A short-lived process cannot hold a whitelist (its resolver
    /// would die with it, leaving every name unresolvable), and the service re-applies whatever
    /// is still valid at its own next start, so the sessions stay in the document; the displaced
    /// block is dropped only once the restore is confirmed.
    /// </summary>
    public PolicyRestoreOutcome RestoreOnly(long nowUnix)
    {
        lock (_lock)
        {
            var open = Call(nameof(IMachineState.RestoreDefaults), _machine.RestoreDefaults, PolicyEvents.RestoreFailed, "RestoreFailed");
            var loaded = ReadDocument();
            var policy = loaded ?? new ClassroomPolicy { Displaced = new DisplacedSettings() };
            var displaced = policy.Displaced is { IsEmpty: false } originals ? originals : null;
            var refined = displaced is null || Call(nameof(IMachineState.RefineRestore), () => _machine.RefineRestore(displaced), PolicyEvents.RestoreFailed, "RefineFailed");
            var confirmed = Call(nameof(IMachineState.Confirm), _machine.Confirm, PolicyEvents.RestoreFailed, "ConfirmFailed");
            open &= refined && confirmed;
            if (refined && confirmed)
            {
                policy.Displaced = new DisplacedSettings();
            }

            _machineOpen = open;
            Save(policy, nowUnix);
            return new PolicyRestoreOutcome(open, [], []);
        }
    }

    /// <summary>
    /// <c>OnStop</c> is also a restore (D-69): a graceful <c>sc stop</c> fires no recovery
    /// action, so without this a whitelist would stay in force for hours. The document is
    /// <b>not</b> cleared — the next start re-applies whatever is still within its limits.
    /// </summary>
    public PolicyRestoreOutcome Stop(long nowUnix)
    {
        lock (_lock)
        {
            _stopped = true;
            var open = Call(nameof(IMachineState.RestoreDefaults), _machine.RestoreDefaults, PolicyEvents.RestoreFailed, "RestoreFailed");
            if (_policy.Displaced is { IsEmpty: false } displaced)
            {
                open &= Call(nameof(IMachineState.RefineRestore), () => _machine.RefineRestore(displaced), PolicyEvents.RestoreFailed, "RefineFailed");
            }

            open &= Call(nameof(IMachineState.Confirm), _machine.Confirm, PolicyEvents.RestoreFailed, "ConfirmFailed");
            _machineOpen = open;
            return new PolicyRestoreOutcome(open, [], []);
        }
    }

    // ------------------------------------------------------------------ the clock

    /// <summary>
    /// Lifts every session whose <c>until</c> or hard limit has passed (D-16): the agent's
    /// own clock ends a lock, an exam or a policy with no console in sight. A lift that fails
    /// keeps the session so the next tick retries, and leaves <see cref="MachineOpen"/> false.
    /// Returns the sessions lifted.
    /// </summary>
    public IReadOnlyList<PolicySession> Tick(long nowUnix)
    {
        lock (_lock)
        {
            var lifted = new List<PolicySession>();
            var changed = false;
            if (_stopped)
            {
                return lifted;
            }

            if (_policy.Internet is { } internet && internet.Ended(nowUnix))
            {
                if (_policy.ExamInternet is not null)
                {
                    // Waiting underneath the exam's policy, never enforced: forgotten, nothing to lift.
                    _policy.Internet = null;
                    lifted.Add(internet);
                    changed = true;
                    Report(Event.Types.Severity.Info, PolicyEvents.Expired, PolicyEvents.Text("SessionExpired", PolicyEvents.KindName(internet), internet.SessionId));
                }
                else
                {
                    changed |= Lift(internet, nameof(IMachineState.LiftInternet), () => _machine.LiftInternet(_policy.Displaced!), () =>
                    {
                        _policy.Internet = null;
                        _policy.Displaced = new DisplacedSettings();
                    }, lifted);
                }
            }

            if (_policy.ExamInternet is { } examInternet && (examInternet.Ended(nowUnix) || _policy.Exam is null || _policy.Exam.Ended(nowUnix)))
            {
                if (_policy.Internet is { } underneath && !underneath.Ended(nowUnix))
                {
                    // The standalone policy comes back when the exam's ends (D-22): the machine
                    // changes from one to the other without opening in between.
                    var result = Guard(nameof(IMachineState.ApplyInternet), () => _machine.ApplyInternet(underneath, _policy.Displaced!, () => SaveOrThrow(_policy, nowUnix)));
                    if (result.Succeeded)
                    {
                        _policy.ExamInternet = null;
                        lifted.Add(examInternet);
                        changed = true;
                        Report(Event.Types.Severity.Info, PolicyEvents.Expired, PolicyEvents.Text("SessionExpired", PolicyEvents.Text("KindExamInternet"), examInternet.SessionId));
                    }
                    else
                    {
                        Report(Event.Types.Severity.Warning, PolicyEvents.ApplyFailed, PolicyEvents.Text("ApplyFailed", PolicyEvents.KindName(underneath), underneath.SessionId, Describe(result, PolicyEvents.KindName(underneath))));
                        _policy.Internet = null;
                        changed |= Lift(examInternet, nameof(IMachineState.LiftInternet), () => _machine.LiftInternet(_policy.Displaced!), () =>
                        {
                            _policy.ExamInternet = null;
                            _policy.Displaced = new DisplacedSettings();
                        }, lifted);
                        changed = true;
                    }
                }
                else
                {
                    changed |= Lift(examInternet, nameof(IMachineState.LiftInternet), () => _machine.LiftInternet(_policy.Displaced!), () =>
                    {
                        _policy.ExamInternet = null;
                        _policy.Displaced = new DisplacedSettings();
                    }, lifted);
                }
            }

            if (_policy.Exam is { } exam && exam.Ended(nowUnix))
            {
                changed |= Lift(exam, nameof(IMachineState.LiftExam), _machine.LiftExam, () => _policy.Exam = null, lifted);
            }

            if (_policy.Overlay is { } overlay && overlay.Ended(nowUnix))
            {
                changed |= Lift(overlay, nameof(IMachineState.LiftOverlay), _machine.LiftOverlay, () => _policy.Overlay = null, lifted);
            }

            if (changed)
            {
                Save(nowUnix);
            }

            return lifted;
        }
    }

    // ------------------------------------------------------------------ overlay (portion 1)

    /// <summary>
    /// A console's <c>Overlay</c>, already past the ownership gate: a <c>LOCK</c> starts or
    /// re-states a lock whose hard limit the agent clamps to <see cref="Defaults.LockHardLimit"/>,
    /// an <c>UNLOCK</c> lifts it. The request carries <b>durations</b> (<c>until_seconds</c>,
    /// <c>hard_limit_seconds</c>), never a console-clock stamp: the lab has no time source, so
    /// every absolute moment is minted here, on this PC's clock, at arrival (D-58's rule that
    /// time decisions are the agent's; D-70 item 6). Returns what the PC shows afterwards —
    /// the truth, never an echo of the request — with <c>problem</c> set when the request was
    /// refused or failed.
    /// </summary>
    /// <param name="setByInstance">The validated instance id of the console that sent it (D-57 item 4).</param>
    public OverlayState SetOverlay(Overlay request, string setByInstance, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(setByInstance);

        lock (_lock)
        {
            if (_stopped)
            {
                return OverlayStateLocked(PolicyEvents.RefusedStopped, nowUnix);
            }

            switch (request.Kind)
            {
                case Overlay.Types.Kind.Lock:
                    return ApplyLockLocked(request, setByInstance, nowUnix);

                case Overlay.Types.Kind.Unlock:
                    return LiftLockLocked(nowUnix);

                case Overlay.Types.Kind.BroadcastStart:
                case Overlay.Types.Kind.BroadcastStop:
                    // A broadcast is not a session and never reaches the document (D-71): the
                    // enforcer hands it to its BroadcastReceiver before the journal sees it, so
                    // only a journal driven on its own (a test) gets here.
                    return OverlayStateLocked(PolicyEvents.Text("NotInThisBuild", PolicyEvents.Text("KindBroadcast")), nowUnix);

                default:
                    return OverlayStateLocked(PolicyEvents.RefusedUnknownKind, nowUnix);
            }
        }
    }

    /// <summary>
    /// Re-asserts the current lock to a machine that lost it — a helper restarted at logon or
    /// logoff (D-30) shows nothing until told again. A no-op without a lock. Returns the
    /// state to publish.
    /// </summary>
    public OverlayState ReassertOverlay(long nowUnix)
    {
        lock (_lock)
        {
            if (_policy.Overlay is not { } overlay)
            {
                return OverlayStateLocked(null, nowUnix);
            }

            if (overlay.Ended(nowUnix))
            {
                // The timer will lift it; do not show a lock that is already over.
                return OverlayStateLocked(null, nowUnix);
            }

            var result = Guard(nameof(IMachineState.ApplyOverlay), () => _machine.ApplyOverlay(overlay));
            return OverlayStateLocked(result.Succeeded ? null : Describe(result, PolicyEvents.KindName(overlay)), nowUnix);
        }
    }

    /// <summary>
    /// What the helper reports about its own enforcement (<c>HelperMessage.overlay_state</c>):
    /// whether the low-level hooks hold, or which fallback and why — as stable
    /// <see cref="OverlayReasons"/> codes, never words. Kept for <see cref="OverlayStateOf"/>;
    /// a helper that shows nothing while a lock is held is the service's cue to
    /// <see cref="ReassertOverlay"/>.
    /// </summary>
    public OverlayState NoteEnforcement(bool inputBlocked, string reason, long nowUnix)
    {
        lock (_lock)
        {
            _inputBlocked = inputBlocked;
            _overlayReason = reason ?? string.Empty;
            return OverlayStateLocked(null, nowUnix);
        }
    }

    /// <summary>The overlay state as it stands, for the re-send after <c>Welcome</c>.</summary>
    public OverlayState OverlayStateOf(long nowUnix)
    {
        lock (_lock)
        {
            return OverlayStateLocked(null, nowUnix);
        }
    }

    private OverlayState ApplyLockLocked(Overlay request, string setByInstance, long nowUnix)
    {
        // A negative duration is a request that ended before it arrived: refused, nothing shown.
        if (request.UntilSeconds < 0)
        {
            return OverlayStateLocked(PolicyEvents.RefusedAlreadyEnded, nowUnix);
        }

        // Durations become moments here, on this clock (D-70 item 6). A proposed hard limit of
        // 0 or less means "none given" and yields the ceiling, as Clamp reads it.
        var session = new OverlaySession
        {
            Kind = Overlay.Types.Kind.Lock,
            SessionId = request.SessionId.Length > 0 ? request.SessionId : Guid.NewGuid().ToString("n"),
            SetByInstance = setByInstance,
            SinceUnix = _policy.Overlay is { } current && current.SessionId == request.SessionId ? current.SinceUnix : nowUnix,
            UntilUnix = request.UntilSeconds > 0 ? ClassroomPolicy.Add(nowUnix, request.UntilSeconds) : 0,
            HardLimitUnix = ClassroomPolicy.Clamp(request.HardLimitSeconds > 0 ? ClassroomPolicy.Add(nowUnix, request.HardLimitSeconds) : 0, Defaults.LockHardLimit, nowUnix),
            Message = OverlayText.ClampMessage(request.Message),
        };

        if (session.Ended(nowUnix))
        {
            return OverlayStateLocked(PolicyEvents.RefusedAlreadyEnded, nowUnix);
        }

        var result = Guard(nameof(IMachineState.ApplyOverlay), () => _machine.ApplyOverlay(session));
        if (!result.Succeeded)
        {
            var problem = Describe(result, PolicyEvents.KindName(session));
            Report(Event.Types.Severity.Warning, PolicyEvents.ApplyFailed, PolicyEvents.Text("ApplyFailed", PolicyEvents.KindName(session), session.SessionId, problem));
            return OverlayStateLocked(problem, nowUnix);
        }

        _policy.Overlay = session;
        Save(nowUnix);
        return OverlayStateLocked(null, nowUnix);
    }

    private OverlayState LiftLockLocked(long nowUnix)
    {
        if (_policy.Overlay is not { } overlay)
        {
            return OverlayStateLocked(null, nowUnix);
        }

        var result = Guard(nameof(IMachineState.LiftOverlay), _machine.LiftOverlay);
        if (!result.Succeeded)
        {
            var problem = Describe(result, PolicyEvents.KindName(overlay));
            _machineOpen = false;
            Report(Event.Types.Severity.Warning, PolicyEvents.RestoreFailed, PolicyEvents.Text("LiftFailed", PolicyEvents.KindName(overlay), overlay.SessionId, problem));
            return OverlayStateLocked(problem, nowUnix);
        }

        _policy.Overlay = null;
        Save(nowUnix);
        return OverlayStateLocked(null, nowUnix);
    }

    /// <summary>
    /// The state as it stands, with <c>remaining_seconds</c> counted on this clock so the
    /// console never has to. Null-safe on purpose: a protobuf string setter throws on
    /// <c>null</c>, and this runs on the unattended paths (the re-send after <c>Welcome</c>,
    /// the helper re-assert), so nothing that reaches it may throw — even if a session ever
    /// carried a null the document validation did not see.
    /// </summary>
    private OverlayState OverlayStateLocked(string? problem, long nowUnix)
    {
        var state = new OverlayState { Problem = problem ?? string.Empty };
        if (_policy.Overlay is { } overlay)
        {
            state.Kind = overlay.Kind;
            state.SessionId = overlay.SessionId ?? string.Empty;
            state.SinceUnix = overlay.SinceUnix;
            state.UntilUnix = overlay.UntilUnix;
            state.HardLimitUnix = overlay.HardLimitUnix;
            state.RemainingSeconds = OverlayText.Remaining(overlay.UntilUnix, overlay.HardLimitUnix, nowUnix) ?? 0;
            state.SetByInstance = overlay.SetByInstance ?? string.Empty;
            state.InputBlocked = _inputBlocked;
            state.Reason = _overlayReason ?? string.Empty;
        }

        return state;
    }

    // ------------------------------------------------------------------ internet (portion 3)

    /// <summary>
    /// A console's standalone <c>InternetPolicy</c>, already past the ownership gate (D-73):
    /// <c>WHITELIST</c> or <c>BLOCKED</c> starts or replaces the standalone policy, its hard
    /// limit clamped to <see cref="Defaults.InternetPolicyHardLimit"/> on this clock;
    /// <c>OPEN</c> lifts it; <c>amend_hosts</c> changes only the host list of the session the PC
    /// holds. The limits arrive as durations and become moments here, as for a lock (D-70
    /// item 6). Host patterns are normalized by <see cref="Internet.HostPattern"/>; one that does
    /// not normalize is dropped. While the exam's internet policy is in force the standalone
    /// one is only kept, not applied (D-22). Returns what the PC enforces afterwards, with
    /// <c>problem</c> set when the request was refused or failed.
    /// </summary>
    public InternetState SetInternet(InternetPolicy request, string setByInstance, long nowUnix, InternetRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(setByInstance);

        lock (_lock)
        {
            if (_stopped)
            {
                return InternetStateLocked(PolicyEvents.RefusedStopped, nowUnix, runtime);
            }

            if (request.AmendHosts)
            {
                return AmendInternetLocked(request, nowUnix, runtime);
            }

            return request.Mode switch
            {
                InternetPolicy.Types.Mode.Open => LiftInternetLocked(nowUnix, runtime),
                InternetPolicy.Types.Mode.Whitelist or InternetPolicy.Types.Mode.Blocked => ApplyInternetLocked(request, setByInstance, nowUnix, runtime),
                _ => InternetStateLocked(PolicyEvents.RefusedUnknownMode, nowUnix, runtime),
            };
        }
    }

    /// <summary>
    /// Re-applies the internet policy in force when the machine fell behind it
    /// (<see cref="IMachineState.InternetNeedsReassert"/>): the same apply, so new originals are
    /// recorded and saved before anything changes. A failure is reported and the policy kept —
    /// what was already enforced still is, and the next refresh tries again. Returns whether it ran.
    /// </summary>
    public bool ReassertInternet(long nowUnix)
    {
        lock (_lock)
        {
            if (_stopped || (_policy.ExamInternet ?? _policy.Internet) is not { } effective || effective.Ended(nowUnix))
            {
                return false;
            }

            var result = Guard(nameof(IMachineState.ApplyInternet), () => _machine.ApplyInternet(effective, _policy.Displaced!, () => SaveOrThrow(_policy, nowUnix)));
            if (!result.Succeeded)
            {
                Report(Event.Types.Severity.Warning, PolicyEvents.ApplyFailed,
                    PolicyEvents.Text("ApplyFailed", PolicyEvents.KindName(effective), effective.SessionId, Describe(result, PolicyEvents.KindName(effective))));
            }

            Save(nowUnix);
            return true;
        }
    }

    /// <summary>The internet state as it stands, for the re-send after <c>Welcome</c> and the refused-names refresh.</summary>
    public InternetState InternetStateOf(long nowUnix, InternetRuntime? runtime = null)
    {
        lock (_lock)
        {
            return InternetStateLocked(null, nowUnix, runtime);
        }
    }

    /// <summary>True while a firewall or DNS policy is held or its originals are not yet confirmed restored: <c>self_update</c> is refused then (D-69 item 6).</summary>
    public bool HoldsNetworkPolicy
    {
        get
        {
            lock (_lock)
            {
                return _policy.Internet is not null || _policy.ExamInternet is not null || _policy.Displaced is { IsEmpty: false };
            }
        }
    }

    private InternetState ApplyInternetLocked(InternetPolicy request, string setByInstance, long nowUnix, InternetRuntime? runtime)
    {
        if (request.UntilSeconds < 0)
        {
            return InternetStateLocked(PolicyEvents.RefusedAlreadyEnded, nowUnix, runtime);
        }

        var previous = _policy.Internet;
        var session = new InternetSession
        {
            Mode = request.Mode,
            SessionId = request.SessionId.Length > 0 ? request.SessionId : Guid.NewGuid().ToString("n"),
            SetByInstance = setByInstance,
            SinceUnix = previous is not null && previous.SessionId == request.SessionId ? previous.SinceUnix : nowUnix,
            UntilUnix = request.UntilSeconds > 0 ? ClassroomPolicy.Add(nowUnix, request.UntilSeconds) : 0,
            HardLimitUnix = ClassroomPolicy.Clamp(request.HardLimitSeconds > 0 ? ClassroomPolicy.Add(nowUnix, request.HardLimitSeconds) : 0, Defaults.InternetPolicyHardLimit, nowUnix),
            AllowedHosts = request.Mode == InternetPolicy.Types.Mode.Whitelist ? Normalize(request.AllowedHosts) : [],
            PresetName = request.PresetName ?? string.Empty,
        };

        if (session.Ended(nowUnix))
        {
            return InternetStateLocked(PolicyEvents.RefusedAlreadyEnded, nowUnix, runtime);
        }

        return EnforceInternetLocked(session, nowUnix, runtime);
    }

    private InternetState AmendInternetLocked(InternetPolicy request, long nowUnix, InternetRuntime? runtime)
    {
        if (_policy.Internet is not { } current || current.SessionId != request.SessionId || current.Mode != InternetPolicy.Types.Mode.Whitelist)
        {
            return InternetStateLocked(PolicyEvents.RefusedNoInternetSession, nowUnix, runtime);
        }

        var amended = Copy(current);
        amended.AllowedHosts = Normalize(request.AllowedHosts);
        return EnforceInternetLocked(amended, nowUnix, runtime);
    }

    /// <summary>Applies a standalone session — or only keeps it while the exam's policy wins — and keeps the document honest about the outcome.</summary>
    private InternetState EnforceInternetLocked(InternetSession session, long nowUnix, InternetRuntime? runtime)
    {
        if (_policy.ExamInternet is not null)
        {
            _policy.Internet = session;
            Save(nowUnix);
            return InternetStateLocked(null, nowUnix, runtime);
        }

        var result = Guard(nameof(IMachineState.ApplyInternet), () => _machine.ApplyInternet(session, _policy.Displaced!, () => SaveOrThrow(_policy, nowUnix)));
        if (result.Succeeded)
        {
            _policy.Internet = session;
            Save(nowUnix);
            return InternetStateLocked(null, nowUnix, runtime);
        }

        var name = PolicyEvents.KindName(session);
        var problem = Describe(result, name);
        Report(Event.Types.Severity.Warning, PolicyEvents.ApplyFailed, PolicyEvents.Text("ApplyFailed", name, session.SessionId, problem));

        // The apply may have changed half the machine. Put it all back: the previous policy, if
        // any, is gone with it — a PC whose state is unknown is opened, never guessed at.
        var undo = Guard(nameof(IMachineState.LiftInternet), () => _machine.LiftInternet(_policy.Displaced!));
        if (undo.Succeeded)
        {
            if (_policy.Internet is { } previous)
            {
                Report(Event.Types.Severity.Warning, PolicyEvents.Expired, PolicyEvents.Text("SessionExpired", PolicyEvents.KindName(previous), previous.SessionId));
            }

            _policy.Internet = null;
            _policy.Displaced = new DisplacedSettings();
        }
        else
        {
            // Not provably open: the new session is kept so the clock and the hard limit retry
            // the lift; the document claims at most what may still be in force.
            _machineOpen = false;
            _policy.Internet = session;
            Report(Event.Types.Severity.Warning, PolicyEvents.RestoreFailed, PolicyEvents.Text("LiftFailed", name, session.SessionId, Describe(undo, name)));
        }

        Save(nowUnix);
        return InternetStateLocked(problem, nowUnix, runtime);
    }

    private InternetState LiftInternetLocked(long nowUnix, InternetRuntime? runtime)
    {
        if (_policy.Internet is not { } internet)
        {
            return InternetStateLocked(null, nowUnix, runtime);
        }

        if (_policy.ExamInternet is not null)
        {
            // Waiting underneath the exam's policy, never enforced: forgotten, nothing to lift.
            _policy.Internet = null;
            Save(nowUnix);
            return InternetStateLocked(null, nowUnix, runtime);
        }

        var result = Guard(nameof(IMachineState.LiftInternet), () => _machine.LiftInternet(_policy.Displaced!));
        if (!result.Succeeded)
        {
            var name = PolicyEvents.KindName(internet);
            var problem = Describe(result, name);
            _machineOpen = false;
            Report(Event.Types.Severity.Warning, PolicyEvents.RestoreFailed, PolicyEvents.Text("LiftFailed", name, internet.SessionId, problem));
            return InternetStateLocked(problem, nowUnix, runtime);
        }

        _policy.Internet = null;
        _policy.Displaced = new DisplacedSettings();
        Save(nowUnix);
        return InternetStateLocked(null, nowUnix, runtime);
    }

    /// <summary>The effective policy — the exam's if one is in force, else the standalone one — with the live side folded in. Never throws.</summary>
    private InternetState InternetStateLocked(string? problem, long nowUnix, InternetRuntime? runtime)
    {
        var state = new InternetState { Mode = InternetPolicy.Types.Mode.Open, Problem = problem ?? string.Empty };
        var reasons = new List<string>();
        var effective = _policy.ExamInternet ?? _policy.Internet;
        if (effective is not null)
        {
            state.Mode = effective.Mode;
            state.SessionId = effective.SessionId ?? string.Empty;
            state.UntilUnix = effective.UntilUnix;
            state.HardLimitUnix = effective.HardLimitUnix;
            state.RemainingSeconds = Math.Max(0, effective.DeadlineUnix - nowUnix);
            state.SetByInstance = effective.SetByInstance ?? string.Empty;
            state.PresetName = effective.PresetName ?? string.Empty;
            state.AllowedHosts.AddRange((effective.AllowedHosts ?? []).Where(h => h is not null));
            if (_policy.ExamInternet is not null && _policy.Internet is not null)
            {
                reasons.Add(InternetReasons.ExamWins);
            }
        }

        if (!_machineOpen && effective is null)
        {
            reasons.Add(InternetReasons.RestoreUnconfirmed);
        }

        if (runtime is not null)
        {
            state.ResolverActive = runtime.ResolverActive;
            if (effective is { Mode: InternetPolicy.Types.Mode.Whitelist })
            {
                state.RefusedRecently.AddRange(runtime.Refused.Where(n => n is not null));
            }

            reasons.AddRange(InternetCodes(runtime.Reason));
        }

        state.Reason = string.Join(' ', reasons.Distinct(StringComparer.Ordinal));
        return state;
    }

    private static IEnumerable<string> InternetCodes(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? [] : reason.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The host patterns a session keeps: normalized, distinct, in the teacher's order; any that does not normalize is dropped.</summary>
    private static List<string> Normalize(IEnumerable<string> hosts)
    {
        var kept = new List<string>();
        foreach (var host in hosts)
        {
            if (Internet.HostPattern.TryNormalize(host, out var pattern, out _) && !kept.Contains(pattern, StringComparer.Ordinal))
            {
                kept.Add(pattern);
            }
        }

        return kept;
    }

    // ------------------------------------------------------------------ the pieces

    private void Reapply<T>(T session, TimeSpan ceiling, long nowUnix, string what, Func<T, MachineResult> apply, Action keep,
        List<PolicySession> reapplied, List<PolicySession> dropped, Action? undo = null)
        where T : PolicySession
    {
        var name = PolicyEvents.KindName(session);
        if (session.Ended(nowUnix))
        {
            dropped.Add(session);
            Report(Event.Types.Severity.Info,
                session is OverlaySession ? PolicyEvents.OverlayExpired : PolicyEvents.Expired,
                session is OverlaySession ? PolicyEvents.Text("OverlayExpired") : PolicyEvents.Text("SessionExpired", name, session.SessionId));
            return;
        }

        // Re-clamped on every re-apply (step 5): a hand-edited limit, or a clock that went
        // backwards since the document was written, must not yield a far-future limit.
        session.HardLimitUnix = ClassroomPolicy.Clamp(session.HardLimitUnix, ceiling, nowUnix);

        var result = Guard(what, () => apply(session));
        if (result.Succeeded)
        {
            keep();
            reapplied.Add(session);
            Report(Event.Types.Severity.Info, PolicyEvents.Reapplied, PolicyEvents.Text("Reapplied", name, session.SessionId, Stamp(session.DeadlineUnix)));
        }
        else
        {
            dropped.Add(session);
            Report(Event.Types.Severity.Warning, PolicyEvents.ApplyFailed, PolicyEvents.Text("ApplyFailed", name, session.SessionId, Describe(result, name)));

            // A failed apply may have changed half the machine; the session is not held, so
            // nothing of it may stay (the document must not claim, nor the PC enforce, what is
            // not in force).
            undo?.Invoke();
        }
    }

    /// <summary>
    /// Puts the machine back after an internet apply that failed during a restore pass: a full
    /// lift with the pass's displaced block, which is forgotten only if the lift proves the
    /// machine open. Only runs when no other internet session of the pass is in force.
    /// </summary>
    private bool UndoInternet(ClassroomPolicy policy)
    {
        if (policy.Internet is not null || policy.ExamInternet is not null || policy.Displaced is not { IsEmpty: false } displaced)
        {
            return true;
        }

        var result = Guard(nameof(IMachineState.LiftInternet), () => _machine.LiftInternet(displaced));
        if (result.Succeeded)
        {
            policy.Displaced = new DisplacedSettings();
            return true;
        }

        Report(Event.Types.Severity.Warning, PolicyEvents.RestoreFailed, PolicyEvents.Text("RestoreFailed", Describe(result, PolicyEvents.Text("KindInternet"))));
        return false;
    }

    /// <summary>An exam internet policy whose exam is not in force is not in force either; said once, never applied.</summary>
    private void DropWithExam(InternetSession session, List<PolicySession> dropped)
    {
        dropped.Add(session);
        Report(Event.Types.Severity.Info, PolicyEvents.Expired, PolicyEvents.Text("SessionExpired", PolicyEvents.Text("KindExamInternet"), session.SessionId));
    }

    private bool Lift(PolicySession session, string what, Func<MachineResult> lift, Action forget, List<PolicySession> lifted)
    {
        var name = PolicyEvents.KindName(session);
        var result = Guard(what, lift);
        if (result.Succeeded)
        {
            forget();
            lifted.Add(session);
            Report(Event.Types.Severity.Info,
                session is OverlaySession ? PolicyEvents.OverlayExpired : PolicyEvents.Expired,
                session is OverlaySession ? PolicyEvents.Text("OverlayExpired") : PolicyEvents.Text("SessionExpired", name, session.SessionId));
            return true;
        }

        // A lift that fails keeps the session so the next pass retries, and the machine is not open.
        _machineOpen = false;
        Report(Event.Types.Severity.Warning, PolicyEvents.RestoreFailed, PolicyEvents.Text("LiftFailed", name, session.SessionId, Describe(result, name)));
        return false;
    }

    /// <summary>Runs one restore step; false — and an event — when it did not succeed.</summary>
    private bool Call(string what, Func<MachineResult> call, string code, string resource)
    {
        var result = Guard(what, call);
        if (result.Succeeded)
        {
            return true;
        }

        Report(Event.Types.Severity.Warning, code, PolicyEvents.Text(resource, Describe(result, what)));
        return false;
    }

    /// <summary>Every machine call goes through here: an exception is a failed result, never a throw.</summary>
    private static MachineResult Guard(string what, Func<MachineResult> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            return MachineResult.Failed(PolicyEvents.Text("MachineThrew", what, ex.Message));
        }
    }

    private static string Describe(MachineResult result, string what) => result.Outcome switch
    {
        MachineOutcome.NotInThisBuild => PolicyEvents.Text("NotInThisBuild", result.Problem.Length > 0 ? result.Problem : what),
        _ => result.Problem.Length > 0 ? result.Problem : result.Outcome.ToString(),
    };

    /// <summary>Step 2: the document's policy, or <c>null</c> — with a <see cref="PolicyEvents.Unreadable"/> event unless there simply is no file.</summary>
    private ClassroomPolicy? ReadDocument()
    {
        string? text;
        try
        {
            text = _read();
        }
        catch (Exception ex)
        {
            Unreadable(PolicyEvents.Text("UnreadableDocument", ex.Message));
            return null;
        }

        if (text is null)
        {
            return null;
        }

        PolicyDocument document;
        try
        {
            document = JsonStore.Parse<PolicyDocument>(text, Defaults.PolicyFileName, PolicyDocument.Migrations);
        }
        catch (SchemaVersionException ex)
        {
            Unreadable(PolicyEvents.Text("UnreadableNewerSchema", ex.FileVersion));
            return null;
        }
        catch (Exception ex)
        {
            Unreadable(PolicyEvents.Text("UnreadableDocument", ex.Message));
            return null;
        }

        if (document.LabId is not null && !string.Equals(document.LabId, _labId, StringComparison.OrdinalIgnoreCase))
        {
            // Another lab's document: not this PC's, and its displaced block is not refined
            // either — the unconditional restore has put Windows defaults (DHCP, allow) in
            // place, and Save overwrites the file with this lab's empty policy (D-69 item 2).
            Unreadable(PolicyEvents.Text("UnreadableForeignLab", document.LabId));
            return null;
        }

        // One pass over every required member, nested ones included: System.Text.Json puts a
        // JSON null into a non-nullable string, list or dictionary without complaint, and a
        // null anywhere makes the document unusable as a whole (B1/B2 of the foundation review).
        if (document.MissingMember() is { } missing)
        {
            Unreadable(PolicyEvents.Text("UnreadableIncomplete", missing));
            return null;
        }

        return document.Policy!;
    }

    private void Unreadable(string message) => Report(Event.Types.Severity.Warning, PolicyEvents.Unreadable, message);

    private void Save(long nowUnix) => Save(_policy, nowUnix);

    /// <summary>
    /// The save behind <c>recordDisplaced</c>: the originals must be on disk before the machine
    /// changes, so a failed write throws into the machine's apply, which fails and is undone,
    /// instead of being reported and carried on past (review finding 5).
    /// </summary>
    private void SaveOrThrow(ClassroomPolicy policy, long nowUnix)
    {
        try
        {
            _write(new PolicyDocument { LabId = _labId, SavedAtUnix = nowUnix, Policy = Copy(policy) });
        }
        catch (Exception ex)
        {
            Report(Event.Types.Severity.Warning, PolicyEvents.SaveFailed, PolicyEvents.Text("SaveFailed", ex.Message));
            throw new IOException(PolicyEvents.Text("SaveFailed", ex.Message), ex);
        }
    }

    private void Save(ClassroomPolicy policy, long nowUnix)
    {
        try
        {
            _write(new PolicyDocument { LabId = _labId, SavedAtUnix = nowUnix, Policy = Copy(policy) });
        }
        catch (Exception ex)
        {
            Report(Event.Types.Severity.Warning, PolicyEvents.SaveFailed, PolicyEvents.Text("SaveFailed", ex.Message));
        }
    }

    private void Report(Event.Types.Severity severity, string code, string message)
    {
        try
        {
            Reported?.Invoke(new PolicyEvent(severity, code, message));
        }
        catch
        {
            // A listener's failure is its own; the journal's state is already correct.
        }
    }

    private static string Stamp(long unix) =>
        unix >= long.MaxValue - 1 ? PolicyEvents.Text("StampNever") : DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(unix, DateTimeOffset.UnixEpoch.ToUnixTimeSeconds(), DateTimeOffset.MaxValue.ToUnixTimeSeconds())).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static InternetSession Copy(InternetSession session) =>
        JsonSerializer.Deserialize<InternetSession>(JsonSerializer.Serialize(session, JsonStore.Options), JsonStore.Options) ?? new InternetSession();

    private static ClassroomPolicy Copy(ClassroomPolicy policy) =>
        JsonSerializer.Deserialize<ClassroomPolicy>(JsonSerializer.Serialize(policy, JsonStore.Options), JsonStore.Options) ?? new ClassroomPolicy { Displaced = new DisplacedSettings() };
}
