using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Runtime.Versioning;

namespace LabControl.Shared.Setup;

[SupportedOSPlatform("windows")]
public static class UpdateRecoveryTaskCleanup
{
    // Called only while holding the update trial lock after a terminal decision.
    public static void RemoveRecovery(UpdateTrialDocument? state)
    {
        if (state is null) return;
        if (state.Phase is not (UpdateTrialPhase.Stable or UpdateTrialPhase.RolledBack))
            throw new InvalidOperationException("An unfinished recovery task cannot be removed.");
        object? scheduler = null;
        object? folder = null;
        object? task = null;
        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            ((dynamic)scheduler).Connect();
            folder = ((dynamic)scheduler).GetFolder("\\");
            var name = UpdateTrial.RecoveryTaskName(state);
            try { task = ((dynamic)folder).GetTask(name); }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x80070002)) { return; }
            catch (FileNotFoundException exception) when (exception.HResult == unchecked((int)0x80070002)) { return; }
            string original = ((dynamic)task).Xml;
            var xml = XDocument.Parse(original);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var actions = xml.Root?.Element(ns + "Actions")?.Elements().ToArray();
            if (actions is not { Length: 1 } || actions[0].Name != ns + "Exec"
                || !string.Equals(actions[0].Element(ns + "Command")?.Value,
                    InstallLayout.Default.AgentExecutable(state.Previous), StringComparison.OrdinalIgnoreCase)
                || actions[0].Element(ns + "Arguments")?.Value != Defaults.RollbackDeadlineSwitch + " " + Uri.EscapeDataString(state.JobId))
                throw new IOException("The update recovery task was edited; preserve it for review.");
            // Task Scheduler has no conditional delete; recheck immediately before
            // deletion while the application's own update lock excludes writers.
            if (!string.Equals(original, (string)((dynamic)task).Xml, StringComparison.Ordinal))
                throw new IOException("The update recovery task changed during removal.");
            ((dynamic)folder).DeleteTask(name, 0);
        }
        finally
        {
            if (task is not null && Marshal.IsComObject(task)) Marshal.FinalReleaseComObject(task);
            if (folder is not null && Marshal.IsComObject(folder)) Marshal.FinalReleaseComObject(folder);
            if (scheduler is not null && Marshal.IsComObject(scheduler)) Marshal.FinalReleaseComObject(scheduler);
        }
    }
}
