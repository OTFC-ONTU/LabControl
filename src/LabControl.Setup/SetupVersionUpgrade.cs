using System.Diagnostics;
using LabControl.Shared;
using LabControl.Shared.Setup;

namespace LabControl.Setup;

internal static class SetupVersionUpgrade
{
    public static SetupCheck Check(string version)
    {
        var current = InstallLayout.Default.ReadCurrent();
        if (current == version) return new(SetupStepStatus.AlreadyDone);
        if (current is null) return new(SetupStepStatus.Conflict, "The installed version marker is missing.");
        return new(SetupStepStatus.Needed);
    }

    public static void Apply(string version, string installationId)
    {
        var layout = InstallLayout.Default;
        var previous = layout.ReadCurrent() ?? throw new IOException("The current version is unknown.");
        if (previous == version) return;
        SetupInstallationFiles.RequireOwnership(installationId);
        _ = new WindowsSetupService(installationId).Read();
        SetupInstallationFiles.ValidateInstalledVersion(previous);
        SetupInstallationFiles.ValidateInstalledVersion(version);
        VerifyExecutable(layout.AgentExecutable(version), version);
        var job = "setup-" + Guid.NewGuid().ToString("d");
        LabControl.Agent.UpdateRecovery.Arm(job, version, previous, layout.AgentExecutable(previous));
        try
        {
            WindowsSetupService.Stop();
            layout.WritePrevious(previous);
            layout.WriteCurrent(version);
            var error = LabControl.Agent.ServiceControl.SetBinaryPath(layout.AgentExecutable(version));
            if (error is not null) throw new IOException("The new service target could not be set.");
            WindowsSetupService.Start();
        }
        catch
        {
            LabControl.Agent.UpdateRecovery.RollBack(false, _ => { }, job);
            throw;
        }
    }

    public static void VerifyExecutable(string executable, string version)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable, Defaults.AgentVersionSwitch)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)Defaults.UpdatePreflightTimeout.TotalMilliseconds))
        {
            process.Kill(true);
            throw new IOException("The new agent did not pass its startup check.");
        }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0 || output.Result.Trim() != InstallLayout.BaseVersionOf(version))
            throw new IOException("The new agent does not match the USB version or this Windows architecture.");
    }
}
