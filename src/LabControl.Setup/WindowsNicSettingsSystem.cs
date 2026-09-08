using System.Management;
using System.Net.NetworkInformation;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Microsoft.Win32;

namespace LabControl.Setup;

/// <summary>WMI identifies hardware/capabilities. Only existing standardized driver
/// REG_SZ switches are written; no adapter disable/restart or guessed vendor aliases.</summary>
internal sealed class WindowsNicSettingsSystem : INicSettingsSystem
{
    public IReadOnlyList<NicIdentity> ListEthernetAdapters() => Guard(() =>
    {
        using var search = Search(Defaults.NicWmiNamespace,
            "SELECT * FROM " + Defaults.NicAdapterWmiClass + " WHERE PhysicalAdapter = TRUE AND AdapterTypeID = 0");
        using var results = search.Get();
        var adapters = new List<NicIdentity>();
        var network = NetworkInterface.GetAllNetworkInterfaces();
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                if (item["GUID"] is not string id || !Guid.TryParse(id, out var guid) || guid == Guid.Empty
                    || item["PNPDeviceID"] is not string pnp || string.IsNullOrWhiteSpace(pnp))
                    throw new NicSettingUnavailableException("A physical Ethernet adapter has no stable identity.");
                // Win32 AdapterTypeID is a media value; Wi-Fi drivers may report
                // Ethernet framing. IP Helper's interface type independently excludes it.
                var actualInterface = RequireNetworkInterface(guid, network);
                if (!NicInterfaceClassification.IsWiredEthernet(actualInterface.NetworkInterfaceType)) continue;
                var mac = item["MACAddress"] as string;
                if (NicWakeSelection.IsUsableMac(mac) && LabControl.Shared.Power.WakeOnLan.TryParseMac(mac, out var address))
                    mac = MachineFacts.FormatMac(address);
                else mac = null;
                adapters.Add(new(guid, pnp.ToUpperInvariant(), item["Name"] as string ?? guid.ToString("D"), mac));
            }
        }
        if (adapters.Select(a => a.InterfaceId).Distinct().Count() != adapters.Count)
            throw new IOException("The Ethernet adapter identity is ambiguous.");
        return (IReadOnlyList<NicIdentity>)adapters;
    });
    public NicWakeSelection SelectWakeAdapter()
    {
        var physical = ListEthernetAdapters();
        var network = NetworkInterface.GetAllNetworkInterfaces();
        var candidates = new List<NicWakeCandidate>();
        foreach (var adapter in physical)
        {
            var match = RequireNetworkInterface(adapter.InterfaceId, network);
            if (!NicInterfaceClassification.IsWiredEthernet(match.NetworkInterfaceType))
                throw new IOException("The physical wake adapter interface type changed during selection.");
            var actual = match.GetPhysicalAddress().GetAddressBytes();
            if (actual.Length != 6 || adapter.MacAddress is null)
            {
                candidates.Add(new(adapter with { MacAddress = null }, match.OperationalStatus == OperationalStatus.Up, match.NetworkInterfaceType));
                continue;
            }
            if (!string.Equals(adapter.MacAddress, MachineFacts.FormatMac(actual), StringComparison.Ordinal))
                throw new IOException("The physical wake adapter hardware address changed during selection.");
            candidates.Add(new(adapter, match.OperationalStatus == OperationalStatus.Up, match.NetworkInterfaceType));
        }
        return NicWakeSelection.Choose(candidates);
    }

    private static NetworkInterface RequireNetworkInterface(Guid interfaceId, NetworkInterface[] network)
    {
        var matches = network.Where(item => Guid.TryParse(item.Id, out var id) && id == interfaceId).ToArray();
        if (matches.Length != 1)
            throw new IOException("The physical adapter cannot be matched to one network interface.");
        return matches[0];
    }

    public IReadOnlyList<NicHardwareInventory> HardwareInventory()
    {
        var adapters = ListEthernetAdapters();
        var drivers = new Dictionary<string, List<(string? Provider, string? Version)>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var search = Search(Defaults.NicWmiNamespace, "SELECT DeviceID, DriverProviderName, DriverVersion FROM " + Defaults.NicDriverWmiClass + " WHERE DeviceClass = 'NET'");
            using var results = search.Get();
            foreach (ManagementObject driver in results)
            {
                using (driver)
                {
                    if (driver["DeviceID"] is not string id) continue;
                    if (!drivers.TryGetValue(id, out var matches)) drivers.Add(id, matches = []);
                    matches.Add((driver["DriverProviderName"] as string, driver["DriverVersion"] as string));
                }
            }
        }
        catch (ManagementException) { drivers.Clear(); } // Inventory is diagnostic, never mutation authority.
        return adapters.Select(adapter =>
            drivers.TryGetValue(adapter.PnpDeviceId, out var matches) && matches.Count == 1
                ? new NicHardwareInventory(adapter, matches[0].Provider, matches[0].Version)
                : new NicHardwareInventory(adapter, null, null)).ToArray();
    }

    public NicSettingSnapshot Read(Guid interfaceId, NicPolicy policy) => Guard(() =>
    {
        var identity = RequireAdapter(interfaceId);
        var first = ReadOnce(identity, policy);
        if (RequireAdapter(interfaceId).PnpDeviceId != identity.PnpDeviceId || first != ReadOnce(identity, policy))
            throw new IOException("The NIC setting changed during inspection.");
        return first;
    });
    public void Write(Guid interfaceId, NicPolicy policy, NicSettingSnapshot expected, NicSettingSnapshot desired) => Guard(() =>
    {
        if (expected.PnpDeviceId != desired.PnpDeviceId || expected.NativeIdentity != desired.NativeIdentity || expected.Kind != desired.Kind)
            throw new IOException("The saved NIC identity cannot select another adapter.");
        if (Read(interfaceId, policy) != expected) throw new IOException("The NIC setting changed before mutation.");
        var identity = RequireAdapter(interfaceId);
        if (policy is NicPolicy.DeviceWake or NicPolicy.DevicePowerManagement or NicPolicy.MagicPacketOnly)
        {
            using var item = PowerInstance(identity, policy);
            if (PowerSnapshot(identity, policy, item) != expected || Read(interfaceId, policy) != expected) throw new IOException();
            item[NicPowerControl.For(policy).PropertyName] = desired.Enabled;
            item.Put(new PutOptions { Type = PutType.UpdateOnly, Timeout = Defaults.SetupWmiTimeout });
        }
        else
        {
            var keyword = Keyword(policy);
            using var key = DriverKey(identity, writable: true);
            if (Read(interfaceId, policy) != expected) throw new IOException();
            // This intentionally writes durable configuration without cycling the NIC.
            // The pipeline must report reboot needed before claiming effective WoL.
            key.SetValue(keyword, desired.Enabled ? "1" : "0", RegistryValueKind.String);
            key.Flush();
        }
        if (Read(interfaceId, policy) != desired) throw new IOException("The NIC setting could not be verified.");
        return true;
    });
    private NicIdentity RequireAdapter(Guid id) => ListEthernetAdapters().SingleOrDefault(a => a.InterfaceId == id)
        ?? throw new NicSettingUnavailableException("The recorded physical Ethernet adapter is no longer present.");
    private static NicSettingSnapshot ReadOnce(NicIdentity identity, NicPolicy policy)
    {
        if (policy is NicPolicy.DeviceWake or NicPolicy.DevicePowerManagement or NicPolicy.MagicPacketOnly)
        {
            using var item = PowerInstance(identity, policy);
            return PowerSnapshot(identity, policy, item);
        }
        var keyword = Keyword(policy);
        using var key = DriverKey(identity);
        RequireAdvancedCapability(identity, keyword);
        if (!key.GetValueNames().Contains(keyword, StringComparer.OrdinalIgnoreCase)
            || key.GetValueKind(keyword) != RegistryValueKind.String || key.GetValue(keyword) is not string value || value is not ("0" or "1"))
            throw new NicSettingUnavailableException("The NIC does not expose a supported existing on/off driver setting.");
        return new(identity.PnpDeviceId, key.Name.ToUpperInvariant(), 1, value == "1");
    }
    private static void RequireAdvancedCapability(NicIdentity identity, string keyword)
    {
        using var search = Search(Defaults.NicAdvancedWmiNamespace, "SELECT * FROM " + Defaults.NicAdvancedWmiClass);
        using var results = search.Get();
        var matches = 0;
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                if (!string.Equals(item["RegistryKeyword"] as string, keyword, StringComparison.OrdinalIgnoreCase)) continue;
                var parts = (item["InstanceID"] as string ?? "").Split("::", StringSplitOptions.None);
                if (parts.Length != 2 || !Guid.TryParse(parts[0], out var guid) || guid != identity.InterfaceId) continue;
                if (item["RegistryDataType"] is not uint kind || kind != 1 || item["Source"] is not uint source || source != 3
                    || item["ValidRegistryValues"] is not string[] valid || !valid.Contains("0") || !valid.Contains("1"))
                    throw new NicSettingUnavailableException("The NIC driver does not advertise a supported on/off capability.");
                matches++;
            }
        }
        if (matches != 1) throw new NicSettingUnavailableException("The NIC driver capability is missing or ambiguous.");
    }
    private static RegistryKey DriverKey(NicIdentity identity, bool writable = false)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var parent = hive.OpenSubKey(Defaults.NicClassRegistryKey) ?? throw new IOException("The network class registry key is missing.");
        RegistryKey? found = null;
        try
        {
            foreach (var name in parent.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;
                var key = parent.OpenSubKey(name, writable);
                if (key is null) continue;
                if (key.GetValue(Defaults.NicInterfaceRegistryValue) is not string id || !Guid.TryParse(id, out var guid) || guid != identity.InterfaceId)
                { key.Dispose(); continue; }
                if (found is not null) { key.Dispose(); throw new IOException("The network registry identity is ambiguous."); }
                found = key;
                if (!string.Equals(key.GetValue(Defaults.NicPnpRegistryValue) as string, identity.PnpDeviceId, StringComparison.OrdinalIgnoreCase))
                    throw new NicSettingUnavailableException("The network driver registry identity does not match the physical adapter.");
            }
            return found ?? throw new NicSettingUnavailableException("The physical NIC has no matching driver registry entry.");
        }
        catch { found?.Dispose(); throw; }
    }
    private static ManagementObject PowerInstance(NicIdentity identity, NicPolicy policy)
    {
        var control = NicPowerControl.For(policy);
        using var search = Search(Defaults.NicPowerWmiNamespace, "SELECT * FROM " + control.ClassName);
        using var results = search.Get();
        ManagementObject? found = null;
        try
        {
            foreach (ManagementObject item in results)
            {
                // Only the conventional exact PNP instance is supported; substring
                // matches can select another NIC or another device entirely.
                if (!NicPowerControl.MatchesInstance(identity.PnpDeviceId, item["InstanceName"] as string))
                { item.Dispose(); continue; }
                if (found is not null) { item.Dispose(); throw new NicSettingUnavailableException("The NIC power instance is ambiguous."); }
                found = item;
                var property = item.Properties.Cast<PropertyData>().SingleOrDefault(property =>
                    string.Equals(property.Name, control.PropertyName, StringComparison.OrdinalIgnoreCase));
                if (item["Active"] is not true || property is null || property.Type != CimType.Boolean || property.Value is not bool
                    || !property.Qualifiers.Cast<QualifierData>().Any(qualifier =>
                        string.Equals(qualifier.Name, "Write", StringComparison.OrdinalIgnoreCase) && qualifier.Value is true))
                    throw new NicSettingUnavailableException("The NIC power capability is inactive, read-only or unsupported.");
            }
            return found ?? throw new NicSettingUnavailableException("The NIC driver does not expose this power capability.");
        }
        catch { found?.Dispose(); throw; }
    }
    private static NicSettingSnapshot PowerSnapshot(NicIdentity identity, NicPolicy policy, ManagementObject item) =>
        new(identity.PnpDeviceId, item.Path.RelativePath.ToUpperInvariant(), 2, (bool)item[NicPowerControl.For(policy).PropertyName]);
    private static string Keyword(NicPolicy policy) => policy switch
    {
        NicPolicy.WakeOnMagicPacket => Defaults.NicMagicRegistryValue,
        NicPolicy.EnergyEfficientEthernet => Defaults.NicEeeRegistryValue,
        NicPolicy.WakeOnPattern => Defaults.NicPatternRegistryValue,
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };
    private static ManagementObjectSearcher Search(string scope, string query) => new(
        new ManagementScope(scope, new ConnectionOptions { Timeout = Defaults.SetupWmiTimeout }),
        new ObjectQuery(query), new System.Management.EnumerationOptions { Timeout = Defaults.SetupWmiTimeout });
    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (ManagementException error) when (error.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.NotSupported or ManagementStatus.InvalidNamespace)
        { throw new NicSettingUnavailableException("This Windows NIC driver does not expose the requested WMI capability."); }
        catch (ManagementException) { throw new IOException("The NIC WMI provider could not complete the operation safely."); }
    }
}
