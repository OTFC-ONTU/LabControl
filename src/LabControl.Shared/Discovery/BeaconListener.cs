using System.Net;
using System.Net.Sockets;

namespace LabControl.Shared.Discovery;

/// <summary>
/// Receives beacon datagrams and hands the raw bytes to whoever is interested: an agent's
/// <c>BeaconGate</c>, or the console watching for other teacher machines. Parsing and
/// verification are the receiver's business; a listener never decides anything.
/// <para>
/// All listeners in one process share one socket per port. macOS delivers a unicast
/// datagram — the loopback copy the console sends for agents on its own machine — to
/// exactly one of several sockets bound to the same port, so a process that opened two
/// would have one of them deaf. Sharing inside the process is what lets the console's own
/// listener and thirty fake agents in a test all hear the same beacon; across processes
/// the network broadcast copy reaches everyone as long as the machine has a network.
/// </para>
/// </summary>
public sealed class BeaconListener : IDisposable
{
    private readonly int _port;
    private SharedSocket? _shared;

    public BeaconListener(int port = Defaults.BeaconPort) => _port = port;

    /// <summary>
    /// A datagram arrived. Called on a background thread; keep it quick. The third argument
    /// is when the socket produced it, stamped once in the receive loop before the fan-out:
    /// the agent's take-over rule turns on whether a beacon arrived before or after its link
    /// came up, and a timestamp read later — after thirty listeners, or after another thread
    /// recorded the link — would answer that question wrongly (D-58).
    /// </summary>
    public event Action<ReadOnlyMemory<byte>, IPEndPoint, DateTimeOffset>? Received;

    /// <summary>The socket could not be opened or died; nothing else is heard until restarted.</summary>
    public event Action<string>? Failed;

    public bool IsListening => _shared is not null;

    public void Start()
    {
        if (_shared is not null)
        {
            return;
        }

        try
        {
            _shared = SharedSocket.Acquire(_port, this);
        }
        catch (SocketException ex)
        {
            Failed?.Invoke($"Cannot listen for beacons on UDP {_port}: {ex.Message}");
        }
    }

    internal void Deliver(ReadOnlyMemory<byte> datagram, IPEndPoint from, DateTimeOffset receivedAt) =>
        Received?.Invoke(datagram, from, receivedAt);

    /// <summary>What the process-wide sockets have seen; for a status line and for tests.</summary>
    public static string Describe() => SharedSocket.Describe();

    internal void Fail(string message) => Failed?.Invoke(message);

    public void Dispose()
    {
        _shared?.Release(this);
        _shared = null;
    }

    /// <summary>One bound socket per port per process, fanning out to every listener.</summary>
    private sealed class SharedSocket
    {
        private static readonly Dictionary<int, SharedSocket> Open = [];
        private static readonly Lock Registry = new();

        private readonly int _port;
        private readonly Socket _socket;
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<BeaconListener> _listeners = [];
        private readonly Lock _gate = new();
        private readonly Task _loop;
        private long _received;
        private string _lastError = string.Empty;

        public static string Describe()
        {
            lock (Registry)
            {
                return string.Join("; ", Open.Values.Select(s =>
                    $"udp {s._port}: {s._listeners.Count} listener(s), {Interlocked.Read(ref s._received)} datagram(s), loop {s._loop.Status}" +
                    (s._lastError.Length > 0 ? $", last error: {s._lastError}" : string.Empty)));
            }
        }

        private SharedSocket(int port)
        {
            _port = port;
            _socket = BeaconSocket.OpenListener(port);
            _loop = Task.Run(() => RunAsync(_stopping.Token));
        }

        public static SharedSocket Acquire(int port, BeaconListener listener)
        {
            lock (Registry)
            {
                if (!Open.TryGetValue(port, out var shared))
                {
                    shared = new SharedSocket(port);
                    Open[port] = shared;
                }

                lock (shared._gate)
                {
                    shared._listeners.Add(listener);
                }

                return shared;
            }
        }

        public void Release(BeaconListener listener)
        {
            lock (Registry)
            {
                bool last;
                lock (_gate)
                {
                    _listeners.Remove(listener);
                    last = _listeners.Count == 0;
                }

                if (!last)
                {
                    return;
                }

                Open.Remove(_port);
                _stopping.Cancel();
                _socket.Dispose();
                try
                {
                    _loop.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                }

                _stopping.Dispose();
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            var buffer = new byte[Defaults.BeaconMaxBytes + 1];
            var anyone = new IPEndPoint(IPAddress.Any, 0);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anyone, token);

                    // Stamped here, once, before anything is parsed or handed on: this is as
                    // close to the datagram's arrival as this process can see (D-58).
                    var receivedAt = DateTimeOffset.UtcNow;
                    Interlocked.Increment(ref _received);
                    if (result.ReceivedBytes is > 0 and <= Defaults.BeaconMaxBytes)
                    {
                        var datagram = buffer.AsMemory(0, result.ReceivedBytes).ToArray();
                        var from = (IPEndPoint)result.RemoteEndPoint;
                        foreach (var listener in Snapshot())
                        {
                            try
                            {
                                listener.Deliver(datagram, from, receivedAt);
                            }
                            catch (Exception ex)
                            {
                                // A subscriber that throws must not deafen every other one.
                                listener.Fail($"A beacon handler failed: {ex.Message}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException ex)
                {
                    _lastError = ex.Message;

                    // ICMP port-unreachable surfaces here on Windows; anything else is worth a line.
                    if (ex.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.MessageSize))
                    {
                        foreach (var listener in Snapshot())
                        {
                            listener.Fail($"Beacon listener error: {ex.Message}");
                        }

                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }
        }

        private BeaconListener[] Snapshot()
        {
            lock (_gate)
            {
                return _listeners.ToArray();
            }
        }
    }
}
