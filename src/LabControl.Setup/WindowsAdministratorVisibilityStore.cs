using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.NetManagement;

namespace LabControl.Setup;

internal sealed class WindowsAdministratorVisibilityStore(Action requireAuthorization, bool restoring = false) : IAdministratorVisibilityStore
{
    public string CurrentLocalAdministratorSid()
    {
        requireAuthorization();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || identity.User is null)
            throw new UnauthorizedAccessException("The current setup identity is not an elevated administrator.");
        var sid = identity.User.Value;
        Resolve(sid);
        return sid;
    }
    public uint? ReadCredentialPrompt(string administratorSid)
    {
        requireAuthorization();
        var name = Resolve(administratorSid);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(Defaults.AdministratorCredentialRegistryKey);
        var value = WindowsStudentSessionPolicyStore.ReadValue(key, Defaults.AdministratorCredentialRegistryValue);
        if (Resolve(administratorSid) != name) throw new IOException("The recorded administrator identity changed.");
        return value;
    }
    public void WriteCredentialPrompt(string administratorSid, uint? expected, uint? desired)
    {
        requireAuthorization();
        var name = Resolve(administratorSid);
        if (ReadCredentialPrompt(administratorSid) != expected) throw new IOException("Administrator credential prompting changed.");
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var existing = hive.OpenSubKey(Defaults.AdministratorCredentialRegistryKey, writable: true);
        if (WindowsStudentSessionPolicyStore.ReadValue(existing, Defaults.AdministratorCredentialRegistryValue) != expected) throw new IOException();
        using var created = existing is null ? hive.CreateSubKey(Defaults.AdministratorCredentialRegistryKey) : null;
        var key = existing ?? created ?? throw new IOException();
        requireAuthorization();
        if (Resolve(administratorSid) != name || WindowsStudentSessionPolicyStore.ReadValue(key, Defaults.AdministratorCredentialRegistryValue) != expected)
            throw new IOException("Administrator credential prompting changed before mutation.");
        if (desired is null) key.DeleteValue(Defaults.AdministratorCredentialRegistryValue, throwOnMissingValue: false);
        else key.SetValue(Defaults.AdministratorCredentialRegistryValue, unchecked((int)desired.Value), RegistryValueKind.DWord);
        key.Flush();
        if (ReadCredentialPrompt(administratorSid) != desired) throw new IOException("Administrator credential prompting could not be verified.");
    }

    public AdministratorVisibilitySnapshot Read(string sid)
    {
        requireAuthorization();
        var name = Resolve(sid);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var winlogon = hive.OpenSubKey(Defaults.WinlogonRegistryKey) ?? throw new IOException("The Winlogon key is missing.");
        using var key = winlogon.OpenSubKey(Defaults.AdministratorVisibilitySubKey);
        var value = WindowsStudentSessionPolicyStore.ReadValue(key, name);
        if (Resolve(sid) != name) throw new IOException("The local administrator identity changed.");
        return new(name, value);
    }
    public void Write(string sid, AdministratorVisibilitySnapshot expected, AdministratorVisibilitySnapshot desired)
    {
        requireAuthorization();
        if (desired.AccountName != expected.AccountName || Read(sid) != expected)
            throw new IOException("The administrator visibility target changed.");
        if (!restoring && desired.Value == 0 && ReadLocalAccount(Defaults.StudentAccountName).Disabled)
            throw new InvalidOperationException("Activate the managed student before hiding the administrator tile.");
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var winlogon = hive.OpenSubKey(Defaults.WinlogonRegistryKey, writable: true) ?? throw new IOException("The Winlogon key is missing.");
        using var existing = winlogon.OpenSubKey(Defaults.AdministratorVisibilitySubKey, writable: true);
        if (WindowsStudentSessionPolicyStore.ReadValue(existing, expected.AccountName) != expected.Value) throw new IOException();
        using var created = existing is null ? winlogon.CreateSubKey(Defaults.AdministratorVisibilitySubKey) : null;
        var key = existing ?? created ?? throw new IOException();
        requireAuthorization();
        if (Resolve(sid) != expected.AccountName || WindowsStudentSessionPolicyStore.ReadValue(key, expected.AccountName) != expected.Value)
            throw new IOException("The administrator visibility changed before mutation.");
        if (desired.Value is null) key.DeleteValue(desired.AccountName, throwOnMissingValue: false);
        else key.SetValue(desired.AccountName, unchecked((int)desired.Value.Value), RegistryValueKind.DWord);
        key.Flush();
        if (Read(sid) != desired) throw new IOException("The administrator visibility could not be verified.");
    }
    private static string Resolve(string sid)
    {
        var account = ((NTAccount)new SecurityIdentifier(sid).Translate(typeof(NTAccount))).Value.Split('\\');
        if (account.Length != 2 || !string.Equals(account[0], Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a verified local administrator can be hidden.");
        var local = ReadLocalAccount(account[1]);
        if (local.Sid != sid) throw new InvalidOperationException("The local account no longer matches its recorded SID.");
        return local.Name;
    }
    private static unsafe (string Sid, string Name, bool Disabled) ReadLocalAccount(string name)
    {
        byte* buffer = null;
        try
        {
            fixed (char* accountName = name)
            {
                var status = PInvoke.NetUserGetInfo(default, new PCWSTR(accountName), 23, &buffer);
                if (status != 0 || buffer is null) throw new IOException("The local account could not be verified.");
            }
            var info = (USER_INFO_23*)buffer;
            return (new SecurityIdentifier((nint)info->usri23_user_sid.Value).Value, info->usri23_name.ToString(),
                (info->usri23_flags & USER_ACCOUNT_FLAGS.UF_ACCOUNTDISABLE) != 0);
        }
        finally { if (buffer is not null) PInvoke.NetApiBufferFree(buffer); }
    }
}
