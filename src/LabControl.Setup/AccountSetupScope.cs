using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using LabControl.Shared;
using LabControl.Shared.Setup;

namespace LabControl.Setup;

/// <summary>Entry point for the future Setup pipeline's account preparation step.
/// The scope owns the lock through intent, SAM creation and durable SID recording.</summary>
internal sealed class AccountSetupScope : IDisposable
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private readonly FileStream _lease;
    private bool _disposed;

    private AccountSetupScope(FileStream lease) => _lease = lease;

    public static AccountSetupScope Open()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Account setup requires an elevated administrator.");

        var directory = new DirectoryInfo(Defaults.AgentDataDirectory);
        // Refuse redirects before any ACL or file write. Parent directories are owned by
        // Windows; the private child is created with its final ACL in the creation call.
        for (var ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
            RejectReparsePoint(ancestor.FullName);

        var security = new DirectorySecurity();
        security.SetOwner(AdministratorsSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { SystemSid, AdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));

        // Do not repair a permissive existing directory and then trust its old journal:
        // somebody may already have supplied false ownership evidence (D-46).
        if (Attributes(directory.FullName) is null) directory.Create(security);
        RejectReparsePoint(directory.FullName);
        RequirePrivate(directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), directory: true);

        var lockPath = Path.Combine(directory.FullName, Defaults.SetupLockFileName);
        CheckExistingFile(lockPath);
        var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            CheckExistingFile(lockPath);
            var journal = Path.Combine(directory.FullName, Defaults.InstallationFileName);
            CheckExistingFile(journal);
            CheckExistingFile(journal + ".tmp");
            var settings = Path.Combine(directory.FullName, Defaults.SetupSettingsFileName);
            CheckExistingFile(settings);
            CheckExistingFile(settings + ".tmp");
            CheckExistingFile(Path.Combine(directory.FullName, Defaults.ProfileTemplatePlanFileName));
            CheckExistingFile(Path.Combine(directory.FullName, Defaults.ProfileTemplatePlanFileName) + ".tmp");
            foreach (var name in new[] { Defaults.SetupReadinessFileName, Defaults.TrustRekeyFileName, Defaults.AgentConfigFileName,
                         Defaults.AgentKeyFileName, Defaults.CaCertificateFileName, Defaults.AgentCertificateFileName })
            {
                CheckExistingFile(Path.Combine(directory.FullName, name));
                CheckExistingFile(Path.Combine(directory.FullName, name + ".tmp"));
            }
            return new AccountSetupScope(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void SaveReadinessWarnings(IEnumerable<string> codes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetupReadiness.Save(Defaults.AgentDataDirectory, codes);
    }

    public StudentAccountAction Prepare(bool existingInstallation, bool? createStudentAccount = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new StudentAccountProvisioning(new InstallationState(Defaults.AgentDataDirectory),
            new WindowsStudentAccountSystem(), ApplyStudentPasswordPolicy).Prepare(existingInstallation, createStudentAccount);
    }

    private void ApplyStudentPasswordPolicy()
    {
        var state = new InstallationState(Defaults.AgentDataDirectory).Read();
        if (state?.CreateStudentAccount != true || state.CreatedStudentSid is not null || !state.StudentCreationPending)
            throw new InvalidOperationException("Password-policy fallback requires a pending new student account.");
        var result = new StudentPasswordPolicy(new WindowsPasswordPolicyStore()).Apply(SettingsJournal());
        if (result == SettingChangeResult.Conflict) throw new InvalidOperationException("A later or interrupted password-policy change was preserved.");
    }

    public SettingChangeResult? RestoreStudentPasswordPolicy()
    {
        if (new InstallationState(Defaults.AgentDataDirectory).Read()?.CreateStudentAccount != true) return null;
        return new StudentPasswordPolicy(new WindowsPasswordPolicyStore()).Restore(SettingsJournal());
    }

    /// <summary>Future pipeline: call once on a positively identified fresh installation,
    /// before changing settings. Never use to replace missing repair/removal history.</summary>
    public void InitializeSettingsJournal() => SettingsJournal().InitializeNew();

    public void EnsureFreshSettingsJournal()
    {
        var state = new InstallationState(Defaults.AgentDataDirectory);
        var document = state.Read() ?? throw new InvalidOperationException("Installation identity is missing.");
        var journal = SettingsJournal();
        if (document.SettingsInitializationPending)
        {
            if (!File.Exists(journal.FilePath)) journal.InitializeNew();
            journal.Validate();
            state.CompleteSettingsInitialization();
        }
        else journal.Validate();
    }

    public IReadOnlyList<string> PendingRestorationIds() => SettingsJournal().PendingRestorationIds();

    public SetupCheck CheckSetting(ISetupSetting setting, Func<byte[]?, byte[]?> desired) => SettingsJournal().Check(setting, desired);

    public SettingChangeResult ApplySetting(ISetupSetting setting, byte[]? desired) =>
        SettingsJournal().Apply(setting, desired);

    public SettingChangeResult RestoreSetting(ISetupSetting setting) => SettingsJournal().Restore(setting);

    public SetupCheck CheckMachineRegistryPolicy(MachineRegistryPolicy policy) =>
        new MachineRegistrySetting(policy, new WindowsMachineRegistryStore()).Check(SettingsJournal());
    public SetupCheck CheckPowerPlanPolicy(PowerPlanPolicy policy) =>
        new PowerPlanSetting(policy, new WindowsPowerPlanSystem()).Check(SettingsJournal());
    public SetupCheck CheckUpdateActiveHours() => new UpdateActiveHoursSetting(new WindowsUpdateActiveHoursStore()).Check(SettingsJournal());
    public SetupCheck CheckFirewallRule(SetupFirewallPolicy policy) => new SetupFirewallSetting(policy, new WindowsSetupFirewallStore()).Check(SettingsJournal());
    public SetupCheck CheckHostname(string name) => new HostnameSetting(new WindowsHostnameSystem()).Check(SettingsJournal(), name);

    public SettingChangeResult ApplyMachineRegistryPolicy(MachineRegistryPolicy policy)
    {
        var journal = SettingsJournal();
        var setting = new MachineRegistrySetting(policy, new WindowsMachineRegistryStore());
        return setting.Apply(journal);
    }

    public SettingChangeResult RestoreMachineRegistryPolicy(MachineRegistryPolicy policy) =>
        SettingsJournal().Restore(new MachineRegistrySetting(policy, new WindowsMachineRegistryStore()));

    public SettingChangeResult ApplyPowerPlanPolicy(PowerPlanPolicy policy) =>
        new PowerPlanSetting(policy, new WindowsPowerPlanSystem()).Apply(SettingsJournal());

    public SettingChangeResult RestorePowerPlanPolicy(PowerPlanPolicy policy) =>
        SettingsJournal().Restore(new PowerPlanSetting(policy, new WindowsPowerPlanSystem()));

    public SettingChangeResult ApplyHostname(string name) =>
        new HostnameSetting(new WindowsHostnameSystem()).Apply(SettingsJournal(), name);
    public SettingChangeResult RestoreHostname() =>
        SettingsJournal().Restore(new HostnameSetting(new WindowsHostnameSystem()));
    public HostnameState HostnameStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new WindowsHostnameSystem().Read();
    }
    public SettingChangeResult ApplyFirewallRule(SetupFirewallPolicy policy) =>
        new SetupFirewallSetting(policy, new WindowsSetupFirewallStore()).Apply(SettingsJournal());
    public SettingChangeResult RestoreFirewallRule(SetupFirewallPolicy policy) =>
        SettingsJournal().Restore(new SetupFirewallSetting(policy, new WindowsSetupFirewallStore()));

    public SettingChangeResult ApplyUpdateActiveHours() =>
        new UpdateActiveHoursSetting(new WindowsUpdateActiveHoursStore()).Apply(SettingsJournal());

    public SettingChangeResult ApplyDefenderExclusion() =>
        new DefenderExclusionSetting(new WindowsDefenderExclusionStore()).Apply(SettingsJournal());
    public SetupCheck CheckDefenderExclusion(bool thirdPartyAntivirusPresent)
    {
        try { return CheckDefenderExclusion(); }
        catch (DefenderProviderUnavailableException) when (thirdPartyAntivirusPresent
            && !PendingRestorationIds().Contains("machine.defender-exclusion", StringComparer.Ordinal))
        {
            return new(SetupStepStatus.Skipped,
                "Defender is unavailable and another antivirus is registered. Configure that product's LabControl exclusion manually.");
        }
    }

    public SetupCheck CheckDefenderExclusion() =>
        new DefenderExclusionSetting(new WindowsDefenderExclusionStore()).Check(SettingsJournal());

    public SettingChangeResult RestoreDefenderExclusion() =>
        SettingsJournal().Restore(new DefenderExclusionSetting(new WindowsDefenderExclusionStore()));

    public SettingChangeResult ApplyHibernation() =>
        new HibernationSetting(new WindowsHibernationSystem()).Apply(SettingsJournal());
    public SetupCheck CheckHibernation() =>
        new HibernationSetting(new WindowsHibernationSystem()).Check(SettingsJournal());

    public SettingChangeResult RestoreHibernation() =>
        SettingsJournal().Restore(new HibernationSetting(new WindowsHibernationSystem()));

    public SettingChangeResult RestoreUpdateActiveHours() =>
        SettingsJournal().Restore(new UpdateActiveHoursSetting(new WindowsUpdateActiveHoursStore()));

    // Null means account mode is off: no SAM, LSA or settings-journal access.
    public SettingChangeResult? ApplyStudentSignInSecret() => SignInSecret().Apply();
    public SettingChangeResult? RestoreStudentSignInSecret() => SignInSecret().Restore();

    public SettingChangeResult? ApplyStudentSessionPolicy(StudentSessionPolicy policy) => SessionSettings().Apply(policy);
    public SettingChangeResult? RestoreStudentSessionPolicy(StudentSessionPolicy policy) => SessionSettings().Restore(policy);
    public SettingChangeResult? HideSetupAdministrator() => AdministratorVisibility().Apply();
    public SettingChangeResult? RestoreSetupAdministratorVisibility() => AdministratorVisibility(restoring: true).Restore();

    private StudentSessionSettings SessionSettings()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = new InstallationState(Defaults.AgentDataDirectory);
        var accounts = new WindowsStudentAccountSystem();
        return new(state, accounts.FindStudentSid, SettingsJournal,
            new WindowsStudentSessionPolicyStore(() => state.RequireManagedStudent(accounts.FindStudentSid())));
    }
    private AdministratorVisibility AdministratorVisibility(bool restoring = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = new InstallationState(Defaults.AgentDataDirectory);
        var accounts = new WindowsStudentAccountSystem();
        void RequireAuthorization()
        {
            if (!restoring) { state.RequireManagedStudent(accounts.FindStudentSid()); return; }
            var document = state.Read();
            if (document?.CreateStudentAccount != true || document.CreatedStudentSid is null
                || document.HiddenAdministratorSid is null)
                throw new InvalidOperationException("Recorded administrator visibility ownership is required.");
        }
        return new(state, accounts.FindStudentSid, SettingsJournal,
            new WindowsAdministratorVisibilityStore(RequireAuthorization, restoring));
    }

    public IReadOnlyList<NicIdentity> EthernetAdapters()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new WindowsNicSettingsSystem().ListEthernetAdapters();
    }
    public SetupCheck CheckNicPolicy(Guid interfaceId, NicPolicy policy) =>
        new NicSetting(interfaceId, policy, new WindowsNicSettingsSystem()).Check(SettingsJournal());
    public IReadOnlyList<NicRestoreResult> RestoreRecordedNicSettings()
    {
        var journal = SettingsJournal();
        var results = new List<NicRestoreResult>();
        var recorded = new List<(Guid InterfaceId, NicPolicy Policy)>();
        foreach (var id in journal.SettingIds().Where(id => id.StartsWith("nic.", StringComparison.Ordinal)))
        {
            var parts = id.Split('.');
            if (parts.Length != 3 || !Guid.TryParseExact(parts[1], "N", out var interfaceId) || interfaceId == Guid.Empty)
                throw new InvalidDataException("A saved NIC setting identifier is invalid.");
            var matching = Enum.GetValues<NicPolicy>().Where(policy => new NicSetting(interfaceId, policy,
                new WindowsNicSettingsSystem()).Id == id).ToArray();
            if (matching.Length != 1) throw new InvalidDataException("A saved NIC setting policy is unsupported.");
            recorded.Add((interfaceId, matching[0]));
        }
        // Earlier journals used enum insertion order. Restore by dependency, not by
        // the order that a particular installer happened to write its entries.
        var order = NicSetting.ApplyOrder.ToList();
        foreach (var (interfaceId, policy) in recorded.OrderByDescending(item => order.IndexOf(item.Policy)))
        {
            SettingChangeResult result;
            try { result = journal.Restore(new NicSetting(interfaceId, policy, new WindowsNicSettingsSystem())); }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
            { result = SettingChangeResult.Conflict; }
            results.Add(new(interfaceId, policy, result));
        }
        return results;
    }
    public SettingChangeResult ApplyNicPolicy(Guid interfaceId, NicPolicy policy) =>
        new NicSetting(interfaceId, policy, new WindowsNicSettingsSystem()).Apply(SettingsJournal());
    public SettingChangeResult RestoreNicPolicy(Guid interfaceId, NicPolicy policy) =>
        SettingsJournal().Restore(new NicSetting(interfaceId, policy, new WindowsNicSettingsSystem()));

    public bool PrepareStudentMembership() => AccountActivation().PrepareMembership();
    public bool ActivateStudentAfterSuccessfulSetup() => AccountActivation().ActivateAfterSuccessfulSetup();

    private StudentAccountActivation AccountActivation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(new InstallationState(Defaults.AgentDataDirectory), new WindowsStudentAccountActivationSystem());
    }

    public SettingChangeResult? ApplyStudentSignIn(string? plannedMachineName = null) => SignIn(plannedMachineName).Apply();
    public SettingChangeResult? RestoreStudentSignIn() => SignIn().Restore();

    private StudentSignIn SignIn(string? plannedMachineName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = new InstallationState(Defaults.AgentDataDirectory);
        var accounts = new WindowsStudentAccountSystem();
        return new StudentSignIn(state, accounts.FindStudentSid, SettingsJournal,
            new WindowsStudentSignInStore(() => state.RequireManagedStudent(accounts.FindStudentSid())),
            plannedMachineName ?? Environment.MachineName,
            () => System.Console.WriteLine(SetupDialog.Text("SignInSidBaselineUnknown")));
    }

    public IReadOnlyList<SettingChangeResult> ApplyProfileTemplate(string archivePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = new InstallationState(Defaults.AgentDataDirectory);
        if (state.Read()?.CreateStudentAccount != true) return [];
        using var archive = File.OpenRead(archivePath);
        return ProfileTemplate(state).Apply(archive);
    }

    public IReadOnlyList<SettingChangeResult> RestoreProfileTemplate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ProfileTemplate(new InstallationState(Defaults.AgentDataDirectory)).Restore();
    }

    private DefaultProfileTemplate ProfileTemplate(InstallationState state) => new(state,
        new WindowsStudentAccountSystem().FindStudentSid, SettingsJournal, new WindowsDefaultProfileStore(), Defaults.AgentDataDirectory);

    private StudentSignInSecret SignInSecret()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = new InstallationState(Defaults.AgentDataDirectory);
        var accounts = new WindowsStudentAccountSystem();
        return new StudentSignInSecret(state, accounts.FindStudentSid, SettingsJournal,
            new WindowsStudentSignInSecretStore(() => state.RequireManagedStudent(accounts.FindStudentSid())));
    }

    private SetupSettingsJournal SettingsJournal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = new InstallationState(Defaults.AgentDataDirectory).Read()
            ?? throw new InvalidOperationException("Installation history is required before settings can be changed.");
        var entropy = Encoding.UTF8.GetBytes("labcontrol/setup-settings/" + state.InstallationId);
        return new SetupSettingsJournal(Defaults.AgentDataDirectory, state.InstallationId,
            bytes => ProtectedData.Protect(bytes, entropy, DataProtectionScope.LocalMachine),
            bytes => ProtectedData.Unprotect(bytes, entropy, DataProtectionScope.LocalMachine));
    }

    private static void CheckExistingFile(string path)
    {
        var attributes = Attributes(path);
        if (attributes is null) return;
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Setup ownership files must be regular files, not directories or reparse points.");
        RequirePrivate(new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
    }

    private static void RequirePrivate(FileSystemSecurity security, bool directory = false)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!IsTrusted(owner)) throw new UnauthorizedAccessException("Setup ownership storage must be owned by SYSTEM or Administrators.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        var fullControl = new HashSet<IdentityReference>();
        var inheritedFullControl = new HashSet<IdentityReference>();
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Deny || !IsTrusted(rule.IdentityReference))
                throw new UnauthorizedAccessException("Setup ownership storage must grant access only to SYSTEM and Administrators. Existing unprotected history cannot establish account ownership.");
            if (rule.PropagationFlags != PropagationFlags.None) continue;
            if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
            {
                fullControl.Add(rule.IdentityReference);
                if ((rule.InheritanceFlags & (InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit))
                    == (InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit))
                    inheritedFullControl.Add(rule.IdentityReference);
            }
        }
        if (!fullControl.Contains(SystemSid) || !fullControl.Contains(AdministratorsSid))
            throw new UnauthorizedAccessException("Setup ownership storage requires SYSTEM and Administrators full control.");
        if (directory && (!inheritedFullControl.Contains(SystemSid) || !inheritedFullControl.Contains(AdministratorsSid)))
            throw new UnauthorizedAccessException("Setup ownership storage must pass its private permissions to new files and directories.");
    }

    private static bool IsTrusted(IdentityReference? sid) => SystemSid.Equals(sid) || AdministratorsSid.Equals(sid);

    private static void RejectReparsePoint(string path)
    {
        if (Attributes(path) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Setup ownership storage cannot use a reparse point.");
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lease.Dispose();
        // Keep the empty lock file: deleting it can race another opener.
    }
}
