using System.Globalization;
using System.Text;

namespace LabControl.Shared.Lab;

/// <summary>
/// The script that moves one installed PC to a new lab key over the network (D-82 item 3).
/// Every installed agent — <c>1.0.0</c> included, which cannot be updated without the lost key
/// — already runs a SYSTEM <c>run_script</c>, and every Setup installation already has
/// <c>C:\Program Files\LabControl\Setup.exe --rekey</c> (the D-52 trust transaction). The script
/// writes that PC's own payload — one single-use enrolment code and the new CA certificate,
/// carried inside the script text, never as a handout in the student's folder — into a
/// directory only SYSTEM and administrators can read, and schedules a one-shot SYSTEM task to
/// run the rekey a few seconds later: the rekey stops the service that runs the script, so the
/// script must have finished and its result left first. It never runs <c>Uninstall.exe</c>,
/// which prepends <c>--uninstall</c> to any arguments. A PC whose installation is not owned
/// (no <c>installation.json</c>, no installed <c>Setup.exe</c>) is refused before anything is written.
/// </summary>
public static class LabMoveScript
{
    /// <summary>Printed once the rekey task is scheduled; the job's output carries it.</summary>
    public const string ScheduledMarker = "LABCONTROL_MOVE_SCHEDULED";

    /// <summary>Printed, with a reason, when the PC cannot be moved this way; the script exits <see cref="RefusedExitCode"/>.</summary>
    public const string RefusedMarker = "LABCONTROL_MOVE_REFUSED";

    public const int RefusedExitCode = 3;

    /// <summary>Seconds between the script finishing and the rekey stopping the service, so the job's result reaches the console first.</summary>
    public const int RekeyDelaySeconds = 20;

    /// <summary>Where each move's payload and log live on the PC; no spaces, so the task's command line needs no nested quoting.</summary>
    public const string MoveRoot = @"C:\ProgramData\LabControlMove";

    /// <summary>The PowerShell text for one PC. <paramref name="moveId"/> is 32 lowercase hex digits (a <c>Guid</c> "N").</summary>
    public static string Build(string moveId, ReadOnlySpan<byte> setupJson, ReadOnlySpan<byte> caCertificate)
    {
        if (moveId.Length != 32 || !moveId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("A move id is 32 lowercase hex digits.", nameof(moveId));
        }

        if (setupJson.IsEmpty || caCertificate.IsEmpty)
        {
            throw new ArgumentException("A move needs its setup document and the new CA certificate.");
        }

        var setup = Convert.ToBase64String(setupJson);
        var authority = Convert.ToBase64String(caCertificate);
        // Windows paths, joined by hand: the console building this may run on a Mac, where
        // Path.Combine would join them with '/' (found by the tests).
        var installed = Defaults.AgentInstallDirectory + @"\" + Defaults.SetupExecutableName;
        var installation = Defaults.AgentDataDirectory + @"\" + Defaults.InstallationFileName;
        var builder = new StringBuilder();
        builder.AppendLine("# LabControl: move this PC to the lab's new key (D-82). Written by the console for this PC only;");
        builder.AppendLine("# it carries a single-use enrolment code. It schedules the installed Setup.exe --rekey.");
        builder.AppendLine("$ErrorActionPreference = 'Stop'");
        builder.AppendLine(CultureInfo.InvariantCulture, $"$id = '{moveId}'");
        builder.AppendLine(CultureInfo.InvariantCulture, $"$setup = '{installed}'");
        builder.AppendLine(CultureInfo.InvariantCulture, $"if (-not (Test-Path -LiteralPath $setup) -or -not (Test-Path -LiteralPath '{installation}')) {{");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    Write-Output '{RefusedMarker} not installed by Setup: move this PC with Setup.exe --rekey from a USB stick'");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    exit {RefusedExitCode}");
        builder.AppendLine("}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"$dir = Join-Path '{MoveRoot}' $id");
        builder.AppendLine("New-Item -ItemType Directory -Force -Path $dir | Out-Null");
        // Well-known SIDs, not names: a Ukrainian or Russian Windows names SYSTEM and Administrators differently.
        builder.AppendLine("& icacls.exe $dir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null");
        builder.AppendLine("if ($LASTEXITCODE -ne 0) { throw 'icacls failed' }");
        builder.AppendLine(CultureInfo.InvariantCulture, $"[IO.File]::WriteAllBytes((Join-Path $dir '{Defaults.SetupFileName}'), [Convert]::FromBase64String('{setup}'))");
        builder.AppendLine(CultureInfo.InvariantCulture, $"[IO.File]::WriteAllBytes((Join-Path $dir '{Defaults.CaCertificateFileName}'), [Convert]::FromBase64String('{authority}'))");
        builder.AppendLine("$log = Join-Path $dir 'move.log'");
        builder.AppendLine("$cmd = Join-Path $dir 'move.cmd'");
        builder.AppendLine("$task = 'LabControl Move ' + $id");
        builder.AppendLine("$lines = @(");
        builder.AppendLine("    '@echo off',");
        builder.AppendLine("    ('\"' + $setup + '\" --rekey --payload \"' + $dir + '\" > \"' + $log + '\" 2>&1'),");
        builder.AppendLine("    ('echo exit %ERRORLEVEL% >> \"' + $log + '\"'),");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    ('del /q \"' + (Join-Path $dir '{Defaults.SetupFileName}') + '\"'),");
        builder.AppendLine("    ('schtasks.exe /Delete /TN \"' + $task + '\" /F')");
        builder.AppendLine(")");
        builder.AppendLine("Set-Content -LiteralPath $cmd -Value $lines -Encoding ASCII");
        builder.AppendLine("$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/d /c \"' + $cmd + '\"')");
        builder.AppendLine(CultureInfo.InvariantCulture, $"$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds({RekeyDelaySeconds})");
        builder.AppendLine("$principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest");
        builder.AppendLine("Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Write-Output ('{ScheduledMarker} ' + $id)");
        return builder.ToString();
    }
}
