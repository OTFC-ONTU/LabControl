using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using LabControl.Setup;
using LabControl.Shared.Setup;

internal static class RoundTrips
{
    // Test-only storage, never the production installation directory.
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabControl.NativeSmoke.Roundtrip");
    private const string JournalIdentity = "ce46c5c5-5931-42fb-85fa-846d0a63fb19";

    public static IReadOnlyList<ProbeResult> Run(bool restoreOnly)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException();
        var directory = new DirectoryInfo(DirectoryPath);
        if (!directory.Exists)
        {
            if (restoreOnly) throw new DirectoryNotFoundException();
            var security = new DirectorySecurity();
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            security.SetAccessRuleProtection(true, false);
            foreach (var kind in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(kind, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.Create(security);
        }
        LabControl.Shared.Files.HandoutDelivery.RejectReparseAncestors(DirectoryPath);
        var acl = directory.GetAccessControl();
        static bool Trusted(IdentityReference sid) => sid.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))
            || sid.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        if (!Trusted(acl.GetOwner(typeof(SecurityIdentifier))!) || !acl.AreAccessRulesProtected
            || acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(rule => rule.AccessControlType == AccessControlType.Allow && !Trusted(rule.IdentityReference)))
            throw new UnauthorizedAccessException();
        using var exclusive = new FileStream(Path.Combine(DirectoryPath, "probe.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var journal = new SetupSettingsJournal(DirectoryPath, JournalIdentity,
            bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine),
            bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine));
        if (restoreOnly) journal.Validate();
        else journal.InitializeNew(); // Existing history must be recovered explicitly, never overwritten.
        var rows = new List<ProbeResult>();
        foreach (var policy in Enum.GetValues<MachineRegistryPolicy>())
        {
            var setting = new MachineRegistrySetting(policy, new WindowsMachineRegistryStore());
            RunOne(setting, () => setting.Apply(journal));
        }
        foreach (var policy in Enum.GetValues<PowerPlanPolicy>())
        {
            var setting = new PowerPlanSetting(policy, new WindowsPowerPlanSystem());
            RunOne(setting, () => setting.Apply(journal));
        }
        var hours = new UpdateActiveHoursSetting(new WindowsUpdateActiveHoursStore());
        RunOne(hours, () => hours.Apply(journal));
        var hibernation = new HibernationSetting(new WindowsHibernationSystem());
        RunOne(hibernation, () => hibernation.Apply(journal));
        foreach (var policy in Enum.GetValues<SetupFirewallPolicy>())
        {
            var setting = new SetupFirewallSetting(policy, new WindowsSetupFirewallStore());
            RunOne(setting, () => setting.Apply(journal));
        }
        return rows;

        void RunOne(ISetupSetting setting, Func<SettingChangeResult> apply)
        {
            byte[]? original = null;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var phase = "read";
            var changed = false;
            try
            {
                if (!restoreOnly)
                {
                    original = setting.Read();
                    phase = "apply";
                    var result = apply();
                    changed = result is SettingChangeResult.Applied or SettingChangeResult.AlreadyApplied;
                    if (result == SettingChangeResult.Conflict) throw new IOException();
                }
                phase = "restore";
                if (journal.Restore(setting) == SettingChangeResult.Conflict) throw new IOException();
                phase = "verify";
                var final = setting.Read();
                if (!restoreOnly && !(original is null ? final is null : final is not null && original.AsSpan().SequenceEqual(final)))
                    throw new IOException();
                rows.Add(new(setting.Id + (restoreOnly ? ".restore-only" : changed ? ".roundtrip" : ".unchanged"), "passed", timer.ElapsedMilliseconds, null));
            }
            catch (Exception error)
            {
                rows.Add(new(setting.Id + "." + phase, "failed", timer.ElapsedMilliseconds, error.GetType().Name + ":" + error.HResult.ToString("X8")));
                // The protected pending record survives. Never overwrite an ambiguous native state.
                try { if (journal.Restore(setting) == SettingChangeResult.Conflict) rows.Add(new(setting.Id + ".recovery", "conflict", 0, null)); }
                catch (Exception recovery) { rows.Add(new(setting.Id + ".recovery", "failed", 0, recovery.GetType().Name + ":" + recovery.HResult.ToString("X8"))); }
            }
            finally { if (original is not null) CryptographicOperations.ZeroMemory(original); }
        }
    }
}
