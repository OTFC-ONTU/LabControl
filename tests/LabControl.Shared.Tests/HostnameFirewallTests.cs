using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class HostnameFirewallTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-network-" + Guid.NewGuid().ToString("N"));
    private readonly SetupSettingsJournal _journal;
    public HostnameFirewallTests()
    {
        Directory.CreateDirectory(_directory);
        _journal = new(_directory, Guid.NewGuid().ToString("D"), b => b.ToArray(), b => b.ToArray());
        _journal.InitializeNew();
    }
    [Fact]
    public void Hostname_repairs_across_reboot_and_restoration_requires_second_reboot()
    {
        var native = new Host();
        var setting = new HostnameSetting(native);
        Assert.Equal(SettingChangeResult.Applied, setting.Apply(_journal, "PC-01"));
        Assert.True(setting.Status().RebootRequired);
        Assert.Equal(SettingChangeResult.AlreadyApplied, setting.Apply(_journal, "PC-01"));
        native.Value = new("PC-01", "PC-01");
        Assert.False(setting.Status().RebootRequired);
        Assert.Equal(SettingChangeResult.AlreadyApplied, setting.Apply(_journal, "PC-01"));
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(setting));
        Assert.Equal("ORIGINAL", native.Value.Pending);
        Assert.True(setting.Status().RebootRequired);
        native.Value = new("ORIGINAL", "ORIGINAL");
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(setting));
        Assert.Equal(2, native.Writes);
    }
    [Fact]
    public void Existing_pending_rename_is_not_overwritten()
    {
        var native = new Host { Value = new("ORIGINAL", "OTHER") };
        Assert.Throws<InvalidOperationException>(() => new HostnameSetting(native).Apply(_journal, "PC-01"));
        Assert.Equal(0, native.Writes);
    }
    [Fact]
    public void Later_pending_rename_is_preserved_on_removal()
    {
        var native = new Host();
        var setting = new HostnameSetting(native);
        setting.Apply(_journal, "PC-01");
        native.Value = new("PC-01", "OTHER");
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
        Assert.Equal("OTHER", native.Value.Pending);
    }
    [Fact]
    public void Hostname_failed_apply_after_native_change_is_ambiguous()
    {
        var native = new Host { FailAfterWrite = true };
        var setting = new HostnameSetting(native);
        Assert.Throws<IOException>(() => setting.Apply(_journal, "PC-01"));
        native.FailAfterWrite = false;
        Assert.Equal(SettingChangeResult.Conflict, setting.Apply(_journal, "PC-01"));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
    }
    [Theory]
    [InlineData("")] [InlineData("1234")] [InlineData("name.with.dot")]
    [InlineData("-name")] [InlineData("a-name-longer-than-netbios")]
    public void Unsupported_hostname_is_refused(string name)
    {
        var native = new Host();
        Assert.Throws<InvalidDataException>(() => new HostnameSetting(native).Apply(_journal, name));
        Assert.Equal(0, native.Writes);
    }
    [Theory]
    [InlineData(SetupFirewallPolicy.Discovery)] [InlineData(SetupFirewallPolicy.Echo)]
    public void Firewall_creates_repairs_and_removes_owned_rule(SetupFirewallPolicy policy)
    {
        var native = new Firewall();
        var setting = new SetupFirewallSetting(policy, native);
        Assert.Equal(SettingChangeResult.Applied, setting.Apply(_journal));
        Assert.True(native.Value!.SameAs(SetupFirewallRule.Desired(policy)));
        Assert.Equal(SettingChangeResult.AlreadyApplied, setting.Apply(_journal));
        Assert.Equal(1, native.Writes);
        Assert.Equal(SettingChangeResult.Restored, _journal.Restore(setting));
        Assert.Null(native.Value);
    }
    [Theory]
    [InlineData(SetupFirewallPolicy.Discovery)] [InlineData(SetupFirewallPolicy.Echo)]
    public void Identical_preexisting_firewall_rule_is_never_owned(SetupFirewallPolicy policy)
    {
        var native = new Firewall { Value = SetupFirewallRule.Desired(policy) };
        var setting = new SetupFirewallSetting(policy, native);
        Assert.Equal(SettingChangeResult.Unchanged, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Untracked, _journal.Restore(setting));
        Assert.NotNull(native.Value);
        Assert.Equal(0, native.Writes);
    }
    [Fact]
    public void Colliding_firewall_rule_is_preserved_without_journal_ownership()
    {
        var native = new Firewall { Value = SetupFirewallRule.Desired(SetupFirewallPolicy.Discovery) with { Profiles = 1 } };
        var setting = new SetupFirewallSetting(SetupFirewallPolicy.Discovery, native);
        Assert.Throws<InvalidOperationException>(() => setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Untracked, _journal.Restore(setting));
        Assert.Equal(0, native.Writes);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Later_firewall_edits_are_preserved(int property)
    {
        var native = new Firewall();
        var setting = new SetupFirewallSetting(SetupFirewallPolicy.Discovery, native);
        setting.Apply(_journal);
        native.Value = property switch
        {
            0 => native.Value! with { Application = "other.exe" },
            1 => native.Value! with { RemoteAddresses = "LocalSubnet" },
            2 => native.Value! with { LocalUserOwner = "S-1-5-21-1-2-3-1001" },
            _ => native.Value! with { Interfaces = ["other-interface"] },
        };
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
        Assert.Equal(1, native.Writes);
    }
    [Fact]
    public void Ambiguous_firewall_apply_is_not_adopted()
    {
        var native = new Firewall { FailAfterWrite = true };
        var setting = new SetupFirewallSetting(SetupFirewallPolicy.Discovery, native);
        Assert.Throws<IOException>(() => setting.Apply(_journal));
        native.FailAfterWrite = false;
        Assert.Equal(SettingChangeResult.Conflict, setting.Apply(_journal));
        Assert.Equal(SettingChangeResult.Conflict, _journal.Restore(setting));
    }
    [Fact]
    public void Failed_firewall_read_never_means_absence()
    {
        var native = new Firewall { FailRead = true };
        Assert.Throws<IOException>(() => new SetupFirewallSetting(SetupFirewallPolicy.Echo, native).Apply(_journal));
        Assert.Equal(0, native.Writes);
    }
    private sealed class Host : IHostnameSystem
    {
        public HostnameState Value = new("ORIGINAL", "ORIGINAL");
        public int Writes; public bool FailAfterWrite;
        public HostnameState Read() => Value;
        public void SetPending(string expected, string desired)
        {
            Assert.Equal(expected, Value.Pending);
            Value = Value with { Pending = desired }; Writes++;
            if (FailAfterWrite) throw new IOException();
        }
    }
    private sealed class Firewall : ISetupFirewallStore
    {
        public SetupFirewallRule? Value; public int Writes; public bool FailAfterWrite; public bool FailRead;
        public SetupFirewallRule? Read(SetupFirewallPolicy policy) => FailRead ? throw new IOException() : Value;
        public void Write(SetupFirewallPolicy policy, SetupFirewallRule? expected, SetupFirewallRule? desired)
        {
            Assert.True(Value is null ? expected is null : expected is not null && Value.SameAs(expected));
            Value = desired; Writes++;
            if (FailAfterWrite) throw new IOException();
        }
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
