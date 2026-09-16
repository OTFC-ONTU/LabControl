using LabControl.Shared.Policy;

namespace LabControl.FakeAgent;

/// <summary>
/// What a simulated PC does with a policy (M6 portion 1): a lock is drawn onto the
/// simulated desktop — the same <see cref="OverlayText.Compose"/> picture the real helper
/// blits, so the console's mosaic shows the lock exactly as a real PC would show it — and
/// nothing else exists yet. The journal, the clamp, the document and the hard limit on the
/// PC's own clock are the shared <see cref="PolicyEnforcer"/>'s, not pretended here.
/// </summary>
internal sealed class FakeMachineState : IMachineState
{
    private readonly Func<FakeScreen?> _desktop;
    private readonly Lock _lock = new();
    private OverlaySession? _overlay;

    /// <param name="desktop">The desktop being drawn right now, or <c>null</c> while nobody watches; a desktop created later is told by <see cref="Overlay"/>.</param>
    public FakeMachineState(Func<FakeScreen?> desktop)
    {
        _desktop = desktop;
    }

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

    public MachineResult ApplyInternet(InternetSession session, DisplacedSettings displaced) =>
        MachineResult.NotInThisBuild(PolicyEvents.KindName(session));

    public MachineResult LiftInternet() => MachineResult.Done;

    public MachineResult ApplyExam(ExamSession session) =>
        MachineResult.NotInThisBuild(PolicyEvents.KindName(session));

    public MachineResult LiftExam() => MachineResult.Done;
}
