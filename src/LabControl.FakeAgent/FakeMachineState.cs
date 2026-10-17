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
        Picture = new BroadcastPicture(log);
    }

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

    public MachineResult ApplyExam(ExamSession session) =>
        MachineResult.NotInThisBuild(PolicyEvents.KindName(session));

    public MachineResult LiftExam() => MachineResult.Done;
}
