using System.Net;
using System.Net.Sockets;
using LabControl.Shared;
using LabControl.Shared.Internet;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent;

/// <summary>
/// The internet half of <see cref="WindowsMachineState"/> (M6 portion 3, D-73, D-74): Windows
/// Firewall's default outbound action and the agent's own rule group, other programs' outbound
/// allow rules switched off for the policy's lifetime, interfaces' DNS pointed at the loopback
/// resolver for a whitelist, and the watchdog task. Every step is wrapped: a failure is a
/// <see cref="MachineResult"/> in the teacher's words, never an exception, and a restore runs
/// every step it can even after one failed — the machine is left as open as possible.
/// </summary>
internal sealed class WindowsInternetControl
{
    private readonly WindowsFirewall _firewall = new();
    private readonly ILogger _log;
    private readonly string _program;
    private readonly AllowedAddressSet _addresses = new();
    private readonly Lock _lock = new();
    private LoopbackResolver? _resolver;
    private InternetSession? _applied;
    private Dictionary<string, string> _expectedOutbound = AllAllow();
    private bool _firewallOff;
    private bool _noUpstream;

    /// <param name="program">The running <c>agent.exe</c>, pinned in the exemption rule (its <c>app\&lt;version&gt;\</c> path, D-69 item 6).</param>
    public WindowsInternetControl(ILogger log, string program)
    {
        _log = log;
        _program = program;
    }

    // ------------------------------------------------------------------ restore (D-69 steps 0, 1, 3, 4)

    /// <summary>
    /// The unconditional restore: every profile's default outbound action to Allow first (a
    /// stale allow is harmless, a stale block is a brick), the resolver stopped, the owned rule
    /// group with the <c>agent.exe</c> exemption last, DNS on any resolver-pointed interface back
    /// to DHCP, the watchdog task deleted. Nothing read from any document.
    /// </summary>
    public MachineResult RestoreDefaults()
    {
        lock (_lock)
        {
            var problems = new List<string>();
            Step(problems, "firewall profiles", () =>
            {
                var (enabled, current) = _firewall.ReadEnabled();
                _firewallOff = WindowsFirewall.Profiles.Any(p => (current & p.Value) != 0 && !enabled[p.Key]);
            });

            foreach (var profile in WindowsFirewall.Profiles.Keys)
            {
                Step(problems, $"{profile} default outbound", () => _firewall.SetDefaultOutbound(profile, "allow"));
            }

            // The resolver before the rules: a late answer would otherwise put the sites rule back
            // after the group was removed. Names fail for the moment until DNS is back.
            Step(problems, "resolver", StopResolver);
            Step(problems, "owned firewall rules", _firewall.RemoveOwnedRules);
            Step(problems, "DNS", () =>
            {
                foreach (var adapter in AllAdapters())
                {
                    foreach (var ipv6 in new[] { false, true })
                    {
                        var dns = TryRead(adapter, ipv6);
                        if (dns is not null && WindowsDns.PointsAtResolver(dns))
                        {
                            WindowsDns.Write(adapter, ipv6, []);
                        }
                    }
                }
            });
            Step(problems, "watchdog task", PolicyRestoreTask.Delete);
            WindowsDns.FlushCache();

            _applied = null;
            _expectedOutbound = AllAllow();
            return Outcome(problems);
        }
    }

    /// <summary>Puts back what the unconditional restore could not know: block-all profiles, static DNS lists, other programs' rules.</summary>
    public MachineResult RefineRestore(DisplacedSettings displaced)
    {
        lock (_lock)
        {
            var problems = new List<string>();
            foreach (var (profile, action) in displaced.ProfileDefaultOutbound)
            {
                if (action == "block" && WindowsFirewall.Profiles.ContainsKey(profile))
                {
                    Step(problems, $"{profile} default outbound", () => _firewall.SetDefaultOutbound(profile, "block"));
                    _expectedOutbound[profile] = "block";
                }
            }

            foreach (var dns in displaced.Dns.Where(d => !d.Dhcp))
            {
                Step(problems, "DNS", () => WindowsDns.Write(Guid.Parse(dns.Interface), dns.Ipv6, dns.Servers));
            }

            if (displaced.DisabledRules.Count > 0)
            {
                Step(problems, "other programs' firewall rules", () => _firewall.SetForeignRules(displaced.DisabledRules, enabled: true));
            }

            WindowsDns.FlushCache();
            return Outcome(problems);
        }
    }

