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
    /// <summary>How long the temporary copy waits for the uninstaller that started it.</summary>
    private static readonly TimeSpan ParentWait = TimeSpan.FromMinutes(5);

    /// <summary>How long the temporary copy keeps retrying the deletes after that.</summary>
    private static readonly TimeSpan RemovalBudget = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan RemovalRetry = TimeSpan.FromMilliseconds(500);

    private readonly ConsoleInstalledFiles _files = new(layout.InstallDirectory);

    public bool IsElevated =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public int Run(ConsoleInstallCommand command)
    {
        var install = command.Request.Action == ConsoleInstallAction.Install;
        var payload = install && ConsolePayload.IsPresent ? ConsolePayload.Open() : null;
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

                // Only a step that merely looked at the machine may claim that nothing changed.
                // Anything else has predecessors that already ran, and may itself have written
                // half of its own work, so the teacher is told to fix the problem and run the
                // installer again — it is idempotent, and a second run finishes the job.
                log.Say(ConsoleInstallPlan.ChangesNothing(step.Id)
                    ? "stopped before anything was changed. The full log is " + log.Path
                    : "stopped at " + step.Id + ". The steps before it were carried out and this one may have been "
                        + "half done; fix the problem above and run this installer again — it repeats safely. "
                        + "The full log is " + log.Path);
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
            return Describe(WindowsRegistrations.RemoveUninstallEntry(layout),
                "it points at another installation and was left for review");
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
                return Describe(WindowsRegistrations.RemoveProgId(type, layout),
                    "the handler points somewhere else and was left for review");
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

                return WindowsConsoleFirewall.EnsureRule(spec, layout.ConsoleExecutable) ? "added" : "already allowed";
            }
        }

        throw new InvalidOperationException("Unknown installer step: " + step.Id);
    }

    private static string Describe(RegistryRemoval outcome, string preserved) => outcome switch
    {
        RegistryRemoval.Removed => "removed",
        RegistryRemoval.Missing => "already gone",
        _ => "left in place: " + preserved,
    };

    /// <summary>
    /// Refuses to touch the program files while a console is using them. Two things are asked,
    /// because either alone lies: the lock file only covers the default data directory, so a
    /// console started with <c>--data</c> somewhere else holds no lock this installer can see,
    /// and a process by that name may be a console this installer does not manage. Both are
    /// good enough reasons to stop.
    /// </summary>
    private string CheckLock()
    {
        if (RunningConsoles() is { Length: > 0 } running)
        {
            throw new IOException(Defaults.ConsoleProductName + " is running (process "
                + string.Join(", ", running.Select(id => id.ToString(CultureInfo.InvariantCulture)))
                + "). Close it — including a console started with --data on another directory — and start this again.");
        }

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

    private static int[] RunningConsoles()
    {
        try
        {
            var found = Process.GetProcessesByName(Defaults.ConsoleExecutableBaseName);
            try
            {
                return [.. found.Select(process => process.Id)];
            }
            finally
            {
                foreach (var process in found)
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            // Windows would not enumerate; the lock file below is then the only evidence.
            return [];
        }
    }

    private string InstallFiles(ConsolePayload payload)
    {
        Directory.CreateDirectory(layout.InstallDirectory);
        var previous = _files.ReadManifest();
        var written = ConsolePayload.ExtractTo(_files);

        // The installed copy of this executable is the uninstaller (the same pattern the
        // student Setup uses), so it is owned and listed like every other file.
        var owned = payload.Entries.ToList();
        var self = Environment.ProcessPath;
        if (self is not null && !string.Equals(Path.GetFullPath(self), Path.GetFullPath(layout.SetupExecutable), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(self, layout.SetupExecutable, true);
            ClearZoneIdentifier(layout.SetupExecutable);
            written++;
        }

        owned.Add(Defaults.ConsoleSetupExecutableName);
        _files.WriteManifest(owned);

        // Only a manifest this installation really wrote may prune anything: the fallback list
        // is a guess about what is there, not a record of what an older payload contained.
        var stale = previous.FromFallback
            ? []
            : previous.Owned.Except(owned, StringComparer.OrdinalIgnoreCase).ToArray();
        var pruned = _files.Prune(stale);

        var refused = Report(pruned.Refused, "the previous " + Defaults.ConsoleInstallManifestFileName);
        return (written == 0 && pruned.Removed.Count == 0
            ? "already up to date (" + owned.Count + " file(s))"
            : written + " file(s) written, " + pruned.Removed.Count + " no longer needed") + refused;
    }

    private string RemoveFiles()
    {
        if (!Directory.Exists(layout.InstallDirectory))
        {
            return "already gone";
        }

        var self = Environment.ProcessPath;
        var removal = _files.RemoveAll(self);
        var refused = Report(removal.Refused, Defaults.ConsoleInstallManifestFileName);
        var fallback = removal.FromFallback
            ? " (no usable " + Defaults.ConsoleInstallManifestFileName + "; the two program files were removed by name)"
            : "";

        if (removal.Kept.Count == 0)
        {
            return (removal.Removed.Count == 0
                ? "nothing was owned here"
                : "removed " + removal.Removed.Count + " file(s)") + fallback + refused;
        }

        HandOverToTemporaryCopy();
        return "removed " + removal.Removed.Count + " file(s); a temporary copy removes this program itself"
            + fallback + refused;
    }

    private static string Report(IReadOnlyList<ConsoleManifestRefusal> refused, string what) =>
        refused.Count == 0
            ? ""
            : "; refused " + refused.Count + " line(s) of " + what + " that named something outside the install directory: "
                + string.Join(", ", refused.Select(entry => entry.ToString()));

    /// <summary>
    /// The uninstaller cannot delete the file it is running from, so it copies itself into the
    /// temp directory and lets that copy finish once this process has exited. The copy is told
    /// which process to wait for and which directory it may remove; nothing is scheduled,
    /// nothing survives a reboot, and the copy deletes itself when it is done.
    /// </summary>
    private void HandOverToTemporaryCopy()
    {
        var self = Environment.ProcessPath;
        if (self is null)
        {
            return;
        }

        var worker = Path.Combine(Path.GetTempPath(), ConsoleTempCopies.NameFor(Guid.NewGuid()));
        File.Copy(self, worker, true);
        ClearZoneIdentifier(worker);
        using var process = Process.Start(new ProcessStartInfo(worker)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                ConsoleInstallCommandLine.FinishRemovalSwitch,
                layout.InstallDirectory,
                ConsoleInstallCommandLine.FinishRemovalProcessSwitch,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            },
        });
    }

    /// <summary>
    /// The temporary copy's whole job: make sure it is the right directory, wait for the
    /// uninstaller to exit, remove what the manifest still lists, and then remove itself.
    ///
    /// It deletes nothing but this installation. The argument is compared with the layout's
    /// own install directory, so a copy started by hand with any other path — a profile, a
    /// documents folder — refuses and changes nothing (D-59 item 1).
    /// </summary>
    public static int FinishRemoval(string directory, int? parentProcessId, ConsoleInstallLayout layout, SetupLog log)
    {
        if (!ConsoleInstalledFiles.IsTheSameDirectory(directory, layout.InstallDirectory))
        {
            log.Say("refused: " + ConsoleInstallCommandLine.FinishRemovalSwitch + " removes only "
                + layout.InstallDirectory + ", never " + directory + ".");
            return 2;
        }

        WaitForParent(parentProcessId);

        var files = new ConsoleInstalledFiles(layout.InstallDirectory);
        var started = Stopwatch.StartNew();
        Exception? last = null;
        while (true)
        {
            try
            {
                var removal = files.RemoveAll();
                if (removal.DirectoryRemoved)
                {
                    log.Step(ConsoleInstallPlan.FilesRemove,
                        "removed by the temporary copy (" + removal.Removed.Count + " file(s))");
                    SelfDelete();
                    return 0;
                }

                last = new IOException(layout.InstallDirectory + " still holds files that are in use.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                last = error;
            }

            if (started.Elapsed >= RemovalBudget)
            {
                log.Fail(ConsoleInstallPlan.FilesRemove, last ?? new IOException("the directory could not be removed"));
                log.Say("The program files are still in " + layout.InstallDirectory
                    + ". Delete that directory by hand, or run the installer again and remove it from Installed apps.");
                SelfDelete();
                return 1;
            }

            Thread.Sleep(RemovalRetry);
        }
    }

    /// <summary>
    /// Nothing may be deleted while the uninstaller that started this copy is still running:
    /// it is still writing its own log, and on Windows its executable cannot be deleted at all.
    /// Waiting for it is what makes the removal actually happen rather than fail quietly.
    /// </summary>
    private static void WaitForParent(int? parentProcessId)
    {
        if (parentProcessId is not { } id)
        {
            return;
        }

        try
        {
            using var parent = Process.GetProcessById(id);
            parent.WaitForExit((int)ParentWait.TotalMilliseconds);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // Already gone, or not a process this copy may wait for. Either way: carry on.
        }
    }

    /// <summary>
    /// The copy is a ~60 MB working installer sitting in the temp directory; leaving it there
    /// invites a double-click months later. A running executable cannot delete itself, so it
    /// hands the deletion to a detached shell that waits a few seconds first.
    /// </summary>
    private static void SelfDelete()
    {
        var self = Environment.ProcessPath;
        if (self is null || !OperatingSystem.IsWindows() || !ConsoleTempCopies.IsTemporaryCopy(Path.GetFileName(self)))
        {
            return;
        }

        try
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            using var _ = Process.Start(new ProcessStartInfo(string.IsNullOrEmpty(comspec) ? "cmd.exe" : comspec)
            {
                UseShellExecute = false,
                CreateNoWindow = true,

                // Arguments, not ArgumentList: ArgumentList quotes each argument the way a C
                // runtime parses argv — the inner quotes come out as \" — and cmd.exe does not
                // understand that escape. It answers "The filename, directory name, or volume
                // label syntax is incorrect", deletes nothing, and leaves a ~150 MB working
                // installer in the teacher's temp directory. cmd's own rules want the line
                // written out as it is, with the path in ordinary quotes: that is also what
                // protects a temp path containing a space or an ampersand.
                Arguments = "/d /c ping 127.0.0.1 -n 5 > nul & del /f /q \"" + self + "\"",
            });
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        {
            // The next run of the installer sweeps it instead.
        }
    }

    /// <summary>
    /// Uninstall runs unelevated, because installing does (D-59 item 1). Removing a firewall
    /// rule needs an administrator, so unless this particular run happens to be elevated the
    /// rules are left exactly as they are and the two commands that remove them are printed.
    /// An open port with nothing listening on it is not a hazard; deleting somebody else's
    /// rule, or asking a teacher for an administrator in the middle of an uninstall started
    /// from Installed apps, would be worse.
    /// </summary>
    private string RemoveFirewallRules()
    {
        if (!IsElevated)
        {
            var present = Present();
            return present.Count == 0
                ? "no rule of this installer is present"
                : "left in place — removing a firewall rule needs an administrator, and uninstalling never asks for one. "
                    + "To remove " + string.Join(" and ", present) + " run in an elevated Command Prompt: "
                    + string.Join("  ", ConsoleFirewallRules.NetshDeleteLines());
        }

        var removed = new List<string>();
        var preserved = new List<string>();
        foreach (var spec in ConsoleFirewallRules.Required)
        {
            try
            {
                switch (WindowsConsoleFirewall.RemoveOwnedRule(spec))
                {
                    case ConsoleFirewallRuleOwnership.Owned:
                        removed.Add(spec.Name);
                        break;
                    case ConsoleFirewallRuleOwnership.Foreign:
                        preserved.Add(spec.Name);
                        break;
                }
            }
            catch (IOException)
            {
                return "skipped — the Windows Firewall would not answer. To remove the rules by hand: "
                    + string.Join("  ", ConsoleFirewallRules.NetshDeleteLines());
            }
        }

        var note = preserved.Count == 0
            ? ""
            : "; left " + string.Join(", ", preserved) + " for review: another group holds a rule of that name";
        return (removed.Count == 0 ? "no rule of this installer was present" : "removed " + string.Join(", ", removed)) + note;
    }

    /// <summary>Which of this installer's own rules Windows still holds. Reading needs no administrator.</summary>
    private static IReadOnlyList<string> Present()
    {
        try
        {
            return WindowsConsoleFirewall.Check().OwnedRuleNames;
        }
        catch (Exception error) when (error is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return [];
        }
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

    /// <summary>
    /// Removes the <c>Zone.Identifier</c> stream Windows attaches to a file copied from a
    /// download or a USB stick, so the installed uninstaller does not raise SmartScreen every
    /// time it is started from Installed apps. Nothing else about the file changes.
    /// </summary>
    private static void ClearZoneIdentifier(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // No stream, or a file system without them. Harmless either way.
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
