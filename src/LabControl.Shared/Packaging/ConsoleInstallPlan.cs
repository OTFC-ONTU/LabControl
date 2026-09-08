using System.Globalization;
using System.Text;

namespace LabControl.Shared.Packaging;

/// <summary>What one run of the console installer was asked to do (D-59 item 1).</summary>
public enum ConsoleInstallAction
{
    /// <summary>Install or replace the console for the signed-in user.</summary>
    Install = 0,

    /// <summary>Remove everything this installer owns; the saved labs stay.</summary>
    Uninstall = 1,

    /// <summary>Add the two inbound rules. The only action that needs elevation.</summary>
    Firewall = 2,

    /// <summary>Delete the console's data directory — labs, keys and logs. Never part of uninstall.</summary>
    RemoveData = 3,
}

/// <summary>The command line, parsed. <see cref="ConsoleInstallPlan"/> turns it into steps.</summary>
public sealed record ConsoleInstallRequest
{
    public ConsoleInstallAction Action { get; init; } = ConsoleInstallAction.Install;

    /// <summary>Off by default: a desktop icon is a preference, not part of installing.</summary>
    public bool DesktopShortcut { get; init; }

    /// <summary>Print the plan and change nothing.</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// One step. <see cref="Id"/> is a stable English key the executor switches on and the log
/// records; <see cref="Text"/> is the same step spelled out with the real paths, which is
/// what <c>--dry-run</c> prints. Every step is idempotent: running the installer twice
/// produces the same machine, and each step reports <i>done</i> or <i>already</i>.
/// </summary>
public sealed record ConsoleInstallStep(string Id, string Text);

/// <summary>
/// The whole of what the per-user Windows installer will do, as data (D-59 item 1). The
/// plan is built before anything is touched, so <c>--dry-run</c> prints exactly the steps
/// the same run would execute, in the same order — including the ones the executor will
/// find already satisfied.
/// </summary>
public sealed class ConsoleInstallPlan
{
    private ConsoleInstallPlan(ConsoleInstallAction action, string version, IReadOnlyList<ConsoleInstallStep> steps)
    {
        Action = action;
        Version = version;
        Steps = steps;
    }

    public ConsoleInstallAction Action { get; }

    public string Version { get; }

    public IReadOnlyList<ConsoleInstallStep> Steps { get; }

    // Step ids. Kept as constants so the executor, the log and the tests agree by name.
    public const string CheckLock = "check.lock";
    public const string FilesReplace = "files.replace";
    public const string FilesRemove = "files.remove";
    public const string ShortcutStartMenu = "shortcut.start-menu";
    public const string ShortcutDesktop = "shortcut.desktop";
    public const string ShortcutStartMenuRemove = "shortcut.start-menu.remove";
    public const string ShortcutDesktopRemove = "shortcut.desktop.remove";
    public const string RegistryUninstall = "registry.uninstall";
    public const string RegistryUninstallRemove = "registry.uninstall.remove";
    public const string ShellNotify = "shell.notify";
    public const string FirewallHint = "firewall.hint";
    public const string FirewallRemove = "firewall.remove";
    public const string DataKeep = "data.keep";
    public const string DataRemove = "data.remove";

    public static string ProgIdStep(ConsoleFileType type) => "progid" + type.Extension;

    public static string ProgIdRemoveStep(ConsoleFileType type) => "progid" + type.Extension + ".remove";

    public static string AssociationStep(ConsoleFileType type) => "assoc" + type.Extension;

    public static string AssociationRemoveStep(ConsoleFileType type) => "assoc" + type.Extension + ".remove";

    public static string FirewallStep(ConsoleFirewallRuleSpec spec) =>
        "firewall." + spec.ProtocolName.ToLowerInvariant() + "." + spec.Port.ToString(CultureInfo.InvariantCulture);

