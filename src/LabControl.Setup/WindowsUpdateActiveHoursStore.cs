using System.Security;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Microsoft.Win32;

namespace LabControl.Setup;

internal sealed class WindowsUpdateActiveHoursStore : IUpdateActiveHoursStore
{
    public UpdateActiveHours Read() => Guard(() =>
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var parent = hive.OpenSubKey(Defaults.WindowsUpdatePolicyParentKey)
            ?? throw new IOException();
        using var key = parent.OpenSubKey(Defaults.WindowsUpdatePolicySubKey);
        return ReadTuple(key);
    });

    public void Write(UpdateActiveHours expected, UpdateActiveHours value) => Guard(() =>
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var parent = hive.OpenSubKey(Defaults.WindowsUpdatePolicyParentKey, writable: true)
            ?? throw new IOException();
        using var existing = parent.OpenSubKey(Defaults.WindowsUpdatePolicySubKey, writable: true);
        if (ReadTuple(existing) != expected) throw new IOException();
        // Only this fixed leaf may be created. Never delete it on restoration: another
        // component may now use the same key, and an empty policy key has no effect.
        using var created = existing is null ? parent.CreateSubKey(Defaults.WindowsUpdatePolicySubKey) : null;
        var key = existing ?? created ?? throw new IOException();
        var current = expected;
        // Set the range before enabling. When restoring a disabled/absent policy,
        // restore its enable flag first. No claim of an atomic multi-value write.
        if (value.Enabled != 1) Change(Defaults.UpdateActiveHoursEnabledValue, current with { Enabled = value.Enabled });
        Change(Defaults.UpdateActiveHoursStartValue, current with { Start = value.Start });
        Change(Defaults.UpdateActiveHoursEndValue, current with { End = value.End });
        if (value.Enabled == 1) Change(Defaults.UpdateActiveHoursEnabledValue, current with { Enabled = value.Enabled });
        key.Flush();
        if (ReadTuple(key) != value) throw new IOException();
        return true;

        void Change(string name, UpdateActiveHours next)
        {
            if (ReadTuple(key) != current) throw new IOException();
            if (next == current) return;
            var desired = name == Defaults.UpdateActiveHoursEnabledValue ? next.Enabled
                : name == Defaults.UpdateActiveHoursStartValue ? next.Start : next.End;
            if (desired is null) key.DeleteValue(name, throwOnMissingValue: true);
            else key.SetValue(name, unchecked((int)desired.Value), RegistryValueKind.DWord);
            key.Flush();
            if (ReadTuple(key) != next) throw new IOException();
            current = next;
        }
    });

    private static UpdateActiveHours ReadTuple(RegistryKey? key)
    {
        if (key is null) return new(null, null, null);
        var result = Once();
        if (Once() != result) throw new IOException();
        return result;

        UpdateActiveHours Once() => new(ReadValue(Defaults.UpdateActiveHoursEnabledValue),
            ReadValue(Defaults.UpdateActiveHoursStartValue), ReadValue(Defaults.UpdateActiveHoursEndValue));

        uint? ReadValue(string name)
        {
            if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
            if (key.GetValueKind(name) != RegistryValueKind.DWord) throw new IOException();
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not int number
                || key.GetValueKind(name) != RegistryValueKind.DWord) throw new IOException();
            return unchecked((uint)number);
        }
    }

    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new IOException("The Windows Update active-hours policy could not be read or changed safely; preserve it for review.");
        }
    }
}
