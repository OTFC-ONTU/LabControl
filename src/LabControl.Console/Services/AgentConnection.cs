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
        Machine = machine;
        CertificateSerial = certificateSerial;
        RemoteAddress = remoteAddress;
        LinkedAt = now;
        LastHeartbeat = now;
    }

    public Hello Hello { get; }

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

    public void ApplySessionState(SessionState state)
    {
        SessionLocked = state.Locked;
        HelperAlive = state.HelperAlive;
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
