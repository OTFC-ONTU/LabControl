using System.Management;
using System.Runtime.InteropServices;
using LabControl.Shared;
using LabControl.Shared.Setup;

namespace LabControl.Setup;

/// <summary>Use the native Defender provider's additive methods; never replace the list.</summary>
internal sealed class WindowsDefenderExclusionStore : IDefenderExclusionStore
{
    public string? Read() => Guard(ReadCore);

    public void Write(string? expected, string? value) => Guard(() =>
    {
        if ((expected is not null && !DefenderExclusionSetting.IsInstallDirectory(expected))
            || (value is not null && !DefenderExclusionSetting.IsInstallDirectory(value)))
            throw new IOException();
        if (!string.Equals(ReadCore(), expected, StringComparison.Ordinal)) throw new IOException();
        if (expected == value) return true;
        using var provider = new ManagementClass(Scope(Defaults.DefenderWmiNamespace),
            new ManagementPath(Defaults.DefenderPreferenceClass), null);
        var method = value is null ? "Remove" : "Add";
        using var arguments = provider.GetMethodParameters(method);
        arguments[Defaults.DefenderExclusionPathProperty] = new[] { value ?? expected! };
        arguments["Force"] = true;
        // Recheck after connecting to WMI, immediately before the mutation.
        if (!string.Equals(ReadCore(), expected, StringComparison.Ordinal)) throw new IOException();
        using var result = provider.InvokeMethod(method, arguments, new InvokeMethodOptions { Timeout = Defaults.SetupWmiTimeout });
        if (result?["ReturnValue"] is not uint status || status != 0
            || !string.Equals(ReadCore(), value, StringComparison.Ordinal)) throw new IOException();
        return true;
    });

    private static string? ReadCore()
    {
        var first = Once();
        if (!string.Equals(first, Once(), StringComparison.Ordinal)) throw new IOException();
        return first;

        static string? Once()
        {
            using var search = new ManagementObjectSearcher(Scope(Defaults.DefenderWmiNamespace),
                new ObjectQuery($"SELECT {Defaults.DefenderExclusionPathProperty} FROM {Defaults.DefenderPreferenceClass}"),
                new System.Management.EnumerationOptions { Timeout = Defaults.SetupWmiTimeout });
            using var results = search.Get();
            string? found = null;
            var count = 0;
            foreach (ManagementObject instance in results)
            {
                using (instance)
                {
                    count++;
                    var raw = instance[Defaults.DefenderExclusionPathProperty];
                    if (raw is null) continue;
                    if (raw is not string[] paths) throw new IOException();
                    foreach (var path in paths.Where(DefenderExclusionSetting.IsInstallDirectory))
                    {
                        if (found is not null) throw new IOException();
                        found = path;
                    }
                }
            }
            if (count != 1) throw new IOException();
            return found;
        }
    }

    internal static ManagementScope Scope(string path) => new(path,
        new ConnectionOptions { EnablePrivileges = true, Timeout = Defaults.SetupWmiTimeout });

    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass)
        { throw new DefenderProviderUnavailableException(); }
        catch (COMException ex) when (ex.HResult is (int)ManagementStatus.InvalidNamespace or (int)ManagementStatus.InvalidClass)
        { throw new DefenderProviderUnavailableException(); }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            throw new IOException("The Defender exclusion could not be read or changed safely. Check antivirus policy or tamper protection; existing exclusions were not replaced.");
        }
    }
}

internal sealed class DefenderProviderUnavailableException() : IOException("The Defender WMI provider is not installed.");

internal sealed record WindowsAntivirusProduct(string Name, bool IsMicrosoftDefender);

internal static class WindowsAntivirusInventory
{
    /// <summary>Return every registered product, including inactive products. A query
    /// failure is unknown inventory, not evidence that no antivirus is installed.</summary>
    public static IReadOnlyList<WindowsAntivirusProduct> Read()
    {
        try
        {
            using var search = new ManagementObjectSearcher(WindowsDefenderExclusionStore.Scope(Defaults.SecurityCenterWmiNamespace),
                new ObjectQuery($"SELECT displayName, pathToSignedReportingExe FROM {Defaults.AntivirusProductClass}"),
                new System.Management.EnumerationOptions { Timeout = Defaults.SetupWmiTimeout });
            using var results = search.Get();
            var products = new List<WindowsAntivirusProduct>();
            var defender = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Defaults.DefenderReportingRelativePath);
            foreach (ManagementObject product in results)
            {
                using (product)
                {
                    if (product["displayName"] is not string name || string.IsNullOrWhiteSpace(name))
                        throw new IOException();
                    var reporting = product["pathToSignedReportingExe"] as string;
                    var isDefender = reporting is not null && string.Equals(
                        Environment.ExpandEnvironmentVariables(reporting), defender, StringComparison.OrdinalIgnoreCase);
                    products.Add(new(new string(name.Where(c => !char.IsControl(c)).Take(256).ToArray()), isDefender));
                }
            }
            return products.Distinct().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            throw new IOException("Registered antivirus products could not be detected. Review antivirus exclusions before relying on this installation.");
        }
    }
}
