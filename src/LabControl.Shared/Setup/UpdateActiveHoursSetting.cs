using System.Buffers.Binary;

namespace LabControl.Shared.Setup;

/// <summary>Absent and DWORD values are distinct. Preserve all original bits,
/// including values outside the range Setup itself would choose.</summary>
public sealed record UpdateActiveHours(uint? Enabled, uint? Start, uint? End);

public interface IUpdateActiveHoursStore
{
    UpdateActiveHours Read();
    // Recheck the entire tuple before each mutation; throw on partial failure.
    void Write(UpdateActiveHours expected, UpdateActiveHours value);
}

/// <summary>One ownership record for the complete active-hours policy. A later edit
/// to any member preserves all three; partial native writes remain ambiguous.</summary>
public sealed class UpdateActiveHoursSetting(IUpdateActiveHoursStore store) : ISetupSetting
{
    private UpdateActiveHours? _lastRead;
    public string Id => "machine.update-active-hours";

    public SetupCheck Check(SetupSettingsJournal journal) => journal.Check(this,
        _ => Encode(new(1, Defaults.SetupActiveHoursStart, Defaults.SetupActiveHoursEnd)));

    public SettingChangeResult Apply(SetupSettingsJournal journal) => journal.Apply(this,
        Encode(new(1, Defaults.SetupActiveHoursStart, Defaults.SetupActiveHoursEnd)));

    public byte[] Read()
    {
        _lastRead = null;
        var current = store.Read();
        _lastRead = current;
        return Encode(current);
    }

    public void Write(byte[]? value)
    {
        var decoded = Decode(value);
        var expected = _lastRead ?? throw new InvalidOperationException("Read the setting before changing it.");
        _lastRead = null;
        store.Write(expected, decoded);
    }

    public static byte[] Encode(UpdateActiveHours value)
    {
        // Version 1, then three slots: 0 + zero bits for absence, 4 + DWORD bits.
        var bytes = new byte[16];
        bytes[0] = 1;
        uint?[] values = [value.Enabled, value.Start, value.End];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is not { } number) continue;
            bytes[1 + i * 5] = 4;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2 + i * 5, 4), number);
        }
        return bytes;
    }

    private static UpdateActiveHours Decode(byte[]? bytes)
    {
        if (bytes is not { Length: 16 } || bytes[0] != 1) throw InvalidSnapshot();
        var values = new uint?[3];
        for (var i = 0; i < values.Length; i++)
        {
            var kind = bytes[1 + i * 5];
            var number = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(2 + i * 5, 4));
            if (kind == 4) values[i] = number;
            else if (kind != 0 || number != 0) throw InvalidSnapshot();
        }
        return new(values[0], values[1], values[2]);
    }

    private static InvalidDataException InvalidSnapshot() => new("The saved active-hours policy is invalid.");
}