    /// <summary>Reads the machine back: the expected outbound actions, no owned rule, no resolver-pointed interface, no resolver.</summary>
    public MachineResult Confirm()
    {
        lock (_lock)
        {
            var problems = new List<string>();
            Step(problems, "firewall profiles", () =>
            {
                var actual = _firewall.ReadDefaultOutbound();
                foreach (var (profile, expected) in _expectedOutbound)
                {
                    if (!string.Equals(actual.GetValueOrDefault(profile), expected, StringComparison.Ordinal))
                    {
                        throw new IOException($"the {profile} profile's default outbound action is {actual.GetValueOrDefault(profile)}, not {expected}");
                    }
                }
            });
            Step(problems, "owned firewall rules", () =>
            {
                if (_firewall.OwnedRules() is { Count: > 0 } left)
                {
                    throw new IOException($"{left.Count} rule(s) of {Defaults.InternetFirewallRuleGroup} are still present");
                }
            });
            Step(problems, "DNS", () =>
            {
                foreach (var adapter in AllAdapters())
                {
                    foreach (var ipv6 in new[] { false, true })
                    {
                        if (TryRead(adapter, ipv6) is { } dns && WindowsDns.PointsAtResolver(dns))
                        {
                            throw new IOException($"interface {adapter:B} still points at the whitelist resolver");
                        }
                    }
                }
            });

            if (_resolver is { Running: true })
            {
                problems.Add("resolver: still listening");
            }

            return Outcome(problems);
        }
    }

    // ------------------------------------------------------------------ apply and lift

