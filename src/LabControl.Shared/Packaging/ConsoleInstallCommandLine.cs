using System.Globalization;

namespace LabControl.Shared.Packaging;

/// <summary>One parsed command line of <c>LabControl.ConsoleSetup.exe</c>.</summary>
public sealed record ConsoleInstallCommand
{
    public ConsoleInstallRequest Request { get; init; } = new();

    /// <summary>Set by the relaunch of <c>--firewall</c> so an elevation loop is impossible.</summary>
    public bool Elevated { get; init; }

    public bool Help { get; init; }

    /// <summary>
    /// Internal: the temporary copy that finishes an uninstall by removing the install
    /// directory the running executable was still sitting in.
    /// </summary>
    public string? FinishRemovalDirectory { get; init; }

    /// <summary>
    /// Internal: the uninstaller that started the temporary copy. The copy waits for that
    /// process to exit before its first delete — otherwise it races an uninstaller that is
    /// still writing its log, and the program files survive with the Installed-apps entry
    /// already gone (D-59 item 1).
    /// </summary>
    public int? FinishRemovalProcessId { get; init; }
}

/// <summary>
/// The installer's command line (D-59 item 1). Parsing lives here, next to the plan, so
/// both are covered by the cross-platform tests; the Windows executable only executes.
/// </summary>
public static class ConsoleInstallCommandLine
{
    public const string FinishRemovalSwitch = "--finish-removal";

    /// <summary>The process the temporary copy waits for before it deletes anything.</summary>
    public const string FinishRemovalProcessSwitch = "--finish-removal-pid";

    public static string Usage =>
        $"""
         {Defaults.ConsoleProductName} installer

           LabControl.ConsoleSetup.exe                        install for the signed-in user
           LabControl.ConsoleSetup.exe {Defaults.ConsoleSetupDesktopShortcutSwitch}     install and add a desktop icon
           LabControl.ConsoleSetup.exe {Defaults.ConsoleSetupUninstallSwitch}            remove the app; the saved labs stay
           LabControl.ConsoleSetup.exe {Defaults.ConsoleSetupRemoveDataSwitch}          delete the saved labs, keys and logs
           LabControl.ConsoleSetup.exe {Defaults.ConsoleSetupFirewallSwitch}             allow the LAN in (asks for an administrator)

         Add {Defaults.ConsoleSetupDryRunSwitch} to any of these to print the plan and change nothing.
         """;

    public static bool TryParse(string[] args, out ConsoleInstallCommand command, out string? error)
    {
        command = new ConsoleInstallCommand();
        error = null;

        ConsoleInstallAction? action = null;
        var desktop = false;
        var dryRun = false;
        var elevated = false;
        var help = false;
        string? finishRemoval = null;
        int? finishRemovalProcess = null;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            switch (argument)
            {
                case "--help" or "-h" or "/?":
                    help = true;
                    break;

                case Defaults.ConsoleSetupDryRunSwitch:
                    dryRun = true;
                    break;

                case Defaults.ConsoleSetupDesktopShortcutSwitch:
                    desktop = true;
                    break;

                case Defaults.ConsoleSetupElevatedSwitch:
                    elevated = true;
                    break;

                case Defaults.ConsoleSetupUninstallSwitch:
                case Defaults.ConsoleSetupRemoveDataSwitch:
                case Defaults.ConsoleSetupFirewallSwitch:
                    var wanted = argument switch
                    {
                        Defaults.ConsoleSetupUninstallSwitch => ConsoleInstallAction.Uninstall,
                        Defaults.ConsoleSetupRemoveDataSwitch => ConsoleInstallAction.RemoveData,
                        _ => ConsoleInstallAction.Firewall,
                    };
                    if (action is { } already && already != wanted)
                    {
                        error = "Choose one of " + Defaults.ConsoleSetupUninstallSwitch + ", "
                            + Defaults.ConsoleSetupRemoveDataSwitch + " and " + Defaults.ConsoleSetupFirewallSwitch + ".";
                        return false;
                    }

                    action = wanted;
                    break;

                case FinishRemovalSwitch:
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        error = FinishRemovalSwitch + " needs the directory to remove.";
                        return false;
                    }

                    finishRemoval = args[++i];
                    action ??= ConsoleInstallAction.Uninstall;
                    break;

                case FinishRemovalProcessSwitch:
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                        || processId <= 0)
                    {
                        error = FinishRemovalProcessSwitch + " needs the process id to wait for.";
                        return false;
                    }

                    finishRemovalProcess = processId;
                    i++;
                    break;

                default:
                    error = "Unknown option: " + argument;
                    return false;
            }
        }

        var resolved = action ?? ConsoleInstallAction.Install;
        if (desktop && resolved != ConsoleInstallAction.Install)
        {
            error = Defaults.ConsoleSetupDesktopShortcutSwitch + " only applies when installing.";
            return false;
        }

        if (finishRemovalProcess is not null && finishRemoval is null)
        {
            error = FinishRemovalProcessSwitch + " only applies together with " + FinishRemovalSwitch + ".";
            return false;
        }

        command = new ConsoleInstallCommand
        {
            Request = new ConsoleInstallRequest { Action = resolved, DesktopShortcut = desktop, DryRun = dryRun },
            Elevated = elevated,
            Help = help,
            FinishRemovalDirectory = finishRemoval,
            FinishRemovalProcessId = finishRemovalProcess,
        };
        return true;
    }
}
