using System.Globalization;
using System.Text.Json;

namespace LabControl.Shared.Setup;

public enum SetupFirewallPolicy { Discovery, Echo }

/// <summary>All INetFwRule3 properties participate in ownership comparisons. Values
/// irrelevant to the selected protocol are null rather than queried through COM.</summary>
public sealed record SetupFirewallRule
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Application { get; init; } = "";
    public string Service { get; init; } = "";
    public int Protocol { get; init; }
    public string? LocalPorts { get; init; }
    public string? RemotePorts { get; init; }
    public string LocalAddresses { get; init; } = "*";
    public string RemoteAddresses { get; init; } = "*";
    public string? IcmpTypesAndCodes { get; init; }
    public int Direction { get; init; } = 1;
    public string[] Interfaces { get; init; } = [];
    public string InterfaceTypes { get; init; } = "All";
    public bool Enabled { get; init; } = true;
    public string Grouping { get; init; } = Defaults.SetupFirewallGroup;
    public int Profiles { get; init; } = 7;
    public bool EdgeTraversal { get; init; }
    public int Action { get; init; } = 1;
    public int EdgeTraversalOptions { get; init; }
    public string LocalAppPackageId { get; init; } = "";
    public string LocalUserOwner { get; init; } = "";
    public string LocalUserAuthorizedList { get; init; } = "";
    public string RemoteUserAuthorizedList { get; init; } = "";
    public string RemoteMachineAuthorizedList { get; init; } = "";
    public int SecureFlags { get; init; }

    public static SetupFirewallRule Desired(SetupFirewallPolicy policy) => policy switch
    {
        SetupFirewallPolicy.Discovery => new()
        {
            Name = Defaults.SetupDiscoveryFirewallRule, Protocol = 17,
            LocalPorts = Defaults.BeaconPort.ToString(CultureInfo.InvariantCulture), RemotePorts = "*",
        },
        SetupFirewallPolicy.Echo => new()
        {
            Name = Defaults.SetupEchoFirewallRule, Protocol = 1, IcmpTypesAndCodes = "8:*",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };
    public byte[] Encode() => [1, .. JsonSerializer.SerializeToUtf8Bytes(this)];
    public bool SameAs(SetupFirewallRule other) => Encode().AsSpan().SequenceEqual(other.Encode());
}

public interface ISetupFirewallStore
{
    // Enumerate all rules: duplicate matching names are conflicts, never use Item(name).
    SetupFirewallRule? Read(SetupFirewallPolicy policy);
    // Creation is absent -> code-defined rule only. Removal is exact expected -> absent.
    void Write(SetupFirewallPolicy policy, SetupFirewallRule? expected, SetupFirewallRule? desired);
}

public sealed class SetupFirewallSetting(SetupFirewallPolicy policy, ISetupFirewallStore store) : ISetupSetting
{
    private bool _read;
    private SetupFirewallRule? _last;
    public string Id => policy switch
    {
        SetupFirewallPolicy.Discovery => "machine.firewall-discovery",
        SetupFirewallPolicy.Echo => "machine.firewall-echo",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };
    public SetupCheck Check(SetupSettingsJournal journal) => journal.Check(this, _ => SetupFirewallRule.Desired(policy).Encode());
    public SettingChangeResult Apply(SetupSettingsJournal journal)
    {
        var desired = SetupFirewallRule.Desired(policy).Encode();
        return journal.ApplyFromCurrent(this, current =>
        {
            if (current is not null && !current.AsSpan().SequenceEqual(desired))
                throw new InvalidOperationException("A firewall rule with the required name already exists with different settings; preserve it for review.");
            return desired;
        });
    }
    public byte[]? Read()
    {
        _read = false;
        _last = store.Read(policy);
        _read = true;
        return _last?.Encode();
    }
    public void Write(byte[]? value)
    {
        if (!_read) throw new InvalidOperationException("Read the firewall rule before changing it.");
        _read = false;
        var desired = value is null ? null : SetupFirewallRule.Desired(policy);
        if (value is not null && !value.AsSpan().SequenceEqual(desired!.Encode()))
            throw new InvalidDataException("The saved firewall rule is unsupported.");
        if (_last is not null && desired is not null)
            throw new InvalidOperationException("Setup never replaces an existing firewall rule.");
        store.Write(policy, _last, desired);
    }
}
