using System.Net.NetworkInformation;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class NicSettingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-nic-" + Guid.NewGuid().ToString("N"));
    private readonly SetupSettingsJournal _journal;
    private readonly Guid _id = Guid.NewGuid();
    private readonly Native _native = new();
    public NicSettingTests()
    {
        Directory.CreateDirectory(_directory);
        _journal = new(_directory, Guid.NewGuid().ToString("D"), b => b.ToArray(), b => b.ToArray());
        _journal.InitializeNew();
    }
    [Fact]
    public void Active_wireless_cannot_replace_unplugged_wired_wake_target()
    {
        var wired = new NicIdentity(Guid.NewGuid(), "PCI\\WIRED", "wired", "02:00:00:00:00:01");
        var wifi = new NicIdentity(Guid.NewGuid(), "PCI\\WIFI", "wireless", "02:00:00:00:00:02");
        var selection = NicWakeSelection.Choose([
            new(wifi, true, NetworkInterfaceType.Wireless80211),
            new(wired, false, NetworkInterfaceType.Ethernet)]);
        Assert.Equal(NicWakeSelectionStatus.Selected, selection.Status);
        Assert.Equal(wired, selection.Adapter);
        Assert.Equal(NicWakeSelectionStatus.NoPhysicalEthernet,
            NicWakeSelection.Choose([new(wifi, true, NetworkInterfaceType.Wireless80211)]).Status);
    }

    [Fact]
    public void Wired_allowlist_rejects_every_other_known_or_unknown_interface_type()
    {
        var allowed = new[] { NetworkInterfaceType.Ethernet, NetworkInterfaceType.Ethernet3Megabit,
            NetworkInterfaceType.FastEthernetT, NetworkInterfaceType.FastEthernetFx, NetworkInterfaceType.GigabitEthernet };
        foreach (var type in Enum.GetValues<NetworkInterfaceType>().Append((NetworkInterfaceType)9999))
            Assert.Equal(allowed.Contains(type), NicInterfaceClassification.IsWiredEthernet(type));
    }

    [Theory]
    [InlineData(NicPolicy.WakeOnMagicPacket, true)]
    [InlineData(NicPolicy.EnergyEfficientEthernet, false)]
    [InlineData(NicPolicy.WakeOnPattern, false)]
    [InlineData(NicPolicy.DeviceWake, true)]
    [InlineData(NicPolicy.DevicePowerManagement, true)]
    [InlineData(NicPolicy.MagicPacketOnly, true)]
    public void Policy_round_trip_retains_adapter_identity(NicPolicy policy, bool desired)
    {
        _native.Value = _native.Value with { Enabled = !desired, Kind = policy is NicPolicy.DeviceWake or NicPolicy.DevicePowerManagement or NicPolicy.MagicPacketOnly ? 2 : 1 };
        var original = _native.Value;
        var setting = new NicSetting(_id, policy, _native);
        Assert.Equal(SettingChangeResult.Applied, setting.Apply(_journal));
        Assert.Equal(original with { Enabled = desired }, _native.Value);
        Assert.Equal(SettingChangeResult.AlreadyApplied, setting.Apply(_journal));
        Assert.Equal(1, _native.Writes);
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(setting));
        Assert.Equal(original, _native.Value);
    }
    [Fact]
    public void Read_only_check_does_not_claim_or_change_setting()
    {
        var setting = new NicSetting(_id, NicPolicy.WakeOnMagicPacket, _native);
        Assert.Equal(SetupStepStatus.Needed, setting.Check(_journal).Status);
        Assert.Equal(0, _native.Writes);
        Assert.Empty(_journal.SettingIds());
        setting.Apply(_journal);
        Assert.Equal(SetupStepStatus.AlreadyDone, setting.Check(_journal).Status);
    }
    [Fact]
    public void Correct_preexisting_setting_is_unowned()
    {
        _native.Value = _native.Value with { Enabled = true };
        var setting = new NicSetting(_id, NicPolicy.DeviceWake, _native);
        Assert.Equal(SettingChangeResult.Unchanged, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Untracked, _journal.Restore(setting));
        Assert.Equal(0, _native.Writes);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Replacement_device_driver_or_native_type_is_preserved(int identity)
    {
        var setting = new NicSetting(_id, NicPolicy.WakeOnMagicPacket, _native);
        setting.Apply(_journal);
        _native.Value = identity switch
        {
            0 => _native.Value with { PnpDeviceId = "PCI\\REPLACEMENT" },
            1 => _native.Value with { NativeIdentity = "REPLACEMENT_DRIVER_KEY" },
            _ => _native.Value with { Kind = 2 },
        };
        Assert.Equal(SettingChangeResult.Conflict, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
        Assert.Equal(1, _native.Writes);
    }
    [Fact]
    public void Unsupported_driver_does_not_gain_journal_ownership()
    {
        _native.Unsupported = true;
        var setting = new NicSetting(_id, NicPolicy.EnergyEfficientEthernet, _native);
        Assert.Throws<NicSettingUnavailableException>(() => setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Untracked, _journal.Restore(setting));
        Assert.Equal(0, _native.Writes);
    }
    [Fact]
    public void Failed_apply_after_native_write_remains_ambiguous()
    {
        _native.FailAfterWrite = true;
        var setting = new NicSetting(_id, NicPolicy.WakeOnMagicPacket, _native);
        Assert.Throws<IOException>(() => setting.Apply(_journal));
        _native.FailAfterWrite = false;
        Assert.Equal(SettingChangeResult.Conflict, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
    }
    [Fact]
    public void Guarded_native_write_preserves_concurrent_edit()
    {
        _native.ChangeBeforeWrite = true;
        var setting = new NicSetting(_id, NicPolicy.WakeOnMagicPacket, _native);
        Assert.Throws<IOException>(() => setting.Apply(_journal));
        Assert.Equal(0, _native.Writes);
    }
    [Theory]
    [InlineData("PCI\\NIC", "PCI\\NIC_0", true)]
    [InlineData("pci\\nic", "PCI\\NIC_0", true)]
    [InlineData("PCI\\NIC", "PCI\\NIC_1", false)]
    [InlineData("PCI\\NIC", "PCI\\NIC_OTHER_0", false)]
    [InlineData("PCI\\NIC", "OTHER_PCI\\NIC_0", false)]
    [InlineData("PCI\\NIC", null, false)]
    [InlineData("", "_0", false)]
    public void Power_control_requires_exact_physical_instance(string pnp, string? instance, bool matches) =>
        Assert.Equal(matches, NicPowerControl.MatchesInstance(pnp, instance));

    [Fact]
    public void Magic_only_uses_its_own_writable_boolean_not_generic_device_wake()
    {
        var control = NicPowerControl.For(NicPolicy.MagicPacketOnly);
        Assert.Equal("MSNdis_DeviceWakeOnMagicPacketOnly", control.ClassName);
        Assert.Equal("EnableWakeOnMagicPacketOnly", control.PropertyName);
        Assert.NotEqual(NicPowerControl.For(NicPolicy.DeviceWake), control);
        Assert.Throws<ArgumentOutOfRangeException>(() => NicPowerControl.For(NicPolicy.WakeOnPattern));
    }

    [Fact]
    public void Wake_selection_uses_unique_connected_physical_adapter_and_preserves_ambiguity()
    {
        var wired = new NicIdentity(Guid.NewGuid(), "PCI\\ETHERNET", "Wired", "02:11:22:33:44:55");
        var second = new NicIdentity(Guid.NewGuid(), "USB\\ETHERNET", "USB Ethernet", "02:11:22:33:44:66");
        Assert.Equal(wired, NicWakeSelection.Choose([new(wired, true), new(second, false)]).Adapter);
        Assert.Equal(wired, NicWakeSelection.Choose([new(wired, false)]).Adapter);
        Assert.Equal(NicWakeSelectionStatus.Ambiguous, NicWakeSelection.Choose([new(wired, true), new(second, true)]).Status);
        Assert.Equal(NicWakeSelectionStatus.Ambiguous, NicWakeSelection.Choose([new(wired, false), new(second, false)]).Status);
        Assert.Equal(NicWakeSelectionStatus.NoPhysicalEthernet, NicWakeSelection.Choose([]).Status);
        Assert.Equal(NicWakeSelectionStatus.NoUsableMac, NicWakeSelection.Choose([new(wired with { MacAddress = null }, true)]).Status);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("invalid")]
    [InlineData("00:00:00:00:00:00")] [InlineData("FF:FF:FF:FF:FF:FF")] [InlineData("01:11:22:33:44:55")]
    public void Wake_selection_never_uses_missing_zero_broadcast_or_multicast_address(string? mac) =>
        Assert.False(NicWakeSelection.IsUsableMac(mac));

    [Fact]
    public void All_policies_apply_power_before_subordinate_wake_controls_and_restore_it_last()
    {
        var ordered = NicSetting.ApplyOrder.ToList();
        Assert.Equal(Enum.GetValues<NicPolicy>().Length, ordered.Distinct().Count());
        Assert.Equal(NicPolicy.DevicePowerManagement, ordered[0]);
        Assert.True(ordered.IndexOf(NicPolicy.DeviceWake) < ordered.IndexOf(NicPolicy.MagicPacketOnly));
        Assert.Equal(NicPolicy.DevicePowerManagement, ordered.AsEnumerable().Reverse().Last());
    }

    private sealed class Native : INicSettingsSystem
    {
        public NicSettingSnapshot Value = new("PCI\\ORIGINAL", "DRIVER_KEY", 1, false);
        public int Writes; public bool Unsupported; public bool FailAfterWrite; public bool ChangeBeforeWrite;
        public IReadOnlyList<NicIdentity> ListEthernetAdapters() => throw new NotImplementedException();
        public NicSettingSnapshot Read(Guid id, NicPolicy policy) => Unsupported ? throw new NicSettingUnavailableException("Unsupported") : Value;
        public void Write(Guid id, NicPolicy policy, NicSettingSnapshot expected, NicSettingSnapshot desired)
        {
            if (ChangeBeforeWrite) Value = Value with { NativeIdentity = "OTHER" };
            if (expected != Value) throw new IOException();
            Value = desired; Writes++;
            if (FailAfterWrite) throw new IOException();
        }
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
