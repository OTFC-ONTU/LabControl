namespace LabControl.Shared.Exam;

/// <summary>What one look at the student's session decided (D-75).</summary>
/// <param name="AskToClose">Programs whose warning ran out: asked to close now (<c>WM_CLOSE</c>), ended after <see cref="Defaults.ExamCloseGrace"/>.</param>
/// <param name="End">Programs to end now: started during the exam, or still running past their grace.</param>
/// <param name="Warn">Programs that were open when the exam (or a pause) began: the student is told they close in <see cref="Defaults.ExamSweepGrace"/>; nothing is done to them yet.</param>
public sealed record WatchDecision(IReadOnlyList<ProcessInfo> AskToClose, IReadOnlyList<ProcessInfo> End, IReadOnlyList<ProcessInfo> Warn)
{
    public WatchDecision(IReadOnlyList<ProcessInfo> askToClose, IReadOnlyList<ProcessInfo> end)
        : this(askToClose, end, [])
    {
    }

    public static WatchDecision Nothing { get; } = new([], [], []);

    public bool IsEmpty => AskToClose.Count == 0 && End.Count == 0 && Warn.Count == 0;
}

/// <summary>
/// The allowed-programs switch over time (M6 portion 4, D-75; owner decisions 2026-09-09 item 2
/// and 2026-10-23): the platform-neutral state machine the Windows service and the simulator
/// both drive with snapshots of the process list. The first look after <see cref="Begin"/> —
/// an exam starting, the service restarting under one, a pause ending — is the <b>opening
/// sweep</b>: every program it would close is <b>warned</b> first — the student sees it will
/// close in <see cref="Defaults.ExamSweepGrace"/> and can save — then asked to close, and ended
/// <see cref="Defaults.ExamCloseGrace"/> later if still running (a lab PC, 2026-10-23: asking at
/// once closed VS Code before anyone could read a word, since it exits without a question). Every later look ends a program it has not seen
/// before <b>at once</b>. Judging is <see cref="ProgramWhitelist.ShouldClose"/>; an
/// <paramref name="exempt"/> process (one the agent itself started for the teacher) is never
/// judged. Not thread-safe; its owner calls it from one thread.
/// </summary>
public sealed class ProgramWatch
{
    private readonly Func<int, bool> _exempt;
    private readonly TimeSpan _grace;
    private readonly TimeSpan _closeGrace;
    private readonly HashSet<(int, long)> _known = [];
    // Per swept process: the moment its next step is due, and whether it was already asked to close.
    private readonly Dictionary<(int, long), (DateTimeOffset Due, bool Asked)> _asked = [];
    private HashSet<string> _allowed = new(StringComparer.Ordinal);
    private bool _sweepPending;
    private bool _active;
    private DateTimeOffset _suspendedUntil = DateTimeOffset.MinValue;
    private bool _resumeSweep;

    /// <param name="exempt">True for a process id the agent started itself; <c>null</c> = none.</param>
    /// <param name="grace">The warning before a swept program is asked to close; <see cref="Defaults.ExamSweepGrace"/> unless a test wants it sooner.</param>
    /// <param name="closeGrace">How long a program asked to close has before it is ended; <see cref="Defaults.ExamCloseGrace"/> by default.</param>
    public ProgramWatch(Func<int, bool>? exempt = null, TimeSpan? grace = null, TimeSpan? closeGrace = null)
    {
        _exempt = exempt ?? (_ => false);
        _grace = grace ?? Defaults.ExamSweepGrace;
        _closeGrace = closeGrace ?? Defaults.ExamCloseGrace;
    }

    /// <summary>The whitelist is on.</summary>
    public bool Active => _active;

    /// <summary>The names allowed now, normalized.</summary>
    public IReadOnlySet<string> Allowed => _allowed;

    /// <summary>
    /// Starts — or re-states — the whitelist. A new list, or a watch that was off, sweeps on
    /// the next look; re-stating the same list (an exam extended) changes nothing.
    /// </summary>
    public void Begin(IEnumerable<string> allowed)
    {
        var names = new HashSet<string>(ProgramNames.NormalizeAll(allowed), StringComparer.Ordinal);
        if (_active && names.SetEquals(_allowed))
        {
            return;
        }

        _allowed = names;
        _active = true;
        _sweepPending = true;
        _asked.Clear();
    }

    /// <summary>Ends the whitelist; nothing is judged until the next <see cref="Begin"/>.</summary>
    public void End()
    {
        _active = false;
        _sweepPending = false;
        _asked.Clear();
        _known.Clear();
        _suspendedUntil = DateTimeOffset.MinValue;
        _resumeSweep = false;
    }

    /// <summary>
    /// Pauses judging until <paramref name="until"/> — a shutdown, reboot or log off is running
    /// and Windows closes everything itself (D-75, "suspended for power jobs"). The look after
    /// the pause is a sweep again: what started meanwhile is asked, not ended.
    /// </summary>
    public void Suspend(DateTimeOffset until)
    {
        if (until > _suspendedUntil)
        {
            _suspendedUntil = until;
        }

        _resumeSweep = true;
        _asked.Clear();
    }

    /// <summary>True while a <see cref="Suspend"/> holds at <paramref name="now"/>.</summary>
    public bool IsSuspended(DateTimeOffset now) => now < _suspendedUntil;

    /// <summary>
    /// One look at the process list. Returns what to ask to close and what to end; the caller
    /// does it and calls again on the next tick. A program that will not end is decided again
    /// on every look until it is gone.
    /// </summary>
    public WatchDecision Observe(IReadOnlyCollection<ProcessInfo> processes, SessionContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(context);

        if (!_active)
        {
            return WatchDecision.Nothing;
        }

        // Nobody to judge — a passing glitch at sign-in or after a pause — keeps the sweep and the
        // grace periods for the look that sees the student again (review finding 5).
        if (IsSuspended(now) || context.UserSid is null)
        {
            return WatchDecision.Nothing;
        }

        var sweep = _sweepPending || _resumeSweep;
        _sweepPending = false;
        _resumeSweep = false;

        var ask = new List<ProcessInfo>();
        var end = new List<ProcessInfo>();
        var warn = new List<ProcessInfo>();
        var present = new HashSet<(int, long)>();
        foreach (var process in processes)
        {
            present.Add(process.Key);
            var isNew = !_known.Contains(process.Key);
            if (_exempt(process.ProcessId) || !ProgramWhitelist.ShouldClose(process, context, _allowed))
            {
                continue;
            }

            if (_asked.TryGetValue(process.Key, out var step))
            {
                if (now >= step.Due)
                {
                    if (step.Asked)
                    {
                        end.Add(process);
                    }
                    else
                    {
                        // The warning ran out: now it is asked to close, and ended if it will not.
                        _asked[process.Key] = (now + _closeGrace, true);
                        ask.Add(process);
                    }
                }

                continue;
            }

            if (sweep)
            {
                // Open before the exam: warned first, asked after the grace, ended after that (owner decision 2026-10-23).
                _asked[process.Key] = (now + _grace, false);
                warn.Add(process);
            }
            else if (isNew)
            {
                end.Add(process);
            }
            else
            {
                // Seen before and judged then: one that would not end, decided again.
                end.Add(process);
            }
        }

        _known.Clear();
        _known.UnionWith(present);
        foreach (var gone in _asked.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _asked.Remove(gone);
        }

        return new WatchDecision(ask, end, warn);
    }
}
