using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using LabControl.Setup;
using LabControl.Shared;
using LabControl.Shared.Setup;

try
{
    if (args is ["--uac-inert"]) return 0; // Deliberately inert elevation target; no state or secrets are accessed.
    if (args is ["--uac-prompt", var studentSid]) return StudentUacFixture.Prompt(studentSid);
    if (args is ["--uac-disposable-vm", var installationId]) return StudentUacFixture.Schedule(installationId);
    if (args is ["--uac-cleanup", var fixtureId]) return StudentUacFixture.Cleanup(fixtureId);
}
catch (Exception error)
{
    System.Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error_type = error.GetType().Name }));
    return 1;
}
if (args.Length == 2 && args[0] is "--partial-create" or "--partial-verify-partial" or "--partial-verify-removed" or "--partial-cleanup")
{
    var results = PartialInstallFixture.Run(args[0]["--partial-".Length..], args[1]);
    System.Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    return results.Any(row => row.Status != "passed") ? 1 : 0;
}
if (args.Length is 1 or 2 && args[0] is "--capture-baseline" or "--verify-baseline")
{
    var results = args.Length == 2
        ? args[0] == "--capture-baseline" ? Baseline.Capture(args[1]) : Baseline.Verify(args[1])
        : args[0] == "--capture-baseline" ? Baseline.Capture() : Baseline.Verify();
    System.Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    return results.Any(row => row.Status != "passed") ? 1 : 0;
}
if (args.Length == 1 && args[0] is "--roundtrip-isolated-clone" or "--restore-roundtrip")
{
    var results = RoundTrips.Run(args[0] == "--restore-roundtrip");
    System.Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    return results.Any(row => row.Status != "passed") ? 1 : 0;
}
if (args.Length != 1 || args[0] != "--read-only")
{
    System.Console.Error.WriteLine("Usage: LabControl.Setup.NativeSmoke.exe --read-only");
    return 2;
}
if (!OperatingSystem.IsWindows()) return 2;
var rows = new List<ProbeResult>();
Probe("elevated-identity", () =>
{
    using var identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException();
});
Probe("dpapi-machine-roundtrip", () =>
{
    var plain = RandomNumberGenerator.GetBytes(32);
    var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine);
    var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
    try { if (!CryptographicOperations.FixedTimeEquals(plain, decrypted)) throw new CryptographicException(); }
    finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(encrypted); CryptographicOperations.ZeroMemory(decrypted); }
});
Probe("hostname-read", () => _ = new WindowsHostnameSystem().Read());
foreach (var policy in Enum.GetValues<MachineRegistryPolicy>())
    Probe("registry-" + policy, () => _ = new WindowsMachineRegistryStore().Read(policy));
foreach (var policy in Enum.GetValues<PowerPlanPolicy>())
    Probe("ac-power-" + policy, () => _ = new PowerPlanSetting(policy, new WindowsPowerPlanSystem()).Read());
Probe("active-hours-read", () => _ = new WindowsUpdateActiveHoursStore().Read());
Probe("hibernation-read", () => _ = new WindowsHibernationSystem().Read());
Probe("defender-read", () => _ = new WindowsDefenderExclusionStore().Read());
foreach (var policy in Enum.GetValues<SetupFirewallPolicy>())
    Probe("firewall-" + policy, () => _ = new WindowsSetupFirewallStore().Read(policy));
Probe("default-profile-desktop-read", () => _ = new WindowsDefaultProfileStore().DirectoryExists("Desktop"));
Probe("default-profile-documents-read", () => _ = new WindowsDefaultProfileStore().DirectoryExists("Documents"));
Probe("student-sam-read", () => _ = new WindowsStudentAccountSystem().FindStudentSid());
var nics = new WindowsNicSettingsSystem();
IReadOnlyList<NicIdentity> adapters = [];
Probe("physical-ethernet-enumeration", () => adapters = nics.ListEthernetAdapters());
for (var index = 0; index < adapters.Count; index++)
{
    var adapter = adapters[index];
    foreach (var policy in Enum.GetValues<NicPolicy>())
        Probe("nic-" + index + "-" + policy, () => _ = nics.Read(adapter.InterfaceId, policy));
}
rows.Add(new("account-signin-mutations", "deferred", 0, null));
System.Console.WriteLine(JsonSerializer.Serialize(new
{
    schemaVersion = 1,
    mode = "read-only",
    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
    probes = rows,
}, new JsonSerializerOptions { WriteIndented = true }));
return rows.Any(row => row.Status == "failed") ? 1 : 0;

void Probe(string id, Action action)
{
    var timer = Stopwatch.StartNew();
    try { action(); rows.Add(new(id, "passed", timer.ElapsedMilliseconds, null)); }
    catch (NicSettingUnavailableException) { rows.Add(new(id, "unsupported", timer.ElapsedMilliseconds, null)); }
    catch (Exception error)
    {
        // Never serialize values, exception messages, paths, identities or inner errors:
        // future probes may touch protected settings. Type/HRESULT suffice for triage.
        rows.Add(new(id, "failed", timer.ElapsedMilliseconds, error.GetType().Name + ":" + error.HResult.ToString("X8")));
    }
}
internal sealed record ProbeResult(string Id, string Status, long Milliseconds, string? ErrorCode);
