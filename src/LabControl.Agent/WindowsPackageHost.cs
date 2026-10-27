using System.Diagnostics;
using LabControl.Shared;
using LabControl.Shared.Jobs;
using LabControl.Shared.Packages;
using Microsoft.Win32;

namespace LabControl.Agent;

/// <summary>
/// <c>install_package</c>'s Windows half (D-23, D-89): the installer is staged in the agent's
/// private data directory and run as SYSTEM in session 0 — never seen by the student, never
/// judged by an exam's whitelist — with no window and no redirected streams (a child that
/// inherits a pipe would keep it open past the installer's exit). The detect rule reads the
/// 64- and 32-bit <i>Uninstall</i> keys of the machine, or tests a file.
/// </summary>
internal sealed class WindowsPackageHost(Func<bool>? examHeld) : IPackageHost
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public bool ExamHeld => examHeld?.Invoke() == true;

    public string StagingDirectory => Path.Combine(Defaults.AgentDataDirectory, Defaults.PackageStagingDirectoryName);

    /// <summary>Removes what an interrupted install left behind; called once when the service starts.</summary>
    public void ClearStaging()
    {
        try
        {
            if (Directory.Exists(StagingDirectory)) Directory.Delete(StagingDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public DetectFinding Detect(DetectRule rule)
    {
        if (rule.Kind == DetectKind.Path)
        {
            var path = Environment.ExpandEnvironmentVariables(rule.Value);
            return new DetectFinding(File.Exists(path) || Directory.Exists(path), File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).ProductVersion : null);
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = root.OpenSubKey(UninstallKey);
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") is string display
                    && display.StartsWith(rule.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return new DetectFinding(true, entry.GetValue("DisplayVersion") as string);
                }
            }
        }

        return new DetectFinding(false, null);
    }

    public async Task<InstallerExit> RunInstallerAsync(InstallPackageRequest request, string stagedPath, CancellationToken token)
    {
        var (executable, arguments) = InstallPackageRequest.CommandLine(request.Family, stagedPath, request.Silent, Environment.SystemDirectory);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(stagedPath)!,
            },
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("the installer did not start");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(request.Timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return new InstallerExit(process.ExitCode, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.Kill(process);
            if (token.IsCancellationRequested) throw;
            return new InstallerExit(-1, TimedOut: true);
        }
    }
}
