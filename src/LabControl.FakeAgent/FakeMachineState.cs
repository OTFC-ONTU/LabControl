using LabControl.Shared;
using LabControl.Shared.Exam;
using LabControl.Shared.Internet;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using Microsoft.Extensions.Logging;

namespace LabControl.FakeAgent;

/// <summary>
/// What a simulated PC does with a policy (M6 portion 1): a lock is drawn onto the
/// simulated desktop — the same <see cref="OverlayText.Compose"/> picture the real helper
/// blits, so the console's mosaic shows the lock exactly as a real PC would show it — and
/// nothing else exists yet. The journal, the clamp, the document and the hard limit on the
/// PC's own clock are the shared <see cref="PolicyEnforcer"/>'s, not pretended here.
/// Since portion 2 a broadcast is decoded by the real <see cref="BroadcastPicture"/> and drawn
/// onto the simulated desktop too, so the mosaic shows the teacher's screen on every tile
/// that receives it — under a lock, which stays on top as on a real PC.
/// </summary>
internal sealed class FakeMachineState : IMachineState, IDisposable
{
    private readonly Func<FakeScreen?> _desktop;
    private readonly Lock _lock = new();
    private OverlaySession? _overlay;
    private BroadcastSession? _broadcast;
    private InternetSession? _internet;
    private readonly RefusedNames _refused = new();

    /// <param name="desktop">The desktop being drawn right now, or <c>null</c> while nobody watches; a desktop created later is told by <see cref="Overlay"/>.</param>
    public FakeMachineState(Func<FakeScreen?> desktop, ILogger log)
    {
        _desktop = desktop;
        _log = log;
        Picture = new BroadcastPicture(log);
    }

    private readonly ILogger _log;

    /// <summary>The teacher's screen as this PC decoded it (M6 portion 2).</summary>
    public BroadcastPicture Picture { get; }

    /// <summary>The broadcast this PC shows, for a desktop created after it started and for the tests.</summary>
    public BroadcastSession? Broadcast
    {
        get
        {
            lock (_lock)
            {
                return _broadcast;
            }
        }
    }

    public MachineResult ShowBroadcast(BroadcastSession session)
    {
        lock (_lock)
        {
            _broadcast = session;
        }

        _desktop()?.SetBroadcast(Picture);
        return MachineResult.Done;
    }

    public ValueTask BroadcastFrameAsync(VideoFrame frame, CancellationToken token)
    {
        Picture.Offer(frame);
        return ValueTask.CompletedTask;
    }

    public MachineResult StopBroadcast()
    {
        lock (_lock)
        {
            _broadcast = null;
        }

        Picture.Clear();
        _desktop()?.SetBroadcast(null);
        return MachineResult.Done;
    }

    public void Dispose() => Picture.Dispose();

    /// <summary>The lock in force, for a desktop created after it was applied and for the status line.</summary>
    public OverlaySession? Overlay
    {
        get
        {
            lock (_lock)
            {
                return _overlay;
            }
        }
    }

    public MachineResult RestoreDefaults() => MachineResult.Done;

    public MachineResult RefineRestore(DisplacedSettings displaced) => MachineResult.Done;

    public MachineResult Confirm() => MachineResult.Done;

    public MachineResult ApplyOverlay(OverlaySession session)
    {
        lock (_lock)
        {
            _overlay = session;
        }

        _desktop()?.SetOverlay(session);
        return MachineResult.Done;
    }

    public MachineResult LiftOverlay()
    {
        lock (_lock)
        {
            _overlay = null;
        }

        _desktop()?.SetOverlay(null);
        return MachineResult.Done;
    }

    /// <summary>The internet policy in force, for the status line and the tests (M6 portion 3).</summary>
    public InternetSession? Internet
    {
        get
        {
            lock (_lock)
            {
                return _internet;
            }
        }
    }

    /// <summary>
    /// A simulated PC has no firewall to change: the policy is only held, and a whitelist's
    /// "student" asks for a few well-known names now and then (<see cref="Browse"/>), so the
    /// console's refused-names list and its <i>Allow</i> button can be developed on a Mac.
    /// </summary>
    public MachineResult ApplyInternet(InternetSession session, DisplacedSettings displaced, Action recordDisplaced)
    {
        lock (_lock)
        {
            _internet = session;
        }

        return MachineResult.Done;
    }

