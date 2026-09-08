using System.Net;
using System.Net.Sockets;
using LabControl.Shared.Identity;

namespace LabControl.Shared.Discovery;

/// <summary>
/// The console's side of discovery: every <see cref="Defaults.BeaconInterval"/> a signed
/// beacon goes out on every network this machine is on, naming the address agents on that
/// network should dial. <i>Take over the lab</i> is a timestamp added to the beacon for
/// <see cref="Defaults.TakeOverWindow"/> (ARCHITECTURE §3.7.2).
/// </summary>
public sealed class BeaconBroadcaster : IDisposable
{
    private readonly ConsoleInstance _instance;
    private readonly int _port;
    private readonly int _beaconPort;
    private readonly IPAddress? _bindTo;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();

    private Socket? _socket;
    private Task? _loop;
    private DateTimeOffset? _takeOverAt;

    public BeaconBroadcaster(ConsoleInstance instance, int port, IPAddress? bindTo = null, Func<DateTimeOffset>? clock = null, int beaconPort = Defaults.BeaconPort)
    {
        if (beaconPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(beaconPort));
        _beaconPort = beaconPort;
        _instance = instance;
        _port = port;
        _bindTo = bindTo;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised when a send fails; the console shows it as an event rather than dying.</summary>
    public event Action<string>? Failed;

    /// <summary>The routes the last round went out on, for the status bar.</summary>
    public IReadOnlyList<BeaconRoute> LastRoutes { get; private set; } = [];

    /// <summary>The moment the teacher pressed <i>Take over</i>, while the window is open.</summary>
    public DateTimeOffset? TakeOverAt
    {
        get
        {
            lock (_gate)
            {
                return _takeOverAt;
            }
        }
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _socket = BeaconSocket.OpenSender();
        _loop = Task.Run(() => RunAsync(_stopping.Token));
    }

    /// <summary>Adds <c>take</c> to the beacon for the next <see cref="Defaults.TakeOverWindow"/>.</summary>
    public void TakeOver()
    {
        lock (_gate)
        {
            _takeOverAt = _clock();
        }

        // Do not wait for the next tick: a teacher who pressed the button expects the room now.
        _ = SendOnceAsync();
    }

    /// <summary>Sends one round now, on top of the periodic ones.</summary>
    public Task SendOnceAsync() => SendRoundAsync();

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Defaults.BeaconInterval);
        try
        {
            await SendRoundAsync();
            while (await timer.WaitForNextTickAsync(token))
            {
                await SendRoundAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendRoundAsync()
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        var now = _clock();
        DateTimeOffset? take;
        lock (_gate)
        {
            if (_takeOverAt is { } at && now - at > Defaults.TakeOverWindow)
            {
                _takeOverAt = null;
            }

            take = _takeOverAt;
        }

        IReadOnlyList<BeaconRoute> routes;
        try
        {
            routes = BeaconSocket.Routes(_bindTo);
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or SocketException)
        {
            Failed?.Invoke($"Could not list network interfaces: {ex.Message}");
            return;
        }

        LastRoutes = routes;

        foreach (var route in routes)
        {
            try
            {
                var datagram = _instance.CreateBeacon(route.Host.ToString(), _port, now, take).ToDatagram();
                await socket.SendToAsync(datagram, SocketFlags.None, new IPEndPoint(route.Broadcast, _beaconPort));

                if (!IPAddress.Loopback.Equals(route.Broadcast))
                {
                    // A broadcast is not always delivered back to sockets on the sending
                    // machine; a copy on loopback is what lets a FakeAgent on the console's
                    // own computer hear it. The beacon still names the network address.
                    await socket.SendToAsync(datagram, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, _beaconPort));
                }
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException or ObjectDisposedException)
            {
                Failed?.Invoke($"Beacon on {route.Host} failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _socket?.Dispose();
        _socket = null;
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stopping.Dispose();
    }
}
