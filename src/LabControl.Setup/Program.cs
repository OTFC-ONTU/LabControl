using LabControl.Shared;

namespace LabControl.Setup;

/// <summary>
/// The one-shot USB installer, run once as a local administrator on each student PC
/// (docs/INSTALLER.md). M0 is a skeleton that only prints the plan; the real
/// <c>ISetupStep</c> pipeline is milestone M4.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("LabControl Setup (skeleton)");
        Console.WriteLine($"install directory: {Defaults.AgentInstallDirectory}");
        Console.WriteLine($"data directory:    {Defaults.AgentDataDirectory}");
        Console.WriteLine($"student account:   {Defaults.StudentAccountName} (standard user, auto-logon)");
        Console.WriteLine(dryRun
            ? "--dry-run: nothing would be changed."
            : "Not implemented yet: the setup pipeline lands in milestone M4.");
        return 0;
    }
}