    public MachineResult LiftInternet(DisplacedSettings displaced)
    {
        lock (_lock)
        {
            _internet = null;
        }

        return MachineResult.Done;
    }

    public InternetRuntime InternetRuntime()
    {
        lock (_lock)
        {
            var whitelist = _internet is { Mode: InternetPolicy.Types.Mode.Whitelist };
            return whitelist
                ? new InternetRuntime(true, _refused.Recent(DateTimeOffset.UtcNow), _refused.Version, string.Empty)
                : Shared.Policy.InternetRuntime.None;
        }
    }

    /// <summary>
    /// One simulated request from the student's browser: under a whitelist, a name the list
    /// does not cover is refused and noted, as the real resolver would. Called by the
    /// simulator's own timer; a no-op otherwise.
    /// </summary>
    public void Browse(string name)
    {
        lock (_lock)
        {
            if (_internet is { Mode: InternetPolicy.Types.Mode.Whitelist } whitelist && !HostPattern.AnyMatches(whitelist.AllowedHosts, name))
            {
                _refused.Note(name, DateTimeOffset.UtcNow);
            }
        }
    }

    // ------------------------------------------------------------------ exam (portion 4)

    /// <summary>The simulated student's user and session: whatever runs under them is judged by the whitelist like a real PC's.</summary>
    private const string StudentSid = "S-1-5-21-1000-1000-1000-1001";
    private const int StudentSession = 1;

    private ExamSession? _exam;
    // No waiting in a simulation: the warning and the close request follow one another at once.
    private readonly ProgramWatch _watch = new(grace: TimeSpan.Zero, closeGrace: TimeSpan.Zero);
    private readonly List<ProcessInfo> _processes = [];
    private readonly List<(string Name, DateTimeOffset At)> _terminated = [];
    private long _terminatedVersion;
    private int _nextPid = 4000;
    private string _notice = string.Empty;
    private DateTimeOffset _noticeUntil;

    /// <summary>The exam in force, for the status line and the tests (M6 portion 4).</summary>
    public ExamSession? Exam
    {
        get
        {
            lock (_lock)
            {
                return _exam;
            }
        }
    }

    /// <summary>The simulated student's programs, by name, for the tests.</summary>
    public IReadOnlyList<string> Running
    {
        get
        {
            lock (_lock)
            {
                return _processes.Select(p => p.Name).ToList();
            }
        }
    }

    /// <summary>
    /// The real <see cref="ProgramWatch"/> over a simulated process list: the opening sweep asks
    /// what is open to close — and simulated programs always comply — and a program the student
    /// starts afterwards is ended at once, as on a real PC. The strip is drawn onto the desktop.
    /// </summary>
    /// <summary>Creates the exam's folder to collect in the simulated profile; best effort, like a real PC.</summary>
    public Action<string>? PrepareCollectFolder { get; init; }