    /// <summary>
    /// Enforces a whitelist or a block (D-73 item 3): records the originals the block does not
    /// hold yet and has the journal save them, arms the watchdog, then creates the exemption,
    /// the local-subnet and the loopback rules, switches other programs' outbound allow rules off
    /// and only then sets every profile's default outbound action to Block. A whitelist starts
    /// the resolver and points every interface's DNS at it; a block stops it and gives DNS back.
    /// Read back before it says done.
    /// </summary>
    public MachineResult Apply(InternetSession session, DisplacedSettings displaced, Action recordDisplaced)
    {
        lock (_lock)
        {
            try
            {
                var (enabled, current) = _firewall.ReadEnabled();
                _firewallOff = WindowsFirewall.Profiles.Any(p => (current & p.Value) != 0 && !enabled[p.Key]);
                if (_firewallOff)
                {
                    return MachineResult.Failed(PolicyEvents.Text("InternetFirewallOff"));
                }

                var whitelist = session.Mode == InternetPolicy.Types.Mode.Whitelist;
                var adapters = WindowsDns.Candidates();

                // 1. Originals, only those the block does not hold yet (IMachineState contract).
                if (displaced.ProfileDefaultOutbound.Count == 0)
                {
                    foreach (var (profile, action) in _firewall.ReadDefaultOutbound())
                    {
                        displaced.ProfileDefaultOutbound[profile] = action;
                    }
                }

                if (displaced.DisabledRules.Count == 0)
                {
                    displaced.DisabledRules.AddRange(_firewall.ForeignAllowRules().Select(r => r.Fingerprint).Distinct(StringComparer.Ordinal));
                }

                if (whitelist)
                {
                    foreach (var adapter in adapters)
                    {
                        foreach (var ipv6 in new[] { false, true })
                        {
                            var key = WindowsDns.IdOf(adapter).ToString("B");
                            if (displaced.Dns.Any(d => string.Equals(d.Interface, key, StringComparison.OrdinalIgnoreCase) && d.Ipv6 == ipv6))
                            {
                                continue;
                            }

                            // A list that already points at the resolver is a restore that did not
                            // finish, never an original; leaving it out restores that family to DHCP.
                            if (TryRead(WindowsDns.IdOf(adapter), ipv6) is { } dns && !WindowsDns.PointsAtResolver(dns))
                            {
                                displaced.Dns.Add(WindowsDns.ToDisplaced(dns));
                            }
                        }
                    }
                }

                displaced.OwnedRules = [WindowsFirewall.LocalSubnetRuleName, WindowsFirewall.LoopbackRuleName, WindowsFirewall.SitesRuleName, WindowsFirewall.AgentRuleName];
                recordDisplaced();

                // 2. The watchdog first: a crash from here on is still undone (D-69 item 6).
                PolicyRestoreTask.Arm(_program, session.HardLimitUnix);

                // 3. The ways that stay open, then the block.
                _firewall.EnsureAgentRule(_program);
                _firewall.EnsureLocalSubnetRule();
                _firewall.EnsureLoopbackRule();
                _firewall.SetForeignRules(displaced.DisabledRules, enabled: false);
                foreach (var profile in WindowsFirewall.Profiles.Keys)
                {
                    _firewall.SetDefaultOutbound(profile, "block");
                    _expectedOutbound[profile] = "block";
                }

                // 4. Names.
                if (whitelist)
                {
                    var problem = StartWhitelist(session, displaced, adapters);
                    if (problem is not null)
                    {
                        return MachineResult.Failed(problem);
                    }
                }
                else
                {
                    StopWhitelist(displaced);
                }

                WindowsDns.FlushCache();

                // 5. Read back.
                if (_firewall.ReadDefaultOutbound().Any(p => p.Value != "block"))
                {
                    return MachineResult.Failed(PolicyEvents.Text("InternetReadBack", "default outbound"));
                }

                if (!_firewall.OwnedRules().Contains(WindowsFirewall.AgentRuleName, StringComparer.OrdinalIgnoreCase))
                {
                    return MachineResult.Failed(PolicyEvents.Text("InternetReadBack", WindowsFirewall.AgentRuleName));
                }

                if (_firewall.CountEnabled(displaced.DisabledRules) > 0)
                {
                    return MachineResult.Failed(PolicyEvents.Text("InternetReadBack", "other programs' firewall rules"));
                }

                displaced.Applied = true;
                _applied = session;
                _log.LogInformation("internet policy {Session} enforced: {Mode}, {Hosts} host pattern(s), {Rules} other rule(s) switched off",
                    session.SessionId, session.Mode, session.AllowedHosts.Count, displaced.DisabledRules.Count);
                return MachineResult.Done;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "applying internet policy {Session} failed: {Message}", session.SessionId, ex.Message);
                return MachineResult.Failed(PolicyEvents.Text("InternetStepFailed", ex.Message));
            }
        }
    }

    /// <summary>A lift is a full restore with the displaced originals: unconditional, refined, confirmed.</summary>
    public MachineResult Lift(DisplacedSettings displaced)
    {
        var restored = RestoreDefaults();
        var refined = displaced.IsEmpty ? MachineResult.Done : RefineRestore(displaced);
        var confirmed = Confirm();
        foreach (var result in new[] { restored, refined, confirmed })
        {
            if (!result.Succeeded)
            {
                return result;
            }
        }

        return MachineResult.Done;
    }

    /// <summary>The live side for <c>InternetState</c>; cheap, never throws.</summary>
    public InternetRuntime Runtime()
    {
        lock (_lock)
        {
            var reasons = new List<string>();
            if (_firewallOff)
            {
                reasons.Add(InternetReasons.FirewallOff);
            }

            var whitelist = _applied is { Mode: InternetPolicy.Types.Mode.Whitelist };
            if (whitelist && _resolver is not { Running: true })
            {
                reasons.Add(InternetReasons.ResolverDown);
            }

            if (whitelist && _noUpstream)
            {
                reasons.Add(InternetReasons.NoUpstream);
            }

            var resolver = _resolver;
            return resolver is null
                ? new InternetRuntime(false, [], 0, string.Join(' ', reasons))
                : new InternetRuntime(resolver.Running, resolver.Refused.Recent(DateTimeOffset.UtcNow), resolver.Refused.Version, string.Join(' ', reasons));
        }
    }

    // ------------------------------------------------------------------ the pieces

    /// <summary>Starts (or retunes) the resolver and points every interface at it; the problem in the teacher's words, or <c>null</c>.</summary>
    private string? StartWhitelist(InternetSession session, DisplacedSettings displaced, IReadOnlyList<System.Net.NetworkInformation.NetworkInterface> adapters)
    {
        // Upstream before the switch: afterwards the interfaces report the loopback. A static
        // original kept from an unfinished restore counts too.
        var upstream = WindowsDns.EffectiveServers(adapters);
        foreach (var dns in displaced.Dns.Where(d => !d.Dhcp))
        {
            foreach (var server in dns.Servers)
            {
                if (IPAddress.TryParse(server, out var address) && !IPAddress.IsLoopback(address) && upstream.All(u => !u.Address.Equals(address)))
                {
                    upstream.Add(new IPEndPoint(address, 53));
                }
            }
        }

        _noUpstream = upstream.Count == 0;
        if (_resolver is { Running: true } running)
        {
            running.SetAllowed(session.AllowedHosts);
            running.SetUpstream(upstream);
        }
        else
        {
            StopResolver();
            _addresses.Clear();
            var resolver = new LoopbackResolver(new LoopbackResolverOptions(LoopbackResolverOptions.ProductionListen, upstream, session.AllowedHosts, Admit, _log));
            if (!resolver.TryStart(out var code))
            {
                resolver.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                return PolicyEvents.Text("InternetResolverFailed", code);
            }

            _resolver = resolver;
        }

        foreach (var adapter in adapters)
        {
            var id = WindowsDns.IdOf(adapter);
            WindowsDns.Write(id, ipv6: false, [WindowsDns.Loopback(false)]);
            try
            {
                WindowsDns.Write(id, ipv6: true, [WindowsDns.Loopback(true)]);
            }
            catch (IOException) when (!HasIpv6Dns(adapter))
            {
                // An interface without IPv6 DNS has nothing to point; one with it must be pointed.
            }
        }

        return null;
    }

    /// <summary>A block after a whitelist: DNS back to its originals, the resolver stopped, the learned addresses forgotten.</summary>
    private void StopWhitelist(DisplacedSettings displaced)
    {
        if (_resolver is null && displaced.Dns.Count == 0)
        {
            return;
        }

        foreach (var dns in displaced.Dns.ToArray())
        {
            WindowsDns.Write(Guid.Parse(dns.Interface), dns.Ipv6, dns.Dhcp ? [] : dns.Servers);
            displaced.Dns.Remove(dns);
        }

        StopResolver();
        _addresses.Clear();
        _firewall.SetSiteAddresses([]);
    }

    /// <summary>
    /// The resolver's call before an allowed answer reaches the client: the addresses join the
    /// sites rule first, so the browser's connection that follows is already allowed (D-74).
    /// False — and SERVFAIL for the client — when the rule could not be updated.
    /// </summary>
    private bool Admit(IReadOnlyList<IPAddress> addresses)
    {
        lock (_addresses)
        {
            try
            {
                if (_addresses.Admit(addresses))
                {
                    _firewall.SetSiteAddresses(_addresses.Snapshot());
                }

                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "the whitelist could not allow {Count} address(es): {Message}", addresses.Count, ex.Message);
                return false;
            }
        }
    }

    private void StopResolver()
    {
        var resolver = _resolver;
        _resolver = null;
        if (resolver is not null && !resolver.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)))
        {
            throw new IOException("the whitelist resolver did not stop within 10 s");
        }
    }

    private static bool HasIpv6Dns(System.Net.NetworkInformation.NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().DnsAddresses.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal && !IPAddress.IsLoopback(a));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>Every interface the restore checks, up or not: one that went down under a whitelist still points at the loopback.</summary>
    private static IEnumerable<Guid> AllAdapters() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .Select(n => Guid.TryParse(n.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct();

    private static InterfaceDns? TryRead(Guid adapter, bool ipv6)
    {
        try
        {
            return WindowsDns.Read(adapter, ipv6);
        }
        catch (Exception ex) when (ex is IOException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static InterfaceDns? TryRead(System.Net.NetworkInformation.NetworkInterface adapter, bool ipv6) => TryRead(WindowsDns.IdOf(adapter), ipv6);

    private static Dictionary<string, string> AllAllow() => WindowsFirewall.Profiles.Keys.ToDictionary(p => p, _ => "allow");

    private void Step(List<string> problems, string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "internet restore step {What} failed: {Message}", what, ex.Message);
            problems.Add($"{what}: {ex.Message}");
        }
    }

    private static MachineResult Outcome(List<string> problems) =>
        problems.Count == 0 ? MachineResult.Done : MachineResult.Failed(string.Join("; ", problems));
}
