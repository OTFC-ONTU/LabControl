using System.Buffers.Binary;

namespace LabControl.Shared.Setup;

public sealed record HibernationState(bool FilePresent, byte NativeFileType, uint? Enabled, uint? FileType, uint? SizePercent);

public interface IHibernationSystem
{
    HibernationState Read();
    void Write(HibernationState expected, HibernationState value);
}

/// <summary>Disabling hibernation owns the native file and saved metadata as one tuple.
/// A changed file type or size on an otherwise disabled PC also blocks restoration.
/// An absent <c>HibernateEnabled</c> value next to an active file is the OS default
/// (<c>HibernateEnabledDefault</c>) and is restored as absent (D-65).</summary>
public sealed class HibernationSetting(IHibernationSystem system) : ISetupSetting, ISetupResolvedAppliedValue
{
    private HibernationState? _lastRead;
    public string Id => "machine.hibernation";

    public SettingChangeResult Apply(SetupSettingsJournal journal) => journal.ApplyFromCurrent(this, Desired);
    public SetupCheck Check(SetupSettingsJournal journal) => journal.Check(this, Desired);

    private static byte[]? Desired(byte[]? current)
    {
        var original = Decode(current);
        if (!original.FilePresent) return current;
        if (original.Enabled is not (null or 1) || original.NativeFileType is not (1 or 2)
            || original.SizePercent is > 100)
            throw new InvalidDataException("The existing hibernation configuration cannot be restored safely.");
        return Encode(original with { FilePresent = false, NativeFileType = 0, Enabled = 0 });
    }

    public byte[] Read()
    {
        _lastRead = null;
        _lastRead = system.Read();
        return Encode(_lastRead);
    }

    public void Write(byte[]? value)
    {
        var desired = Decode(value);
        var expected = _lastRead ?? throw new InvalidOperationException("Read hibernation before changing it.");
        _lastRead = null;
        system.Write(expected, desired);
    }

    public bool AcceptsResolvedAppliedValue(byte[]? requested, byte[]? actual)
    {
        try
        {
            var requestedState = Decode(requested);
            var actualState = Decode(actual);
            return IsDisabled(requestedState) && IsDisabled(actualState);
        }
        catch (InvalidDataException) { return false; }
    }

    public static bool IsDisabled(HibernationState state) => !state.FilePresent
        && state.Enabled is null or 0 && state.SizePercent is null or <= 100;

    public static byte[] Encode(HibernationState state)
    {
        var bytes = new byte[18];
        bytes[0] = 1;
        bytes[1] = state.FilePresent ? (byte)1 : (byte)0;
        bytes[2] = state.NativeFileType;
        Put(3, state.Enabled); Put(8, state.FileType); Put(13, state.SizePercent);
        return bytes;

        void Put(int offset, uint? value)
        {
            bytes[offset] = value is null ? (byte)0 : (byte)1;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 1, 4), value ?? 0);
        }
    }

    private static HibernationState Decode(byte[]? value)
    {
        if (value is not { Length: 18 } || value[0] != 1 || value[1] > 1)
            throw new InvalidDataException("The saved hibernation configuration is invalid.");
        return new(value[1] == 1, value[2], Get(3), Get(8), Get(13));

        uint? Get(int offset)
        {
            var number = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(offset + 1, 4));
            if (value[offset] > 1 || (value[offset] == 0 && number != 0))
                throw new InvalidDataException("The saved hibernation configuration is invalid.");
            return value[offset] == 0 ? null : number;
        }
    }
}