    public MachineResult ApplyExam(ExamSession session)
    {
        if (session.CollectFolder is { Length: > 0 } folder)
        {
            try
            {
                PrepareCollectFolder?.Invoke(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogInformation("the exam's folder {Folder} was not created: {Message}", folder, ex.Message);
            }
        }

        lock (_lock)
        {
            if (_exam?.SessionId != session.SessionId)
            {
                ForgetTerminatedLocked();
            }

            _exam = session;
            if (session.RestrictPrograms)
            {
                _watch.Begin(session.AllowedPrograms);
                WatchLocked(DateTimeOffset.UtcNow);
            }
            else
            {
                _watch.End();
            }
        }

        UpdateStrip();
        return MachineResult.Done;
    }

    /// <summary>What one exam closed is not the next one's, as on a real PC.</summary>
    private void ForgetTerminatedLocked()
    {
        if (_terminated.Count > 0)
        {
            _terminated.Clear();
            _terminatedVersion++;
        }
    }

    public MachineResult LiftExam()
    {
        lock (_lock)
        {
            _exam = null;
            ForgetTerminatedLocked();
            _watch.End();
            _notice = string.Empty;
        }

        _desktop()?.SetExamStrip(null);
        return MachineResult.Done;
    }

    public ExamRuntime ExamRuntime()
    {
        lock (_lock)
        {
            if (_exam is null)
            {
                return Shared.Policy.ExamRuntime.None;
            }

            var since = DateTimeOffset.UtcNow - Defaults.ExamTerminatedWindow;
            var recent = _terminated.Where(t => t.At >= since).OrderByDescending(t => t.At).Select(t => t.Name).Take(Defaults.ExamTerminatedMax).ToList();
            return new ExamRuntime(_watch.Active, recent, _terminatedVersion, string.Empty);
        }
    }

    /// <summary>
    /// The simulated student starts <paramref name="program"/>; under a whitelist that does not
    /// allow it, it is ended at once and noted. Called by the simulator's own timer and the tests.
    /// </summary>
    public void Launch(string program)
    {
        if (!ProgramNames.TryNormalize(program, out var name))
        {
            return;
        }

        lock (_lock)
        {
            _processes.Add(new ProcessInfo(_nextPid += 4, name, StudentSession, StudentSid, @"C:\Program Files\" + name, _nextPid));
            WatchLocked(DateTimeOffset.UtcNow);
        }

        UpdateStrip();
    }

    /// <summary>The desktop a simulated PC starts with: the shell, a browser and an editor left open from the last lesson.</summary>
    public void Seed(IEnumerable<string> programs)
    {
        lock (_lock)
        {
            foreach (var program in programs)
            {
                if (ProgramNames.TryNormalize(program, out var name))
                {
                    _processes.Add(new ProcessInfo(_nextPid += 4, name, StudentSession, StudentSid, @"C:\Program Files\" + name, _nextPid));
                }
            }
        }
    }

    /// <summary>What a desktop created after the exam started draws.</summary>
    public FakeStrip? StripContent()
    {
        lock (_lock)
        {
            if (_exam is not { } exam)
            {
                return null;
            }

            return new FakeStrip(exam.Message, exam.UntilUnix, _notice, _noticeUntil, exam.Language ?? string.Empty);
        }
    }

    private void UpdateStrip() => _desktop()?.SetExamStrip(StripContent());

    private void WatchLocked(DateTimeOffset now)
    {
        // Warned, then asked, then closed: three looks at the same moment walk a swept program through.
        for (var look = 0; look < 3; look++)
        {
            var decision = _watch.Observe(_processes, new SessionContext(StudentSession, StudentSid, []), now);
            if (decision.AskToClose.Count == 0 && decision.End.Count == 0 && decision.Warn.Count == 0)
            {
                break;
            }

            Close(decision.AskToClose.Concat(decision.End), now);
        }

        _terminated.RemoveAll(t => t.At < now - Defaults.ExamTerminatedWindow);
    }

    private void Close(IEnumerable<ProcessInfo> processes, DateTimeOffset now)
    {
        foreach (var process in processes.ToList())
        {
            // Simulated programs close when asked: the grace never runs out here.
            _processes.RemoveAll(p => p.Key == process.Key);
            _terminated.Add((process.Name, now));
            _terminatedVersion++;
            _notice = OverlayText.NotAllowedText(process.Name, _exam?.Language ?? string.Empty);
            _noticeUntil = now + Defaults.ExamNoticeDuration;
        }
    }
}

/// <summary>The exam strip as a simulated desktop draws it: the countdown is counted at every tick, the notice shown until <paramref name="NoticeUntil"/>.</summary>
public sealed record FakeStrip(string Message, long UntilUnix, string Notice, DateTimeOffset NoticeUntil, string Language = "")
{
    public ExamStripContent At(DateTimeOffset now) => new(
        Message,
        UntilUnix > 0 ? Math.Max(0, UntilUnix - now.ToUnixTimeSeconds()) : null,
        now < NoticeUntil ? Notice : string.Empty,
        Language);
}
