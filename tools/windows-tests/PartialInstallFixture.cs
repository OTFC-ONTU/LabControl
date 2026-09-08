using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LabControl.Setup;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Setup;

/// <summary>A disabled foreign rule creates a deterministic pre-service Setup conflict.
/// Only the isolated clone operator invokes this fixture; production never references it.</summary>
internal static class PartialInstallFixture
{
    public static IReadOnlyList<ProbeResult> Run(string action, string fixtureId)
    {
        var rows = new List<ProbeResult>();
        try
        {
            if (!Guid.TryParseExact(fixtureId, "D", out var guid)) throw new ArgumentException();
            fixtureId = guid.ToString("D");
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException();
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabControl.PartialInstallFixture." + fixtureId);
            var create = action == "create";
            HandoutDelivery.RejectReparseAncestors(root);
            if (!Directory.Exists(root))
            {
                if (!create) throw new DirectoryNotFoundException();
                var acl = new DirectorySecurity();
                acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                acl.SetAccessRuleProtection(true, false);
                foreach (var kind in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                    acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(kind, null), FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(root).Create(acl);
            }
            CheckAcl(new DirectoryInfo(root).GetAccessControl());
            var path = Path.Combine(root, "intent.json");
            CheckFile(path); CheckFile(path + ".tmp");
            var lockPath = Path.Combine(root, "fixture.lock");
            CheckFile(lockPath);
            using var exclusive = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var native = new WindowsSetupFirewallStore();
            if (create)
            {
                RequireUninstalled();
                if (File.Exists(path) || File.Exists(path + ".tmp") || native.Read(SetupFirewallPolicy.Discovery) is not null)
                    throw new IOException("Existing fixture or foreign rule must be preserved.");
                var baseline = Baseline.Capture(fixtureId);
                rows.AddRange(baseline);
                if (baseline.Any(row => row.Status != "passed")) return rows;
                var marker = "LabControl disposable partial-install " + fixtureId;
                var foreign = SetupFirewallRule.Desired(SetupFirewallPolicy.Discovery) with
                { Enabled = false, Description = marker, Grouping = marker };
                var document = new Intent(1, fixtureId, foreign, new WindowsHostnameSystem().Read().Pending);
                using (var output = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(JsonSerializer.SerializeToUtf8Bytes(document)); output.Flush(true); }
                File.Move(path + ".tmp", path, false); // Durable exact ownership intent precedes fixture mutation.
                WithRules(rules =>
                {
                    if (native.Read(SetupFirewallPolicy.Discovery) is not null) throw new IOException();
                    dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID(Defaults.SetupFirewallRuleProgId, true)!)!;
                    try
                    {
                        rule.Name = foreign.Name; rule.Description = foreign.Description; rule.Grouping = foreign.Grouping;
                        rule.Protocol = foreign.Protocol; rule.LocalPorts = foreign.LocalPorts; rule.RemotePorts = foreign.RemotePorts;
                        rule.LocalAddresses = foreign.LocalAddresses; rule.RemoteAddresses = foreign.RemoteAddresses;
                        rule.Direction = foreign.Direction; rule.InterfaceTypes = foreign.InterfaceTypes;
                        rule.Enabled = false; rule.Profiles = foreign.Profiles; rule.EdgeTraversal = false;
                        rule.Action = foreign.Action; rule.EdgeTraversalOptions = foreign.EdgeTraversalOptions;
                        rules.Add(rule);
                    }
                    finally { Marshal.ReleaseComObject(rule); }
                });
                RequireRule(native, foreign);
            }
            else
            {
                if (new FileInfo(path).Length > 32768) throw new InvalidDataException();
                var document = JsonSerializer.Deserialize<Intent>(File.ReadAllText(path)) ?? throw new InvalidDataException();
                var marker = "LabControl disposable partial-install " + fixtureId;
                var expected = SetupFirewallRule.Desired(SetupFirewallPolicy.Discovery) with
                { Enabled = false, Description = marker, Grouping = marker };
                if (document.Schema != 1 || document.FixtureId != fixtureId || !expected.SameAs(document.Rule)) throw new InvalidDataException();
                RequireRule(native, expected);
                rows.AddRange(Baseline.Verify(fixtureId));
                if (rows.Any(row => row.Status != "passed")) return rows;
                if (action == "verify-partial")
                {
                    var state = new InstallationState(Defaults.AgentDataDirectory).Read();
                    if (state?.CreateStudentAccount != false || state.CreatedStudentSid is not null || !state.InstallDirectoryOwned
                        || WindowsSetupService.Exists() || !File.Exists(Path.Combine(Defaults.AgentDataDirectory, Defaults.AgentConfigFileName)))
                        throw new IOException();
                    SetupInstallationFiles.RequireOwnership(state.InstallationId);
                    if (!File.Exists(Path.Combine(Defaults.AgentInstallDirectory, Defaults.UninstallExecutableName))) throw new IOException();
                }
                else if (action is "verify-removed" or "cleanup")
                {
                    RequireUninstalled();
                    if (new WindowsHostnameSystem().Read().Pending != document.OriginalPendingHostname) throw new IOException();
                    if (action == "cleanup")
                    {
                        WithRules(rules => { RequireRule(native, expected); rules.Remove(expected.Name); });
                        if (native.Read(SetupFirewallPolicy.Discovery) is not null) throw new IOException();
                    }
                }
                else throw new ArgumentException();
            }
            rows.Add(new("partial-fixture-" + action, "passed", 0, null));
        }
        catch (Exception error) { rows.Add(new("partial-fixture-" + action, "failed", 0, error.GetType().Name + ":" + error.HResult.ToString("X8"))); }
        return rows;
    }
    private static void RequireUninstalled()
    {
        if (WindowsSetupService.Exists() || Directory.Exists(Defaults.AgentInstallDirectory) || Directory.Exists(Defaults.AgentDataDirectory))
            throw new IOException("Complete prior removal before this fixture.");
    }
    private static void RequireRule(WindowsSetupFirewallStore native, SetupFirewallRule expected)
    {
        var actual = native.Read(SetupFirewallPolicy.Discovery);
        if (actual is null || !expected.SameAs(actual)) throw new IOException("A changed or missing fixture rule was preserved.");
    }
    private static void WithRules(Action<dynamic> action)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID(Defaults.SetupFirewallPolicyProgId, true)!)!;
        dynamic rules = policy.Rules;
        try { action(rules); }
        finally { Marshal.ReleaseComObject(rules); Marshal.ReleaseComObject(policy); }
    }
    private static void CheckFile(string path)
    {
        HandoutDelivery.RejectReparseAncestors(path);
        if (Directory.Exists(path)) throw new IOException();
        if (File.Exists(path)) CheckAcl(new FileInfo(path).GetAccessControl());
    }
    private static void CheckAcl(FileSystemSecurity security)
    {
        static bool Trusted(IdentityReference sid) => sid is SecurityIdentifier value &&
            (value.IsWellKnown(WellKnownSidType.LocalSystemSid) || value.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier))!)) throw new UnauthorizedAccessException();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType != AccessControlType.Allow || !Trusted(rule.IdentityReference)) throw new UnauthorizedAccessException();
    }
    private sealed record Intent(int Schema, string FixtureId, SetupFirewallRule Rule, string OriginalPendingHostname);
}
