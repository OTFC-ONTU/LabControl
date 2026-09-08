using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabControl.Shared.Setup;

public enum NicPolicy { WakeOnMagicPacket, EnergyEfficientEthernet, WakeOnPattern, DeviceWake, DevicePowerManagement, MagicPacketOnly }
public sealed record NicIdentity(Guid InterfaceId, string PnpDeviceId, string Name, string? MacAddress = null);
public sealed record NicHardwareInventory(NicIdentity Adapter, string? DriverProvider, string? DriverVersion);
public sealed record NicWakeCandidate(NicIdentity Adapter, bool IsUp,
    NetworkInterfaceType InterfaceType = NetworkInterfaceType.Ethernet);
public static class NicInterfaceClassification
{
    // Native callers must also prove PhysicalAdapter and exact GUID/PNP identity.
    public static bool IsWiredEthernet(NetworkInterfaceType type) => type is
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
        or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
        or NetworkInterfaceType.GigabitEthernet;
}
public enum NicWakeSelectionStatus { Selected, NoPhysicalEthernet, NoUsableMac, Ambiguous }
public sealed record NicWakeSelection(NicWakeSelectionStatus Status, NicIdentity? Adapter)
{
    /// <summary>Call only with positively identified physical Ethernet adapters.
    /// Never prefer a generic virtual/wireless interface over the wired wake target.</summary>
    public static NicWakeSelection Choose(IReadOnlyList<NicWakeCandidate> physical)
    {
        var wired = physical.Where(candidate => NicInterfaceClassification.IsWiredEthernet(candidate.InterfaceType)).ToArray();
        if (wired.Length == 0) return new(NicWakeSelectionStatus.NoPhysicalEthernet, null);
        var usable = wired.Where(candidate => IsUsableMac(candidate.Adapter.MacAddress)).ToArray();
        if (usable.Length == 0) return new(NicWakeSelectionStatus.NoUsableMac, null);
        var up = usable.Where(candidate => candidate.IsUp).ToArray();
        var selected = up.Length == 1 ? up[0] : up.Length == 0 && usable.Length == 1 ? usable[0] : null;
        return selected is null ? new(NicWakeSelectionStatus.Ambiguous, null) : new(NicWakeSelectionStatus.Selected, selected.Adapter);
    }
    public static bool IsUsableMac(string? value) => LabControl.Shared.Power.WakeOnLan.TryParseMac(value, out var bytes)
        && bytes.Any(value => value != 0) && (bytes[0] & 1) == 0;
}
public sealed record NicRestoreResult(Guid InterfaceId, NicPolicy Policy, SettingChangeResult Result);
public sealed class NicSettingUnavailableException(string message) : InvalidOperationException(message);
public sealed record NicSettingSnapshot([property: JsonRequired] string PnpDeviceId,
    [property: JsonRequired] string NativeIdentity, [property: JsonRequired] int Kind,
    [property: JsonRequired] bool Enabled);

public interface INicSettingsSystem
{
    IReadOnlyList<NicIdentity> ListEthernetAdapters();
    NicSettingSnapshot Read(Guid interfaceId, NicPolicy policy);
    void Write(Guid interfaceId, NicPolicy policy, NicSettingSnapshot expected, NicSettingSnapshot desired);
}

/// <summary>One policy on one physical adapter. PNP and native setting identity are
/// part of the protected baseline; replacing hardware or its driver cannot adopt it.</summary>
public sealed class NicSetting(Guid interfaceId, NicPolicy policy, INicSettingsSystem system) : ISetupSetting
{
    public static readonly IReadOnlyList<NicPolicy> ApplyOrder =
    [
        NicPolicy.DevicePowerManagement, NicPolicy.WakeOnMagicPacket, NicPolicy.WakeOnPattern,
        NicPolicy.DeviceWake, NicPolicy.MagicPacketOnly, NicPolicy.EnergyEfficientEthernet,
    ];
    private NicSettingSnapshot? _last;
    public string Id => "nic." + interfaceId.ToString("N") + "." + PolicyName(policy);
    private static string PolicyName(NicPolicy policy) => policy switch
    {
        NicPolicy.WakeOnMagicPacket => "magic", NicPolicy.EnergyEfficientEthernet => "eee",
        NicPolicy.WakeOnPattern => "pattern", NicPolicy.DeviceWake => "wake",
        NicPolicy.DevicePowerManagement => "power", NicPolicy.MagicPacketOnly => "magic-only",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };
    public SetupCheck Check(SetupSettingsJournal journal) => journal.Check(this, Desired);
    public SettingChangeResult Apply(SetupSettingsJournal journal) => journal.ApplyFromCurrent(this, Desired);
    private byte[] Desired(byte[]? current)
    {
        var snapshot = Decode(current);
        return Encode(snapshot with { Enabled = policy is not (NicPolicy.EnergyEfficientEthernet or NicPolicy.WakeOnPattern) });
    }
    public byte[] Read()
    {
        if (interfaceId == Guid.Empty) throw new ArgumentException("A physical adapter identity is required.");
        _last = null;
        _last = system.Read(interfaceId, policy);
        Validate(_last);
        return Encode(_last);
    }
    public void Write(byte[]? bytes)
    {
        var desired = Decode(bytes);
        var expected = _last ?? throw new InvalidOperationException("Read the NIC setting before changing it.");
        _last = null;
        if (desired.PnpDeviceId != expected.PnpDeviceId || desired.NativeIdentity != expected.NativeIdentity || desired.Kind != expected.Kind)
            throw new InvalidOperationException("The network adapter or driver setting identity changed.");
        system.Write(interfaceId, policy, expected, desired);
    }
    private static byte[] Encode(NicSettingSnapshot value) => [1, .. JsonSerializer.SerializeToUtf8Bytes(value)];
    private static NicSettingSnapshot Decode(byte[]? bytes)
    {
        try
        {
            if (bytes is not { Length: > 1 } || bytes[0] != 1) throw new InvalidDataException();
            var snapshot = JsonSerializer.Deserialize<NicSettingSnapshot>(bytes.AsSpan(1)) ?? throw new InvalidDataException();
            Validate(snapshot);
            return snapshot;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { throw new InvalidDataException("The saved NIC setting is invalid."); }
    }
    private static void Validate(NicSettingSnapshot value)
    {
        if (string.IsNullOrWhiteSpace(value.PnpDeviceId) || string.IsNullOrWhiteSpace(value.NativeIdentity) || value.Kind is not (1 or 2))
            throw new InvalidDataException("The NIC setting identity or native type is unsupported.");
    }
}

/// <summary>Named Windows power controls, matched to one exact PNP WMI instance.
/// A different suffix or substring match cannot establish hardware ownership.</summary>
public sealed record NicPowerControl(string ClassName, string PropertyName)
{
    public static NicPowerControl For(NicPolicy policy) => policy switch
    {
        NicPolicy.DeviceWake => new(Defaults.NicWakeWmiClass, Defaults.NicPowerEnableProperty),
        NicPolicy.DevicePowerManagement => new(Defaults.NicPowerWmiClass, Defaults.NicPowerEnableProperty),
        NicPolicy.MagicPacketOnly => new(Defaults.NicMagicOnlyWmiClass, Defaults.NicMagicOnlyEnableProperty),
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };
    public static bool MatchesInstance(string pnpDeviceId, string? instanceName) =>
        !string.IsNullOrWhiteSpace(pnpDeviceId)
        && string.Equals(instanceName, pnpDeviceId + "_0", StringComparison.OrdinalIgnoreCase);
}
