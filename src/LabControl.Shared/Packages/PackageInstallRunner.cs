using System.Globalization;
using LabControl.Shared.Localization;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Packages;

/// <summary>The stable codes an <c>install_package</c> result carries in <c>JobResult.outcome</c> (D-89); the console keys its per-PC status on them.</summary>
public static class PackageOutcomes
{
    /// <summary>The installer ran and the detect rule now finds the program.</summary>
    public const string Installed = "package.installed";

    /// <summary>As <see cref="Installed"/>, and the installer asked for a restart (3010 or 1641); the PC does not restart by itself.</summary>
    public const string RebootRequired = "package.reboot_required";

    /// <summary><see cref="InstallMode.Missing"/> or <see cref="InstallMode.Check"/>: the detect rule already finds the program.</summary>
    public const string Present = "package.present";

    /// <summary><see cref="InstallMode.Check"/>: the detect rule finds nothing.</summary>
    public const string Absent = "package.absent";

    /// <summary>The installer ended with a code other than success.</summary>
    public const string Failed = "package.failed";

    /// <summary>The installer said success, but the detect rule still finds nothing.</summary>
    public const string NotDetected = "package.not_detected";

    /// <summary>The installer ran past its time and was ended — usually a question nobody can see.</summary>
    public const string TimedOut = "package.timed_out";

    /// <summary>The installer could not be fetched, written or started.</summary>
    public const string NotRun = "package.not_run";

    /// <summary>Refused: an exam is held on this PC (owner decision 2026-10-27).</summary>
    public const string ExamHeld = "package.exam";

    public static bool MeansInstalled(string outcome) => outcome is Installed or RebootRequired or Present;
}

/// <summary>What a PC's detect rule found: whether the program is there, and the version Windows lists for it.</summary>
public readonly record struct DetectFinding(bool Installed, string? Version);

/// <summary>How the installer process ended.</summary>
public readonly record struct InstallerExit(int ExitCode, bool TimedOut);

/// <summary>The machine-specific half of <c>install_package</c>: the real PC's registry and processes, or the simulator's pretence.</summary>
public interface IPackageHost
{
    /// <summary>True while an exam is held on this PC; the install is refused then.</summary>
    bool ExamHeld { get; }

    /// <summary>A private directory only SYSTEM and administrators can write, where the installer is staged.</summary>
    string StagingDirectory { get; }

    DetectFinding Detect(DetectRule rule);

    /// <summary>Runs the staged installer silently, ending it (and everything it started) after <see cref="InstallPackageRequest.Timeout"/>.</summary>
    Task<InstallerExit> RunInstallerAsync(InstallPackageRequest request, string stagedPath, CancellationToken token);
}

/// <summary>
/// <c>install_package</c> on a PC (D-23, D-89), shared by the Windows agent and the simulator:
/// refuse during an exam; evaluate the detect rule; for <see cref="InstallMode.Check"/> stop
/// there, for <see cref="InstallMode.Missing"/> stop when the program is already there; otherwise
/// pull the installer into the private staging directory (hash-verified), run it with a
/// "still installing" line every <see cref="Defaults.PackageInstallHeartbeat"/>, read its exit
/// code, evaluate the rule again, and delete the staged file. One installer at a time per PC —
/// Windows Installer refuses a second one anyway (1618).
/// </summary>
public sealed class PackageInstallRunner
{
    /// <summary>Downloads a referenced file into a stream, verifying its hash; <c>AgentLink.PullFileAsync</c> on a PC.</summary>
    public delegate Task<long> PullFile(string reference, string sha256, Stream destination, CancellationToken token, Func<long, Task>? progress);

    private readonly IPackageHost _host;
    private readonly PullFile _pull;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _heartbeat;

    public PackageInstallRunner(IPackageHost host, PullFile pull, TimeSpan? heartbeat = null)
    {
        _host = host;
        _pull = pull;
        _heartbeat = heartbeat ?? Defaults.PackageInstallHeartbeat;
    }

    public async Task<JobResult> RunAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        if (!InstallPackageRequest.TryParse(job, out var request, out var error))
        {
            return Result(job, false, PackageOutcomes.NotRun, PcText.Get("JobCannotRun", error));
        }

        if (_host.ExamHeld)
        {
            return Result(job, false, PackageOutcomes.ExamHeld, PcText.Get("PackageExamHeld", request.Package));
        }

        var before = SafeDetect(request);
        if (request.Mode == InstallMode.Check)
        {
            return before.Installed
                ? Result(job, true, PackageOutcomes.Present, PcText.Get("PackagePresent", request.Package, VersionText(before)))
                : Result(job, true, PackageOutcomes.Absent, PcText.Get("PackageAbsent", request.Package));
        }

