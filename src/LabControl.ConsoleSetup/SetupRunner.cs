using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using LabControl.Shared;
using LabControl.Shared.Packaging;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace LabControl.ConsoleSetup;

/// <summary>
/// Executes a <see cref="ConsoleInstallPlan"/> step by step (D-59 item 1). Every step is
/// idempotent and reports what it did, so a second run of the same installer produces the
/// same machine and a log that says <i>already</i> instead of <i>done</i>.
/// </summary>
internal sealed class SetupRunner(ConsoleInstallLayout layout, SetupLog log, string version)
{
    public bool IsElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public int Run(ConsoleInstallCommand command)
    {
        var payload = command.Request.Action == ConsoleInstallAction.Install ? ConsolePayload.Open() : null;
        var plan = ConsoleInstallPlan.Build(command.Request, layout, version, payload?.Entries.Count ?? 0);
        log.Header(plan);

        if (command.Request.DryRun)
        {
            System.Console.Out.Write(plan.Describe());
            log.Say("dry run: nothing was changed");
            return 0;
        }

        if (command.Request.Action == ConsoleInstallAction.RemoveData && !Confirmed())
        {
            log.Say("cancelled: the saved labs, keys and logs were kept");
            return 1;
        }

        foreach (var step in plan.Steps)
        {
            try
            {
                log.Step(step.Id, Execute(step, payload));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.Fail(step.Id, error);
                log.Say("stopped. Nothing further was changed; the full log is " + log.Path);
                return 1;
            }
        }

        log.Say(plan.Action switch
        {
            ConsoleInstallAction.Install => Defaults.ConsoleProductName + " is installed. Open it from the Start menu.",
            ConsoleInstallAction.Uninstall => Defaults.ConsoleProductName + " is removed. The saved labs are still in " + layout.DataDirectory + ".",
            ConsoleInstallAction.Firewall => "The LAN may now reach " + Defaults.ConsoleProductName + " on this computer.",
            _ => "Done.",
        });
        return 0;
    }

    private string Execute(ConsoleInstallStep step, ConsolePayload? payload)
    {
        if (step.Id == ConsoleInstallPlan.CheckLock)
        {
            return CheckLock();
        }

        if (step.Id == ConsoleInstallPlan.FilesReplace)
        {
            return InstallFiles(payload!);
        }

        if (step.Id == ConsoleInstallPlan.FilesRemove)
        {
            return RemoveFiles();
        }

        if (step.Id == ConsoleInstallPlan.ShortcutStartMenu)
        {
            WindowsShortcut.Write(layout.StartMenuShortcut, layout.ConsoleExecutable, layout.InstallDirectory,
                Defaults.ConsoleProductName, layout.ConsoleExecutable);
            return "written";
        }

        if (step.Id == ConsoleInstallPlan.ShortcutDesktop)
        {
            WindowsShortcut.Write(layout.DesktopShortcut, layout.ConsoleExecutable, layout.InstallDirectory,
                Defaults.ConsoleProductName, layout.ConsoleExecutable);
            return "written";
        }

        if (step.Id == ConsoleInstallPlan.ShortcutStartMenuRemove)
        {
            return WindowsShortcut.Remove(layout.StartMenuShortcut) ? "removed" : "already gone";
        }

        if (step.Id == ConsoleInstallPlan.ShortcutDesktopRemove)
        {
            return WindowsShortcut.Remove(layout.DesktopShortcut) ? "removed" : "already gone";
        }

        if (step.Id == ConsoleInstallPlan.RegistryUninstall)
        {
            WindowsRegistrations.WriteUninstallEntry(layout, version);
            return "written";
        }

        if (step.Id == ConsoleInstallPlan.RegistryUninstallRemove)
        {
            return WindowsRegistrations.RemoveUninstallEntry(layout) ? "removed" : "already gone";
        }

        if (step.Id == ConsoleInstallPlan.ShellNotify)
        {
            Notify();
            return "notified";
        }

        if (step.Id == ConsoleInstallPlan.FirewallHint)
        {
            return "left to the console's LAN access banner";
        }

        if (step.Id == ConsoleInstallPlan.FirewallRemove)
        {
            return RemoveFirewallRules();
        }

        if (step.Id == ConsoleInstallPlan.DataKeep)
        {
            return Directory.Exists(layout.DataDirectory) ? "kept" : "nothing saved on this computer";
        }

        if (step.Id == ConsoleInstallPlan.DataRemove)
        {
            return RemoveData();
        }

        foreach (var type in ConsoleFileTypes.All)
        {
            if (step.Id == ConsoleInstallPlan.ProgIdStep(type))
            {
                WindowsRegistrations.WriteProgId(type, layout);
                return "written";
            }

            if (step.Id == ConsoleInstallPlan.ProgIdRemoveStep(type))
            {
                return WindowsRegistrations.RemoveProgId(type, layout) ? "removed" : "already gone";
            }

            if (step.Id == ConsoleInstallPlan.AssociationStep(type))
            {
                return WindowsRegistrations.Associate(type) ? "offered, and the default for " + type.Extension : "offered; the existing default was kept";
            }

            if (step.Id == ConsoleInstallPlan.AssociationRemoveStep(type))
            {
                return WindowsRegistrations.Disassociate(type) ? "removed" : "already gone";
            }
        }

        foreach (var spec in ConsoleFirewallRules.Required)
        {
            if (step.Id == ConsoleInstallPlan.FirewallStep(spec))
            {
                if (!IsElevated)
                {
                    throw new UnauthorizedAccessException(
                        "Adding a firewall rule needs an administrator. Run: " + spec.NetshLine);
                }

                return WindowsConsoleFirewall.EnsureRule(spec) ? "added" : "already allowed";
            }
        }

        throw new InvalidOperationException("Unknown installer step: " + step.Id);
    }

