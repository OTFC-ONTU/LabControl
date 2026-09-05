using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LabControl.Shared.Power;

/// <summary>
/// Wake-on-LAN from the console (ARCHITECTURE §6): a magic packet — six <c>0xFF</c> bytes
/// then the MAC sixteen times — on UDP port <see cref="Defaults.WolPort"/>. It goes out as
/// a limited broadcast, as a directed broadcast on every subnet this machine is on (the
/// MacBook is on Wi-Fi, the PCs on the wired hub of the same router) and unicast to the
/// address the PC last connected from. Sending is best effort: the result of a wake is the
/// PC showing up on <c>Link</c>, not the send succeeding (PROTOCOL, <c>Job</c>).
/// </summary>
public static class WakeOnLan
{
    public const int MagicPacketBytes = 6 + 16 * 6;

    /// <summary>The 102-byte magic packet for <paramref name="mac"/> (<c>AA:BB:CC:DD:EE:FF</c>, separators optional).</summary>
    public static byte[] MagicPacket(string mac)
    {
        if (!TryParseMac(mac, out var address))
        {
            throw new FormatException($"'{mac}' is not a MAC address");
        }

        var packet = new byte[MagicPacketBytes];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var i = 0; i < 16; i++)
        {
            address.CopyTo(packet, 6 + i * 6);
        }

        return packet;
    }

    public static bool TryParseMac(string? text, out byte[] address)
    {
        address = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12)
        {
            return false;
        }

        address = Convert.FromHexString(hex);
        return true;
    }

    /// <summary>
    /// Every destination one send covers: limited broadcast, the directed broadcast of each
    /// IPv4 interface that is up, and the PC's last address if known. Duplicates are fine.
    /// </summary>
    public static IReadOnlyList<IPEndPoint> Destinations(string? lastIp)
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, Defaults.WolPort) };

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    {
                        continue;
                    }

                    var ip = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++)
                    {
                        broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    }

                    targets.Add(new IPEndPoint(new IPAddress(broadcast), Defaults.WolPort));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        if (IPAddress.TryParse(lastIp, out var last) && last.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(last))
        {
            targets.Add(new IPEndPoint(last, Defaults.WolPort));
        }

        return targets.Distinct().ToArray();
    }

    /// <summary>
    /// Sends the magic packet to every destination, <see cref="Defaults.WakePacketRepeats"/>
    /// times. Returns the destinations that were written to; a socket that refuses (no
    /// network at all) is reported through <paramref name="onFailure"/> and the rest go on.
    /// </summary>
    public static async Task<IReadOnlyList<IPEndPoint>> SendAsync(string mac, string? lastIp, Action<string>? onFailure, CancellationToken token)
    {
        var packet = MagicPacket(mac);
        var destinations = Destinations(lastIp);
        var reached = new List<IPEndPoint>();

        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.EnableBroadcast = true;

        for (var repeat = 0; repeat < Defaults.WakePacketRepeats; repeat++)
        {
            if (repeat > 0)
            {
                await Task.Delay(Defaults.WakePacketSpacing, token);
            }

            foreach (var destination in destinations)
            {
                try
                {
                    await socket.SendAsync(packet, destination, token);
                    if (repeat == 0)
                    {
                        reached.Add(destination);
                    }
                }
                catch (SocketException ex)
                {
                    if (repeat == 0)
                    {
                        onFailure?.Invoke(string.Format(CultureInfo.InvariantCulture, "could not send to {0}: {1}", destination, ex.Message));
                    }
                }
            }
        }

        return reached;
    }
}
