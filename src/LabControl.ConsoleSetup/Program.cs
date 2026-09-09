using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using LabControl.Shared;
using LabControl.Shared.Packaging;
using Windows.Win32;

namespace LabControl.ConsoleSetup;

/// <summary>
/// The per-user installer of the teacher console (D-59 item 1). It installs into
/// <c>%LOCALAPPDATA%\Programs\LabControl\Console\</c>, registers the two file types and the
/// Installed-apps entry for the signed-in user only, and never asks for an administrator
/// except for the one step that genuinely needs one: <c>--firewall</c>.
///
/// It carries no lab data and no secret: the program files and nothing else.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!ConsoleInstallCommandLine.TryParse(args, out var command, out var error))
        {
            System.Console.Error.WriteLine(error);
            System.Console.Error.WriteLine();
            System.Console.Error.WriteLine(ConsoleInstallCommandLine.Usage);
            return 2;
        }

        if (command.Help)
        {
            System.Console.Out.WriteLine(ConsoleInstallCommandLine.Usage);
            return 0;
        }

        ConsoleInstallLayout layout;
        try
        {
            layout = ConsoleInstallLayout.Current();
        }
        catch (InvalidOperationException failure)
        {
            // Windows would not say where this user's profile is; nothing may be written blind.
            System.Console.Error.WriteLine(failure.Message);
            return 5;
        }

        var log = new SetupLog(layout.LogFile);

        if (command.FinishRemovalDirectory is { } finishing)
        {
            return SetupRunner.FinishRemoval(finishing, command.FinishRemovalProcessId, layout, log);
        }

        SweepOldCopies(log);

        var code = Execute(command, layout, log);

        // The log lives outside the data directory, so removing the labs has to remove it too:
        // it is the last file on the machine that still names this teacher's installation.
        if (code == 0 && command.Request.Action == ConsoleInstallAction.RemoveData)
        {
            log.Remove();
        }

        Pause(command);
        return code;
    }

    private static int Execute(ConsoleInstallCommand command, ConsoleInstallLayout layout, SetupLog log)
    {
        var runner = new SetupRunner(layout, log, Version);

        if (command.Request.Action == ConsoleInstallAction.Install && !ConsolePayload.IsPresent)
        {
            // A dry run changes nothing, so a payload-less build may still print its plan —
            // which is exactly what a developer or a reviewer wants from `dotnet build`.
            if (!command.Request.DryRun)
            {
                System.Console.Error.WriteLine(
                    "This installer was built without the " + Defaults.ConsoleProductName + " program files, so it cannot install anything.");
                System.Console.Error.WriteLine(
                    "Build the real installer with tools/package-windows.sh (it publishes the console and embeds it) and run that.");
                return 3;
            }

            System.Console.Out.WriteLine(
                "Note: this build carries no program files, so the plan below counts none. "
                + "tools/package-windows.sh builds the installer a teacher runs.");
        }

        if (command.Request.Action == ConsoleInstallAction.Firewall && !command.Request.DryRun && !runner.IsElevated)
        {
            return command.Elevated ? Denied(log) : Elevate(log);
        }

        try
        {
            return runner.Run(command);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
            or InvalidOperationException or InvalidDataException or SecurityException)
        {
            // A corrupt payload, a registry this user may not write, a file that vanished: all
            // of them are things to say in one sentence, never a stack trace at a teacher.
            System.Console.Error.WriteLine(failure.Message);
            log.Say("failed: " + failure.Message);
            return 1;
        }
    }

    /// <summary>
    /// Removes the copies of this installer that earlier uninstalls left in the temp directory
    /// (D-59 item 1). Each one is a working ~60 MB installer; one that survived an interrupted
    /// uninstall must not sit there waiting to be double-clicked.
    /// </summary>
    private static void SweepOldCopies(SetupLog log)
    {
        try
        {
            var swept = ConsoleTempCopies.Sweep(
                Path.GetTempPath(), Environment.ProcessPath, ConsoleTempCopies.Stale, DateTime.UtcNow);
            if (swept.Count > 0)
            {
                log.Say("swept " + swept.Count + " leftover copy/copies of this installer out of the temp directory");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Housekeeping never stops an installation.
        }
    }

    /// <summary>Asks Windows for the one elevated step, once. The child carries --elevated, so it can never ask again.</summary>
    private static int Elevate(SetupLog log)
    {
        var self = Environment.ProcessPath;
        if (self is null)
        {
            return Denied(log);
        }

        try
        {
            var start = new ProcessStartInfo(self)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { Defaults.ConsoleSetupFirewallSwitch, Defaults.ConsoleSetupElevatedSwitch },
            };
            using var elevated = Process.Start(start);
            if (elevated is null)
            {
                return Denied(log);
            }

            elevated.WaitForExit();
            return elevated.ExitCode;
        }
        catch (Win32Exception)
        {
            // The teacher answered No to the Windows prompt, or policy forbids elevation.
            return Denied(log);
        }
    }

    private static int Denied(SetupLog log)
    {
        log.Say("LAN access was not granted. Run these two lines in an elevated Command Prompt instead:");
        foreach (var line in ConsoleFirewallRules.NetshLines())
        {
            log.Say("  " + line);
        }

        return 4;
    }

    /// <summary>
    /// Only when this process owns its console window — a double-click from Explorer — is
    /// there anything to keep open, and only when somebody is there to read it.
    ///
    /// Two runs must never wait for a keypress. The elevated <c>--firewall</c> child is being
    /// waited for by the console's <i>Allow…</i> banner, which would sit on a keypress nobody
    /// is going to give it; and an uninstall is followed by a temporary copy that cannot delete
    /// the program files until this process has exited, so a pause there leaves the executables
    /// on the machine with the Installed-apps entry already gone.
    /// </summary>
    private static void Pause(ConsoleInstallCommand command)
    {
        if (command.Elevated
            || command.Request.Action == ConsoleInstallAction.Uninstall
            || System.Console.IsInputRedirected
            || System.Console.IsOutputRedirected
            || !OwnsConsoleWindow())
        {
            return;
        }

        System.Console.Out.WriteLine();
        System.Console.Out.Write("Press Enter to close this window. ");
        System.Console.In.ReadLine();
    }

    private static bool OwnsConsoleWindow()
    {
        try
        {
            Span<uint> processes = stackalloc uint[4];
            return PInvoke.GetConsoleProcessList(processes) == 1;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    private static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
