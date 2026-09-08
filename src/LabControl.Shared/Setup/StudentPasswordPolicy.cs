using System.Buffers.Binary;
using System.Globalization;

namespace LabControl.Shared.Setup;

public readonly record struct PasswordPolicySnapshot(int MinimumLength, bool Complexity);
public interface IPasswordPolicyStore
{
    PasswordPolicySnapshot Read();
    void Write(PasswordPolicySnapshot expected, PasswordPolicySnapshot desired);
}

/// <summary>Own only the two local-policy fields needed by the explicit student mode.
/// No account SID is required: a rejected account has not been created yet.</summary>
public sealed class StudentPasswordPolicy(IPasswordPolicyStore store) : ISetupSetting
{
    public string Id => "student.password-policy";
    private PasswordPolicySnapshot? _last;
    public byte[] Read() { _last = null; _last = store.Read(); return Encode(_last.Value); }
    public void Write(byte[]? value)
    {
        var expected = _last ?? throw new InvalidOperationException("Read password policy before changing it.");
        _last = null;
        store.Write(expected, Decode(value));
    }
    public SettingChangeResult Apply(SetupSettingsJournal journal) => journal.Apply(this, Encode(new(1, false)));
    public SettingChangeResult Restore(SetupSettingsJournal journal) => journal.Restore(this);

    public static PasswordPolicySnapshot ParseExport(string text)
    {
        int? minimum = null, complexity = null;
        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inSection = line.Equals("[System Access]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inSection || line.StartsWith(';') || string.IsNullOrEmpty(line)) continue;
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (parts[0].Equals("MinimumPasswordLength", StringComparison.OrdinalIgnoreCase))
            {
                if (minimum is not null || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw Invalid();
                minimum = number;
            }
            if (parts[0].Equals("PasswordComplexity", StringComparison.OrdinalIgnoreCase))
            {
                if (complexity is not null || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw Invalid();
                complexity = number;
            }
        }
        if (minimum is null or < 0 or > 128 || complexity is null or < 0 or > 1) throw Invalid();
        return new(minimum.Value, complexity == 1);
    }
    public static string Configuration(PasswordPolicySnapshot value)
    {
        _ = Encode(value);
        return string.Create(CultureInfo.InvariantCulture,
            $"[Unicode]\r\nUnicode=yes\r\n[Version]\r\nsignature=\"$CHICAGO$\"\r\nRevision=1\r\n[System Access]\r\nMinimumPasswordLength = {value.MinimumLength}\r\nPasswordComplexity = {(value.Complexity ? 1 : 0)}\r\n");
    }
    private static byte[] Encode(PasswordPolicySnapshot value)
    {
        if (value.MinimumLength is < 0 or > 128) throw Invalid();
        var result = new byte[6]; result[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1), value.MinimumLength);
        result[5] = value.Complexity ? (byte)1 : (byte)0;
        return result;
    }
    private static PasswordPolicySnapshot Decode(byte[]? bytes)
    {
        if (bytes is not { Length: 6 } || bytes[0] != 1 || bytes[5] > 1) throw Invalid();
        var result = new PasswordPolicySnapshot(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1)), bytes[5] == 1);
        _ = Encode(result); return result;
    }
    private static InvalidDataException Invalid() => new("The local password policy format is unsupported.");
}
