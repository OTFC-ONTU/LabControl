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
}

/// <summary>
/// The installer's command line (D-59 item 1). Parsing lives here, next to the plan, so
/// both are covered by the cross-platform tests; the Windows executable only executes.
/// </summary>
public static class ConsoleInstallCommandLine
{
    public const string FinishRemovalSwitch = "--finish-removal";

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

        command = new ConsoleInstallCommand
        {
            Request = new ConsoleInstallRequest { Action = resolved, DesktopShortcut = desktop, DryRun = dryRun },
            Elevated = elevated,
            Help = help,
            FinishRemovalDirectory = finishRemoval,
        };
        return true;
    }
}
