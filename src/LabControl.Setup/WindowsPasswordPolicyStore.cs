using System.Diagnostics;
using System.Management;
using System.Text;
using LabControl.Shared;
using LabControl.Shared.Setup;

namespace LabControl.Setup;

/// <summary>Only used inside AccountSetupScope. Export/configuration artifacts inherit
/// its private ACL and are discarded; only the protected two-field journal is retained.</summary>
internal sealed class WindowsPasswordPolicyStore : IPasswordPolicyStore
{
    public PasswordPolicySnapshot Read()
    {
        RequireLocalPolicy();
        return InPrivateDirectory(directory =>
        {
            var export = Path.Combine(directory, "policy.inf");
            Run("/export", "/cfg", export, "/areas", "SECURITYPOLICY", "/log", Path.Combine(directory, "export.log"), "/quiet");
            return StudentPasswordPolicy.ParseExport(File.ReadAllText(export, Encoding.Unicode));
        });
    }

    public void Write(PasswordPolicySnapshot expected, PasswordPolicySnapshot desired)
    {
        RequireLocalPolicy();
        if (Read() != expected) throw new IOException("The local password policy changed before setup could write it.");
        InPrivateDirectory(directory =>
        {
            var template = Path.Combine(directory, "policy.inf");
            File.WriteAllText(template, StudentPasswordPolicy.Configuration(desired), Encoding.Unicode);
            if (Read() != expected) throw new IOException("The local password policy changed before setup could write it.");
            Run("/configure", "/db", Path.Combine(directory, "policy.sdb"), "/cfg", template,
                "/overwrite", "/areas", "SECURITYPOLICY", "/log", Path.Combine(directory, "configure.log"), "/quiet");
            if (Read() != desired) throw new IOException("Windows did not confirm the requested local password policy.");
            return true;
        });
    }

    private static void RequireLocalPolicy()
    {
        using var search = new ManagementObjectSearcher(
            WindowsDefenderExclusionStore.Scope(Defaults.SetupComputerSystemWmiNamespace),
            new ObjectQuery("SELECT PartOfDomain FROM " + Defaults.SetupComputerSystemWmiClass),
            new System.Management.EnumerationOptions { Timeout = Defaults.SetupWmiTimeout });
        using var rows = search.Get();
        var count = 0;
        foreach (ManagementObject row in rows)
        {
            using (row)
            {
                count++;
                if (row["PartOfDomain"] is not false)
                    throw new InvalidOperationException("Student password-policy fallback is available only on a verified non-domain PC.");
            }
        }
        if (count != 1) throw new IOException("Windows could not confirm the local policy authority.");
    }

    private static T InPrivateDirectory<T>(Func<string, T> operation)
    {
        var directory = Path.Combine(Defaults.AgentDataDirectory, Defaults.PasswordPolicyWorkDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { return operation(directory); }
        finally
        {
            // This fresh, random directory is inside the already validated private root.
            // A cleanup failure must not conceal a successful/failed native mutation.
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "secedit.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows could not start the policy operation.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)Defaults.SetupPasswordPolicyTimeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            catch (InvalidOperationException) { }
            throw new IOException("The Windows policy operation timed out.");
        }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException($"Windows policy operation failed (exit {process.ExitCode}).");
    }
}
