using System.Globalization;

namespace LabControl.Shared.Packaging;

/// <summary>
/// One inbound rule the teacher console needs on Windows (D-59 item 2): a single port,
/// a single protocol, allow, inbound, on the Private and Domain profiles. Nothing here
/// opens a program, a subnet or the Public profile, and nothing disables the firewall.
/// </summary>
public sealed record ConsoleFirewallRuleSpec(string Name, string Description, int Protocol, int Port)
{
    public const int Tcp = 6;
    public const int Udp = 17;

    /// <summary>Inbound; the COM API calls it <c>NET_FW_RULE_DIR_IN</c>.</summary>
    public const int DirectionIn = 1;

    /// <summary>Allow; the COM API calls it <c>NET_FW_ACTION_ALLOW</c>.</summary>
    public const int ActionAllow = 1;

    /// <summary>Block; the COM API calls it <c>NET_FW_ACTION_BLOCK</c>. A matching block rule wins over every allow rule.</summary>
    public const int ActionBlock = 0;

    /// <summary>
    /// Domain (1) | Private (2). Public (4) is deliberately not requested.
    /// <c>NET_FW_PROFILE_TYPE2</c> numbers the profiles Domain 1, Private 2, Public 4 — not
    /// Private 2, Domain 4 — and this one constant both creates the rules and decides whether
    /// somebody else's rule already covers a port, so a wrong value opens the teacher's
    /// machine on a café network and rejects the very rule the printed netsh line creates.
    /// </summary>
    public const int Profiles = 3;

    /// <summary>What the COM API reports for a rule that is on every profile.</summary>
    public const int AllProfiles = 0x7FFFFFFF;

    public string ProtocolName => Protocol == Tcp ? "TCP" : "UDP";

    public string LocalPorts => Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The exact command line a teacher can run in an elevated prompt when the console cannot
    /// add the rule itself. There is no <c>group=</c> here on purpose: <c>netsh advfirewall
    /// firewall add rule</c> rejects that argument outright ("'group' is not a valid argument
    /// for this command"), so a line carrying it creates nothing at all. A rule made by hand
    /// therefore has no group, which is why uninstall leaves it for review instead of removing
    /// it by name — the safe half of the trade.
    /// </summary>
    public string NetshLine =>
        $"netsh advfirewall firewall add rule name=\"{Name}\" dir=in action=allow protocol={ProtocolName} " +
        $"localport={LocalPorts} profile=private,domain enable=yes";

    /// <summary>The matching removal, for the uninstall that could not elevate itself (D-59 item 1).</summary>
    public string NetshDeleteLine =>
        $"netsh advfirewall firewall delete rule name=\"{Name}\" dir=in protocol={ProtocolName} localport={LocalPorts}";
}

/// <summary>
/// One rule as Windows reports it. A plain record so the detection logic is pure and
/// testable on any OS; the COM reader fills it in and never decides anything itself.
/// </summary>
public sealed record ConsoleFirewallRuleView
{
    public string Name { get; init; } = "";
    public string Grouping { get; init; } = "";
    public int Protocol { get; init; }
    public string? LocalPorts { get; init; }
    public int Direction { get; init; }
    public int Action { get; init; }
    public bool Enabled { get; init; }
    public int Profiles { get; init; }

    /// <summary>The program the rule is scoped to. A rule for Teams opens nothing for this console.</summary>
    public string? ApplicationName { get; init; }

    /// <summary>The Windows service the rule is scoped to. This console is not a service, so anything here disqualifies the rule.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Who may connect. <c>*</c>, empty or <c>LocalSubnet</c> covers a classroom; a single host does not.</summary>
    public string? RemoteAddresses { get; init; }

    /// <summary>Which local addresses the rule applies to. Same test as <see cref="RemoteAddresses"/>.</summary>
    public string? LocalAddresses { get; init; }

    /// <summary>
    /// <c>All</c>, or a list of <c>Lan</c>, <c>Wireless</c> and <c>RemoteAccess</c>. Anything
    /// narrower is not evidence that this teacher machine — which may be on Wi-Fi one day and
    /// on the wire the next (D-21) — can be reached.
    /// </summary>
    public string? InterfaceTypes { get; init; }
}

/// <summary>Whether a rule of this installer's own is present, and whether it is still ours to remove.</summary>
public enum ConsoleFirewallRuleOwnership
{
    /// <summary>No rule of that name exists.</summary>
    Absent = 0,

    /// <summary>Every rule of that name is in this installer's group: uninstall may remove it.</summary>
    Owned = 1,

    /// <summary>A rule of that name is in another group, so removing by name would take somebody else's rule with it.</summary>
    Foreign = 2,
}

/// <summary>What the console found: whether the LAN may reach it, and which of its own rules exist.</summary>
public sealed record ConsoleFirewallStatus(
    bool Allowed,
    IReadOnlyList<ConsoleFirewallRuleSpec> Missing,
    IReadOnlyList<string> OwnedRuleNames)
{
    public static ConsoleFirewallStatus AllAllowed { get; } = new(true, [], []);
}

