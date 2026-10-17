using System.Text;

namespace LabControl.Shared.Setup;

/// <summary>Only the fixed installation parent; null means absent. An existing equivalent
/// entry is returned verbatim so a later representation edit is also preserved.</summary>
public interface IDefenderExclusionStore
{
    string? Read();
    void Write(string? expected, string? value);
}

public sealed class DefenderExclusionSetting(IDefenderExclusionStore store) : ISetupSetting, ISetupPreservableExistingValue
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private bool _hasRead;
    private string? _lastRead;
    public string Id => "machine.defender-exclusion";
    public SettingChangeResult Apply(SetupSettingsJournal journal) => journal.ApplyFromCurrent(this,
        current => current ?? Encode(Defaults.AgentInstallDirectory));
    public SetupCheck Check(SetupSettingsJournal journal) => journal.Check(this,
        current => current ?? Encode(Defaults.AgentInstallDirectory));

    public byte[]? Read()
    {
        _hasRead = false;
        _lastRead = store.Read();
        if (_lastRead is not null && !IsInstallDirectory(_lastRead))
            throw new InvalidDataException("The antivirus adapter returned an unrelated exclusion.");
        _hasRead = true;
        return Encode(_lastRead);
    }

    public void Write(byte[]? value)
    {
        var desired = Decode(value);
        if (!_hasRead) throw new InvalidOperationException("Read the exclusion before changing it.");
        _hasRead = false;
        store.Write(_lastRead, desired);
    }

    public static bool IsInstallDirectory(string value) =>
        string.Equals(value.TrimEnd('\\'), Defaults.AgentInstallDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static byte[]? Encode(string? value) => value is null ? null : [1, .. Utf8.GetBytes(value)];
    private static string? Decode(byte[]? value)
    {
        if (value is null) return null;
        if (value.Length is < 2 or > 4096 || value[0] != 1)
            throw new InvalidDataException("The saved antivirus exclusion is invalid.");
        var text = Utf8.GetString(value.AsSpan(1));
        if (!IsInstallDirectory(text)) throw new InvalidDataException("The saved antivirus exclusion is unrelated to this installation.");
        return text;
    }
}
