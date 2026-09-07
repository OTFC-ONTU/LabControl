using System.Buffers.Binary;

namespace LabControl.Shared.Setup;

public enum MachineRegistryPolicy { FastStartup, SoftwareSas }

/// <summary>A code-defined DWORD value. Read errors and unexpected native types must
/// throw. Write must recheck expected on the same native key handle before mutation.
/// This is an optimistic guard, not an atomic transaction with other administrators.</summary>
public interface IMachineRegistryStore
{
    uint? Read(MachineRegistryPolicy policy);
    void Write(MachineRegistryPolicy policy, uint? expected, uint? value);
}

/// <summary>Typed bridge to the encrypted settings journal. Only DWORDs are supported;
/// an unexpected native type is preserved by refusing the operation.</summary>
public sealed class MachineRegistrySetting(MachineRegistryPolicy policy, IMachineRegistryStore store) : ISetupSetting
{
    private bool _hasRead;
    private uint? _lastRead;

    public string Id => policy switch
    {
        MachineRegistryPolicy.FastStartup => "machine.fast-startup",
        MachineRegistryPolicy.SoftwareSas => "machine.software-sas",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    public SettingChangeResult Apply(SetupSettingsJournal journal) =>
        journal.ApplyFromCurrent(this, current => Encode(policy switch
        {
            MachineRegistryPolicy.FastStartup => 0u,
            // Preserve an existing services-and-accessibility policy.
            MachineRegistryPolicy.SoftwareSas => current is not null
                && current.AsSpan().SequenceEqual(Encode(3)) ? 3u : 1u,
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        }));

    public byte[]? Read()
    {
        _hasRead = false;
        _lastRead = store.Read(policy);
        _hasRead = true;
        return Encode(_lastRead);
    }

    public void Write(byte[]? value)
    {
        var decoded = Decode(value);
        if (!_hasRead) throw new InvalidOperationException("Read the setting before changing it.");
        _hasRead = false;
        store.Write(policy, _lastRead, decoded);
    }

    public static byte[]? Encode(uint? value)
    {
        if (value is null) return null;
        // REG_DWORD (4), followed by exactly four little-endian bytes.
        var bytes = new byte[5];
        bytes[0] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), value.Value);
        return bytes;
    }

    private static uint? Decode(byte[]? bytes)
    {
        if (bytes is null) return null;
        if (bytes.Length != 5 || bytes[0] != 4)
            throw new InvalidDataException("The saved registry setting is not a DWORD.");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1));
    }
}