/// <summary>
/// The two rules and the read-only detection behind the <i>LAN access</i> banner (D-59 item 2).
///
/// A port counts as reachable when some inbound allow rule really opens it <b>for this
/// console</b> and nothing blocks it. Somebody else's port-scoped rule is still a perfectly
/// good answer — the console never adds a duplicate — but a rule scoped to another program or
/// to a Windows service, cut down to one address range or to one interface type, opens nothing
/// for the classroom, and one matching block rule overrides every allow rule there is. Saying
/// <i>allowed</i> when the lab cannot connect is the one answer the banner must never give.
///
/// <see cref="ConsoleFirewallStatus.OwnedRuleNames"/> lists only what the installer created,
/// which is all that uninstall is allowed to remove.
/// </summary>
public static class ConsoleFirewallRules
{
    public static string Group => Defaults.ConsoleFirewallGroup;

    public static ConsoleFirewallRuleSpec Control { get; } = new(
        Defaults.ConsoleControlFirewallRule,
        "Lets the student PCs of this lab reach the LabControl teacher console.",
        ConsoleFirewallRuleSpec.Tcp,
        Defaults.ConsolePort);

    public static ConsoleFirewallRuleSpec Discovery { get; } = new(
        Defaults.ConsoleDiscoveryFirewallRule,
        "Lets the student PCs of this lab hear the LabControl console's discovery beacon.",
        ConsoleFirewallRuleSpec.Udp,
        Defaults.BeaconPort);

    public static IReadOnlyList<ConsoleFirewallRuleSpec> Required { get; } = [Control, Discovery];

    public static IReadOnlyList<string> NetshLines() => [.. Required.Select(spec => spec.NetshLine)];

    /// <summary>The two lines a teacher runs by hand when uninstall could not elevate itself.</summary>
    public static IReadOnlyList<string> NetshDeleteLines() => [.. Required.Select(spec => spec.NetshDeleteLine)];

    /// <summary>
    /// One rule exactly as this installer creates it, as the reader sees it back: no program,
    /// no service, no address or interface restriction. Used by the tests to prove that what
    /// the installer adds is what the check accepts.
    /// </summary>
    public static ConsoleFirewallRuleView AsCreated(ConsoleFirewallRuleSpec spec) => new()
    {
        Name = spec.Name,
        Grouping = Group,
        Protocol = spec.Protocol,
        LocalPorts = spec.LocalPorts,
        Direction = ConsoleFirewallRuleSpec.DirectionIn,
        Action = ConsoleFirewallRuleSpec.ActionAllow,
        Enabled = true,
        Profiles = ConsoleFirewallRuleSpec.Profiles,
        ApplicationName = null,
        ServiceName = null,
        RemoteAddresses = "*",
        LocalAddresses = "*",
        InterfaceTypes = "All",
    };

