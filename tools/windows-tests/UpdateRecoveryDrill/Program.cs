using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using LabControl.Agent;
using LabControl.Setup;
using LabControl.Shared;
using LabControl.Shared.Setup;

// Outcome modes start AFTER the real console sends a signed bundle. This driver
// never stages binaries or calls Accept/RollBack to fake success. The task-probe mode
// independently registers and deletes a unique future task without changing a trial.
if (!OperatingSystem.IsWindows() || args.Length != 3 || args[0] != "--disposable-vm"
    || !Guid.TryParseExact(args[1], "D", out var installationId)
    || args[2] is not ("crash-start" or "broken-crash" or "no-link-deadline" or "good-ten-minutes" or "task-probe"))
{
    System.Console.Error.WriteLine("Usage: LabControl.UpdateRecoveryDrill.exe --disposable-vm <installation-id> crash-start|broken-crash|no-link-deadline|good-ten-minutes|task-probe");
    return 2;
}
try
{
    using var identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException();
    var id = installationId.ToString("D");
    using (var scope = AccountSetupScope.Open())
    {
        var installation = new InstallationState(Defaults.AgentDataDirectory).Read();
        if (installation?.InstallationId != id || installation.RemovalReady)
            throw new IOException("The explicit disposable installation identity does not match.");
        SetupInstallationFiles.RequireOwnership(id);
        if (new WindowsSetupService(id).Read() is null) throw new IOException("The owned service is missing.");
    }
    if (args[2] == "task-probe")
    {
        using var fixtureScope = AccountSetupScope.Open();
        var currentVersion = InstallLayout.Default.ReadCurrent() ?? throw new IOException("The current agent version is missing.");
        var job = "fixture-" + Guid.NewGuid().ToString("N");
        var deadline = UpdateTrial.SchedulerDeadline(DateTimeOffset.UtcNow.AddHours(1));
        // Unique future task executes only a job-mismatched production callback if
        // interrupted. It never changes the installed trial, service, or clock.
        UpdateRecovery.CreateDeadlineTask(InstallLayout.Default.AgentExecutable(currentVersion), deadline, job);
        var completed = new UpdateTrialDocument { JobId = job, Previous = currentVersion,
            Version = currentVersion + "-probe", Deadline = deadline, Phase = UpdateTrialPhase.RolledBack };
        UpdateRecoveryTaskCleanup.RemoveRecovery(completed);
        UpdateRecoveryTaskCleanup.RemoveRecovery(completed); // Native missing-task exception mapping must be idempotent.
        System.Console.WriteLine("PASS task-probe production-registration exact-cleanup missing-task-cleanup");
        return 0;
    }
    var trial = new UpdateTrial(Defaults.AgentDataDirectory);
    var initial = trial.Read() ?? throw new IOException("Start a real signed update before the drill.");
    if (initial.Phase != UpdateTrialPhase.OnProbation) throw new IOException("A new update must be on probation.");
    var expected = args[2] == "good-ten-minutes" ? UpdateTrialPhase.Stable : UpdateTrialPhase.RolledBack;
    var trialStarted = initial.Deadline - Defaults.UpdateProbation - Defaults.ServiceRestartWait * 2;
    var timeout = initial.Deadline + Defaults.ServiceStopTimeout * 4;
    var watch = Stopwatch.StartNew();
    var remaining = timeout - DateTimeOffset.UtcNow;
    if (remaining <= TimeSpan.Zero) throw new IOException("The trial is too old for this drill.");
    var killed = new HashSet<int>();
    var lastReport = TimeSpan.MinValue;
    while (watch.Elapsed < remaining)
    {
        UpdateTrialDocument? current;
        try { current = trial.Read(); }
        catch (IOException) { await Task.Delay(250); continue; } // Real external rollback holds the lock.
        if (current?.JobId != initial.JobId) throw new IOException("Another update replaced the drill's trial.");
        if (current.Phase is UpdateTrialPhase.Stable or UpdateTrialPhase.RolledBack)
        {
            if (current.Phase != expected) throw new IOException("The actual terminal update outcome does not match this drill.");
            if (args[2] is "broken-crash" or "crash-start" && DateTimeOffset.UtcNow >= initial.Deadline)
                throw new IOException("Crash recovery was not observed before the scheduled deadline.");
            if (args[2] == "no-link-deadline" && DateTimeOffset.UtcNow < initial.Deadline)
                throw new IOException("The no-link release rolled back before its real deadline.");
            if (expected == UpdateTrialPhase.Stable && DateTimeOffset.UtcNow < trialStarted + Defaults.UpdateProbation)
                throw new IOException("The update was accepted before its real ten-minute probation.");
            // Require the production agent/callback to finish native cleanup itself.
            // Calling the idempotent API afterwards is a no-op, never test assistance.
            if (!current.RecoveryFinalized) { await Task.Delay(500); continue; }
            UpdateRecovery.FinalizeTerminal(current);
            var terminal = trial.Read()!;
            var version = expected == UpdateTrialPhase.Stable ? initial.Version : initial.Previous;
            if (!terminal.RecoveryFinalized || InstallLayout.Default.ReadCurrent() != version
                || new WindowsSetupService(id).Read() is null)
                throw new IOException("The terminal service, marker, or recovery cleanup could not be verified.");
            System.Console.WriteLine($"PASS {args[2]} phase={terminal.Phase} version={version} kills={killed.Count} elapsedSeconds={watch.Elapsed.TotalSeconds:0}");
            return 0;
        }
        if (args[2] == "crash-start" && current.Phase == UpdateTrialPhase.OnProbation
            && killed.Count <= Defaults.ServiceRestartsBeforeRollback)
        {
            // Query the actual SCM service PID, verify the executable twice, then
            // terminate only this trial's new agent. Never kill the previous binary.
            using var search = new ManagementObjectSearcher("root\\cimv2",
                "SELECT ProcessId, PathName FROM Win32_Service WHERE Name='" + Defaults.ServiceName + "'");
            using var services = search.Get();
            foreach (ManagementObject service in services)
            {
                using (service)
                {
                    var pid = Convert.ToInt32(service["ProcessId"]);
                    if (pid == 0 || killed.Contains(pid)) continue;
                    var executable = InstallLayout.Default.AgentExecutable(initial.Version);
                    if ((string?)service["PathName"] != "\"" + executable + "\"") continue;
                    if (new WindowsSetupService(id).Read() is null) throw new IOException("Owned service disappeared.");
                    using var process = Process.GetProcessById(pid);
                    if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The service PID no longer refers to the pending agent.");
                    process.Kill();
                    killed.Add(pid);
                }
            }
        }
        if (lastReport == TimeSpan.MinValue || watch.Elapsed - lastReport >= TimeSpan.FromSeconds(30))
        {
            System.Console.WriteLine($"WAIT {args[2]} phase={current.Phase} kills={killed.Count} elapsedSeconds={watch.Elapsed.TotalSeconds:0}");
            lastReport = watch.Elapsed;
        }
        await Task.Delay(500);
    }
    throw new IOException("The production recovery/acceptance deadline elapsed without the expected outcome.");
}
catch (Exception error)
{
    System.Console.Error.WriteLine("FAIL recovery drill: " + error.GetType().Name);
    return 1;
}
