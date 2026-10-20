using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using LabControl.Shared;

namespace LabControl.Agent;

/// <summary>What one walk over the firewall's rules found (D-73): the agent group's rule names, and other programs' enabled outbound allow rules that reach past the lab, as fingerprints.</summary>
internal sealed record FirewallScan(IReadOnlyList<string> Owned, IReadOnlyList<string> ForeignEnabled);

/// <summary>One outbound allow rule another program owns, as the policy switched it off (D-73 item 4).</summary>
/// <param name="Fingerprint">What identifies the rule when it is switched back on: firewall rule names are not unique.</param>
internal sealed record ForeignRule(string Fingerprint, string Name);

/// <summary>
/// Windows Firewall through its COM automation object (<c>HNetCfg.FwPolicy2</c>), late-bound like
/// Setup's <c>WindowsSetupFirewallStore</c> (M6 portion 3, D-73): the profiles' default outbound
/// action, the agent's own rule group <see cref="Defaults.InternetFirewallRuleGroup"/>, and the
/// other programs' outbound allow rules a policy switches off. Every call may throw a
/// <see cref="COMException"/> or <see cref="IOException"/>; <see cref="WindowsInternetControl"/>
/// turns each into a <c>MachineResult</c>, never an exception out of the service loop.
/// </summary>
internal sealed class WindowsFirewall
{
    /// <summary><c>NET_FW_PROFILE_TYPE2</c>: Domain 1, Private 2, Public 4 — never 6, which is Private and Public (the portion-7 lesson).</summary>
    public static readonly IReadOnlyDictionary<string, int> Profiles = new Dictionary<string, int>
    {
        ["domain"] = 1,
        ["private"] = 2,
        ["public"] = 4,
    };

    private const int DirectionOut = 2;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;
    private const int ProtocolAny = 256;
    private const int AllProfiles = 0x7FFFFFFF;

    /// <summary>The <i>Core Networking</i> group (DHCP, DNS, IPv6 neighbour discovery): never switched off, or a PC loses its address mid-lesson.</summary>
    private const string CoreNetworkingGroup = "@FirewallAPI.dll,-25000";

    public const string AgentRuleName = Defaults.InternetFirewallRuleGroup + " (agent)";
    public const string LocalSubnetRuleName = Defaults.InternetFirewallRuleGroup + " (local subnet)";
    public const string SitesRuleName = Defaults.InternetFirewallRuleGroup + " (sites)";

    /// <summary>Remote-address keywords that only ever reach the lab's own network; a rule scoped to them alone is not a way out.</summary>
    private static readonly string[] LocalKeywords = ["localsubnet", "defaultgateway", "dhcp", "dns", "wins"];

    /// <summary>Whether the firewall is on for each profile, and which profiles are in use now.</summary>
    public (Dictionary<string, bool> Enabled, int CurrentProfiles) ReadEnabled() => WithPolicy(policy =>
    {
        var enabled = Profiles.ToDictionary(p => p.Key, p => Convert.ToBoolean(Get(policy, "FirewallEnabled", p.Value), CultureInfo.InvariantCulture));
        var current = Convert.ToInt32(Get(policy, "CurrentProfileTypes"), CultureInfo.InvariantCulture);
        return (enabled, current);
    });

    /// <summary>Each profile's default outbound action, <c>allow</c> or <c>block</c>.</summary>
    public Dictionary<string, string> ReadDefaultOutbound() => WithPolicy(policy =>
        Profiles.ToDictionary(p => p.Key, p => Convert.ToInt32(Get(policy, "DefaultOutboundAction", p.Value), CultureInfo.InvariantCulture) == ActionBlock ? "block" : "allow"));

    /// <summary>Sets one profile's default outbound action and reads it back.</summary>
    public void SetDefaultOutbound(string profile, string action) => WithPolicy(policy =>
    {
        var value = action == "block" ? ActionBlock : ActionAllow;
        Set(policy, "DefaultOutboundAction", Profiles[profile], value);
        if (Convert.ToInt32(Get(policy, "DefaultOutboundAction", Profiles[profile]), CultureInfo.InvariantCulture) != value)
        {
            throw new IOException($"the {profile} profile's default outbound action did not read back as {action}");
        }

        return true;
    });

