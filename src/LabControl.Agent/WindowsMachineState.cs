using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent;

/// <summary>
/// What a real PC does with a policy (M6, D-69): the Windows half of the
/// <see cref="PolicyEnforcer"/>. In portion 1 only the overlay exists — a lock goes down the
/// helper pipe as an <c>Overlay</c> and the helper's <c>overlay_state</c> comes back through
/// <see cref="SessionSupervisor"/> — so the restore steps have nothing to restore yet (the
/// overlay lives in a helper that a restart has already ended, D-69 item 2) and the internet
/// and exam calls answer <see cref="MachineOutcome.NotInThisBuild"/> without touching anything.
/// Portion 3 fills <see cref="RestoreDefaults"/>, <see cref="RefineRestore"/> and
/// <see cref="Confirm"/> with the firewall, DNS and watchdog-task work; the hooks are marked.
/// <para>
/// Nothing here throws: a pipe that is not there is "not shown yet", which the helper's own
/// report and the supervisor's re-assert (D-70 item 4) turn into a lock the moment it connects.
/// </para>
/// </summary>
internal sealed class WindowsMachineState : IMachineState
{
    private readonly Func<ServiceMessage, bool> _sendToHelper;
    private readonly ILogger _log;

    /// <param name="sendToHelper">Writes a message down the helper pipe, fire-and-forget; <c>false</c> when no helper is connected.</param>
    public WindowsMachineState(Func<ServiceMessage, bool> sendToHelper, ILogger log)
    {
        _sendToHelper = sendToHelper;
        _log = log;
    }

    public MachineResult RestoreDefaults()
    {
        // Portion 3 (D-69 item 2, D-73): every profile's default outbound action to Allow,
        // the owned rule group with the agent.exe exemption last, loopback DNS back to DHCP,
        // the resolver stopped, the watchdog task deleted. In portion 1 nothing on the machine
        // outlives the helper, so there is nothing to put back.
        return MachineResult.Done;
    }

    public MachineResult RefineRestore(DisplacedSettings displaced)
    {
        // Portion 3: a static DNS list back exactly, a profile that was genuinely block-all.
        return displaced.IsEmpty ? MachineResult.Done : MachineResult.NotInThisBuild(PolicyEvents.Text("KindInternet"));
    }

    public MachineResult Confirm()
    {
        // Portion 3: read the firewall profiles and DNS back. Nothing to confirm yet.
        return MachineResult.Done;
    }

    public MachineResult ApplyOverlay(OverlaySession session)
    {
        // The pipe carries durations like the link does (D-70 item 6): what is left of the
        // session on this clock, at least a second so a lock that is still valid is never
        // handed over as already ended. The helper stamps them onto the same clock.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var overlay = new Overlay
        {
            Kind = session.Kind,
            Message = session.Message,
            SessionId = session.SessionId,
            UntilSeconds = session.UntilUnix > 0 ? Math.Max(1, session.UntilUnix - now) : 0,
            HardLimitSeconds = Math.Max(1, session.HardLimitUnix - now),
        };

        if (!_sendToHelper(new ServiceMessage { Overlay = overlay }))
        {
            // Not a failure: the supervisor tells the helper when it connects, and the console
            // sees the gap in OverlayState.reason until then.
            _log.LogInformation("lock {Session} recorded; no session helper is connected yet, it is shown when one connects", session.SessionId);
        }

        return MachineResult.Done;
    }

    public MachineResult LiftOverlay()
    {
        _sendToHelper(new ServiceMessage { Overlay = new Overlay { Kind = Overlay.Types.Kind.Unlock } });
        return MachineResult.Done;
    }

    public MachineResult ApplyInternet(InternetSession session, DisplacedSettings displaced)
    {
        // Portion 3 (D-73, D-74): the watchdog task is armed here, right after the first
        // displaced setting is confirmed applied (UpdateRecovery.Arm pattern).
        return MachineResult.NotInThisBuild(PolicyEvents.KindName(session));
    }

    public MachineResult LiftInternet() => MachineResult.NotInThisBuild(PolicyEvents.Text("KindInternet"));

    public MachineResult ApplyExam(ExamSession session) => MachineResult.NotInThisBuild(PolicyEvents.KindName(session));

    public MachineResult LiftExam() => MachineResult.NotInThisBuild(PolicyEvents.Text("KindExam"));
}
