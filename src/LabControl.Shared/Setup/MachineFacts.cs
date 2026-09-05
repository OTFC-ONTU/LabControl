using System.Net.NetworkInformation;

namespace LabControl.Shared.Setup;

/// <summary>
/// Facts about the machine an install or an inventory needs, gathered the portable way
/// so the simulator and the tests can use the same code as a real PC.
/// </summary>
public static class MachineFacts
{
    /// <summary>
    /// The MAC the console will send Wake-on-LAN to: the first wired adapter that is up,
    /// then a wireless one, then anything with a hardware address. <c>null</c> on a machine
    /// with no usable adapter, which an installer must refuse rather than invent.
    /// </summary>
    public static string? PrimaryMac()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return null;
        }

        var candidates = interfaces
            .Where(i => i.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(i => i.GetPhysicalAddress().GetAddressBytes().Length == 6)
            .Where(i => !i.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase) || i.OperationalStatus == OperationalStatus.Up)
            .OrderBy(i => i.OperationalStatus == OperationalStatus.Up ? 0 : 1)
            .ThenBy(i => i.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => 0,
                NetworkInterfaceType.Wireless80211 => 1,
                _ => 2,
            });

        var chosen = candidates.FirstOrDefault();
        return chosen is null ? null : FormatMac(chosen.GetPhysicalAddress().GetAddressBytes());
    }

    /// <summary>The one spelling of a MAC used everywhere: <c>AA:BB:CC:DD:EE:FF</c>.</summary>
    public static string FormatMac(ReadOnlySpan<byte> address) =>
        string.Join(':', address.ToArray().Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    public static string Hostname() => Environment.MachineName;
}