    /// <summary>The names of every rule in the agent's group, the agent's own exemption last.</summary>
    public List<string> OwnedRules() => WithRules(rules =>
    {
        var names = new List<string>();
        foreach (object item in (IEnumerable)rules)
        {
            try
            {
                dynamic rule = item;
                if (string.Equals(Text(rule.Grouping), Defaults.InternetFirewallRuleGroup, StringComparison.Ordinal))
                {
                    names.Add((string)rule.Name);
                }
            }
            finally
            {
                Release(item);
            }
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => string.Equals(n, AgentRuleName, StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
    });

    /// <summary>
    /// Removes every rule of the agent's group, the <c>agent.exe</c> exemption last (D-69 item 2):
    /// the link to the console survives every step of a restore. <c>Remove</c> takes the first
    /// rule of a name, so a name is removed until none is left.
    /// </summary>
    public void RemoveOwnedRules()
    {
        foreach (var name in OwnedRules())
        {
            WithRules(rules =>
            {
                for (var i = 0; i < 64 && Exists(rules, name); i++)
                {
                    rules.Remove(name);
                }

                return true;
            });
        }

        if (OwnedRules().Count > 0)
        {
            throw new IOException("rules of the " + Defaults.InternetFirewallRuleGroup + " group are still present after removal");
        }
    }

    /// <summary>
    /// The outbound allow rule for <paramref name="program"/> (the running <c>agent.exe</c>, pinned
    /// to its <c>app\&lt;version&gt;\</c> path), every protocol, every remote address: what keeps
    /// the console link, the file channel and the resolver's upstream queries alive under a block
    /// — <c>LocalSubnet</c> alone is not enough when the teacher's laptop is on Wi-Fi.
    /// </summary>
    public void EnsureAgentRule(string program) => EnsureRule(AgentRuleName, "*", program);

    /// <summary>The lab's own subnet stays reachable for every program — shares, the printer, the console (ARCHITECTURE §6.2).</summary>
    public void EnsureLocalSubnetRule() => EnsureRule(LocalSubnetRuleName, "LocalSubnet", null);

    /// <summary>
    /// The whitelist's learned addresses as one rule (D-74). An empty list removes the rule: a
    /// rule's empty remote address list means <i>any</i>, which would open everything.
    /// </summary>
    public void SetSiteAddresses(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
        {
            WithRules(rules =>
            {
                for (var i = 0; i < 64 && Exists(rules, SitesRuleName); i++)
                {
                    rules.Remove(SitesRuleName);
                }

                return true;
            });
            return;
        }

        var list = string.Join(',', addresses.Select(a => a.ToString()));
        WithRules(rules =>
        {
            // Item() rather than a walk over every rule: this runs on the resolver's path, before
            // the answer reaches the browser. The name is the agent's own and unique.
            object? existing = null;
            try
            {
                existing = rules.Item(SitesRuleName);
            }
            catch (Exception ex) when (ex is COMException or IOException or ArgumentException)
            {
                // A missing rule is HRESULT 0x80070002, which the COM binder may surface as
                // FileNotFoundException rather than COMException (review finding 1).
            }

            if (existing is null)
            {
                Add(rules, SitesRuleName, list, null);
                return true;
            }

            try
            {
                ((dynamic)existing).RemoteAddresses = list;
            }
            finally
            {
                Release(existing);
            }

            return true;
        });
    }

    /// <summary>
    /// Every other program's enabled outbound allow rule that reaches past the lab's own network
    /// (owner decision 2026-10-17, D-73 item 4): under a default outbound block an allow rule is
    /// a way out, so a policy switches these off. The <i>Core Networking</i> group stays on, and
    /// so does a rule scoped only to the local network.
    /// </summary>
    public List<ForeignRule> ForeignAllowRules() => WithRules(rules =>
    {
        var found = new List<ForeignRule>();
        foreach (object item in (IEnumerable)rules)
        {
            try
            {
                dynamic rule = item;
                if ((int)rule.Direction != DirectionOut || (int)rule.Action != ActionAllow || !(bool)rule.Enabled)
                {
                    continue;
                }

                var group = Text(rule.Grouping);
                if (string.Equals(group, Defaults.InternetFirewallRuleGroup, StringComparison.Ordinal)
                    || string.Equals(group, CoreNetworkingGroup, StringComparison.OrdinalIgnoreCase)
                    || LocalOnly(Text(rule.RemoteAddresses)))
                {
                    continue;
                }

                found.Add(new ForeignRule(Fingerprint(rule), Text(rule.Name)));
            }
            finally
            {
                Release(item);
            }
        }

        return found;
    });

    /// <summary>
    /// Switches the rules with these fingerprints on or off; returns how many changed. A rule
    /// already in the asked state is left alone, so a repeat is harmless.
    /// </summary>
    public int SetForeignRules(IReadOnlyCollection<string> fingerprints, bool enabled) => WithRules(rules =>
    {
        var wanted = new HashSet<string>(fingerprints, StringComparer.Ordinal);
        var changed = 0;
        foreach (object item in (IEnumerable)rules)
        {
            try
            {
                dynamic rule = item;
                if ((int)rule.Direction != DirectionOut || (int)rule.Action != ActionAllow || (bool)rule.Enabled == enabled)
                {
                    continue;
                }

                if (wanted.Contains(Fingerprint(rule)))
                {
                    rule.Enabled = enabled;
                    changed++;
                }
            }
            finally
            {
                Release(item);
            }
        }

        return changed;
    });

    /// <summary>The fingerprints among <paramref name="fingerprints"/> whose rules are still enabled — the read-back of a switch-off.</summary>
    public int CountEnabled(IReadOnlyCollection<string> fingerprints) => WithRules(rules =>
    {
        var wanted = new HashSet<string>(fingerprints, StringComparer.Ordinal);
        var count = 0;
        foreach (object item in (IEnumerable)rules)
        {
            try
            {
                dynamic rule = item;
                if ((int)rule.Direction == DirectionOut && (int)rule.Action == ActionAllow && (bool)rule.Enabled && wanted.Contains(Fingerprint(rule)))
                {
                    count++;
                }
            }
            finally
            {
                Release(item);
            }
        }

        return count;
    });

    // ------------------------------------------------------------------ the pieces

    /// <summary>
    /// Adds the rule unless the group already has it. Never removes and re-adds: on a change of
    /// policy the block is already in force, and an exemption that vanished for a moment would
    /// cut the console link. A rule left by an older build is gone already — every service start
    /// removes the whole group first.
    /// </summary>
    /// <summary>
    /// Adds the rule unless the group already has it. Never removes and re-adds: on a change of
    /// policy the block is already in force, and an exemption that vanished for a moment would
    /// cut the console link. Looked up by name, never by a walk over every rule — a classroom PC
    /// has hundreds, and each late-bound property read is a cross-process call (found in the lab,
    /// 2026-10-20: an apply took 31 s). A rule left by an older build is gone already — every
    /// service start removes the whole group first.
    /// </summary>
    private void EnsureRule(string name, string remoteAddresses, string? program) => WithRules(rules =>
    {
        if (Find(rules, name))
        {
            return true;
        }

        Add(rules, name, remoteAddresses, program);
        if (!Find(rules, name))
        {
            throw new IOException($"the firewall rule {name} did not read back");
        }

        return true;
    });

    /// <summary>True when a rule of this name exists in the agent's group; one <c>Item</c> call, no walk.</summary>
    private static bool Find(dynamic rules, string name)
    {
        object? existing = null;
        try
        {
            existing = rules.Item(name);
            return existing is not null && string.Equals(Text(((dynamic)existing).Grouping), Defaults.InternetFirewallRuleGroup, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException)
        {
            // HRESULT 0x80070002 for a missing rule, raised as either exception by the binder.
            return false;
        }
        finally
        {
            Release(existing);
        }
    }

    /// <summary>
    /// One walk over every rule (D-73, the lab's 31-second apply): the agent group's rule names and
    /// the fingerprints of the other programs' enabled outbound allow rules that reach past the lab.
    /// A rule's cheap properties are read first and the fingerprint only for the few that qualify.
    /// </summary>
    public FirewallScan Scan() => WithRules(rules =>
    {
        var owned = new List<string>();
        var foreign = new List<string>();
        foreach (object item in (IEnumerable)rules)
        {
            try
            {
                dynamic rule = item;
                var group = Text(rule.Grouping);
                if (string.Equals(group, Defaults.InternetFirewallRuleGroup, StringComparison.Ordinal))
                {
                    owned.Add((string)rule.Name);
                    continue;
                }

                if ((int)rule.Direction != DirectionOut || (int)rule.Action != ActionAllow || !(bool)rule.Enabled
                    || string.Equals(group, CoreNetworkingGroup, StringComparison.OrdinalIgnoreCase) || LocalOnly(Text(rule.RemoteAddresses)))
                {
                    continue;
                }

                foreign.Add(Fingerprint(rule));
            }
            finally
            {
                Release(item);
            }
        }

        return new FirewallScan(owned.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), foreign.Distinct(StringComparer.Ordinal).ToList());
    });

    private static void Add(dynamic rules, string name, string remoteAddresses, string? program)
    {
        dynamic rule = Create(Defaults.SetupFirewallRuleProgId);
        try
        {
            rule.Name = name;
            rule.Description = "LabControl internet control (M6). Removed when the policy ends; agent.exe --restore-policy removes it by hand.";
            rule.Direction = DirectionOut;
            rule.Protocol = ProtocolAny;
            rule.RemoteAddresses = remoteAddresses;
            if (program is not null)
            {
                rule.ApplicationName = program;
            }

            rule.Profiles = AllProfiles;
            rule.Grouping = Defaults.InternetFirewallRuleGroup;
            rule.Action = ActionAllow;
            rule.Enabled = true;
            rules.Add(rule);
        }
        finally
        {
            Release(rule);
        }
    }

    private static bool Exists(dynamic rules, string name)
    {
        foreach (object item in (IEnumerable)rules)
        {
            try
            {
                dynamic rule = item;
                if (string.Equals((string)rule.Name, name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Text(rule.Grouping), Defaults.InternetFirewallRuleGroup, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            finally
            {
                Release(item);
            }
        }

        return false;
    }

    /// <summary>What identifies a rule well enough to switch the same one back on: name, program, service, package, protocol, ports, addresses, profiles, group.</summary>
    private static string Fingerprint(dynamic rule)
    {
        int protocol = rule.Protocol;
        var ports = protocol is 6 or 17 ? Text(rule.RemotePorts) + "/" + Text(rule.LocalPorts) : string.Empty;
        return string.Join('|',
            Text(rule.Name), Text(rule.ApplicationName), Text(rule.ServiceName), Text(rule.LocalAppPackageId),
            protocol.ToString(CultureInfo.InvariantCulture), ports, Text(rule.RemoteAddresses),
            ((int)rule.Profiles).ToString(CultureInfo.InvariantCulture), Text(rule.Grouping));
    }

    /// <summary>True when every remote address of a rule is a local keyword, or a private, link-local or multicast address or range.</summary>
    internal static bool LocalOnly(string remoteAddresses)
    {
        if (string.IsNullOrWhiteSpace(remoteAddresses) || remoteAddresses.Trim() == "*")
        {
            return false;
        }

        foreach (var part in remoteAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (LocalKeywords.Contains(part.ToLowerInvariant()))
            {
                continue;
            }

            // An address, a range (a-b) or a subnet (a/b): every end must be local.
            var ends = part.Split('-', '/');
            if (!IPAddress.TryParse(ends[0], out var first) || !IsLocal(first))
            {
                return false;
            }

            if (part.Contains('-', StringComparison.Ordinal) && (!IPAddress.TryParse(ends[1], out var last) || !IsLocal(last)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
        {
            return true;
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || b[0] >= 224;
    }

    private static object? Get(object target, string property, params object[] index) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, index, CultureInfo.InvariantCulture);

    /// <summary>An indexed COM property put: the index first, the value last.</summary>
    private static void Set(object target, string property, int index, int value) =>
        target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, [index, value], CultureInfo.InvariantCulture);

    private static string Text(object? value) => value as string ?? string.Empty;

    private static object Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!) ?? throw new IOException($"{progId} could not be created");

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private static T WithPolicy<T>(Func<object, T> action)
    {
        object? policy = null;
        try
        {
            policy = Create(Defaults.SetupFirewallPolicyProgId);
            return action(policy);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new IOException(ex.InnerException.Message, ex.InnerException);
        }
        finally
        {
            Release(policy);
        }
    }

    private static T WithRules<T>(Func<dynamic, T> action) => WithPolicy(policy =>
    {
        object? rules = null;
        try
        {
            rules = ((dynamic)policy).Rules;
            return action(rules!);
        }
        finally
        {
            Release(rules);
        }
    });
}
