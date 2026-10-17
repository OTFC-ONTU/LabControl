using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LabControl.Shared;
using LabControl.Shared.Policy;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;

namespace LabControl.Agent;

/// <summary>One interface's static DNS list for one family; empty = the servers come from DHCP (or router advertisement).</summary>
internal sealed record InterfaceDns(Guid Interface, bool Ipv6, IReadOnlyList<string> Static);

/// <summary>
/// Points interfaces' DNS at the whitelist resolver and back (M6 portion 3, D-74), through
/// <c>GetInterfaceDnsSettings</c> / <c>SetInterfaceDnsSettings</c> (Windows 10 2004 and later),
/// which notify the DNS client themselves. Both address families are changed: an interface whose
/// IPv6 DNS kept pointing at the router would resolve allowed names past the resolver, and the
/// firewall would then refuse the sites the teacher allowed. Calls may throw; the caller wraps.
/// </summary>
internal static class WindowsDns
{
    /// <summary>The interfaces a whitelist points at the resolver: up, not loopback, not a tunnel.</summary>
    public static IReadOnlyList<NetworkInterface> Candidates() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .ToArray();

    /// <summary>The interface's GUID, which is what <see cref="NetworkInterface.Id"/> holds on Windows.</summary>
    public static Guid IdOf(NetworkInterface adapter) => Guid.Parse(adapter.Id);

    /// <summary>
    /// The DNS servers the machine really uses right now on these interfaces — DHCP's included —
    /// minus the loopback and the IPv6 site-local defaults Windows lists when nothing is set:
    /// where the resolver forwards allowed names. IPv4 first.
    /// </summary>
    public static List<IPEndPoint> EffectiveServers(IEnumerable<NetworkInterface> adapters)
    {
        var servers = new List<IPAddress>();
        foreach (var adapter in adapters)
        {
            try
            {
                foreach (var address in adapter.GetIPProperties().DnsAddresses)
                {
                    if (!IPAddress.IsLoopback(address) && !address.IsIPv6SiteLocal && !servers.Contains(address))
                    {
                        servers.Add(address);
                    }
                }
            }
            catch (NetworkInformationException)
            {
            }
        }

        return servers
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Select(a => new IPEndPoint(a, 53))
            .ToList();
    }

    /// <summary>One interface's static DNS list for one family; empty when it comes from DHCP.</summary>
    public static unsafe InterfaceDns Read(Guid adapter, bool ipv6)
    {
        var settings = new DNS_INTERFACE_SETTINGS
        {
            Version = PInvoke.DNS_INTERFACE_SETTINGS_VERSION1,
            Flags = PInvoke.DNS_SETTING_NAMESERVER | (ipv6 ? PInvoke.DNS_SETTING_IPV6 : 0),
        };

        var error = PInvoke.GetInterfaceDnsSettings(adapter, &settings);
        if (error != WIN32_ERROR.NO_ERROR)
        {
            throw new IOException($"GetInterfaceDnsSettings failed: {error}");
        }

        try
        {
            var text = settings.NameServer.Value is null ? string.Empty : settings.NameServer.ToString();
            return new InterfaceDns(adapter, ipv6, Split(text));
        }
        finally
        {
            PInvoke.FreeInterfaceDnsSettings(&settings);
        }
    }

    /// <summary>Sets one interface's static DNS list for one family — empty means "from DHCP" — and reads it back.</summary>
    public static unsafe void Write(Guid adapter, bool ipv6, IReadOnlyList<string> servers)
    {
        var text = string.Join(',', servers);
        fixed (char* nameServer = text)
        {
            var settings = new DNS_INTERFACE_SETTINGS
            {
                Version = PInvoke.DNS_INTERFACE_SETTINGS_VERSION1,
                Flags = PInvoke.DNS_SETTING_NAMESERVER | (ipv6 ? PInvoke.DNS_SETTING_IPV6 : 0),
                NameServer = new PWSTR(nameServer),
            };

            var error = PInvoke.SetInterfaceDnsSettings(adapter, &settings);
            if (error != WIN32_ERROR.NO_ERROR)
            {
                throw new IOException($"SetInterfaceDnsSettings failed: {error}");
            }
        }

        var back = Read(adapter, ipv6);
        if (!back.Static.SequenceEqual(servers, StringComparer.OrdinalIgnoreCase))
        {
            throw new IOException($"the DNS servers of interface {adapter:B} did not read back as set");
        }
    }

    /// <summary>The loopback address the resolver listens on for a family.</summary>
    public static string Loopback(bool ipv6) => ipv6 ? Defaults.WhitelistResolverAddressV6 : Defaults.WhitelistResolverAddress;

    /// <summary>True when a static list points only at the resolver — what the unconditional restore puts back to DHCP.</summary>
    public static bool PointsAtResolver(InterfaceDns dns) =>
        dns.Static.Count > 0 && dns.Static.All(s => s is Defaults.WhitelistResolverAddress or Defaults.WhitelistResolverAddressV6 or "::1" or "127.0.0.1");

    /// <summary>A displaced entry for the journal: DHCP or the exact static list.</summary>
    public static DisplacedDns ToDisplaced(InterfaceDns dns) => new()
    {
        Interface = dns.Interface.ToString("B"),
        Ipv6 = dns.Ipv6,
        Dhcp = dns.Static.Count == 0,
        Servers = [.. dns.Static],
    };

    /// <summary>
    /// Forgets what the DNS client cached: names resolved before a whitelist would otherwise keep
    /// reaching addresses the firewall now refuses, and after a lift sites would stay unreachable
    /// until their records expired. <c>DnsFlushResolverCache</c> has no metadata entry, so this is
    /// <c>ipconfig /flushdns</c>; a failure only delays the change by the records' TTL.
    /// </summary>
    public static void FlushCache()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ipconfig.exe"), "/flushdns")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static List<string> Split(string text) =>
        text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
