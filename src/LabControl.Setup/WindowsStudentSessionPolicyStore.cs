using LabControl.Shared;
using LabControl.Shared.Setup;
using Microsoft.Win32;

namespace LabControl.Setup;

internal sealed class WindowsStudentSessionPolicyStore(Action requireStudent) : IStudentSessionPolicyStore
{
    public uint? Read(StudentSessionPolicy policy)
    {
        requireStudent();
        var (path, name) = Location(policy);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hive.OpenSubKey(path);
        return ReadValue(key, name);
    }
    public void Write(StudentSessionPolicy policy, uint? expected, uint? desired)
    {
        requireStudent();
        var (path, name) = Location(policy);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var existing = hive.OpenSubKey(path, writable: true);
        if (ReadValue(existing, name) != expected) throw new IOException("The session policy changed before setup could write it.");
        using var created = existing is null ? hive.CreateSubKey(path) : null;
        var key = existing ?? created ?? throw new IOException("The session policy key could not be opened.");
        requireStudent();
        if (ReadValue(key, name) != expected) throw new IOException("The session policy changed before setup could write it.");
        if (desired is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, unchecked((int)desired.Value), RegistryValueKind.DWord);
        key.Flush();
        if (ReadValue(key, name) != desired) throw new IOException("The session policy could not be verified.");
        // Leave even an empty fixed policy key: other software may now own its contents.
    }
    internal static uint? ReadValue(RegistryKey? key, string name)
    {
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        if (key.GetValueKind(name) != RegistryValueKind.DWord || key.GetValue(name) is not int value
            || key.GetValueKind(name) != RegistryValueKind.DWord)
            throw new IOException("The session setting uses an unsupported registry type.");
        return unchecked((uint)value);
    }
    private static (string Path, string Name) Location(StudentSessionPolicy policy) => policy switch
    {
        StudentSessionPolicy.PrivacyExperience => (Defaults.StudentPrivacyPolicyRegistryKey, Defaults.StudentPrivacyPolicyRegistryValue),
        StudentSessionPolicy.EdgeFirstRun => (Defaults.StudentEdgePolicyRegistryKey, Defaults.StudentEdgePolicyRegistryValue),
        StudentSessionPolicy.OneDriveSignInNotifications => (Defaults.StudentOneDrivePolicyRegistryKey, Defaults.StudentOneDrivePolicyRegistryValue),
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };
}