    /// <param name="payloadFileCount">
    /// How many files the embedded console publish contains. Only <see cref="ConsoleInstallAction.Install"/>
    /// uses it; the caller has already refused to install without a payload.
    /// </param>
    public static ConsoleInstallPlan Build(
        ConsoleInstallRequest request,
        ConsoleInstallLayout layout,
        string version,
        int payloadFileCount)
    {
        var steps = new List<ConsoleInstallStep>();

        switch (request.Action)
        {
            case ConsoleInstallAction.Install:
                steps.Add(new(CheckLock, $"Check that no {Defaults.ConsoleProductName} is running ({layout.LockFile})"));
                steps.Add(new(FilesReplace, $"Put {payloadFileCount} file(s) into {layout.InstallDirectory}, replacing an older copy"));
                steps.Add(new(ShortcutStartMenu, $"Create the Start-menu shortcut {layout.StartMenuShortcut}"));
                if (request.DesktopShortcut)
                {
                    steps.Add(new(ShortcutDesktop, $"Create the desktop shortcut {layout.DesktopShortcut}"));
                }

                steps.Add(new(RegistryUninstall, $"Register \"{Defaults.ConsoleProductName}\" {version} in Installed apps (HKCU\\{ConsoleUninstallEntry.Key})"));
                foreach (var type in ConsoleFileTypes.All)
                {
                    steps.Add(new(ProgIdStep(type),
                        $"Register the file type {type.ProgId} ({type.Extension}) as {ConsoleFileType.CommandFor(layout.ConsoleExecutable)}"));
                }

                foreach (var type in ConsoleFileTypes.All)
                {
                    steps.Add(new(AssociationStep(type),
                        $"Offer {Defaults.ConsoleProductName} for {type.Extension}, and make it the default only when Windows holds no user choice"));
                }

                steps.Add(new(ShellNotify, "Tell Explorer that the file types changed"));
                steps.Add(new(FirewallHint,
                    $"Leave LAN access alone: the console asks for \"{layout.SetupExecutable}\" {Defaults.ConsoleSetupFirewallSwitch} when it needs it"));
                break;

            case ConsoleInstallAction.Uninstall:
                steps.Add(new(CheckLock, $"Check that no {Defaults.ConsoleProductName} is running ({layout.LockFile})"));
                foreach (var type in ConsoleFileTypes.All)
                {
                    steps.Add(new(AssociationRemoveStep(type),
                        $"Stop offering {Defaults.ConsoleProductName} for {type.Extension} (a user choice is left alone)"));
                }

                foreach (var type in ConsoleFileTypes.All)
                {
                    steps.Add(new(ProgIdRemoveStep(type), $"Remove the file type {type.ProgId} ({type.Extension})"));
                }

                steps.Add(new(RegistryUninstallRemove, $"Remove the Installed apps entry (HKCU\\{ConsoleUninstallEntry.Key})"));
                steps.Add(new(ShortcutStartMenuRemove, $"Remove the Start-menu shortcut {layout.StartMenuShortcut}"));
                steps.Add(new(ShortcutDesktopRemove, $"Remove the desktop shortcut {layout.DesktopShortcut}"));
                steps.Add(new(FirewallRemove, $"Remove this installer's firewall rules, group \"{ConsoleFirewallRules.Group}\" (needs elevation; other rules are left alone)"));
                steps.Add(new(ShellNotify, "Tell Explorer that the file types changed"));
                steps.Add(new(FilesRemove, $"Remove {layout.InstallDirectory}"));
                steps.Add(new(DataKeep,
                    $"Keep the saved labs, keys and logs in {layout.DataDirectory} ({Defaults.ConsoleSetupRemoveDataSwitch} removes them)"));
                break;

            case ConsoleInstallAction.Firewall:
                foreach (var spec in ConsoleFirewallRules.Required)
                {
                    steps.Add(new(FirewallStep(spec),
                        $"Allow inbound {spec.ProtocolName} {spec.Port} on the Private and Domain profiles, group \"{ConsoleFirewallRules.Group}\", rule \"{spec.Name}\""));
                }

                break;

            case ConsoleInstallAction.RemoveData:
                steps.Add(new(CheckLock, $"Check that no {Defaults.ConsoleProductName} is running ({layout.LockFile})"));
                steps.Add(new(DataRemove,
                    $"Delete every saved lab, lab key, script and log in {layout.DataDirectory}. This cannot be undone and no exported backup is touched"));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        return new ConsoleInstallPlan(request.Action, version, steps);
    }

    public string Header => Action switch
    {
        ConsoleInstallAction.Install => $"{Defaults.ConsoleProductName} {Version} — install for the signed-in user",
        ConsoleInstallAction.Uninstall => $"{Defaults.ConsoleProductName} {Version} — remove",
        ConsoleInstallAction.Firewall => $"{Defaults.ConsoleProductName} {Version} — allow LAN access",
        ConsoleInstallAction.RemoveData => $"{Defaults.ConsoleProductName} {Version} — remove local data",
        _ => Defaults.ConsoleProductName,
    };

    /// <summary>What <c>--dry-run</c> prints: the header, then one <c>id: text</c> line per step.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n');
        foreach (var step in Steps)
        {
            text.Append("  ").Append(step.Id).Append(": ").Append(step.Text).Append('\n');
        }

        return text.ToString();
    }
}