    private string CheckLock()
    {
        if (!File.Exists(layout.LockFile))
        {
            return "no console is running";
        }

        try
        {
            using var held = new FileStream(layout.LockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return "no console is running";
        }
        catch (IOException)
        {
            throw new IOException(Defaults.ConsoleProductName + " is running. Close it and start this again.");
        }
        catch (UnauthorizedAccessException)
        {
            return "the lock file could not be tested; close the console before continuing";
        }
    }

    private string InstallFiles(ConsolePayload payload)
    {
        Directory.CreateDirectory(layout.InstallDirectory);
        var manifestPath = Path.Combine(layout.InstallDirectory, Defaults.ConsoleInstallManifestFileName);
        var previous = File.Exists(manifestPath)
            ? File.ReadAllLines(manifestPath).Where(line => line.Length > 0).ToArray()
            : [];

        var written = payload.ExtractTo(layout.InstallDirectory);

        // The installed copy of this executable is the uninstaller (the same pattern the
        // student Setup uses), so it is owned and listed like every other file.
        var owned = payload.Entries.ToList();
        var self = Environment.ProcessPath;
        if (self is not null && !string.Equals(Path.GetFullPath(self), Path.GetFullPath(layout.SetupExecutable), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(self, layout.SetupExecutable, true);
            written++;
        }

        owned.Add(Defaults.ConsoleSetupExecutableName);
        File.WriteAllLines(manifestPath, owned.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        var stale = previous.Except(owned, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var relative in stale)
        {
            DeleteOwned(Path.Combine(layout.InstallDirectory, relative));
        }

        return written == 0 && stale.Length == 0
            ? "already up to date (" + owned.Count + " file(s))"
            : written + " file(s) written, " + stale.Length + " no longer needed";
    }

    private string RemoveFiles()
    {
        var manifestPath = Path.Combine(layout.InstallDirectory, Defaults.ConsoleInstallManifestFileName);
        if (!Directory.Exists(layout.InstallDirectory))
        {
            return "already gone";
        }

        var owned = File.Exists(manifestPath)
            ? File.ReadAllLines(manifestPath).Where(line => line.Length > 0).ToArray()
            : [];

        var self = Environment.ProcessPath is { } path ? Path.GetFullPath(path) : null;
        var left = 0;
        foreach (var relative in owned)
        {
            var target = Path.Combine(layout.InstallDirectory, relative);
            if (self is not null && string.Equals(Path.GetFullPath(target), self, StringComparison.OrdinalIgnoreCase))
            {
                left++;
                continue;
            }

            DeleteOwned(target);
        }

        DeleteOwned(manifestPath);
        RemoveEmptyDirectories(layout.InstallDirectory);

        if (left == 0)
        {
            return owned.Length == 0 ? "nothing was owned here" : "removed " + owned.Length + " file(s)";
        }

        HandOverToTemporaryCopy();
        return "removed " + (owned.Length - left) + " file(s); a temporary copy removes this program itself";
    }

    /// <summary>
    /// The uninstaller cannot delete the file it is running from, so it copies itself into
    /// the temp directory and lets that copy finish once this process has exited. Nothing is
    /// scheduled, nothing survives a reboot, and the copy removes only the install directory.
    /// </summary>
    private void HandOverToTemporaryCopy()
    {
        var self = Environment.ProcessPath;
        if (self is null)
        {
            return;
        }

        var worker = Path.Combine(Path.GetTempPath(),
            "LabControl.ConsoleSetup." + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(self, worker, true);
        using var process = Process.Start(new ProcessStartInfo(worker)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                ConsoleInstallCommandLine.FinishRemovalSwitch,
                layout.InstallDirectory,
            },
        });
    }

    /// <summary>The temporary copy's whole job: wait for the uninstaller to exit, then remove what is left.</summary>
    public static int FinishRemoval(string directory, SetupLog log)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    log.Step(ConsoleInstallPlan.FilesRemove, "removed by the temporary copy");
                    return 0;
                }

                Directory.Delete(directory, true);
                log.Step(ConsoleInstallPlan.FilesRemove, "removed by the temporary copy");
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 30)
                {
                    log.Fail(ConsoleInstallPlan.FilesRemove, error);
                    return 1;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }
        }

        return 1;
    }

    private string RemoveFirewallRules()
    {
        var removed = new List<string>();
        foreach (var spec in ConsoleFirewallRules.Required)
        {
            try
            {
                if (WindowsConsoleFirewall.RemoveOwnedRule(spec))
                {
                    removed.Add(spec.Name);
                }
            }
            catch (IOException)
            {
                return "skipped — removing a firewall rule needs an administrator. To do it by hand: "
                    + string.Join("  ", ConsoleFirewallRules.Required.Select(rule =>
                        "netsh advfirewall firewall delete rule name=\"" + rule.Name + "\""));
            }
        }

        return removed.Count == 0 ? "no rule of this installer was present" : "removed " + string.Join(", ", removed);
    }

    private string RemoveData()
    {
        if (!Directory.Exists(layout.DataDirectory))
        {
            return "nothing saved on this computer";
        }

        Directory.Delete(layout.DataDirectory, true);
        return "removed " + layout.DataDirectory;
    }

    /// <summary>The one destructive action, so it is typed out in full and never assumed.</summary>
    private bool Confirmed()
    {
        if (System.Console.IsInputRedirected)
        {
            log.Say("Refusing to delete " + layout.DataDirectory + " without an answer typed at the keyboard.");
            return false;
        }

        System.Console.Out.WriteLine();
        System.Console.Out.WriteLine("This deletes every saved lab, lab key, script and log in " + layout.DataDirectory + ".");
        System.Console.Out.WriteLine("A lab whose key exists nowhere else can never be driven again, and every student PC");
        System.Console.Out.WriteLine("would have to be visited with a USB stick. Exported .lcbak backups are not touched.");
        System.Console.Out.Write("Type REMOVE to continue: ");
        return string.Equals(System.Console.In.ReadLine()?.Trim(), "REMOVE", StringComparison.Ordinal);
    }

    private static void DeleteOwned(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The delete below reports the real problem.
        }

        File.Delete(path);
    }

    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(root))
        {
            RemoveEmptyDirectories(directory);
        }

        if (Directory.GetFileSystemEntries(root).Length == 0)
        {
            Directory.Delete(root);
        }
    }

    private static void Notify()
    {
        unsafe
        {
            PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null);
        }
    }

}
