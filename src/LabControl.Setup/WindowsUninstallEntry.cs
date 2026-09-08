using System.Text.Json;
using System.Runtime.InteropServices;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Microsoft.Win32;

namespace LabControl.Setup;

internal sealed class WindowsUninstallEntry(string installationId) : ISetupSetting, ISetupOwnedCreation
{
    private byte[]? _last;
    private bool _read;
    public string Id => "machine.uninstall-entry";
    public byte[] Desired => JsonSerializer.SerializeToUtf8Bytes(Values());
    private SortedDictionary<string, string> Values() => new(StringComparer.Ordinal)
    {
        ["DisplayName"] = "LabControl",
        ["Publisher"] = "LabControl",
        ["InstallLocation"] = Defaults.AgentInstallDirectory,
        ["UninstallString"] = "\"" + Path.Combine(Defaults.AgentInstallDirectory, Defaults.UninstallExecutableName) + "\" --uninstall",
        ["LabControlInstallationId"] = installationId,
    };

    private byte[] RecoveryValues(string worker)
    {
        var values = Values();
        values["UninstallString"] = "\"" + worker + "\" " + Defaults.FinishUninstallSwitch + " " + installationId + " 0";
        return JsonSerializer.SerializeToUtf8Bytes(values);
    }

    public void PointToCleanup(string worker)
    {
        var current = Read();
        var recovery = RecoveryValues(worker);
        if (current is not null && !current.AsSpan().SequenceEqual(Desired) && !current.AsSpan().SequenceEqual(recovery))
            throw new IOException("The Installed apps entry was edited; preserve it for review.");
        if (current is null) WriteNewValuesAtomically(recovery);
        else
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(Defaults.SetupUninstallRegistryKey, true)
                ?? throw new IOException("The Installed apps entry disappeared.");
            var observed = Read();
            if (observed is null || !current.AsSpan().SequenceEqual(observed))
                throw new IOException("The Installed apps entry changed during cleanup preparation.");
            // All other values are unchanged; this single registry value is the
            // atomic transition from the installed executable to its retry worker.
            key.SetValue("UninstallString", JsonSerializer.Deserialize<SortedDictionary<string, string>>(recovery)!["UninstallString"], RegistryValueKind.String);
            key.Flush();
        }
        if (Read() is not { } actual || !actual.AsSpan().SequenceEqual(recovery))
            throw new IOException("The cleanup retry entry could not be verified.");
    }

    public void RemoveCleanupEntry(string worker)
    {
        var current = Read();
        if (current is null) return;
        if (!current.AsSpan().SequenceEqual(RecoveryValues(worker)))
            throw new IOException("The cleanup retry entry was edited; preserve it for review.");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (Read() is not { } observed || !current.AsSpan().SequenceEqual(observed))
            throw new IOException("The cleanup retry entry changed.");
        root.DeleteSubKey(Defaults.SetupUninstallRegistryKey, false);
    }

    public byte[]? Read()
    {
        _read = false;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(Defaults.SetupUninstallRegistryKey);
        if (key is null) { _read = true; _last = null; return null; }
        if (key.SubKeyCount != 0 || key.ValueCount != Values().Count)
            throw new IOException("The Installed apps entry contains unrecognized data; preserve it for review.");
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValueKind(name) != RegistryValueKind.String || key.GetValue(name) is not string value)
                throw new IOException("The Installed apps entry contains unsupported values.");
            values.Add(name, value);
        }
        _last = JsonSerializer.SerializeToUtf8Bytes(values);
        _read = true;
        return _last.ToArray();
    }

    public void Write(byte[]? value)
    {
        if (!_read) throw new InvalidOperationException("Read the Installed apps entry first.");
        var previous = _last?.ToArray();
        var current = Read();
        if (!(previous is null ? current is null : current is not null && current.AsSpan().SequenceEqual(previous)))
            throw new IOException("The Installed apps entry changed.");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (value is null)
        {
            if (current is not null && !current.AsSpan().SequenceEqual(Desired))
                throw new IOException("The Installed apps entry is not owned by this installation.");
            root.DeleteSubKey(Defaults.SetupUninstallRegistryKey, false);
            return;
        }
        if (!value.AsSpan().SequenceEqual(Desired) || current is not null)
            throw new IOException("An existing Installed apps entry cannot be replaced.");
        WriteNewValuesAtomically(value);
    }

    public bool ConfirmsOwnedCreation(byte[] expected) => expected.AsSpan().SequenceEqual(Desired)
        && Read() is { } current && current.AsSpan().SequenceEqual(expected);

    private void WriteNewValuesAtomically(byte[] desired)
    {
        var values = JsonSerializer.Deserialize<SortedDictionary<string, string>>(desired)!;
        var separator = Defaults.SetupUninstallRegistryKey.LastIndexOf('\\');
        var parentPath = Defaults.SetupUninstallRegistryKey[..separator];
        var finalName = Defaults.SetupUninstallRegistryKey[(separator + 1)..];
        if (!Guid.TryParseExact(installationId, "D", out var id) || id == Guid.Empty)
            throw new IOException("The uninstall registration identity is invalid.");
        var stagingName = finalName + "." + installationId;
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var parent = root.OpenSubKey(parentPath, true)
            ?? throw new IOException("The Installed apps registry parent is missing.");
        using (var staging = parent.CreateSubKey(stagingName, true))
        {
            // The unpredictable sibling name is derived solely from protected
            // installation history. Resume only a partial exact owned tuple.
            if (staging.SubKeyCount != 0 || staging.ValueCount > values.Count)
                throw new IOException("The staged uninstall entry contains unexpected data.");
            var original = Values();
            foreach (var name in staging.GetValueNames())
            {
                if (staging.GetValueKind(name) != RegistryValueKind.String
                    || staging.GetValue(name) is not string observed || !values.TryGetValue(name, out var intended)
                    || observed != intended && observed != original[name])
                    throw new IOException("The staged uninstall entry changed; preserve it for review.");
            }
            foreach (var pair in values) staging.SetValue(pair.Key, pair.Value, RegistryValueKind.String);
            staging.Flush();
        }
        // RegRenameKey fails if the destination appeared. There is never a partially
        // populated public entry for repair/uninstall to mistake for foreign data.
        var result = RegRenameKey(parent.Handle.DangerousGetHandle(), stagingName, finalName);
        if (result != 0) throw new IOException("The complete uninstall registration could not be committed.");
        parent.Flush();
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegRenameKey(IntPtr key, string oldName, string newName);
}
