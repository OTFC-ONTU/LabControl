using System.ComponentModel;
using System.Runtime.InteropServices;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace LabControl.Setup;

internal sealed class WindowsHostnameSystem : IHostnameSystem
{
    public HostnameState Read()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var first = Once();
        if (first != Once()) throw new IOException("The computer names changed during setup.");
        return first;
        HostnameState Once()
        {
            var active = Value(Defaults.ActiveComputerNameRegistryKey, Defaults.ComputerNameRegistryValue);
            var pending = Value(Defaults.PendingComputerNameRegistryKey, Defaults.ComputerNameRegistryValue);
            var dns = Value(Defaults.TcpipParametersRegistryKey, Defaults.PendingDnsHostnameRegistryValue);
            if (pending != dns) throw new InvalidOperationException("Separate DNS and NetBIOS names are configured; preserve them for review.");
            return new(active, pending);
        }
        string Value(string path, string name)
        {
            using var key = hive.OpenSubKey(path) ?? throw new IOException("The computer name registry key is missing.");
            if (key.GetValueKind(name) != RegistryValueKind.String || key.GetValue(name) is not string value)
                throw new IOException("The computer name registry type is unsupported.");
            return HostnameSetting.Normalize(value);
        }
    }
    public void SetPending(string expectedPending, string desired)
    {
        desired = HostnameSetting.Normalize(desired);
        if (Read().Pending != expectedPending) throw new IOException("The pending hostname changed before setup could write it.");
        if (!PInvoke.SetComputerNameEx(COMPUTER_NAME_FORMAT.ComputerNamePhysicalDnsHostname, desired))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not set the pending computer name.");
        if (Read().Pending != desired) throw new IOException("The pending computer name could not be verified.");
    }
}
