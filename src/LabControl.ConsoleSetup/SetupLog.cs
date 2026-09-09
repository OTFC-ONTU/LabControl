using System.Globalization;
using System.Text;
using LabControl.Shared;
using LabControl.Shared.Packaging;

namespace LabControl.ConsoleSetup;

/// <summary>
/// The installer's append-only log at <c>%LOCALAPPDATA%\LabControl\console-setup.log</c>
/// (D-59 item 1), plus whatever the teacher sees on screen. It records step ids, paths and
/// outcomes; it never sees a lab, a key or a passphrase, because the installer never opens
/// the data directory — it only checks whether a console holds the lock.
/// </summary>
internal sealed class SetupLog(string path)
{
    private readonly object _gate = new();

    public string Path => path;

    /// <summary>A line for the teacher and for the file.</summary>
    public void Say(string text)
    {
        System.Console.Out.WriteLine(text);
        Append("     " + text);
    }

    public void Step(string id, string outcome)
    {
        System.Console.Out.WriteLine("  " + id + ": " + outcome);
        Append("step " + id + ": " + outcome);
    }

    public void Fail(string id, Exception error)
    {
        System.Console.Error.WriteLine("  " + id + ": failed — " + error.Message);
        Append("fail " + id + ": " + error.Message);
    }

    /// <summary>
    /// Deletes the log itself. Only <c>--remove-data</c> does this: once the labs, the keys and
    /// the console's own logs are gone, this file is the last thing on the computer that still
    /// records which labs were on it, and leaving it behind would contradict what the teacher
    /// was asked to type REMOVE for.
    /// </summary>
    public void Remove()
    {
        try
        {
            lock (_gate)
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Console.Error.WriteLine("The installer log " + path + " could not be removed; delete it by hand.");
        }
    }

    private void Append(string line)
    {
        try
        {
            lock (_gate)
            {
                var directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(
                    path,
                    DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written must not stop an installation.
        }
    }

    public void Header(ConsoleInstallPlan plan)
    {
        System.Console.Out.WriteLine(plan.Header);
        Append("---- " + plan.Header + " (" + Defaults.ConsoleSetupExecutableName + ")");
    }
}
