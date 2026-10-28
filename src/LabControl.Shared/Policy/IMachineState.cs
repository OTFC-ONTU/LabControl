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

    /// <summary>
    /// The codecs besides JPEG whatever shows the broadcast can decode — <c>BroadcastRequest.codecs</c>
    /// (M7 portion 6, D-91): <c>"h264"</c> when it can, nothing for JPEG only. Read at every dial.
    /// </summary>
    IReadOnlyList<string> BroadcastCodecs => [];

    /// <summary>Give the screen back after a broadcast; a held lock stays.</summary>
    MachineResult StopBroadcast() => MachineResult.Done;

    /// <summary>
    /// Enforce an internet policy (portion 3, D-73): <c>WHITELIST</c> or <c>BLOCKED</c>. Called
    /// again with the same or another session to change it — another mode, another host list —
    /// and must then change only what differs. Records what it displaces into
    /// <paramref name="displaced"/> and calls <paramref name="recordDisplaced"/> <i>before</i>
    /// mutating (the journal saves the document there, the D-47 discipline), and marks the block
    /// applied only after native read-back. The same block is passed for every internet session
    /// of a pass — the standalone policy and then the exam's — and may already hold originals
    /// kept from a restore that was not confirmed, so an implementation records an original
    /// <b>only while the block has none for it</b>; overwriting would replace the teacher's
    /// values with the first policy's. A failure may leave the machine half-changed: the journal
    /// then calls <see cref="LiftInternet"/> with the same block.
    /// </summary>
    MachineResult ApplyInternet(InternetSession session, DisplacedSettings displaced, Action recordDisplaced);

    /// <summary>
    /// End every internet policy: the unconditional restore, then the refine from
    /// <paramref name="displaced"/>, then the read-back — the same steps a restore pass takes,
    /// so a lift proves the machine open or fails. On success the journal forgets the block.
    /// </summary>
    MachineResult LiftInternet(DisplacedSettings displaced);

    /// <summary>
    /// What the internet enforcement is doing right now, for <c>InternetState</c>: whether the
    /// whitelist resolver runs, the names it refused lately and a counter that moves when they
    /// change, and <see cref="InternetReasons"/> codes for anything weaker than asked. Cheap and
    /// thread-safe: the enforcer reads it every <see cref="Defaults.InternetStateRefusedInterval"/>.
    /// </summary>
    InternetRuntime InternetRuntime() => Policy.InternetRuntime.None;

    /// <summary>
    /// True when the enforcement in force has fallen behind the machine — an interface that came
    /// up after a whitelist was applied still has its own DNS (D-74 item 6) — so the enforcer
    /// re-applies through the journal, which records the new originals first. Cheap; never throws.
    /// </summary>
    bool InternetNeedsReassert() => false;

    /// <summary>
    /// Enforce an exam (portion 4, D-75): the strip on the student's screen — the countdown to
    /// <see cref="PolicySession.UntilUnix"/> and the message — and, when
    /// <see cref="ExamSession.RestrictPrograms"/> is on, the allowed-programs watch, whose first
    /// look is the opening sweep. Called again with the same session to re-state it (extended,
    /// a new list, a helper that restarted) and must then change only what differs: the same
    /// list does not sweep again. The internet switch is not this call's: the journal applies
    /// it as an internet session (D-76).
    /// </summary>
    MachineResult ApplyExam(ExamSession session);

    /// <summary>End the exam's strip and watch. Nothing it closed comes back.</summary>
    MachineResult LiftExam();

    /// <summary>The live side of the exam for <c>ExamState</c>; cheap and thread-safe, read every <see cref="Defaults.ExamStateRefreshInterval"/>.</summary>
    ExamRuntime ExamRuntime() => Policy.ExamRuntime.None;
}

/// <summary>The live side of an exam (D-75), read by the enforcer for <c>ExamState</c>.</summary>
/// <param name="WhitelistEnforced">The allowed-programs watch is running and can see the student's session.</param>
/// <param name="Terminated">Programs the watch closed within <see cref="Defaults.ExamTerminatedWindow"/>, most recent first.</param>
/// <param name="TerminatedVersion">Moves whenever <paramref name="Terminated"/> changes.</param>
/// <param name="Reason"><see cref="ExamReasons"/> codes, space-separated; empty when all is well.</param>
public sealed record ExamRuntime(bool WhitelistEnforced, IReadOnlyList<string> Terminated, long TerminatedVersion, string Reason)
{
    public static ExamRuntime None { get; } = new(false, [], 0, string.Empty);
}

/// <summary>The live side of internet enforcement (D-74), read by the enforcer for <c>InternetState</c>.</summary>
/// <param name="ResolverActive">The whitelist resolver is listening.</param>
/// <param name="Refused">Names refused within <see cref="Defaults.WhitelistRefusedWindow"/>, most recent first.</param>
/// <param name="RefusedVersion">Moves whenever <paramref name="Refused"/> changes.</param>
/// <param name="Reason"><see cref="InternetReasons"/> codes, space-separated; empty when all is well.</param>
public sealed record InternetRuntime(bool ResolverActive, IReadOnlyList<string> Refused, long RefusedVersion, string Reason)
{
    public static InternetRuntime None { get; } = new(false, [], 0, string.Empty);
}
