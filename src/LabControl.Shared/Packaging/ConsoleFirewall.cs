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

    /// <summary>Private (2) | Domain (4). Public is deliberately not requested.</summary>
    public const int Profiles = 6;

    /// <summary>What the COM API reports for a rule that is on every profile.</summary>
    public const int AllProfiles = 0x7FFFFFFF;

    public string ProtocolName => Protocol == Tcp ? "TCP" : "UDP";

    public string LocalPorts => Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>The exact command line a teacher can run in an elevated prompt when the console cannot add the rule itself.</summary>
    public string NetshLine =>
        $"netsh advfirewall firewall add rule name=\"{Name}\" dir=in action=allow protocol={ProtocolName} " +
        $"localport={LocalPorts} profile=private,domain group=\"{ConsoleFirewallRules.Group}\" enable=yes";
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
/// A port counts as reachable when <b>any</b> inbound allow rule covers it — a rule someone
/// else added is a perfectly good answer, so the console never adds a duplicate — while
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

    public static ConsoleFirewallStatus Evaluate(IEnumerable<ConsoleFirewallRuleView> observed)
    {
        var rules = observed as IReadOnlyList<ConsoleFirewallRuleView> ?? [.. observed];
        var missing = Required.Where(spec => !rules.Any(rule => Satisfies(rule, spec))).ToArray();
        var owned = rules
            .Where(rule => string.Equals(rule.Grouping, Group, StringComparison.OrdinalIgnoreCase)
                && Required.Any(spec => string.Equals(rule.Name, spec.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(rule => rule.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return new ConsoleFirewallStatus(missing.Length == 0, missing, owned);
    }

    private static bool Satisfies(ConsoleFirewallRuleView rule, ConsoleFirewallRuleSpec spec) =>
        rule.Enabled
        && rule.Direction == ConsoleFirewallRuleSpec.DirectionIn
        && rule.Action == ConsoleFirewallRuleSpec.ActionAllow
        && rule.Protocol == spec.Protocol
        && CoversProfiles(rule.Profiles)
        && CoversPort(rule.LocalPorts, spec.Port);

    /// <summary>Both the profiles the lab runs on must be covered; a rule limited to Public is not an answer.</summary>
    public static bool CoversProfiles(int profiles) =>
        (profiles & ConsoleFirewallRuleSpec.Profiles) == ConsoleFirewallRuleSpec.Profiles;

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
