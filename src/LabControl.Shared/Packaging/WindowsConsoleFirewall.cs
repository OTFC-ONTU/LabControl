using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LabControl.Shared.Packaging;

/// <summary>
/// The Windows Firewall, seen through the same COM object the student Setup uses
/// (<c>HNetCfg.FwPolicy2</c>), for the teacher console's two inbound rules (D-59 item 2).
/// Reading needs no elevation and is what the console's banner does; adding and removing
/// need it and belong to <c>LabControl.ConsoleSetup.exe</c>.
///
/// Nothing here disables the firewall, opens a program, widens an address range or touches
/// the Public profile, and removal only ever deletes a rule that still looks exactly like
/// the one this installer created.
/// </summary>
public static class WindowsConsoleFirewall
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Every rule Windows holds, flattened into plain records. Read-only.</summary>
    public static IReadOnlyList<ConsoleFirewallRuleView> ReadRules()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Unsupported();
        }

        return ReadRulesCore();
    }

    /// <summary>Whether the LAN may reach this console, and which of the installer's rules exist.</summary>
    public static ConsoleFirewallStatus Check() => ConsoleFirewallRules.Evaluate(ReadRules());

    /// <summary>
    /// Adds the rule when the port is not already reachable. Returns <c>true</c> when a rule
    /// was created, <c>false</c> when nothing was needed — so a second run is a no-op, and a
    /// rule somebody else already added is never duplicated.
    /// </summary>
    public static bool EnsureRule(ConsoleFirewallRuleSpec spec)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Unsupported();
        }

        return ConsoleFirewallRules.Evaluate(ReadRulesCore()).Missing.Any(missing => missing.Name == spec.Name)
            && AddCore(spec);
    }

    /// <summary>
    /// Removes a rule this installer owns: the name and the group must both still match,
    /// so a rule a teacher edited or renamed is preserved for review rather than deleted.
    /// </summary>
    public static bool RemoveOwnedRule(ConsoleFirewallRuleSpec spec)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Unsupported();
        }

        return ConsoleFirewallRules.Evaluate(ReadRulesCore()).OwnedRuleNames
                .Contains(spec.Name, StringComparer.OrdinalIgnoreCase)
            && RemoveCore(spec);
    }

    private static PlatformNotSupportedException Unsupported() =>
        new("The Windows Firewall is only reachable on Windows.");

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ConsoleFirewallRuleView> ReadRulesCore() => WithRules(rules =>
    {
        var found = new List<ConsoleFirewallRuleView>();
        foreach (var item in (IEnumerable)rules)
        {
            try
            {
                found.Add(Describe(item));
            }
            finally
            {
                Release(item);
            }
        }

        return (IReadOnlyList<ConsoleFirewallRuleView>)found;
    });

    [SupportedOSPlatform("windows")]
    private static bool AddCore(ConsoleFirewallRuleSpec spec) => WithRules(rules =>
    {
        var rule = Create(Defaults.SetupFirewallRuleProgId);
        try
        {
            Set(rule, "Name", spec.Name);
            Set(rule, "Description", spec.Description);
            Set(rule, "Protocol", spec.Protocol);
            Set(rule, "LocalPorts", spec.LocalPorts);
            Set(rule, "Direction", ConsoleFirewallRuleSpec.DirectionIn);
            Set(rule, "Action", ConsoleFirewallRuleSpec.ActionAllow);
            Set(rule, "Profiles", ConsoleFirewallRuleSpec.Profiles);
            Set(rule, "Grouping", ConsoleFirewallRules.Group);
            Set(rule, "Enabled", true);
            Call(rules, "Add", rule);
        }
        finally
        {
            Release(rule);
        }

        return true;
    });

    [SupportedOSPlatform("windows")]
    private static bool RemoveCore(ConsoleFirewallRuleSpec spec) => WithRules(rules =>
    {
        Call(rules, "Remove", spec.Name);
        return true;
    });

    [SupportedOSPlatform("windows")]
    private static ConsoleFirewallRuleView Describe(object rule)
    {
        var protocol = Number(Get(rule, "Protocol"));
        return new ConsoleFirewallRuleView
        {
            Name = Text(Get(rule, "Name")),
            Grouping = Text(Get(rule, "Grouping")),
            Protocol = protocol,
            LocalPorts = protocol is ConsoleFirewallRuleSpec.Tcp or ConsoleFirewallRuleSpec.Udp
                ? Text(Get(rule, "LocalPorts"))
                : null,
            Direction = Number(Get(rule, "Direction")),
            Action = Number(Get(rule, "Action")),
            Enabled = Get(rule, "Enabled") is true,
            Profiles = Number(Get(rule, "Profiles")),
        };
    }

    private static string Text(object? value) => value as string ?? "";

    private static int Number(object? value) =>
        value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    [SupportedOSPlatform("windows")]
    private static object Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)
        ?? throw new IOException("The Windows Firewall COM object could not be created.");

    [SupportedOSPlatform("windows")]
    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    [SupportedOSPlatform("windows")]
    private static void Set(object target, string name, object? value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value], CultureInfo.InvariantCulture);

    [SupportedOSPlatform("windows")]
    private static void Call(object target, string name, params object?[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    [SupportedOSPlatform("windows")]
    private static void Release(object value)
    {
        if (Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    [SupportedOSPlatform("windows")]
    private static T WithRules<T>(Func<object, T> action)
    {
        object? policy = null;
        object? rules = null;
        try
        {
            policy = Create(Defaults.SetupFirewallPolicyProgId);
            rules = Get(policy, "Rules") ?? throw new IOException("The Windows Firewall rule collection is unavailable.");
            return action(rules);
        }
        catch (Exception error) when (error is COMException or TargetInvocationException
            or MissingMemberException or InvalidCastException or UnauthorizedAccessException)
        {
            throw new IOException("The Windows Firewall rules could not be read or changed safely.", error);
        }
        finally
        {
            if (rules is not null)
            {
                Release(rules);
            }

            if (policy is not null)
            {
                Release(policy);
            }
        }
    }
}
