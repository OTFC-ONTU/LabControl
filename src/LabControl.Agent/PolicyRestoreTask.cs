using System.Globalization;
using System.Xml.Linq;
using LabControl.Shared;

namespace LabControl.Agent;

/// <summary>
/// The watchdog task <see cref="Defaults.PolicyRestoreTaskName"/> (D-69 item 6, D-73 item 7):
/// armed while — and only while — a firewall or DNS policy is applied, it runs
/// <c>agent.exe --restore-policy</c> as SYSTEM at startup and once at the policy's hard limit
/// plus <see cref="Defaults.PolicyRestoreTaskGrace"/>. Registered with <c>schtasks /XML</c> in
/// UTF-16, like <see cref="UpdateRecovery"/>; a re-arm replaces it.
/// </summary>
internal static class PolicyRestoreTask
{
    /// <summary>Creates or replaces the task for a policy whose hard limit is <paramref name="hardLimitUnix"/>.</summary>
    public static void Arm(string executable, long hardLimitUnix)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(hardLimitUnix, 0, DateTimeOffset.MaxValue.ToUnixTimeSeconds() - 86400))
            + Defaults.PolicyRestoreTaskGrace;
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, params object[] children) => new(ns + name, children);
        var xml = new XDocument(new XDeclaration("1.0", "utf-16", null),
            E("Task", new XAttribute("version", "1.2"),
                E("Triggers",
                    E("BootTrigger", E("Enabled", "true")),
                    E("TimeTrigger", E("StartBoundary", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)), E("Enabled", "true"))),
                E("Principals", E("Principal", new XAttribute("id", "System"), E("UserId", "S-1-5-18"), E("RunLevel", "HighestAvailable"))),
                E("Settings", E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                    E("StopIfGoingOnBatteries", "false"), E("StartWhenAvailable", "true"), E("ExecutionTimeLimit", "PT5M"),
                    E("RestartOnFailure", E("Interval", "PT1M"), E("Count", "3"))),
                E("Actions", new XAttribute("Context", "System"), E("Exec", E("Command", executable), E("Arguments", Defaults.PolicyRestoreSwitch)))));

        var path = Path.Combine(Defaults.AgentDataDirectory, "policy-restore-task.xml");
        using (var writer = System.Xml.XmlWriter.Create(path, new System.Xml.XmlWriterSettings { Encoding = System.Text.Encoding.Unicode, Indent = true }))
        {
            xml.Save(writer);
        }

        try
        {
            UpdateRecovery.Run("schtasks.exe", "/Create", "/TN", Defaults.PolicyRestoreTaskName, "/XML", path, "/F");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>True while the task is registered.</summary>
    public static bool Exists()
    {
        try
        {
            UpdateRecovery.Run("schtasks.exe", "/Query", "/TN", Defaults.PolicyRestoreTaskName);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Deletes the task; an absent task is already the goal. Throws only when one is there and stays.</summary>
    public static void Delete()
    {
        if (!Exists())
        {
            return;
        }

        UpdateRecovery.Run("schtasks.exe", "/Delete", "/TN", Defaults.PolicyRestoreTaskName, "/F");
        if (Exists())
        {
            throw new IOException($"the scheduled task {Defaults.PolicyRestoreTaskName} is still registered");
        }
    }
}
