using System.Security;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Microsoft.Win32;

namespace LabControl.Setup;

/// <summary>Two machine policies only; journal contents never select registry paths.
/// Parent keys must already exist. Setup never creates or deletes a registry tree.</summary>
internal sealed class WindowsMachineRegistryStore : IMachineRegistryStore
{
    public uint? Read(MachineRegistryPolicy policy) => Guard(() =>
    {
        var (path, name) = Location(policy);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(path, writable: false)
            ?? throw new IOException("The system registry key is missing.");
        return ReadValue(key, name);
    });

    public void Write(MachineRegistryPolicy policy, uint? expected, uint? value) => Guard(() =>
    {
        var (path, name) = Location(policy);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(path, writable: true)
            ?? throw new IOException("The system registry key is missing.");
        if (ReadValue(key, name) != expected)
            throw new IOException("The registry setting changed before the write.");
        if (value is null) key.DeleteValue(name, throwOnMissingValue: true);
        else key.SetValue(name, unchecked((int)value.Value), RegistryValueKind.DWord);
        key.Flush();
        if (ReadValue(key, name) != value)
            throw new IOException("The registry setting did not retain the requested change.");
        return true;
    });

    private static uint? ReadValue(RegistryKey key, string name)
    {
        // Enumerate first: GetValue returns its default on some read failures, which
        // must not turn into evidence that an existing setting was absent.
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        if (key.GetValueKind(name) != RegistryValueKind.DWord)
            throw new IOException("The registry setting has an unsupported type.");
        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not int value
            || key.GetValueKind(name) != RegistryValueKind.DWord)
            throw new IOException("The registry setting could not be read consistently.");
        return unchecked((uint)value);
    }

    private static (string Path, string Name) Location(MachineRegistryPolicy policy) => policy switch
    {
        MachineRegistryPolicy.FastStartup => (Defaults.FastStartupPolicyKey, Defaults.FastStartupPolicyValue),
        MachineRegistryPolicy.SoftwareSas => (Defaults.SoftwareSasPolicyKey, Defaults.SoftwareSasPolicyValue),
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    private static T Guard<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            // No native values or exception details enter setup logs.
            throw new IOException("The machine registry setting could not be read or changed safely; preserve it for review.");
        }
    }
}
