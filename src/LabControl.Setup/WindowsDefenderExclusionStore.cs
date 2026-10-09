using System.Management;
using System.Runtime.InteropServices;
using System.Diagnostics;
using LabControl.Shared;
using LabControl.Shared.Setup;

namespace LabControl.Setup;

/// <summary>Use the native Defender provider's additive methods; never replace the list.</summary>
internal sealed class WindowsDefenderExclusionStore : IDefenderExclusionStore
{
    public string? Read() => Guard(ReadCore, SetupDiagnosticCode.DefenderReadFailed);

    public void Write(string? expected, string? value) => Guard(() =>
    {
        if ((expected is not null && !DefenderExclusionSetting.IsInstallDirectory(expected))
            || (value is not null && !DefenderExclusionSetting.IsInstallDirectory(value)))
            throw new SetupDiagnosticException(SetupDiagnosticCode.DefenderProviderDataInvalid);
        if (!string.Equals(Read(), expected, StringComparison.Ordinal))
            throw new SetupDiagnosticException(SetupDiagnosticCode.DefenderConcurrentChange);
        if (expected == value) return true;
        using var provider = new ManagementClass(Scope(Defaults.DefenderWmiNamespace),
            new ManagementPath(Defaults.DefenderPreferenceClass), null);
        var method = value is null ? "Remove" : "Add";
        using var arguments = provider.GetMethodParameters(method);
        arguments[Defaults.DefenderExclusionPathProperty] = new[] { value ?? expected! };
        arguments["Force"] = true;
        for (var attempt = 1; ; attempt++)
        {
            // Recheck after connecting to WMI, immediately before the mutation. A retry
            // that finds the previous attempt applied after all is simply done.
            var current = Read();
            if (attempt > 1 && string.Equals(current, value, StringComparison.Ordinal)) break;
            if (!string.Equals(current, expected, StringComparison.Ordinal))
                throw new SetupDiagnosticException(SetupDiagnosticCode.DefenderConcurrentChange);
            using var result = provider.InvokeMethod(method, arguments, new InvokeMethodOptions { Timeout = Defaults.SetupWmiTimeout });
            var status = result?["ReturnValue"];
            if (Succeeded(status)) break;
            if (attempt >= Defaults.SetupDefenderMutationAttempts)
                throw new SetupDiagnosticException(SetupDiagnosticCode.DefenderProviderRejected, StatusNumber(status));
            Thread.Sleep(Defaults.SetupDefenderMutationRetryDelay);
        }
        if (!WaitForValue(value))
            throw new SetupDiagnosticException(SetupDiagnosticCode.DefenderReadBackTimeout);
        return true;
    }, SetupDiagnosticCode.DefenderProviderCallFailed);

    private static string? ReadCore()
    {
        var first = Once();
        if (!string.Equals(first, Once(), StringComparison.Ordinal))
            throw new SetupDiagnosticException(SetupDiagnosticCode.DefenderReadUnstable);
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
                    var paths = raw switch
                    {
                        string path => [path],
                        string[] array => array,
                        _ => throw new SetupDiagnosticException(SetupDiagnosticCode.DefenderProviderDataInvalid),
                    };
                    foreach (var path in paths.Where(DefenderExclusionSetting.IsInstallDirectory))
                    {
                        if (found is not null)
                            throw new SetupDiagnosticException(SetupDiagnosticCode.DefenderProviderDataInvalid);
                        found = path;
                    }
                }
            }
            if (count != 1)
                throw new SetupDiagnosticException(SetupDiagnosticCode.DefenderProviderDataInvalid);
            return found;
        }
    }

    private bool WaitForValue(string? expected)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            try
            {
                if (string.Equals(Read(), expected, StringComparison.Ordinal)) return true;
            }
            catch (SetupDiagnosticException error) when (error.Code is SetupDiagnosticCode.DefenderReadFailed
                or SetupDiagnosticCode.DefenderReadUnstable) { }
            if (elapsed.Elapsed >= Defaults.SetupDefenderReadBackTimeout) break;
            Thread.Sleep(250);
        }
        while (true);
        return false;
    }

    // UInt32 is the provider contract. Older System.Management/provider combinations
    // have also surfaced a successful zero as another CLR integral type.
    private static long? StatusNumber(object? value) => value switch
    {
        byte or ushort or uint or sbyte or short or int or long => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
        ulong status => unchecked((long)status),
        _ => null,
    };

    private static bool Succeeded(object? value) => value switch
    {
        byte status => status == 0,
        ushort status => status == 0,
        uint status => status == 0,
        ulong status => status == 0,
        sbyte status => status == 0,
        short status => status == 0,
        int status => status == 0,
        long status => status == 0,
        _ => false,
    };

    internal static ManagementScope Scope(string path) => new(path,
        new ConnectionOptions { EnablePrivileges = true, Timeout = Defaults.SetupWmiTimeout });

    private static T Guard<T>(Func<T> action, SetupDiagnosticCode fallback)
    {
        try { return action(); }
        catch (SetupDiagnosticException) { throw; }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass)
        { throw new DefenderProviderUnavailableException(); }
        catch (COMException ex) when (ex.HResult is (int)ManagementStatus.InvalidNamespace or (int)ManagementStatus.InvalidClass)
        { throw new DefenderProviderUnavailableException(); }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            throw new SetupDiagnosticException(fallback);
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