    /// <param name="consoleExecutable">
    /// The console this check is about, when it is known: a program-scoped rule counts only
    /// when it is scoped to exactly that executable. <c>null</c> falls back to the executable
    /// name alone, which is all an installer that has not been told where the console lives
    /// can honestly compare.
    /// </param>
    public static ConsoleFirewallStatus Evaluate(
        IEnumerable<ConsoleFirewallRuleView> observed,
        string? consoleExecutable = null)
    {
        var rules = observed as IReadOnlyList<ConsoleFirewallRuleView> ?? [.. observed];
        var missing = Required.Where(spec => !IsReachable(rules, spec, consoleExecutable)).ToArray();
        var owned = rules
            .Where(rule => string.Equals(rule.Grouping, Group, StringComparison.OrdinalIgnoreCase)
                && Required.Any(spec => string.Equals(rule.Name, spec.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(rule => rule.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return new ConsoleFirewallStatus(missing.Length == 0, missing, owned);
    }

    /// <summary>
    /// Whether the classroom can actually reach this port: some rule opens it for this
    /// console, and no rule blocks it. Windows applies the most specific block first, so one
    /// matching block rule — an administrator's, a policy's — makes every allow rule
    /// irrelevant, and the console must say <i>blocked</i> rather than <i>allowed</i>.
    /// </summary>
    public static bool IsReachable(
        IReadOnlyList<ConsoleFirewallRuleView> rules,
        ConsoleFirewallRuleSpec spec,
        string? consoleExecutable = null) =>
        rules.Any(rule => Allows(rule, spec, consoleExecutable))
        && !rules.Any(rule => Blocks(rule, spec, consoleExecutable));

    /// <summary>Whether a rule of this name is present and still entirely this installer's own (D-59 item 1).</summary>
    public static ConsoleFirewallRuleOwnership Ownership(
        IEnumerable<ConsoleFirewallRuleView> observed,
        ConsoleFirewallRuleSpec spec)
    {
        var named = observed
            .Where(rule => string.Equals(rule.Name, spec.Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (named.Length == 0)
        {
            return ConsoleFirewallRuleOwnership.Absent;
        }

        // Windows removes rules by name, so one foreign rule sharing the name makes the whole
        // removal unsafe: it would take a rule this installer never created.
        return named.All(rule => string.Equals(rule.Grouping, Group, StringComparison.OrdinalIgnoreCase))
            ? ConsoleFirewallRuleOwnership.Owned
            : ConsoleFirewallRuleOwnership.Foreign;
    }

    private static bool Allows(ConsoleFirewallRuleView rule, ConsoleFirewallRuleSpec spec, string? consoleExecutable) =>
        Concerns(rule, spec)
        && rule.Action == ConsoleFirewallRuleSpec.ActionAllow
        && CoversProfiles(rule.Profiles)
        && IsForThisConsole(rule, consoleExecutable)
        && ReachesTheClassroom(rule.RemoteAddresses)
        && ReachesTheClassroom(rule.LocalAddresses)
        && CoversEveryInterface(rule.InterfaceTypes);

    /// <summary>
    /// A block rule is judged generously: any profile the lab uses, any address range and any
    /// interface list is enough to stop the classroom, so it is never explained away.
    /// </summary>
    private static bool Blocks(ConsoleFirewallRuleView rule, ConsoleFirewallRuleSpec spec, string? consoleExecutable) =>
        Concerns(rule, spec)
        && rule.Action == ConsoleFirewallRuleSpec.ActionBlock
        && TouchesProfiles(rule.Profiles)
        && IsForThisConsole(rule, consoleExecutable);

    private static bool Concerns(ConsoleFirewallRuleView rule, ConsoleFirewallRuleSpec spec) =>
        rule.Enabled
        && rule.Direction == ConsoleFirewallRuleSpec.DirectionIn
        && rule.Protocol == spec.Protocol
        && CoversPort(rule.LocalPorts, spec.Port);

    /// <summary>
    /// A rule scoped to a program or a service says nothing about this console unless that
    /// program is this console. A conferencing tool, a game or a service rule that happens to
    /// cover the port is not an answer — the classroom would still not get through.
    /// </summary>
    public static bool IsForThisConsole(ConsoleFirewallRuleView rule, string? consoleExecutable)
    {
        if (!IsUnrestricted(rule.ServiceName))
        {
            return false;
        }

        if (IsUnrestricted(rule.ApplicationName))
        {
            return true;
        }

        var application = rule.ApplicationName!.Trim().Trim('"');
        if (!string.IsNullOrEmpty(consoleExecutable))
        {
            return string.Equals(application, consoleExecutable.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(LeafOf(application), Defaults.ConsoleExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Both profiles the lab runs on must be covered; a rule limited to Public is not an answer.</summary>
    public static bool CoversProfiles(int profiles) =>
        (profiles & ConsoleFirewallRuleSpec.Profiles) == ConsoleFirewallRuleSpec.Profiles;

    /// <summary>Any of the lab's profiles — enough for a block rule to matter.</summary>
    public static bool TouchesProfiles(int profiles) =>
        (profiles & ConsoleFirewallRuleSpec.Profiles) != 0;

    /// <summary>
    /// Windows writes an address scope as <c>*</c>, a keyword such as <c>LocalSubnet</c>, or a
    /// list of addresses and ranges. Only <c>*</c> and <c>LocalSubnet</c> cover a whole
    /// classroom; a rule cut down to one host or one range is not evidence that the lab can
    /// connect.
    /// </summary>
    public static bool ReachesTheClassroom(string? addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses))
        {
            return true;
        }

        foreach (var raw in addresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw != "*"
                && !string.Equals(raw, "Any", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(raw, "LocalSubnet", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>All</c> or nothing: the teacher machine may be on Wi-Fi today and on the wire
    /// tomorrow (D-21), so a rule limited to one interface type is not an answer for the lab.
    /// </summary>
    public static bool CoversEveryInterface(string? interfaceTypes) =>
        string.IsNullOrWhiteSpace(interfaceTypes)
        || string.Equals(interfaceTypes.Trim(), "All", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnrestricted(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == "*";

    /// <summary>The file name of a Windows path, computed the same way on every operating system.</summary>
    private static string LeafOf(string path)
    {
        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>
    /// Windows writes <c>LocalPorts</c> as <c>*</c>, a number, an <c>a-b</c> range, a comma
    /// separated list of those, or a keyword such as <c>RPC</c>. A keyword is not evidence
    /// that this port is open, so it never satisfies a rule.
    /// </summary>
    public static bool CoversPort(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts))
        {
            return true;
        }

        foreach (var raw in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*")
            {
                return true;
            }

            var dash = raw.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var single) && single == port)
                {
                    return true;
                }

                continue;
            }

            if (int.TryParse(raw[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var low)
                && int.TryParse(raw[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var high)
                && port >= low && port <= high)
            {
                return true;
            }
        }

        return false;
    }
}
