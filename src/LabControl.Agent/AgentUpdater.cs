using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using LabControl.Shared;
using LabControl.Shared.Jobs;
using LabControl.Shared.Link;
using LabControl.Shared.Localization;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent;

/// <summary>Verifies lab-signed manifests and executable hashes, installs a new version,
/// arms external probation recovery, then restarts the service. A re-sent job confirms
/// the running version while the independent monitor decides whether probation passed.</summary>
internal sealed class AgentUpdater
{
    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private readonly AgentLink _link;
    private readonly X509Certificate2 _authority;
    private readonly ILogger _log;
    private readonly InstallLayout _layout = InstallLayout.Default;

    public AgentUpdater(AgentLink link, ILogger log, X509Certificate2 authority)
    {
        _link = link;
        _authority = authority;
        _log = log;
    }

    public async Task<JobResult> RunAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        if (!await OperationGate.WaitAsync(0, token)) return Fail(job, PcText.Get("UpdateOperationRunning"));
        try { return await RunCoreAsync(job, report, token); }
        finally { OperationGate.Release(); }
    }

    private async Task<JobResult> RunCoreAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        if (!SelfUpdateRequest.TryParse(job, out var request, out var error))
        {
            return Fail(job, PcText.Get("JobCannotRun", error));
        }

        var running = Program.InstalledVersion;

        var executable = Environment.ProcessPath;
        if (executable is null || _layout.VersionOf(executable) is null)
        {
            return Fail(job, PcText.Get("UpdateWrongLayout", executable ?? PcText.Get("UpdateUnknownPath"), _layout.AppDirectory));
        }

        // ---- the manifest

        UpdateManifest manifest;
        try
        {
            using var buffer = new MemoryStream();
            await _link.PullFileAsync(request.ManifestReference, request.ManifestSha256, buffer, token);
            if (!UpdateBundle.TryReadVerified(buffer.ToArray(), request, _authority, running, out manifest, out var problem))
            {
                // Fixed teacher-facing event: never echo signed or untrusted manifest fields here.
                _link.Report(Event.Types.Severity.Warning, "update.refused",
                    PcText.Get("UpdateRefusedEvent"));
                return Fail(job, PcText.Get("UpdateRefused", problem));
            }
        }
        catch (FilePullException ex)
        {
            return Fail(job, ex.Message);
        }

        // Share the installer lock through every filesystem/service mutation. Service
        // cancellation does not await detached jobs, so uninstall must exclude them.
        FileStream installationLease;
        try
        {
            token.ThrowIfCancellationRequested();
            installationLease = new FileStream(Path.Combine(Defaults.AgentDataDirectory, Defaults.SetupLockFileName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return Fail(job, PcText.Get("UpdatePrivilegedBusy")); }
        using var heldInstallationLease = installationLease;
        token.ThrowIfCancellationRequested();

        // ---- the re-sent job after the restart, or the same build pushed twice

        if (string.Equals(request.Version, running, StringComparison.Ordinal))
        {
            var trial = new UpdateTrial(Defaults.AgentDataDirectory).Read();
            if (trial is not null && trial.Version == running && trial.Phase == UpdateTrialPhase.OnProbation)
                return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0,
                    Message = PcText.Get("UpdateOnProbation", running, trial.Deadline.ToString("u", CultureInfo.InvariantCulture)) };
            var previous = _layout.ReadPrevious();
            var pruned = PruneOldVersions(running, previous);
            var message = previous is null || previous == running
                ? PcText.Get("UpdateRunningNow", running)
                : PcText.Get("UpdateRunningNowWas", running, previous);
            if (pruned.Count > 0)
            {
                message += PcText.Get("UpdatePruned", string.Join(", ", pruned));
            }

            return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = message };
        }

        var pendingTrial = new UpdateTrial(Defaults.AgentDataDirectory).Read();
        if (pendingTrial?.Phase is UpdateTrialPhase.OnProbation or UpdateTrialPhase.RollbackPending)
            return Fail(job, PcText.Get("UpdateStillOnProbation"));
        // A leftover directory of the same version (rolled back from, or half-placed) is replaced when
        // the files are placed below; only the running version and the rollback target are kept.
        if (Directory.Exists(_layout.VersionDirectory(request.Version)) && _layout.HoldsRecovery(request.Version))
            return Fail(job, PcText.Get("UpdateSameVersion"));

        // ---- the files, into the staging directory

        var staging = Path.Combine(Defaults.AgentDataDirectory, Defaults.UpdateStagingDirectoryName, request.Version);
        var total = manifest.Files.Sum(f => f.Size);
        long done = 0;

        try
        {
            TryDelete(staging);
            Directory.CreateDirectory(staging);

            foreach (var file in manifest.Files)
            {
                var path = Path.Combine(staging, file.RelativePath);
                long received;
                var startedAt = done;
                await using (var destination = new ProgressStream(File.Create(path), total, startedAt, percent => report(new JobProgress { JobId = job.Id, Percent = percent, Line = string.Empty })))
                {
                    received = await _link.PullFileAsync(file.Sha256, file.Sha256, destination, token);
                }

                if (received != file.Size)
                {
                    TryDelete(staging);
                    return Fail(job, PcText.Get("UpdateSizeMismatch", file.RelativePath, received, file.Size));
                }

                done += received;
                await report(new JobProgress { JobId = job.Id, Percent = Percent(done, total), Line = PcText.Get("UpdatePulled", file.RelativePath, AgentBuild.Megabytes(received)) });
            }
        }
        catch (FilePullException ex)
        {
            TryDelete(staging);
            return Fail(job, PcText.Get("UpdateNothingInstalled", ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            return Fail(job, PcText.Get("UpdateWriteFailed", staging, ex.Message));
        }

        // ---- preflight: does the new agent.exe run on this PC, and is it the version it claims?

        var expectedBase = InstallLayout.BaseVersionOf(request.Version);
        var preflight = await PreflightAsync(Path.Combine(staging, Defaults.AgentExecutableName), token);
        if (preflight.Error is not null)
        {
            TryDelete(staging);
            return Fail(job, PcText.Get("UpdateNotInstalled", Defaults.AgentExecutableName, preflight.Error));
        }

        if (!string.Equals(preflight.Version, expectedBase, StringComparison.Ordinal))
        {
            TryDelete(staging);
            return Fail(job, PcText.Get("UpdateVersionMismatch", Defaults.AgentExecutableName, preflight.Version, request.Version));
        }

        await report(new JobProgress { JobId = job.Id, Percent = 100, Line = PcText.Get("UpdateRunsHere", Defaults.AgentExecutableName, preflight.Version) });

        // ---- into app\<version>

        var target = _layout.VersionDirectory(request.Version);
        try
        {
            _layout.RemoveVersion(request.Version);
            await MoveDirectoryAsync(staging, target, job, report, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            TryDelete(target);
            return Fail(job, PcText.Get("UpdatePlaceFailed", target, ex.Message));
        }

        if (!WindowsServiceHelpers.IsWindowsService())
        {
            // A foreground `agent.exe --run` has no service to repoint; the files are in place
            // and the developer starts the new version by hand.
            return new JobResult
            {
                JobId = job.Id,
                Ok = true,
                ExitCode = 0,
                Message = PcText.Get("UpdateInstalledNotService", target),
            };
        }

        // ---- markers and the service

        token.ThrowIfCancellationRequested();
        try
        {
            UpdateRecovery.Arm(job.Id, request.Version, running, executable, message => _log.LogWarning("{Message}", message));
        }
        catch (Exception ex)
        {
            TryDelete(target);
            return Fail(job, PcText.Get("UpdateRecoveryFailed", ex.Message));
        }

        var previousMarker = _layout.ReadPrevious();
        var newExecutable = _layout.AgentExecutable(request.Version);

        try
        {
            _layout.WritePrevious(running);
            _layout.WriteCurrent(request.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Revert(job.Id, running, previousMarker, executable, target);
            return Fail(job, PcText.Get("UpdatePointersFailed", ex.Message));
        }

        var repoint = ServiceControl.SetBinaryPath(newExecutable);
        if (repoint is not null)
        {
            Revert(job.Id, running, previousMarker, executable, target);
            return Fail(job, PcText.Get("UpdateRepointFailed", repoint));
        }

        _log.LogInformation("{Pc}: service repointed at {Executable}; asking for a restart", _link.Name, newExecutable);
        await report(new JobProgress { JobId = job.Id, Percent = 100, Line = PcText.Get("UpdateRestarting", request.Version) });

        var spawn = ServiceControl.SpawnRestart(executable);
        if (spawn is not null)
        {
            Revert(job.Id, running, previousMarker, executable, target);
            return Fail(job, PcText.Get("UpdateSpawnFailed", spawn, running));
        }

        // The stop arrives as cancellation: the link is disposed, this throws, no result is
        // sent, and the console re-sends the job to the new version (PROTOCOL, self_update).
        try
        {
            await Task.Delay(Defaults.ServiceRestartWait, token);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("{Pc}: stopping for the update to {Version}", _link.Name, request.Version);
            throw;
        }

        _log.LogError("{Pc}: the service was not restarted within {Seconds:0} s; putting {Running} back", _link.Name, Defaults.ServiceRestartWait.TotalSeconds, running);
        Revert(job.Id, running, previousMarker, executable, target);
        return Fail(job, PcText.Get("UpdateRestartTimeout", Defaults.ServiceRestartWait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture), running));
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Preflight(string? Version, string? Error);

    /// <summary>Runs the staged executable with <c>--version</c>; the last line it prints is its version.</summary>
    private static async Task<Preflight> PreflightAsync(string executable, CancellationToken token)
    {
        var lines = new List<string>();
        ProcessOutcome outcome;
        try
        {
            var info = new ProcessStartInfo(executable, Defaults.AgentVersionSwitch)
            {
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.SystemDirectory,
            };
            outcome = await ProcessRunner.RunAsync(info, Defaults.UpdatePreflightTimeout, line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            }, token);
        }
        catch (Win32Exception ex)
        {
            return new Preflight(null, PcText.Get("PreflightWontStart", ex.Message));
        }

        if (outcome.TimedOut)
        {
            return new Preflight(null, PcText.Get("PreflightSilent", Defaults.UpdatePreflightTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)));
        }

        var version = lines.LastOrDefault(l => l.Length > 0 && !l.StartsWith(ProcessRunner.StderrPrefix, StringComparison.Ordinal))?.Trim();
        if (outcome.ExitCode != 0 || version is null || !InstallLayout.IsValidVersion(version))
        {
            var said = lines.Count == 0 ? PcText.Get("PreflightNothing") : string.Join(" | ", lines.TakeLast(3));
            return new Preflight(null, PcText.Get("PreflightExitCode", Defaults.AgentVersionSwitch, outcome.ExitCode, said));
        }

        return new Preflight(version, null);
    }

    private void Revert(string jobId, string running, string? previousMarker, string runningExecutable, string newDirectory)
    {
        try
        {
            var trial = new UpdateTrial(Defaults.AgentDataDirectory);
            var restored = trial.RollBack(DateTimeOffset.UtcNow, true, state =>
            {
                if (state.Previous != running)
                    throw new InvalidOperationException("The pending update does not belong to this running version.");
                var repoint = ServiceControl.SetBinaryPath(runningExecutable);
                if (repoint is not null)
                    throw new IOException("Could not restore the service binary path: " + repoint);
                _layout.WriteCurrent(running);
                if (previousMarker is null) _layout.ClearPrevious();
                else _layout.WritePrevious(previousMarker);
            }, jobId);
            // Never delete an executable while the service still points to it, or
            // after a concurrent recovery changed the trial identity. A failed callback
            // leaves RollbackPending for the external task to finish.
            if (restored)
            {
                TryDelete(newDirectory);
                if (trial.Read() is { } state) UpdateRecovery.FinalizeTerminal(state);
            }
        }
        catch (Exception ex)
        {
            _log.LogError("{Pc}: update rollback is incomplete; keeping both executables for external recovery: {Message}",
                _link.Name, ex.Message);
        }
    }

    /// <summary>Removes every version directory but the running one and <c>app\previous</c>, so pushes do not pile up (D-33).</summary>
    private IReadOnlyList<string> PruneOldVersions(string running, string? previous)
    {
        var removed = new List<string>();
        foreach (var version in _layout.InstalledVersions())
        {
            if (version == running || version == previous)
            {
                continue;
            }

            try
            {
                _layout.RemoveVersion(version);
                removed.Add(version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "{Pc}: could not remove old version {Version}: {Message}", _link.Name, version, ex.Message);
            }
        }

        return removed;
    }

    /// <summary>
    /// Moves the staged version into <c>app\</c>. A freshly written 90 MB executable that has
    /// just been run once is exactly what an antivirus scans, and while it does the file is
    /// "access denied" — seen on `PC-10` (D-33 item 9). So the move is retried for
    /// <see cref="Defaults.UpdatePlaceTimeout"/> before it counts as a failure.
    /// </summary>
    private async Task MoveDirectoryAsync(string source, string target, Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + Defaults.UpdatePlaceTimeout;
        var waiting = false;
        while (true)
        {
            try
            {
                MoveDirectory(source, target);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTimeOffset.UtcNow < deadline)
            {
                if (!waiting)
                {
                    waiting = true;
                    _log.LogWarning("{Pc}: cannot place the new version yet ({Message}); retrying for up to {Seconds:0} s", _link.Name, ex.Message, Defaults.UpdatePlaceTimeout.TotalSeconds);
                    await report(new JobProgress { JobId = job.Id, Percent = 100, Line = PcText.Get("UpdateFilesLocked", ex.Message) });
                }

                TryDelete(target);
                await Task.Delay(Defaults.UpdatePlaceRetryInterval, token);
            }
        }
    }

    private static void MoveDirectory(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            // Another volume: copy, then remove the staging copy.
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }

            Directory.Delete(source, recursive: true);
        }
    }

    private static int Percent(long done, long total) => total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);

    private static JobResult Fail(Job job, string message) => new() { JobId = job.Id, Ok = false, ExitCode = -1, Message = message };

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "could not remove {Directory}: {Message}", directory, ex.Message);
        }
    }

    /// <summary>Counts what a pull writes and reports whole-bundle percent every few points, so the jobs panel moves during a long pull.</summary>
    private sealed class ProgressStream(Stream inner, long total, long before, Func<int, Task> onPercent) : Stream
    {
        private long _written;
        private int _lastReported = -1;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            _written += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            _written += buffer.Length;

            var percent = Percent(before + _written, total);
            if (percent / 5 != _lastReported / 5 && percent < 100)
            {
                _lastReported = percent;
                await onPercent(percent);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
