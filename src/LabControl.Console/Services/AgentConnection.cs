using System.Net;
using System.Threading.Channels;
using LabControl.Shared;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;

namespace LabControl.Console.Services;

/// <summary>
/// One live <c>Link</c> stream: the PC on the other end, what it said in <c>Hello</c>, when
/// it last heartbeated, and the queue of messages on their way to it. Owned by
/// <see cref="LabSession"/>; the UI reads it, never writes it.
/// </summary>
public sealed class AgentConnection
{
    private readonly Channel<ConsoleMessage> _outgoing = Channel.CreateUnbounded<ConsoleMessage>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _closing = new();
    private readonly Action? _abort;

    public AgentConnection(Hello hello, MachineRecord machine, string certificateSerial, IPAddress remoteAddress, DateTimeOffset now, Action? abort = null)
    {
        _abort = abort;
        Hello = hello;
        UpdateState = hello.UpdateState?.Clone() ?? new UpdateState();
        Machine = machine;
        CertificateSerial = certificateSerial;
        RemoteAddress = remoteAddress;
        LinkedAt = now;
        LastHeartbeat = now;
    }

    public Hello Hello { get; }

    public UpdateState UpdateState { get; private set; }

    public MachineRecord Machine { get; }

    public string AgentId => Hello.AgentId;

    public int Number => Hello.Number;

    public string CertificateSerial { get; }

    public IPAddress RemoteAddress { get; }

    public DateTimeOffset LinkedAt { get; }

    public DateTimeOffset LastHeartbeat { get; private set; }

    /// <summary>Why the link ended, once it has; for the tile tooltip and the event.</summary>
    public string? ClosedBecause { get; private set; }

    /// <summary>
    /// The console instance this PC said it was leaving for, from its <c>link.taken_over</c>
    /// event (M5, D-58); <c>null</c> unless the PC actually said so. It is the only evidence
    /// this console will accept that another teacher machine now holds the PC — the end of a
    /// stream on its own says nothing, because a PC that is switched off looks exactly the
    /// same. Written on the link's read thread and read when the link ends.
    /// </summary>
    public string? LeavingForInstanceId { get; private set; }

    /// <summary>The PC named the machine that took it over; remembered until the stream ends.</summary>
    public void NoteLeavingFor(string instanceId) => LeavingForInstanceId = instanceId;

    /// <summary>The lock screen is up on the PC, from the agent's last <c>SessionState</c> (M2).</summary>
    public bool SessionLocked { get; private set; }

    /// <summary>
    /// <c>session.exe</c> is running in the interactive session and talking to the service;
    /// <c>null</c> until the agent has said either way (an M1-era agent never does).
    /// </summary>
    public bool? HelperAlive { get; private set; }

    /// <summary>The video mode the console last asked this link for (M3); <see cref="VideoMode.Unspecified"/> when none.</summary>
    public VideoMode RequestedVideo { get; internal set; }

    /// <summary>The interactive session's id from the last <c>SessionState</c>; 0 when there is none (logon screen not up yet, or a headless boot).</summary>
    public uint SessionId { get; private set; }

    /// <summary>The agent has said there is no interactive session at all — nothing to capture or drive.</summary>
    public bool NoSession => HelperAlive is not null && SessionId == 0;

    /// <summary>
    /// Why the PC cannot capture its screen, from the helper's last <c>capture.&lt;reason&gt;</c>
    /// event (D-35 item 5); <c>null</c> once it reported <c>capture.recovered</c>. The tile
    /// shows it instead of "picture stalled" (M3 portion 3).
    /// </summary>
    public string? CaptureProblem { get; private set; }

    /// <summary>The teacher's input is not reaching the desktop, from the helper's last <c>input.&lt;reason&gt;</c> event.</summary>
    public string? InputProblem { get; private set; }

    public void ApplySessionState(SessionState state)
    {
        SessionLocked = state.Locked;
        HelperAlive = state.HelperAlive;
        SessionId = state.SessionId;
    }

    /// <summary>
    /// What the PC is showing, from its last <c>OverlayState</c> (M6 portion 1, D-70): the
    /// enforcer's truth, never an echo of what this console asked for. <c>null</c> until the
    /// agent said anything (an agent from before M6 never does). A refusal — <c>problem</c>
    /// set, <c>kind</c> unspecified — is kept in <see cref="OverlayProblem"/> and does
    /// <b>not</b> replace the last state that described the screen.
    /// </summary>
    public OverlayState? OverlayState { get; private set; }

    /// <summary>Why the last <c>Overlay</c> was refused or failed, until the next state without a problem; <c>null</c> otherwise.</summary>
    public string? OverlayProblem { get; private set; }

    /// <summary>The PC holds a lock (its own word, from <see cref="OverlayState"/>).</summary>
    public bool OverlayLocked => OverlayState is { Kind: Overlay.Types.Kind.Lock };

