using System.Collections;
using System.Runtime.InteropServices;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Microsoft.CSharp.RuntimeBinder;

namespace LabControl.Setup;

internal sealed class WindowsSetupFirewallStore : ISetupFirewallStore
{
    public SetupFirewallRule? Read(SetupFirewallPolicy policy) => WithRules(rules => ReadRule(rules, policy));
    public void Write(SetupFirewallPolicy policy, SetupFirewallRule? expected, SetupFirewallRule? desired) => WithRules(rules =>
    {
        Check();
        if (desired is null)
        {
            if (expected is null) return true;
            // The journal can remove only the exact fixed rule it created.
            if (!expected.SameAs(SetupFirewallRule.Desired(policy))) throw new IOException();
            Check();
            rules.Remove(SetupFirewallRule.Desired(policy).Name);
        }
        else
        {
            if (expected is not null || !desired.SameAs(SetupFirewallRule.Desired(policy))) throw new IOException();
            dynamic rule = Create(Defaults.SetupFirewallRuleProgId);
            try
            {
                rule.Name = desired.Name;
                rule.Description = desired.Description;
                rule.Protocol = desired.Protocol;
                if (desired.Protocol == 17)
                {
                    rule.LocalPorts = desired.LocalPorts;
                    rule.RemotePorts = desired.RemotePorts;
                }
                else rule.IcmpTypesAndCodes = desired.IcmpTypesAndCodes;
                rule.LocalAddresses = desired.LocalAddresses;
                rule.RemoteAddresses = desired.RemoteAddresses;
                rule.Direction = desired.Direction;
                rule.InterfaceTypes = desired.InterfaceTypes;
                rule.Enabled = desired.Enabled;
                rule.Grouping = desired.Grouping;
                rule.Profiles = desired.Profiles;
                rule.EdgeTraversal = desired.EdgeTraversal;
                rule.Action = desired.Action;
                rule.EdgeTraversalOptions = desired.EdgeTraversalOptions;
                Check();
                rules.Add(rule);
            }
            finally { Release(rule); }
        }
        var confirmed = ReadRule(rules, policy);
        if (!Same(confirmed, desired)) throw new IOException();
        return true;
        void Check() { if (!Same(ReadRule(rules, policy), expected)) throw new IOException(); }
    });

    private static SetupFirewallRule? ReadRule(dynamic rules, SetupFirewallPolicy policy)
    {
        SetupFirewallRule? Once()
        {
            SetupFirewallRule? found = null;
            foreach (object item in (IEnumerable)rules)
            {
                dynamic rule = item;
                try
                {
                    if (!string.Equals((string)rule.Name, SetupFirewallRule.Desired(policy).Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (found is not null) throw new IOException("Duplicate firewall rule names are ambiguous.");
                    int protocol = rule.Protocol;
                    found = new SetupFirewallRule
                    {
                        Name = rule.Name, Description = Text(rule.Description),
                        Application = Text(rule.ApplicationName), Service = Text(rule.ServiceName), Protocol = protocol,
                        LocalPorts = protocol is 6 or 17 ? Text(rule.LocalPorts) : null,
                        RemotePorts = protocol is 6 or 17 ? Text(rule.RemotePorts) : null,
                        LocalAddresses = Text(rule.LocalAddresses), RemoteAddresses = Text(rule.RemoteAddresses),
                        IcmpTypesAndCodes = protocol is 1 or 58 ? Text(rule.IcmpTypesAndCodes) : null,
                        Direction = rule.Direction, Interfaces = Interfaces(rule.Interfaces),
                        InterfaceTypes = Text(rule.InterfaceTypes), Enabled = rule.Enabled,
                        Grouping = Text(rule.Grouping), Profiles = rule.Profiles,
                        EdgeTraversal = rule.EdgeTraversal, Action = rule.Action,
                        EdgeTraversalOptions = rule.EdgeTraversalOptions,
                        LocalAppPackageId = Text(rule.LocalAppPackageId), LocalUserOwner = Text(rule.LocalUserOwner),
                        LocalUserAuthorizedList = Text(rule.LocalUserAuthorizedList),
                        RemoteUserAuthorizedList = Text(rule.RemoteUserAuthorizedList),
                        RemoteMachineAuthorizedList = Text(rule.RemoteMachineAuthorizedList), SecureFlags = rule.SecureFlags,
                    };
                }
                finally { Release(item); }
            }
            return found;
        }
        var first = Once();
        if (!Same(first, Once())) throw new IOException();
        return first;
    }
    private static string Text(object? value) => value is null ? "" : value as string ?? throw new IOException();
    private static string[] Interfaces(object? value)
    {
        if (value is null) return [];
        if (value is not Array array) throw new IOException();
        return array.Cast<object>().Select(Text).OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }
    private static bool Same(SetupFirewallRule? a, SetupFirewallRule? b) => a is null ? b is null : b is not null && a.SameAs(b);
    private static object Create(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)
        ?? throw new IOException();
    private static void Release(object value) { if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static T WithRules<T>(Func<dynamic, T> action)
    {
        object? policy = null; object? rules = null;
        try
        {
            policy = Create(Defaults.SetupFirewallPolicyProgId);
            rules = ((dynamic)policy).Rules;
            return action(rules);
        }
        catch (Exception error) when (error is COMException or IOException or RuntimeBinderException or UnauthorizedAccessException)
        { throw new IOException("The Windows firewall rule could not be inspected or changed safely; preserve it for review."); }
        finally
        {
            if (rules is not null) Release(rules);
            if (policy is not null) Release(policy);
        }
    }
}
