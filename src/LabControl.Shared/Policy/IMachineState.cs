using LabControl.Shared.Protocol;

namespace LabControl.Shared.Policy;

/// <summary>How a machine call ended, without an exception ever leaving it.</summary>
public enum MachineOutcome
{
    /// <summary>Done, and — where the call promises it — confirmed by native read-back.</summary>
    Done = 0,

    /// <summary>The call ran and failed; <see cref="MachineResult.Problem"/> says how, in the teacher's words.</summary>
    Failed = 1,

    /// <summary>This build cannot do it yet (a session kind whose portion has not landed); nothing was touched.</summary>
    NotInThisBuild = 2,
}

/// <summary>The result of one <see cref="IMachineState"/> call.</summary>
public readonly record struct MachineResult(MachineOutcome Outcome, string Problem)
{
    public static MachineResult Done { get; } = new(MachineOutcome.Done, string.Empty);

    public static MachineResult Failed(string problem) => new(MachineOutcome.Failed, problem);

    public static MachineResult NotInThisBuild(string what) => new(MachineOutcome.NotInThisBuild, what);

    public bool Succeeded => Outcome == MachineOutcome.Done;
}

/// <summary>
/// The machine the <see cref="PolicyJournal"/> drives (M6, D-69): the Windows implementation
/// in the agent, the simulator's in <c>FakeAgent</c>, a fake in the tests. The journal is
/// the only caller and wraps every call, but an implementation is still expected to return
/// a <see cref="MachineResult"/> rather than throw — a Win32 failure is a result, never an
/// exception out of the service loop.
/// </summary>
public interface IMachineState
{
    /// <summary>
    /// Step 1 of a restore pass: put Windows defaults back <b>without reading any document</b>
    /// — every profile's default outbound action to Allow first, then the owned firewall rule
    /// group with the <c>agent.exe</c> exemption last, DNS on any loopback-pointed interface
    /// back to DHCP, the resolver stopped, the watchdog task deleted. A firewall profile whose
    /// <c>FirewallEnabled</c> reads false is "cannot enforce", never "enforcing" (step 0).
    /// The overlay needs no restore: it lives in a helper that has already been restarted.
    /// </summary>
    MachineResult RestoreDefaults();

    /// <summary>
    /// Step 3: put back what step 1 could not know — a static DNS list exactly as it was, a
    /// profile that was genuinely block-all before the policy. Only ever called with the
    /// document's <see cref="DisplacedSettings"/>, after <see cref="RestoreDefaults"/>.
    /// </summary>
    MachineResult RefineRestore(DisplacedSettings displaced);

    /// <summary>Step 4: read the machine back and say whether it is as open as steps 1 and 3 meant it to be.</summary>
    MachineResult Confirm();

    /// <summary>Show the lock — to the helper, which may not be there yet; then the reason says so.</summary>
    MachineResult ApplyOverlay(OverlaySession session);

    MachineResult LiftOverlay();

    /// <summary>
    /// Take the screen for the teacher's broadcast (M6 portion 2, D-71): full-screen picture,
    /// input blocked like a lock, and a lock — if one is held — stays on top. Called by the
    /// <see cref="BroadcastReceiver"/>, not the journal: a broadcast is never in
    /// <c>policy.json</c>. Called again with the same session to re-assert it to a new helper.
    /// </summary>
    MachineResult ShowBroadcast(BroadcastSession session) => MachineResult.NotInThisBuild(PolicyEvents.Text("KindBroadcast"));

    /// <summary>
    /// One frame of the broadcast, as <c>PullBroadcast</c> delivered it — a keyframe or a
    /// delta on the one before. Awaited, so a machine that shows frames slowly holds the
    /// stream back rather than piling frames up; never throws.
    /// </summary>
    ValueTask BroadcastFrameAsync(VideoFrame frame, CancellationToken token) => ValueTask.CompletedTask;

    /// <summary>Give the screen back after a broadcast; a held lock stays.</summary>
    MachineResult StopBroadcast() => MachineResult.Done;

    /// <summary>
    /// Enforce an internet policy (portion 3), recording what it displaces into
    /// <paramref name="displaced"/> <i>before</i> mutating and marking it applied only after
    /// native read-back. The same block is passed for every internet session of a pass — the
    /// standalone policy and then the exam's — and may already hold originals kept from a
    /// restore that was not confirmed, so an implementation records an original <b>only
    /// while the block has none for it</b>; overwriting would replace the teacher's values
    /// with the first policy's. Until portion 3 lands this returns <see cref="MachineOutcome.NotInThisBuild"/>.
    /// </summary>
    MachineResult ApplyInternet(InternetSession session, DisplacedSettings displaced);

    MachineResult LiftInternet();

    /// <summary>Enforce an exam's timer, whitelist and collection (portion 4); <see cref="MachineOutcome.NotInThisBuild"/> until then.</summary>
    MachineResult ApplyExam(ExamSession session);

    MachineResult LiftExam();
}