    /// <summary>The PC shows the teacher's screen (its own word, M6 portion 2, D-71); a lock on top of it reads as a lock.</summary>
    public bool OverlayBroadcasting => OverlayState is { Kind: Overlay.Types.Kind.BroadcastStart };

    /// <summary>
    /// Merges an <c>OverlayState</c>; returns <c>true</c> when what the tile shows changed.
    /// A refused request carries no picture of the screen (PROTOCOL <i>Overlay</i>), so only
    /// its <c>problem</c> is taken; a state without a problem clears the last one.
    /// </summary>
    public bool ApplyOverlayState(OverlayState state)
    {
        var lockedBefore = OverlayLocked;
        var kindBefore = OverlayState?.Kind;
        var problemBefore = OverlayProblem;
        var setByBefore = OverlayState?.SetByInstance;
        var sessionBefore = OverlayState?.SessionId;
        var blockedBefore = OverlayState?.InputBlocked;
        var reasonBefore = OverlayState?.Reason;
        if (state.Problem.Length > 0 && state.Kind == Overlay.Types.Kind.Unspecified && OverlayState is not null)
        {
            OverlayProblem = state.Problem;
        }
        else
        {
            OverlayState = state;
            OverlayProblem = state.Problem.Length > 0 ? state.Problem : null;
        }

        // The weakness line on the badge follows input_blocked and reason too, not only the lock itself.
        return lockedBefore != OverlayLocked || kindBefore != OverlayState?.Kind || problemBefore != OverlayProblem
            || setByBefore != OverlayState?.SetByInstance || sessionBefore != OverlayState?.SessionId
            || blockedBefore != OverlayState?.InputBlocked || !string.Equals(reasonBefore, OverlayState?.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The internet policy the PC enforces, from its last <c>InternetState</c> (M6 portion 3,
    /// D-73): its own word, never an echo of what this console asked. <c>null</c> until the agent
    /// said anything. A refusal — <c>problem</c> set, mode unspecified — is kept in
    /// <see cref="InternetProblem"/> and does not replace the last state that described the PC.
    /// </summary>
    public InternetState? InternetState { get; private set; }

    /// <summary>Why the last <c>InternetPolicy</c> was refused or failed, until the next state without a problem; <c>null</c> otherwise.</summary>
    public string? InternetProblem { get; private set; }

    /// <summary>The policy the PC enforces: <c>OPEN</c> when none or unknown.</summary>
    public InternetPolicy.Types.Mode InternetMode =>
        InternetState is { Mode: InternetPolicy.Types.Mode.Whitelist or InternetPolicy.Types.Mode.Blocked } state ? state.Mode : InternetPolicy.Types.Mode.Open;

    /// <summary>Merges an <c>InternetState</c>; returns <c>true</c> when what the tile shows changed.</summary>
    public bool ApplyInternetState(InternetState state)
    {
        var before = InternetState?.Clone();
        var problemBefore = InternetProblem;
        if (state.Problem.Length > 0 && state.Mode == InternetPolicy.Types.Mode.Unspecified && InternetState is not null)
        {
            InternetProblem = state.Problem;
        }
        else
        {
            InternetState = state;
            InternetProblem = state.Problem.Length > 0 ? state.Problem : null;
        }

        // remaining_seconds moves with every send; it is not a change the tile needs to redraw for.
        var after = InternetState?.Clone();
        if (before is not null)
        {
            before.RemainingSeconds = 0;
            before.Problem = string.Empty;
        }

        if (after is not null)
        {
            after.RemainingSeconds = 0;
            after.Problem = string.Empty;
        }

        return problemBefore != InternetProblem || !Equals(before, after);
    }

    /// <summary>
    /// The exam the PC enforces, from its last <c>ExamState</c> (M6 portion 4, D-75): its own
    /// word, never an echo of the request. <c>null</c> until the agent said anything. A refusal
    /// — <c>problem</c> set and not active — is kept in <see cref="ExamProblem"/> and does not
    /// replace the last state that described the PC.
    /// </summary>
    public ExamState? ExamState { get; private set; }

    /// <summary>Why the last <c>ExamMode</c> was refused or failed, until the next state without a problem; <c>null</c> otherwise.</summary>
    public string? ExamProblem { get; private set; }

    /// <summary>The PC holds an exam right now, by its own word.</summary>
    public bool InExam => ExamState is { Active: true };

    /// <summary>The exam sessions this connection has seen the PC hold or has been sent; a lab-wide exam is sent to a late PC once (owner decision 2026-10-23).</summary>
    internal HashSet<string> ExamSessionsSeen { get; } = new(StringComparer.Ordinal);

    /// <summary>When, on this console's clock, the last <c>ExamState</c> describing the PC arrived: the badge counts <c>remaining_seconds</c> down from here, never from the PC's clock.</summary>
    public DateTimeOffset ExamStateAt { get; private set; }

    /// <summary>Merges an <c>ExamState</c>; returns <c>true</c> when what the tile shows changed.</summary>
    public bool ApplyExamState(ExamState state, DateTimeOffset now)
    {
        var before = ExamState?.Clone();
        var problemBefore = ExamProblem;
        if (state.Problem.Length > 0 && !state.Active && ExamState is not null)
        {
            ExamProblem = state.Problem;
        }
        else
        {
            ExamState = state;
            ExamStateAt = now;
            ExamProblem = state.Problem.Length > 0 ? state.Problem : null;
        }

        if (ExamState is { Active: true, SessionId.Length: > 0 } held)
        {
            ExamSessionsSeen.Add(held.SessionId);
        }

        // remaining_seconds moves with every send; it is not a change the tile needs to redraw for.
        var after = ExamState?.Clone();
        if (before is not null)
        {
            before.RemainingSeconds = 0;
            before.Problem = string.Empty;
        }

        if (after is not null)
        {
            after.RemainingSeconds = 0;
            after.Problem = string.Empty;
        }

        return problemBefore != ExamProblem || !Equals(before, after);
    }

    /// <summary>
    /// Reads the capture and input problems out of the agent's events; returns <c>true</c>
    /// when something the tile shows changed.
    /// </summary>
    public bool ApplyEvent(Event reported)
    {
        var code = reported.Code;
        if (code == LabControl.Shared.Setup.SetupReadiness.EventCode)
        {
            try
            {
                var snapshot = LabControl.Shared.Setup.SetupReadiness.Parse(reported.Message);
                if ((Machine.SetupReadinessCodes ?? []).SequenceEqual(snapshot.Codes)) return false;
                Machine.SetupReadinessCodes = snapshot.Codes;
                return true;
            }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or System.Text.Json.JsonException
                or LabControl.Shared.Persistence.SchemaVersionException) { return false; }
        }
        if (code == LabControl.Shared.Setup.UpdateTerminalReport.RolledBackCode)
        {
            if (!LabControl.Shared.Setup.InstallLayout.IsValidVersion(reported.Message)) return false;
            var changed = UpdateState.Phase != UpdateState.Types.Phase.RolledBack || UpdateState.FailedVersion != reported.Message;
            UpdateState = new UpdateState { Phase = UpdateState.Types.Phase.RolledBack, FailedVersion = reported.Message };
            return changed;
        }
        if (code == "update.stable")
        {
            UpdateState = new UpdateState { Phase = UpdateState.Types.Phase.Stable };
            return true;
        }
        if (code.StartsWith("capture.", StringComparison.Ordinal))
        {
            var reason = code["capture.".Length..];
            var before = CaptureProblem;
            CaptureProblem = reason switch
            {
                "recovered" => null,
                "fallback" => CaptureProblem,
                _ => reason,
            };
            return before != CaptureProblem;
        }

        if (code.StartsWith("input.", StringComparison.Ordinal))
        {
            var reason = code["input.".Length..];
            var before = InputProblem;
            InputProblem = reason switch
            {
                "recovered" => null,
                "sas" or "sas_failed" => InputProblem, // Ctrl+Alt+Del is the service's, not the injector's
                _ => reason,
            };
            return before != InputProblem;
        }

        return false;
    }

    /// <summary>Below the console's minimum: still linked, tile marked outdated, only the frozen subset used (D-19).</summary>
    public bool IsOutdated => Hello.ProtocolVersion < Defaults.MinimumProtocolVersion;

    /// <summary>Newer than this console: usable, but the console should be updated too.</summary>
    public bool IsNewer => Hello.ProtocolVersion > Defaults.ProtocolVersion;

    /// <summary>
    /// The agent enforces classroom control (M6, D-79 item 4): a lock, a broadcast, an internet
    /// policy or an exam is <i>set</i> only on such a PC. Ending one is sent to any PC.
    /// </summary>
    public bool SupportsClassroomControl => !IsOutdated && Hello.ProtocolVersion >= Defaults.ClassroomControlProtocolVersion;

    public ChannelReader<ConsoleMessage> Outgoing => _outgoing.Reader;

    public CancellationToken Closing => _closing.Token;

    public void Touch(DateTimeOffset now) => LastHeartbeat = now;

    /// <summary>Queues a message; returns <c>false</c> once the link is closing.</summary>
    public bool TrySend(ConsoleMessage message) => _outgoing.Writer.TryWrite(message);

    /// <summary>Ends the stream from the console's side: heartbeat timeout, a replacement link, shutdown.</summary>
    public void Close(string reason)
    {
        if (ClosedBecause is not null)
        {
            return;
        }

        ClosedBecause = reason;
        _outgoing.Writer.TryComplete();
        _closing.Cancel();

        // Ending the handler is not enough: the PC must see the stream die now, not when a
        // keepalive ping times out, so the transport connection is torn down as well.
        try
        {
            _abort?.Invoke();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
