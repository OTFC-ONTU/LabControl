using LabControl.Shared;
using LabControl.Shared.Setup;

namespace LabControl.Setup;

internal static class UninstallCoordinator
{
    public static int Run(SetupArguments arguments)
    {
        if (arguments.DryRun)
        {
            System.Console.WriteLine("[plan] Stop the owned service; restore recorded settings without overwriting later edits; keep personal accounts and profiles; remove owned LabControl files.");
            return 0;
        }
        if (!SetupDialog.Confirm("Uninstall")) return 2;
        if (arguments.RemoveStudent && !arguments.ConfirmRemoveStudent && !SetupDialog.Confirm("RemoveStudent")) return 2;
        using var scope = AccountSetupScope.Open();
        var state = new InstallationState(Defaults.AgentDataDirectory);
        var document = state.Read() ?? throw new InvalidOperationException("Removal ownership history is missing. Existing accounts and settings were preserved.");
        if (!document.InstallDirectoryOwned && Directory.Exists(Defaults.AgentInstallDirectory)) throw new InvalidOperationException("The installed directory is not proven to belong to this installation.");
        if (document.RemovalReady)
        {
            UninstallCleanup.Start(document.InstallationId);
            System.Console.WriteLine("Previously authorized removal cleanup has been restarted.");
            return 0;
        }
        using var updateLease = new UpdateTrial(Defaults.AgentDataDirectory).AcquireRemovalLease(out var terminalUpdate);
        var service = new WindowsSetupService(document.InstallationId);
        if (WindowsSetupService.Exists())
        {
            _ = service.Read(); // Refuse a renamed/replaced service before stopping it.
            WindowsSetupService.Stop();
        }
        var failed = false;
        void Restore(string name, Func<SettingChangeResult?> operation)
        {
            try
            {
                var result = operation();
                if (result == SettingChangeResult.Conflict) failed = true;
                System.Console.WriteLine($"[{result?.ToString() ?? "Skipped"}] {name}");
            }
            catch (Exception)
            {
                failed = true;
                System.Console.Error.WriteLine($"[Conflict] {name}: could not safely restore; the protected history was kept.");
            }
        }
        var pendingAccount = scope.PendingRestorationIds();
        if (document.CreateStudentAccount == true)
        {
            if (pendingAccount.Contains("student.password-policy", StringComparer.Ordinal))
                Restore("Student password policy", scope.RestoreStudentPasswordPolicy);
            if (pendingAccount.Contains("student.admin-visibility", StringComparer.Ordinal)
                || pendingAccount.Contains("student.admin-credential-prompt", StringComparer.Ordinal))
                Restore("Administrator sign-in visibility", scope.RestoreSetupAdministratorVisibility);
            foreach (var policy in Enum.GetValues<StudentSessionPolicy>())
            {
                var id = policy switch
                {
                    StudentSessionPolicy.PrivacyExperience => "student.session-privacy",
                    StudentSessionPolicy.EdgeFirstRun => "student.session-edge",
                    StudentSessionPolicy.OneDriveSignInNotifications => "student.session-onedrive-notifications",
                    _ => throw new ArgumentOutOfRangeException(nameof(policy)),
                };
                if (pendingAccount.Contains(id, StringComparer.Ordinal))
                    Restore("Session policy " + policy, () => scope.RestoreStudentSessionPolicy(policy));
            }
        }
        else if (pendingAccount.Any(id => id.StartsWith("student.", StringComparison.Ordinal)))
            System.Console.WriteLine("[Skipped] Account and sign-in changes retained because managed-account mode is off.");
        if (document.CreateStudentAccount == true)
        {
            foreach (var result in scope.RestoreProfileTemplate())
            {
                System.Console.WriteLine($"[{result}] Default profile template");
                if (result == SettingChangeResult.Conflict) failed = true;
            }
        }
        foreach (var nic in scope.RestoreRecordedNicSettings())
        {
            System.Console.WriteLine($"[{nic.Result}] Network {nic.InterfaceId} {nic.Policy}");
            if (nic.Result == SettingChangeResult.Conflict) failed = true;
        }
        // Account gates must not run for an absent/already-restored setting. This
        // also lets cleanup resume after deleting the owned account or after an
        // early account-name collision that never configured sign-in.
        if (document.CreateStudentAccount == true && (pendingAccount.Contains("student.autologon-tuple", StringComparer.Ordinal)
            || pendingAccount.Contains("student.autologon-sid", StringComparer.Ordinal)))
            Restore("Student automatic sign-in", scope.RestoreStudentSignIn);
        foreach (var policy in Enum.GetValues<SetupFirewallPolicy>()) Restore("Firewall " + policy, () => scope.RestoreFirewallRule(policy));
        Restore("Defender exclusion", () => scope.RestoreDefenderExclusion());
        Restore("Windows Update active hours", () => scope.RestoreUpdateActiveHours());
        foreach (var policy in Enum.GetValues<PowerPlanPolicy>()) Restore("AC power " + policy, () => scope.RestorePowerPlanPolicy(policy));
        Restore("Hibernation", () => scope.RestoreHibernation());
        foreach (var policy in Enum.GetValues<MachineRegistryPolicy>()) Restore("Machine policy " + policy, () => scope.RestoreMachineRegistryPolicy(policy));
        Restore("PC name", () => scope.RestoreHostname());
        Restore("Service", () => scope.RestoreSetting(service));
        var registration = new WindowsUninstallEntry(document.InstallationId);
        bool NeedsRestoration(string id) => id != registration.Id
            && (document.CreateStudentAccount == true || !id.StartsWith("student.", StringComparison.Ordinal));
        var pending = scope.PendingRestorationIds().Where(NeedsRestoration).ToArray();
        if (pending.Length > 0)
        {
            failed = true;
            System.Console.Error.WriteLine("Some settings still require restoration; removal history and the standalone uninstaller were retained.");
        }
        if (failed || WindowsSetupService.Exists()) return 1;
        UpdateRecoveryTaskCleanup.RemoveRecovery(terminalUpdate);
        if ((arguments.RemoveStudent || document.StudentRemovalPending) && document.CreateStudentAccount == true)
        {
            if (document.StudentRemovalPending) System.Console.WriteLine(SetupDialog.Text("StudentRemovalResuming"));
            WindowsStudentRemoval.Remove(state);
        }
        if (failed || scope.PendingRestorationIds().Any(NeedsRestoration)) return 1;
        // Keep a discoverable entry until the external worker has removed every
        // critical file. It will point this entry at its protected retry executable.
        SetupInstallationFiles.RemoveOwnedStaging(document.InstallationId);
        state.MarkRemovalReady();
        UninstallCleanup.Start(document.InstallationId);
        System.Console.WriteLine("Removal cleanup will finish after this process closes. Reboot will be required for temporary cleanup.");
        return 0;
    }
}
