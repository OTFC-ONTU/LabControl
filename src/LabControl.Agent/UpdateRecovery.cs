using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using LabControl.Shared;
using LabControl.Shared.Setup;

namespace LabControl.Agent;

/// <summary>External recovery is scheduled against the outgoing, known-good executable.</summary>
internal static class UpdateRecovery
{
    private static UpdateTrial Trial => new(Defaults.AgentDataDirectory);

    public static void Arm(string jobId, string version, string previous, string executable, Action<string>? log = null)
    {
        if (Trial.Read() is { } preceding) FinalizeTerminal(preceding);
        // Startup has a bounded allowance in addition to ten continuous linked minutes.
        var calculated = DateTimeOffset.UtcNow + Defaults.UpdateProbation + Defaults.ServiceRestartWait * 2;
        // Scheduler triggers have whole-second precision. Round the persisted deadline
        // up too; formatting alone could otherwise fire before the durable deadline.
        var deadline = UpdateTrial.SchedulerDeadline(calculated);
        Trial.Begin(jobId, version, previous, deadline);
        try
        {
            CreateDeadlineTask(executable, deadline, jobId);
            var delay = ((int)Defaults.ServiceRestartDelay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            var actions = string.Join('/', Enumerable.Repeat("restart/" + delay, Defaults.ServiceRestartsBeforeRollback)) + "/run/0";
            Run("sc.exe", "failure", Defaults.ServiceName, "reset=", "86400", "command=",
                "\"" + executable + "\" " + Defaults.RollbackSwitch + " " + Uri.EscapeDataString(jobId), "actions=", actions);
            Run("sc.exe", "failureflag", Defaults.ServiceName, "1");
        }
        catch
        {
            // No service switch has happened. Preserve the old version and make this
            // terminal so a later push does not adopt half-configured recovery.
            try
            {
                Trial.RollBack(DateTimeOffset.UtcNow, true, _ => { }, jobId);
                FinalizeTerminal(Trial.Read()!);
            }
            catch (Exception cleanupError)
            {
                // Preserve the original Arm failure. Diagnostics contain no exception
                // message/arguments, which could include external data.
                try { (log ?? Console.Error.WriteLine)("update.recovery_cleanup_failed " + cleanupError.GetType().Name
                    + " 0x" + cleanupError.HResult.ToString("X8", CultureInfo.InvariantCulture)); }
                catch (Exception) { }
            }
            throw;
        }
    }

    public static void RollBack(bool deadlineOnly, Action<string> log, string expectedJobId)
    {
        Trial.RollBack(DateTimeOffset.UtcNow, !deadlineOnly, state =>
        {
            var layout = InstallLayout.Default;
            var previousExecutable = layout.AgentExecutable(state.Previous);
            if (!File.Exists(previousExecutable) || !File.Exists(layout.SessionExecutable(state.Previous)))
                throw new IOException("The previous version is incomplete; recovery requires repair.");
            var error = ServiceControl.SetBinaryPath(previousExecutable);
            if (error is not null) throw new IOException(error);
            layout.WriteCurrent(state.Previous);
            // Restart is idempotent; a interrupted callback is retried by the task.
            error = ServiceControl.Restart(log);
            if (error is not null) throw new IOException(error);
            log($"Rolled back {state.Version} to {state.Previous}.");
        }, expectedJobId);
        if (Trial.Read() is { } completed && completed.JobId == expectedJobId
            && completed.Phase is UpdateTrialPhase.RolledBack or UpdateTrialPhase.Stable)
            FinalizeTerminal(completed);
    }

    internal static void CreateDeadlineTask(string executable, DateTimeOffset deadline, string jobId)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, params object[] children) => new(ns + name, children);
        var xml = new XDocument(new XDeclaration("1.0", "utf-16", null),
            E("Task", new XAttribute("version", "1.2"),
                E("Triggers", E("TimeTrigger", E("StartBoundary", deadline.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)), E("Enabled", "true"))),
                E("Principals", E("Principal", new XAttribute("id", "System"), E("UserId", "S-1-5-18"), E("RunLevel", "HighestAvailable"))),
                E("Settings", E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                    E("StopIfGoingOnBatteries", "false"), E("StartWhenAvailable", "true"), E("ExecutionTimeLimit", "PT5M"),
                    E("RestartOnFailure", E("Interval", "PT1M"), E("Count", "3"))),
                E("Actions", new XAttribute("Context", "System"), E("Exec", E("Command", executable),
                    E("Arguments", Defaults.RollbackDeadlineSwitch + " " + Uri.EscapeDataString(jobId))))));
        var path = Path.Combine(Defaults.AgentDataDirectory, Defaults.UpdateRollbackTaskFileName);
        // schtasks /XML consumes UTF-16 reliably; UTF-8 XML can fail with
        // "unable to switch the encoding" on Windows. Keep BOM and declaration aligned.
        using (var writer = System.Xml.XmlWriter.Create(path, new System.Xml.XmlWriterSettings
        { Encoding = System.Text.Encoding.Unicode, Indent = true })) xml.Save(writer);
        try
        {
            // Task name belongs to this durable job, never overwrite a pre-existing task.
            Run("schtasks.exe", "/Create", "/TN", TaskName(new UpdateTrialDocument { JobId = jobId }), "/XML", path);
        }
        finally
        {
            // A stale private XML file is retriable; do not mask a registration error.
            try { File.Delete(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string TaskName(UpdateTrialDocument state) => UpdateTrial.RecoveryTaskName(state);

    public static void FinalizeTerminal(UpdateTrialDocument state) => Trial.FinalizeTerminal(state.JobId, completed =>
    {
        var delay = ((int)Defaults.ServiceRestartDelay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        // SCM repeats the last action. A stable service must keep restarting, never
        // invoke an accepted trial's old binary after a later unrelated crash.
        Run("sc.exe", "failure", Defaults.ServiceName, "reset=", "86400", "command=", "", "actions=", "restart/" + delay);
        Run("sc.exe", "failureflag", Defaults.ServiceName, "1");
        TryCleanupTask(completed);
        var layout = InstallLayout.Default;
        var expectedCurrent = completed.Phase == UpdateTrialPhase.Stable ? completed.Version : completed.Previous;
        if (layout.ReadCurrent() == expectedCurrent) layout.ClearPrevious();
        var staging = Path.Combine(Defaults.AgentDataDirectory, Defaults.UpdateStagingDirectoryName, completed.Version);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
    });

    /// <summary>Native cleanup failure retries before another update may begin.</summary>
    public static void TryCleanupTask(UpdateTrialDocument state) => UpdateRecoveryTaskCleanup.RemoveRecovery(state);

    internal static string Run(string name, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, name))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)Defaults.ServiceStopTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException($"{name} did not finish before its timeout.");
        }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new IOException($"{name} failed with exit code {process.ExitCode}.");
        return output.Result;
    }
}
