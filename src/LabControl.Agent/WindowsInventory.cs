using System.Globalization;
using System.Runtime.InteropServices;
using LabControl.Shared.Protocol;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RemoteDesktop;

namespace LabControl.Agent;

/// <summary>
/// The real inventory (ROADMAP M2): hostname, Windows build, CPU, RAM, disk, uptime and the
/// user in the interactive session. Every call is guarded; a field that cannot be read is
/// left empty rather than failing the whole report.
/// </summary>
internal static class WindowsInventory
{
    private const uint NoSession = 0xFFFFFFFF;

    public static Inventory Collect(string configuredHostname)
    {
        var inventory = new Inventory
        {
            Hostname = Try(() => Environment.MachineName) ?? configuredHostname,
            WindowsBuild = Try(WindowsBuild) ?? string.Empty,
            Cpu = Try(Cpu) ?? string.Empty,
            MemoryBytes = Try(() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes),
            UptimeSeconds = Environment.TickCount64 / 1000,
            LoggedOnUser = Try(ActiveSessionUser) ?? string.Empty,
        };

        var disk = Try(SystemDrive);
        if (disk is not null)
        {
            inventory.DiskTotalBytes = disk.Value.Total;
            inventory.DiskFreeBytes = disk.Value.Free;
        }

        return inventory;
    }

    public static DateTimeOffset BootTime() =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>"Windows 11 Pro 24H2 (build 26100.2894)".</summary>
    private static string WindowsBuild()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (key is null)
        {
            return RuntimeInformation.OSDescription;
        }

        var product = key.GetValue("ProductName") as string ?? "Windows";
        var display = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string ?? string.Empty;
        var build = key.GetValue("CurrentBuild") as string ?? string.Empty;
        var revision = key.GetValue("UBR") is int ubr ? "." + ubr.ToString(CultureInfo.InvariantCulture) : string.Empty;

        // Windows 11 still reports "Windows 10 …" in ProductName; the build number tells them apart.
        if (int.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 22000 && product.StartsWith("Windows 10", StringComparison.Ordinal))
        {
            product = "Windows 11" + product["Windows 10".Length..];
        }

        return $"{product} {display} (build {build}{revision})".Replace("  ", " ", StringComparison.Ordinal).Trim();
    }

    private static string Cpu()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = key?.GetValue("ProcessorNameString") as string;
        var cores = Environment.ProcessorCount;
        return string.IsNullOrWhiteSpace(name)
            ? $"{cores} logical processors"
            : $"{name.Trim()} ({cores} logical)";
    }

    private static (long Total, long Free)? SystemDrive()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (root is null)
        {
            return null;
        }

        var drive = new DriveInfo(root);
        return (drive.TotalSize, drive.AvailableFreeSpace);
    }

    /// <summary>
    /// The account in the physical console session, or empty at the logon screen. Read
    /// through WTS rather than <c>Environment.UserName</c>, which for a service is SYSTEM.
    /// </summary>
    private static unsafe string ActiveSessionUser()
    {
        var session = PInvoke.WTSGetActiveConsoleSessionId();
        if (session == NoSession)
        {
            return string.Empty;
        }

        var user = QuerySessionString(session, WTS_INFO_CLASS.WTSUserName);
        if (user.Length == 0)
        {
            return string.Empty;
        }

        var domain = QuerySessionString(session, WTS_INFO_CLASS.WTSDomainName);
        return domain.Length == 0 || string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            ? user
            : $"{domain}\\{user}";
    }

    private static unsafe string QuerySessionString(uint session, WTS_INFO_CLASS what)
    {
        PWSTR buffer = default;
        uint bytes = 0;
        if (!PInvoke.WTSQuerySessionInformation(HANDLE.Null, session, what, &buffer, &bytes) || buffer.Value is null)
        {
            return string.Empty;
        }

        try
        {
            return buffer.ToString().Trim();
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer.Value);
        }
    }

    private static T? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or ArgumentException or ExternalException)
        {
            return default;
        }
    }
}
