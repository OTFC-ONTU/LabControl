using System.Buffers.Binary;

namespace LabControl.Shared.Setup;

public enum PowerPlanPolicy { Sleep, Display, Disk }

/// <summary>Native power API seam. Only fixed AC timeout settings are exposed.</summary>
public interface IPowerPlanSystem
{
    Guid GetActiveScheme();
    uint ReadAcSeconds(Guid scheme, PowerPlanPolicy policy);
    void WriteAcSeconds(Guid scheme, PowerPlanPolicy policy, uint seconds);
    void ActivateScheme(Guid scheme);
}

/// <summary>Journals the active scheme identity together with its persisted AC timeout.
/// A different active scheme is a conflict, never permission to edit or switch it.</summary>
public sealed class PowerPlanSetting(PowerPlanPolicy policy, IPowerPlanSystem system)
    : ISetupSetting, ISetupSettingActivation
{
    private Snapshot? _lastRead;

    public string Id => policy switch
    {
        PowerPlanPolicy.Sleep => "machine.power.ac.sleep",
        PowerPlanPolicy.Display => "machine.power.ac.display",
        PowerPlanPolicy.Disk => "machine.power.ac.disk",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    public SetupCheck Check(SetupSettingsJournal journal) => journal.Check(this, Desired);
    public SettingChangeResult Apply(SetupSettingsJournal journal) => journal.ApplyFromCurrent(this, Desired);
    private byte[] Desired(byte[]? current) => Encode(Decode(current) with
        {
            Seconds = policy switch
            {
                PowerPlanPolicy.Sleep => Defaults.SetupSleepAcSeconds,
                PowerPlanPolicy.Display => Defaults.SetupDisplayAcSeconds,
                PowerPlanPolicy.Disk => Defaults.SetupDiskAcSeconds,
                _ => throw new ArgumentOutOfRangeException(nameof(policy))
            }
        });

    public byte[] Read()
    {
        _lastRead = null;
        var current = ReadCurrent();
        _lastRead = current;
        return Encode(current);
    }

    public void Write(byte[]? value)
    {
        var desired = Decode(value);
        var expected = _lastRead ?? throw new InvalidOperationException("Read the power setting before changing it.");
        _lastRead = null;
        if (desired.Scheme != expected.Scheme || ReadCurrent() != expected) throw Conflict();
        // The native API targets this explicit scheme, never an alias for 'current'.
        system.WriteAcSeconds(expected.Scheme, policy, desired.Seconds);
        if (ReadCurrent() != desired) throw Conflict();
    }

    public void Activate(byte[]? expected)
    {
        var snapshot = Decode(expected);
        _lastRead = null;
        if (ReadCurrent() != snapshot) throw Conflict();
        system.ActivateScheme(snapshot.Scheme);
        if (ReadCurrent() != snapshot) throw Conflict();
    }

    private Snapshot ReadCurrent()
    {
        _ = Id; // Reject undefined policies before invoking native code.
        var scheme = system.GetActiveScheme();
        if (scheme == Guid.Empty) throw new IOException("The active power scheme is invalid.");
        var seconds = system.ReadAcSeconds(scheme, policy);
        if (system.GetActiveScheme() != scheme) throw Conflict();
        return new Snapshot(scheme, seconds);
    }

    private static byte[] Encode(Snapshot value)
    {
        var bytes = new byte[21];
        bytes[0] = 1; // Snapshot format version; GUID bytes followed by uint32 seconds.
        value.Scheme.TryWriteBytes(bytes.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(17), value.Seconds);
        return bytes;
    }

    private static Snapshot Decode(byte[]? bytes)
    {
        if (bytes is not { Length: 21 } || bytes[0] != 1)
            throw new InvalidDataException("The saved power setting is invalid.");
        var scheme = new Guid(bytes.AsSpan(1, 16));
        if (scheme == Guid.Empty) throw new InvalidDataException("The saved power scheme is invalid.");
        return new Snapshot(scheme, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(17)));
    }

    private static IOException Conflict() => new("The power scheme or setting changed; preserve it for review.");
    private readonly record struct Snapshot(Guid Scheme, uint Seconds);
}
