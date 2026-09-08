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
