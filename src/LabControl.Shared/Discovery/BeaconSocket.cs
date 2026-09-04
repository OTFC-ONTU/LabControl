using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LabControl.Shared.Discovery;

/// <summary>
/// The UDP plumbing both sides of discovery share (PROTOCOL, "Discovery beacon"). Nothing
/// here knows what a beacon means; it only moves datagrams. Several LabControl processes
/// may share one machine during development — a console, a second console profile, a
/// <c>FakeAgent</c> — so every socket is opened with address and port reuse.
/// </summary>
public static class BeaconSocket
{
    /// <summary>
    /// <c>SO_REUSEPORT</c> as BSD and macOS number it. .NET's <see cref="SocketOptionName.ReuseAddress"/>
    /// alone is not enough there for two processes to <i>both</i> receive one broadcast.
    /// </summary>
    private const int BsdReusePort = 0x0200;

    /// <summary>A socket bound to the beacon port on every interface, shareable with other local processes.</summary>
    public static Socket OpenListener(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        AllowSharing(socket);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        return socket;
    }

    /// <summary>A socket for sending broadcasts; unbound, so the OS picks the source per interface.</summary>
    public static Socket OpenSender()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        AllowSharing(socket);
        socket.EnableBroadcast = true;
        return socket;
    }

    private static void AllowSharing(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)BsdReusePort, true);
            }
            catch (SocketException)
            {
                // Sharing is a development convenience; a lab machine runs one process.
            }
        }
    }

    /// <summary>
    /// One entry per interface a beacon should go out on: the address agents will dial and
    /// the broadcast address that reaches them. Empty when the machine has no network at all.
    /// </summary>
    public static IReadOnlyList<BeaconRoute> Routes(IPAddress? bindTo = null)
    {
        var routes = new List<BeaconRoute>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (bindTo is not null && !IPAddress.Any.Equals(bindTo) && !bindTo.Equals(unicast.Address))
                {
                    continue;
                }

                routes.Add(new BeaconRoute(unicast.Address, BroadcastFor(unicast)));
            }
        }

        if (routes.Count == 0)
        {
            // No network: the only agents that can hear us live on this machine (FakeAgent).
            var loopback = bindTo is not null && !IPAddress.Any.Equals(bindTo) ? bindTo : IPAddress.Loopback;
            routes.Add(new BeaconRoute(loopback, IPAddress.Loopback));
        }

        return routes;
    }

    private static IPAddress BroadcastFor(UnicastIPAddressInformation unicast)
    {
        var address = unicast.Address.GetAddressBytes();
        var mask = unicast.IPv4Mask?.GetAddressBytes() ?? [255, 255, 255, 0];

        var broadcast = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            broadcast[i] = (byte)(address[i] | ~mask[i]);
        }

        return new IPAddress(broadcast);
    }
}

/// <param name="Host">The address written into the beacon: what an agent on that network dials.</param>
/// <param name="Broadcast">Where the datagram is sent so every PC on that network hears it.</param>
public sealed record BeaconRoute(IPAddress Host, IPAddress Broadcast);
