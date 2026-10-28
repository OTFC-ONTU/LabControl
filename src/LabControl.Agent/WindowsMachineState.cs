using LabControl.Shared;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent;

/// <summary>
/// What a real PC does with a policy (M6, D-69): the Windows half of the
/// <see cref="PolicyEnforcer"/>. A lock goes down the helper pipe as an <c>Overlay</c> and the
/// helper's <c>overlay_state</c> comes back through <see cref="SessionSupervisor"/>; the overlay
/// needs no restore, it lives in a helper a restart has already ended (D-69 item 2). Since portion 3 the
/// restore steps and the internet calls are <see cref="WindowsInternetControl"/>'s — the
/// firewall, the DNS, the resolver and the watchdog task (D-73, D-74); the exam calls still
/// answer <see cref="MachineOutcome.NotInThisBuild"/> without touching anything.
/// <para>
/// Since portion 2 the broadcast goes the same way (D-71): <c>BROADCAST_START</c> and
/// <c>BROADCAST_STOP</c> as <c>Overlay</c>s, every frame as <c>ServiceMessage.broadcast_frame</c>,
/// awaited so a slow helper holds the stream back.
/// </para>
/// <para>
/// Nothing here throws: a pipe that is not there is "not shown yet", which the helper's own
/// report and the supervisor's re-assert (D-70 item 4) turn into a lock the moment it connects.
/// </para>
/// </summary>
internal sealed class WindowsMachineState : IMachineState
{
    private readonly Func<ServiceMessage, bool> _sendToHelper;
    private readonly Func<ServiceMessage, ValueTask<bool>> _sendToHelperAsync;
    private readonly Func<IReadOnlyList<string>> _helperDecoders;
    private readonly ILogger _log;
    private readonly WindowsInternetControl _internet;
    private readonly WindowsExamControl _exam;

    /// <param name="sendToHelper">Writes a message down the helper pipe, fire-and-forget; <c>false</c> when no helper is connected.</param>
    /// <param name="sendToHelperAsync">The same, waiting until it is written — for broadcast frames, so a slow helper holds the stream back.</param>
    /// <param name="helperDecoders">What the connected helper decodes besides JPEG (D-91); empty when none is connected.</param>
    public WindowsMachineState(Func<ServiceMessage, bool> sendToHelper, Func<ServiceMessage, ValueTask<bool>> sendToHelperAsync, ILogger log,
        Func<IReadOnlyList<string>>? helperDecoders = null)
    {
        _sendToHelper = sendToHelper;
        _sendToHelperAsync = sendToHelperAsync;
        _helperDecoders = helperDecoders ?? (() => []);
        _log = log;
        _internet = new WindowsInternetControl(log, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "agent.exe"));

        // LabControl's own binaries — the install directory, and wherever this build runs from — are never judged by an exam's whitelist.
        _exam = new WindowsExamControl(log, sendToHelper, [Defaults.AgentInstallDirectory, AppContext.BaseDirectory], SystemDirectories());
    }

    /// <summary>
    /// The internet half of the unconditional restore (D-69 item 2, D-73); the overlay lives in a
    /// helper a restart has already ended. The exam's watch stops too: a restore at stop must not
    /// leave it closing programs until the process exits.
    /// </summary>
    public MachineResult RestoreDefaults()
    {
        _exam.Lift();
        return _internet.RestoreDefaults();
    }

    public MachineResult RefineRestore(DisplacedSettings displaced) => _internet.RefineRestore(displaced);

    public MachineResult Confirm() => _internet.Confirm();

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
            Language = session.Language ?? string.Empty,
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

    /// <summary>
    /// The broadcast goes down the pipe like the lock (M6 portion 2, D-71): the helper takes
    /// the screen, blocks input with the same hooks, and keeps a held lock on top. The hard
    /// limit travels as what is left of it; the receiver here is what ends the broadcast.
    /// </summary>
    public MachineResult ShowBroadcast(BroadcastSession session)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var overlay = new Overlay
        {
            Kind = Overlay.Types.Kind.BroadcastStart,
            SessionId = session.SessionId,
            Message = session.ConsoleName,
            HardLimitSeconds = Math.Max(1, session.HardLimitUnix - now),
        };

        if (!_sendToHelper(new ServiceMessage { Overlay = overlay }))
        {
            _log.LogInformation("broadcast {Session} received; no session helper is connected yet, it is shown when one connects", session.SessionId);
        }

        return MachineResult.Done;
    }

    /// <summary>The helper shows the broadcast, so what it decodes is what this PC asks for (D-91 item 3).</summary>
    public IReadOnlyList<string> BroadcastCodecs => _helperDecoders();

    public async ValueTask BroadcastFrameAsync(VideoFrame frame, CancellationToken token)
    {
        // A helper that is not there misses the frame; the next keyframe brings the picture back.
        await _sendToHelperAsync(new ServiceMessage { BroadcastFrame = frame });
    }

    public MachineResult StopBroadcast()
    {
        _sendToHelper(new ServiceMessage { Overlay = new Overlay { Kind = Overlay.Types.Kind.BroadcastStop } });
        return MachineResult.Done;
    }

    public MachineResult ApplyInternet(InternetSession session, DisplacedSettings displaced, Action recordDisplaced) =>
        _internet.Apply(session, displaced, recordDisplaced);

    public MachineResult LiftInternet(DisplacedSettings displaced) => _internet.Lift(displaced);

    public InternetRuntime InternetRuntime() => _internet.Runtime();

    public bool InternetNeedsReassert() => _internet.NeedsReassert();

    /// <summary>Where the never-touch names are Windows' own: a copy of <c>conhost.exe</c> anywhere else is judged like any program.</summary>
    private static string[] SystemDirectories()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return [windows, Path.Combine(programFiles, "WindowsApps"), Path.Combine(programFilesX86, "Microsoft", "EdgeWebView"), Path.Combine(programFiles, "Microsoft", "EdgeWebView")];
    }

    /// <summary>
    /// The strip down the pipe and the allowed-programs watch in this service (M6 portion 4,
    /// D-75), and the folder the console will collect created for the student (portion 5,
    /// D-77) — off the policy's path and best effort: an exam never fails because a folder
    /// could not be made, and the collection says so later.
    /// </summary>
    public MachineResult ApplyExam(ExamSession session)
    {
        var result = _exam.Apply(session);
        if (session.CollectFolder is { Length: > 0 } folder && LabControl.Shared.Jobs.CollectRequest.TryNormalize(folder, out var normalized))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    using var student = ManagedStudentAccess.Open();
                    var path = CollectRunner.Resolve(student, new LabControl.Shared.Jobs.CollectRequest(normalized));
                    System.Security.Principal.WindowsIdentity.RunImpersonated(student.Token, () => Directory.CreateDirectory(path));
                }
                catch (Exception ex)
                {
                    _log.LogInformation("the exam's folder {Folder} was not created: {Message}", normalized, ex.Message);
                }
            });
        }

        return result;
    }

    public MachineResult LiftExam() => _exam.Lift();

    public ExamRuntime ExamRuntime() => _exam.Runtime();
}
