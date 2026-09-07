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
            return new AccountSetupScope(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public StudentAccountAction Prepare(bool existingInstallation, bool? createStudentAccount = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new StudentAccountProvisioning(new InstallationState(Defaults.AgentDataDirectory),
            new WindowsStudentAccountSystem()).Prepare(existingInstallation, createStudentAccount);
    }

    /// <summary>Future pipeline: call once on a positively identified fresh installation,
    /// before changing settings. Never use to replace missing repair/removal history.</summary>
    public void InitializeSettingsJournal() => SettingsJournal().InitializeNew();

    public SettingChangeResult ApplySetting(ISetupSetting setting, byte[]? desired) =>
        SettingsJournal().Apply(setting, desired);

    public SettingChangeResult RestoreSetting(ISetupSetting setting) => SettingsJournal().Restore(setting);

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
