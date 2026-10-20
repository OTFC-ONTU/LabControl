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

    // The rules this service has switched off for the policy in force: a re-apply (Allow, a
    // reassert) does not walk them again. Cleared by every restore.
    private HashSet<string> _disabledHere = new(StringComparer.Ordinal);

    // The reassert's memory (D-74 item 9): when each interface was last re-pointed, and the ones it
    // gave up on because they do not keep their DNS on the resolver.
    private readonly Dictionary<string, DateTimeOffset> _repointedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _dnsNotHeld = new(StringComparer.OrdinalIgnoreCase);

    // The upstream servers, gathered the first time a whitelist starts and only ever added to:
    // on a re-apply the interfaces already report the loopback, and an empty list would make
    // every allowed name fail (review finding 2).
    private readonly List<IPEndPoint> _upstream = [];

    // Under _addresses: false from the moment the resolver is being stopped, so an answer that
    // arrives late never puts the sites rule back (review finding 4).
    private bool _admitting;

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

            // At boot the agent may start before Windows Firewall, whose COM object then refuses
            // every change as write-protected (PC-01, 2026-10-20). A firewall that never comes up
            // is reported by the steps below, as before.
            if (!ServiceControl.WaitRunning(Defaults.FirewallServiceName, Defaults.FirewallServiceWait))
            {
                _log.LogWarning("Windows Firewall ({Service}) is not running after {Seconds} s; restoring anyway", Defaults.FirewallServiceName, Defaults.FirewallServiceWait.TotalSeconds);
            }

            // The background pass must not switch rules off behind the refine that switches them back on.
            Step(problems, "other rules pass", StopDisabler);
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
            _upstream.Clear();
            Step(problems, "owned firewall rules", _firewall.RemoveOwnedRules);
            foreach (var adapter in AllAdapters())
            {
                // One step per interface: one that refuses must not keep the others on the loopback.
                Step(problems, $"DNS of {adapter:B}", () => ResetIfPointed(adapter));
            }
            Step(problems, "watchdog task", PolicyRestoreTask.Delete);
            WindowsDns.FlushCache();

            _applied = null;
            _repointedAt.Clear();
            _dnsNotHeld.Clear();
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

            var present = AllAdapters().ToHashSet();
            foreach (var dns in displaced.Dns.Where(d => !d.Dhcp))
            {
                // An interface that is gone (a USB NIC unplugged, a driver reinstalled) has nothing
                // to put back; failing on it forever would keep the originals, and with them the
                // self_update refusal, for good (review finding 7).
                if (Guid.TryParse(dns.Interface, out var id) && present.Contains(id))
                {
                    Step(problems, $"DNS of {dns.Interface}", () => WindowsDns.Write(id, dns.Ipv6, dns.Servers));
                }
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
    /// hold yet and has the journal save them, arms the watchdog, then creates the exemption and
    /// the local-subnet rule, switches other programs' outbound allow rules off
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
                ServiceControl.WaitRunning(Defaults.FirewallServiceName, Defaults.FirewallServiceWait);
                var (enabled, current) = _firewall.ReadEnabled();
                _firewallOff = WindowsFirewall.Profiles.Any(p => (current & p.Value) != 0 && !enabled[p.Key]);
                if (_firewallOff)
                {
                    return MachineResult.Failed(PolicyEvents.Text("InternetFirewallOff"));
                }

                var whitelist = session.Mode == InternetPolicy.Types.Mode.Whitelist;
                var adapters = WindowsDns.Candidates();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var steps = new List<string>();
                void Mark(string step)
                {
                    steps.Add($"{step} {clock.ElapsedMilliseconds} ms");
                    clock.Restart();
                }

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
                    displaced.DisabledRules.AddRange(_firewall.Scan().ForeignEnabled);
                    Mark("scan");
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

                displaced.OwnedRules = [WindowsFirewall.LocalSubnetRuleName, WindowsFirewall.SitesRuleName, WindowsFirewall.AgentRuleName];
                recordDisplaced();
                Mark("record");

                // 2. The watchdog first: a crash from here on is still undone (D-69 item 6).
                PolicyRestoreTask.Arm(_program, session.HardLimitUnix);
                Mark("watchdog");

                // 3. The ways that stay open, then the block. No loopback rule: Windows Firewall
                // refuses loopback addresses in a rule (E_INVALIDARG, found on the VM) because it
                // never filters the loopback at all.
                _firewall.EnsureAgentRule(_program);
                _firewall.EnsureLocalSubnetRule();
                Mark("allow rules");
                foreach (var profile in WindowsFirewall.Profiles.Keys)
                {
                    _firewall.SetDefaultOutbound(profile, "block");
                    _expectedOutbound[profile] = "block";
                }

                Mark("block");

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
                Mark("names");

                // 5. Read back, in one walk.
                if (_firewall.ReadDefaultOutbound().Any(p => p.Value != "block"))
                {
                    return MachineResult.Failed(PolicyEvents.Text("InternetReadBack", "default outbound"));
                }

                var after = _firewall.Scan();
                if (!after.Owned.Contains(WindowsFirewall.AgentRuleName, StringComparer.OrdinalIgnoreCase))
                {
                    return MachineResult.Failed(PolicyEvents.Text("InternetReadBack", WindowsFirewall.AgentRuleName));
                }

                Mark("read back");

                // 6. Other programs' outbound allow rules off — after the block, in the background.
                // Windows rebuilds its filters after every rule change, about 300 ms each, and a
                // classroom PC has a hundred such rules: done in line it kept the internet open for
                // half a minute after Apply and held the policy lock all that time (PC-14,
                // 2026-10-20). The block is in force meanwhile; only those programs' own allow
                // rules stay open until the background pass reaches them.
                StartDisabler(session.SessionId, displaced.DisabledRules);
                _log.LogInformation("internet policy {Session} steps: {Steps}", session.SessionId, string.Join(", ", steps));

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

            if (whitelist && _dnsNotHeld.Values.Any(until => DateTimeOffset.UtcNow < until))
            {
                reasons.Add(InternetReasons.DnsNotHeld);
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
        // original kept from an unfinished restore counts too. Only ever added to.
        foreach (var server in WindowsDns.EffectiveServers(adapters))
        {
            AddUpstream(server.Address);
        }

        foreach (var dns in displaced.Dns.Where(d => !d.Dhcp))
        {
            foreach (var server in dns.Servers)
            {
                if (IPAddress.TryParse(server, out var address))
                {
                    AddUpstream(address);
                }
            }
        }

        _noUpstream = _upstream.Count == 0;
        if (_resolver is { Running: true } running)
        {
            running.SetAllowed(session.AllowedHosts);
            running.SetUpstream(_upstream.ToArray());
        }
        else
        {
            StopResolver();
            lock (_addresses)
            {
                _addresses.Clear();
                _admitting = true;
            }

            var resolver = new LoopbackResolver(new LoopbackResolverOptions(LoopbackResolverOptions.ProductionListen, _upstream.ToArray(), session.AllowedHosts, Admit, _log));
            if (!resolver.TryStart(out var code))
            {
                lock (_addresses)
                {
                    _admitting = false;
                }

                resolver.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                return PolicyEvents.Text("InternetResolverFailed", code);
            }

            _resolver = resolver;
        }

        foreach (var adapter in adapters)
        {
            PointAtResolver(adapter);
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

        StopResolver();
        var present = AllAdapters().ToHashSet();
        foreach (var dns in displaced.Dns.ToArray())
        {
            if (Guid.TryParse(dns.Interface, out var id) && present.Contains(id))
            {
                WindowsDns.Write(id, dns.Ipv6, dns.Dhcp ? [] : dns.Servers);
            }

            displaced.Dns.Remove(dns);
        }

        // An interface pointed without a recorded original goes back to DHCP, as a restore does (review finding 8).
        foreach (var adapter in present)
        {
            ResetIfPointed(adapter);
        }

        lock (_addresses)
        {
            _addresses.Clear();
        }

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
            if (!_admitting)
            {
                return false;
            }

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

    private readonly Lock _disablerGate = new();
    private Task? _disabler;
    private CancellationTokenSource? _disablerStop;

    /// <summary>
    /// Switches the recorded rules off on a worker, unless this service already did for the policy
    /// in force. Takes only its own gate, never <c>_lock</c>, so a restore can stop and wait for it.
    /// </summary>
    private void StartDisabler(string sessionId, IReadOnlyCollection<string> fingerprints)
    {
        var wanted = fingerprints.ToArray();
        lock (_disablerGate)
        {
            if (wanted.Length == 0 || wanted.All(_disabledHere.Contains) || _disabler is { IsCompleted: false })
            {
                return;
            }

            var stop = new CancellationTokenSource();
            _disablerStop = stop;
            _disabler = Task.Run(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var changed = _firewall.SetForeignRules(wanted, enabled: false, stop.Token);
                    if (stop.IsCancellationRequested)
                    {
                        return;
                    }

                    var left = _firewall.CountEnabled(wanted);
                    lock (_disablerGate)
                    {
                        if (left == 0)
                        {
                            _disabledHere = new HashSet<string>(wanted, StringComparer.Ordinal);
                        }
                    }

                    _log.LogInformation("internet policy {Session}: {Changed} other program rule(s) switched off in {Ms} ms; {Left} still on",
                        sessionId, changed, clock.ElapsedMilliseconds, left);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "switching other programs' rules off failed: {Message}", ex.Message);
                }
            });
        }
    }

    /// <summary>Stops the background pass and waits for it; a rule mid-change finishes first.</summary>
    private void StopDisabler()
    {
        Task? running;
        lock (_disablerGate)
        {
            _disablerStop?.Cancel();
            running = _disabler;
            _disabler = null;
            _disablerStop = null;
            _disabledHere = new HashSet<string>(StringComparer.Ordinal);
        }

        if (running is not null && !running.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new IOException("the background pass over other programs' rules did not stop within 30 s");
        }
    }

    private void StopResolver()
    {
        lock (_addresses)
        {
            _admitting = false;
        }

        var resolver = _resolver;
        _resolver = null;
        if (resolver is not null && !resolver.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)))
        {
            throw new IOException("the whitelist resolver did not stop within 10 s");
        }
    }

    private void AddUpstream(IPAddress address)
    {
        if (!IPAddress.IsLoopback(address) && !address.IsIPv6SiteLocal && _upstream.All(u => !u.Address.Equals(address)))
        {
            _upstream.Add(new IPEndPoint(address, 53));
        }
    }

    /// <summary>
    /// Points one interface at the resolver. An interface that refuses is tolerated only when it
    /// cannot reach anything past the lab anyway — no default gateway in that family (a virtual
    /// switch, a Bluetooth PAN); one that can must be pointed, or names would resolve past the
    /// resolver (review finding 10).
    /// </summary>
    private void PointAtResolver(System.Net.NetworkInformation.NetworkInterface adapter)
    {
        var id = WindowsDns.IdOf(adapter);
        foreach (var ipv6 in new[] { false, true })
        {
            try
            {
                WindowsDns.Write(id, ipv6, [WindowsDns.Loopback(ipv6)]);
            }
            catch (IOException) when (!HasGateway(adapter, ipv6) && !(ipv6 && HasIpv6Dns(adapter)))
            {
                _log.LogInformation("interface {Name} refused its {Family} DNS change and has no gateway there; left as it is", adapter.Name, ipv6 ? "IPv6" : "IPv4");
            }
        }
    }

    private static void ResetIfPointed(Guid adapter)
    {
        foreach (var ipv6 in new[] { false, true })
        {
            if (TryRead(adapter, ipv6) is { } dns && WindowsDns.PointsAtResolver(dns))
            {
                WindowsDns.Write(adapter, ipv6, []);
            }
        }
    }

    /// <summary>
    /// True while a whitelist is in force and an interface that is up does not point at the
    /// resolver — the wired NIC that came up after a reboot's re-apply (review finding 3) — so
    /// the enforcer re-applies through the journal, which records its originals first.
    /// </summary>
    public bool NeedsReassert()
    {
        lock (_lock)
        {
            if (_applied is not { Mode: InternetPolicy.Types.Mode.Whitelist })
            {
                return false;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var adapter in WindowsDns.Candidates())
                {
                    if (!HasGateway(adapter, false) || TryRead(adapter, false) is not { } dns || WindowsDns.PointsAtResolver(dns))
                    {
                        continue;
                    }

                    var id = adapter.Id;
                    if (_dnsNotHeld.TryGetValue(id, out var givenUpUntil) && now < givenUpUntil)
                    {
                        continue;
                    }

                    // An interface pointed again within a minute did not hold its DNS: something on
                    // the PC sets it back. Re-applying every two seconds held the policy lock for half
                    // a minute each time and starved the helper (PC-14, 2026-10-20); give up on it for
                    // ten minutes and say so instead.
                    if (_repointedAt.TryGetValue(id, out var last) && now - last < TimeSpan.FromMinutes(1))
                    {
                        _dnsNotHeld[id] = now + TimeSpan.FromMinutes(10);
                        _log.LogWarning("interface {Name} ({Description}) does not keep its DNS on the resolver (it reads {Servers}); not re-pointed for ten minutes",
                            adapter.Name, adapter.Description, string.Join(",", dns.Static.DefaultIfEmpty("DHCP")));
                        continue;
                    }

                    _repointedAt[id] = now;
                    _log.LogInformation("interface {Name} ({Description}) points its DNS at {Servers}, not the resolver; re-applying the whitelist",
                        adapter.Name, adapter.Description, string.Join(",", dns.Static.DefaultIfEmpty("DHCP")));
                    return true;
                }

                return false;
            }
            catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or FormatException)
            {
                return false;
            }
        }
    }

    private static bool HasGateway(System.Net.NetworkInformation.NetworkInterface adapter, bool ipv6)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses.Any(g =>
                (g.Address.AddressFamily == AddressFamily.InterNetworkV6) == ipv6 && !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return false;
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
