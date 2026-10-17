using LabControl.Shared;
using LabControl.Shared.Persistence;
using LabControl.Shared.Setup;

namespace LabControl.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var build = typeof(Program).Module.ModuleVersionId.ToString("N")[..8];
        System.Console.WriteLine($"LabControl Setup {version} build {build}");

        var failureShown = false;
        var removing = args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(Environment.ProcessPath), Defaults.UninstallExecutableName, StringComparison.OrdinalIgnoreCase);
        void ShowFailure(string? resource = null)
        {
            if (failureShown || !Environment.UserInteractive || System.Diagnostics.Process.GetCurrentProcess().SessionId == 0
                || System.Console.IsOutputRedirected || args.Contains("--number", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase) || args.Contains(Defaults.FinishUninstallSwitch, StringComparer.OrdinalIgnoreCase)) return;
            failureShown = true;
            try
            {
                System.Windows.Forms.MessageBox.Show(SetupDialog.Text(resource ?? (removing ? "UninstallFailed" : "OperationFailed")), SetupDialog.Text("Title"),
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
            catch (Exception) { /* Notification failure must not replace the operation's exit code. */ }
        }
        try
        {
            if (args.Length == 3 && args[0] == Defaults.FinishUninstallSwitch)
            {
                if (!Guid.TryParseExact(args[1], "D", out _) || !int.TryParse(args[2], out var parentPid)) return 2;
                return UninstallCleanup.Finish(args[1], parentPid);
            }
            if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
            {
                System.Console.WriteLine("Setup.exe [--number N] [--no-student | --create-student] [--payload directory] [--dry-run] [--no-reboot]");
                System.Console.WriteLine("Setup.exe --uninstall [--remove-student --confirm-remove-student] | --rekey --payload directory");
                return 0;
            }
            if (string.Equals(Path.GetFileName(Environment.ProcessPath), Defaults.UninstallExecutableName, StringComparison.OrdinalIgnoreCase)
                && !args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase)) args = ["--uninstall", .. args];
            var options = SetupArguments.Parse(args);
            using var processLock = SetupCoordinator.AcquireProcessLock();
            if (options.Uninstall)
            {
                var removalResult = UninstallCoordinator.Run(options);
                if (removalResult == 1) ShowFailure();
                return removalResult;
            }
            if (options.Rekey)
            {
                var rekeyResult = SetupCoordinator.Rekey(options, options.Payload ?? AppContext.BaseDirectory);
                if (rekeyResult == 1) ShowFailure();
                return rekeyResult;
            }
            var directory = options.Payload ?? AppContext.BaseDirectory;
            if (!File.Exists(Path.Combine(directory, Defaults.SetupFileName))) directory = Path.Combine(directory, Defaults.PayloadDirectoryName);
            var payload = SetupPayload.Open(directory);
            using var authority = payload.Authority;
            var state = new InstallationState(Defaults.AgentDataDirectory).Read();
            var number = options.Number ?? state?.Number ?? AgentProvisioning.NumberFromHostname(Environment.MachineName) ?? payload.Document.NextNumber;
            var create = options.CreateStudent;
            if (options.Number is null && !options.DryRun)
            {
                var choice = SetupDialog.Choose(number, create ?? state?.CreateStudentAccount ?? true);
                if (choice is null) return 2;
                number = choice.Value.Number;
                create = choice.Value.CreateStudent;
            }
            var result = SetupCoordinator.Install(options, number, create, directory);
            if (result == 0 && !options.DryRun)
            {
                var previousColor = System.Console.ForegroundColor;
                try
                {
                    System.Console.ForegroundColor = ConsoleColor.Green;
                    System.Console.WriteLine(SetupDialog.Text("Completed"));
                }
                finally { System.Console.ForegroundColor = previousColor; }
                try { payload.AdvanceNumber(number); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { System.Console.WriteLine(SetupDialog.Text("UsbNumberNotSaved")); }
            }
            if (result != 0 && options.Number is null && !options.DryRun) ShowFailure("Failed");
            if (result == 0 && !options.DryRun && !options.NoReboot && SetupDialog.RebootCountdown())
            {
                var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false };
                start.ArgumentList.Add("/r"); start.ArgumentList.Add("/t"); start.ArgumentList.Add("0");
                using var reboot = System.Diagnostics.Process.Start(start) ?? throw new IOException("Windows could not start the reboot.");
            }
            return result;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or SchemaVersionException)
        {
            System.Console.Error.WriteLine(error.Message);
            ShowFailure();
            return 1;
        }
        catch (Exception)
        {
            System.Console.Error.WriteLine("Setup could not complete a Windows operation. Existing accounts and unresolved settings were preserved; repair can retry.");
            ShowFailure();
            return 1;
        }
    }
}