        if (request.Mode == InstallMode.Missing && before.Installed)
        {
            return Result(job, true, PackageOutcomes.Present, PcText.Get("PackageAlreadyInstalled", request.Package, VersionText(before)));
        }

        while (!await _gate.WaitAsync(TimeSpan.FromSeconds(10), token))
        {
            await report(new JobProgress { JobId = job.Id, Line = PcText.Get("PackageWaiting") });
        }

        try
        {
            return await InstallAsync(job, request, report, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JobResult> InstallAsync(Job job, InstallPackageRequest request, Func<JobProgress, Task> report, CancellationToken token)
    {
        var directory = Path.Combine(_host.StagingDirectory, Guid.NewGuid().ToString("N"));
        var staged = Path.Combine(directory, request.File);
        try
        {
            try
            {
                Directory.CreateDirectory(directory);
                await using var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, Defaults.FileChunkBytes, FileOptions.Asynchronous);
                var lastReport = DateTimeOffset.MinValue;
                await _pull(request.Reference, request.Sha256, destination, token, async received =>
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - lastReport < TimeSpan.FromSeconds(1)) return;
                    lastReport = now;
                    await report(new JobProgress { JobId = job.Id, Line = PcText.Get("PackageReceiving", request.File, received) });
                });
                await destination.FlushAsync(token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Link.FilePullException)
            {
                return Result(job, false, PackageOutcomes.NotRun, PcText.Get("PackagePullFailed", request.File, ex.Message));
            }

            var installer = staged;
            if (request.Run.Length > 0)
            {
                // A .zip package (D-89 addendum): unpacked beside the archive, run from its own folder.
                try
                {
                    var unpacked = Path.Combine(directory, "x");
                    await PackageArchive.ExtractAsync(staged, unpacked, token);
                    installer = Path.Combine(unpacked, Path.Combine(request.Run.Split('/')));
                    if (!File.Exists(installer)) throw new InvalidDataException(request.Run);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    return Result(job, false, PackageOutcomes.NotRun, PcText.Get("PackageUnpackFailed", request.File, ex.Message));
                }
            }

            await report(new JobProgress { JobId = job.Id, Line = PcText.Get("PackageStarting", Path.GetFileName(installer), request.Silent) });
            var started = DateTimeOffset.UtcNow;
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
            var beats = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(_heartbeat, heartbeat.Token);
                        var minutes = (int)(DateTimeOffset.UtcNow - started).TotalMinutes;
                        await report(new JobProgress { JobId = job.Id, Line = PcText.Get("PackageInstalling", minutes, (int)request.Timeout.TotalMinutes) });
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None);

            InstallerExit exit;
            try
            {
                exit = await _host.RunInstallerAsync(request, installer, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return Result(job, false, PackageOutcomes.NotRun, PcText.Get("PackageStartFailed", request.File, ex.Message));
            }
            finally
            {
                await heartbeat.CancelAsync();
                await beats;
            }

            if (exit.TimedOut)
            {
                return Result(job, false, PackageOutcomes.TimedOut, PcText.Get("PackageTimedOut", request.File, (int)request.Timeout.TotalMinutes), exit.ExitCode);
            }

            var reboot = exit.ExitCode is 3010 or 1641;
            if (exit.ExitCode != 0 && !reboot)
            {
                var key = exit.ExitCode == 1618 ? "PackageAnotherInstall" : "PackageFailedCode";
                return Result(job, false, PackageOutcomes.Failed, PcText.Get(key, request.File, exit.ExitCode.ToString(CultureInfo.InvariantCulture)), exit.ExitCode);
            }

            var after = SafeDetect(request);
            if (!after.Installed)
            {
                return Result(job, false, PackageOutcomes.NotDetected, PcText.Get("PackageNotDetected", request.File, exit.ExitCode.ToString(CultureInfo.InvariantCulture)), exit.ExitCode);
            }

            return reboot
                ? Result(job, true, PackageOutcomes.RebootRequired, PcText.Get("PackageInstalledReboot", request.Package, VersionText(after)), exit.ExitCode)
                : Result(job, true, PackageOutcomes.Installed, PcText.Get("PackageInstalled", request.Package, VersionText(after)), exit.ExitCode);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file the installer still holds; the next install's directory is a new one, and the agent clears the staging root at start.
            }
        }
    }

    private DetectFinding SafeDetect(InstallPackageRequest request)
    {
        try
        {
            return _host.Detect(request.Detect);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return new DetectFinding(false, null);
        }
    }

    private static string VersionText(DetectFinding finding) => string.IsNullOrWhiteSpace(finding.Version) ? "—" : finding.Version!;

    private static JobResult Result(Job job, bool ok, string outcome, string message, int exitCode = 0) =>
        new() { JobId = job.Id, Ok = ok, ExitCode = ok ? exitCode : (exitCode == 0 ? -1 : exitCode), Message = message, Outcome = outcome };
}
