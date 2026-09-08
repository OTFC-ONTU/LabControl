using System.Security;
using System.Security.Cryptography;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Microsoft.Win32;

namespace LabControl.Setup;

internal sealed class WindowsStudentSignInStore(Action requireAccount) : IStudentSignInStore
{
    private readonly WindowsStudentSignInSecretStore _secret = new(requireAccount);
    public string? ReadAutoLogonSid() => Guard(() =>
    {
        requireAccount();
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(Defaults.WinlogonRegistryKey) ?? throw new IOException();
        return ReadSid(key);
    });

    public void WriteAutoLogonSid(string? expected, string? value) => Guard(() =>
    {
        requireAccount();
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(Defaults.WinlogonRegistryKey, writable: true) ?? throw new IOException();
        if (ReadSid(key) != expected) throw new IOException();
        requireAccount();
        if (ReadSid(key) != expected) throw new IOException();
        if (value is null) key.DeleteValue(Defaults.AutoLogonSidValue, throwOnMissingValue: false);
        else key.SetValue(Defaults.AutoLogonSidValue, value, RegistryValueKind.String);
        key.Flush();
        if (ReadSid(key) != value) throw new IOException();
        return true;
    });

    private static string? ReadSid(RegistryKey key)
    {
        if (!key.GetValueNames().Contains(Defaults.AutoLogonSidValue, StringComparer.OrdinalIgnoreCase)) return null;
        if (key.GetValueKind(Defaults.AutoLogonSidValue) != RegistryValueKind.String) throw new IOException();
        if (key.GetValue(Defaults.AutoLogonSidValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value
            || key.GetValueKind(Defaults.AutoLogonSidValue) != RegistryValueKind.String) throw new IOException();
        return value;
    }
    public StudentSignInSnapshot Read() => Guard(() =>
    {
        requireAccount();
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(Defaults.WinlogonRegistryKey) ?? throw new IOException();
        return ReadTuple(key);
    });

    public void Write(StudentSignInSnapshot expected, StudentSignInSnapshot value) => Guard(() =>
    {
        expected.LogonCount?.Validate();
        value.LogonCount?.Validate();
        requireAccount();
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(Defaults.WinlogonRegistryKey, writable: true) ?? throw new IOException();
        var current = expected;
        Check();
        Change(Defaults.AutoAdminLogonValue, "0", current with { Enabled = "0" });
        ChangeCount();
        Change(Defaults.AutoLogonUserValue, value.User, current with { User = value.User });
        Change(Defaults.AutoLogonDomainValue, value.Domain, current with { Domain = value.Domain });
        Change(Defaults.AutoLogonPasswordValue, value.PlaintextPassword, current with { PlaintextPassword = value.PlaintextPassword });
        Check();
        _secret.Write(current.Secret, value.Secret);
        current = current with { Secret = value.Secret };
        Check();
        Change(Defaults.AutoAdminLogonValue, value.Enabled, current with { Enabled = value.Enabled });
        return true;

        void ChangeCount()
        {
            Check();
            value.LogonCount?.Validate();
            if (current.LogonCount == value.LogonCount) return;
            if (value.LogonCount is null) key.DeleteValue(Defaults.AutoLogonCountValue, throwOnMissingValue: false);
            else if (value.LogonCount.Kind == "dword")
                key.SetValue(Defaults.AutoLogonCountValue, unchecked((int)value.LogonCount.Dword!.Value), RegistryValueKind.DWord);
            else key.SetValue(Defaults.AutoLogonCountValue, value.LogonCount.Text!, RegistryValueKind.String);
            key.Flush();
            current = current with { LogonCount = value.LogonCount };
            Check();
        }
        void Check()
        {
            requireAccount();
            var observed = ReadTuple(key);
            try { if (!current.SameAs(observed)) throw new IOException(); }
            finally { Clear(observed.Secret); }
        }
        void Change(string name, string? desired, StudentSignInSnapshot next)
        {
            Check();
            if (current.SameAs(next)) return;
            if (desired is null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, desired, RegistryValueKind.String);
            key.Flush();
            current = next;
            Check();
        }
    });

    private StudentSignInSnapshot ReadTuple(RegistryKey key)
    {
        StudentSignInSnapshot Once()
        {
            var count = ReadCount();
            return new(ReadValue(Defaults.AutoAdminLogonValue), ReadValue(Defaults.AutoLogonUserValue),
                ReadValue(Defaults.AutoLogonDomainValue), ReadValue(Defaults.AutoLogonPasswordValue), _secret.Read(), count);
        }
        var first = Once();
        try
        {
            var second = Once();
            try { if (!first.SameAs(second)) throw new IOException(); }
            finally { Clear(second.Secret); }
            return first;
        }
        catch { Clear(first.Secret); throw; }
        StudentSignInCount? ReadCount()
        {
            if (!key.GetValueNames().Contains(Defaults.AutoLogonCountValue, StringComparer.OrdinalIgnoreCase)) return null;
            var kind = key.GetValueKind(Defaults.AutoLogonCountValue);
            var raw = key.GetValue(Defaults.AutoLogonCountValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (key.GetValueKind(Defaults.AutoLogonCountValue) != kind) throw new IOException();
            return (kind, raw) switch
            {
                (RegistryValueKind.DWord, int bits) => new StudentSignInCount("dword", null, unchecked((uint)bits)),
                (RegistryValueKind.String, string text) => new StudentSignInCount("string", text, null),
                _ => throw new IOException(),
            };
        }
        string? ReadValue(string name)
        {
            if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
            if (key.GetValueKind(name) != RegistryValueKind.String) throw new IOException();
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value
                || key.GetValueKind(name) != RegistryValueKind.String) throw new IOException();
            return value;
        }
    }
    private static void Clear(byte[]? bytes) { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        { throw new IOException("Automatic sign-in could not be changed safely; preserve settings for review."); }
    }
}
