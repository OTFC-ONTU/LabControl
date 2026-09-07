using System.ComponentModel;
using System.Diagnostics;
using LabControl.Shared;
using LabControl.Shared.Jobs;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace LabControl.Agent;

/// <summary>
/// The minimal push-and-restart of M2 (ROADMAP M2, ARCHITECTURE §7.2 steps 2–3, D-33):
/// pull the manifest and every file it names into <c>ProgramData\LabControl\update\</c>,
/// verify each hash, run the new <c>agent.exe --version</c> once to prove it runs here and
/// says what the bundle claims, move it into <c>app\&lt;version&gt;\</c>, write
/// <c>app\previous</c> and <c>app\current</c>, repoint the service and have it restarted.
/// No signature, no probation, no rollback — those are M4. The outgoing agent never sends a
/// <c>JobResult</c>: it is stopped mid-job, the console re-sends the job to the new version,
/// and the new version answers because it <i>is</i> the version the job names. Anything
/// that fails before the restart is undone and reported by the outgoing agent.
/// </summary>
internal sealed class AgentUpdater
{
    private readonly AgentLink _link;
    private readonly ILogger _log;
    private readonly InstallLayout _layout = InstallLayout.Default;

    public AgentUpdater(AgentLink link, ILogger log)
    {
        _link = link;
        _log = log;
    }

    public async Task<JobResult> RunAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        if (!SelfUpdateRequest.TryParse(job, out var request, out var error))
        {
            return Fail(job, $"This job cannot run: {error}.");
        }

        var running = Program.InstalledVersion;

        // ---- the re-sent job after the restart, or the same build pushed twice

        if (string.Equals(request.Version, running, StringComparison.Ordinal))
        {
            var previous = _layout.ReadPrevious();
            var pruned = PruneOldVersions(running, previous);
            var message = previous is null || previous == running
                ? $"Running {running} now."
                : $"Running {running} now (was {previous}; app\\previous still names it).";
            if (pruned.Count > 0)
            {
                message += $" Removed older version director{(pruned.Count == 1 ? "y" : "ies")}: {string.Join(", ", pruned)}.";
            }

            return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = message };
        }

        var executable = Environment.ProcessPath;
        if (executable is null || _layout.VersionOf(executable) is null)
        {
            return Fail(job, $"This agent runs from {executable ?? "an unknown path"}, not from {_layout.AppDirectory}\\<version>\\; " +
                             $"install it with dev-install.ps1 (or Setup) first, then push builds to it.");
        }

        // ---- the manifest

        UpdateManifest manifest;
        try
        {
            using var buffer = new MemoryStream();
            await _link.PullFileAsync(request.ManifestReference, request.ManifestSha256, buffer, token);
            if (!UpdateBundle.TryRead(buffer.ToArray(), request, out manifest, out var problem))
            {
                return Fail(job, $"The update was refused: {problem}.");
            }
        }
        catch (FilePullException ex)
        {
            return Fail(job, ex.Message);
        }

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
                    return Fail(job, $"{file.RelativePath} is {received} bytes but the manifest says {file.Size}; nothing was installed.");
                }

                done += received;
                await report(new JobProgress { JobId = job.Id, Percent = Percent(done, total), Line = $"pulled {file.RelativePath} ({AgentBuild.Megabytes(received)})" });
            }
        }
        catch (FilePullException ex)
        {
            TryDelete(staging);
            return Fail(job, ex.Message + " Nothing was installed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            return Fail(job, $"Could not write the update under {staging}: {ex.Message}");
        }

        // ---- preflight: does the new agent.exe run on this PC, and is it the version it claims?

        var expectedBase = InstallLayout.BaseVersionOf(request.Version);
        var preflight = await PreflightAsync(Path.Combine(staging, Defaults.AgentExecutableName), token);
        if (preflight.Error is not null)
        {
            TryDelete(staging);
            return Fail(job, $"The new {Defaults.AgentExecutableName} was not installed: {preflight.Error}");
        }

        if (!string.Equals(preflight.Version, expectedBase, StringComparison.Ordinal))
        {
            TryDelete(staging);
            return Fail(job, $"The new {Defaults.AgentExecutableName} reports version {preflight.Version}, but the bundle is named {request.Version}; " +
                             "correct the version in the push dialog. Nothing was installed.");
        }

        await report(new JobProgress { JobId = job.Id, Percent = 100, Line = $"{Defaults.AgentExecutableName} {preflight.Version} runs on this PC" });

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
            return Fail(job, $"Could not place the new version in {target}: {ex.Message}");
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
                Message = $"Installed into {target}. This agent is not running as a service, so nothing was repointed or restarted; start the new version yourself.",
            };
        }

        // ---- markers and the service

        var previousMarker = _layout.ReadPrevious();
        var newExecutable = _layout.AgentExecutable(request.Version);

        try
        {
            _layout.WritePrevious(running);
            _layout.WriteCurrent(request.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Revert(running, previousMarker, executable, target);
            return Fail(job, $"Could not write app\\current and app\\previous: {ex.Message}. Nothing was changed.");
        }

        var repoint = ServiceControl.SetBinaryPath(newExecutable);
        if (repoint is not null)
        {
            Revert(running, previousMarker, executable, target);
            return Fail(job, $"Could not repoint the service: {repoint}. Nothing was changed.");
        }

        _log.LogInformation("{Pc}: service repointed at {Executable}; asking for a restart", _link.Name, newExecutable);
        await report(new JobProgress { JobId = job.Id, Percent = 100, Line = $"installed into app\\{request.Version}; service repointed, restarting it now" });

        var spawn = ServiceControl.SpawnRestart(executable);
        if (spawn is not null)
        {
            Revert(running, previousMarker, executable, target);
            return Fail(job, $"{spawn}. The service was pointed back at {running}; nothing was changed.");
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
        Revert(running, previousMarker, executable, target);
        return Fail(job, $"The service was not restarted within {Defaults.ServiceRestartWait.TotalSeconds:0} s. It was pointed back at {running} and the new version removed; look at the agent log on the PC.");
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
            return new Preflight(null, $"it does not start on this PC ({ex.Message}); a build for the wrong architecture?");
        }

        if (outcome.TimedOut)
        {
            return new Preflight(null, $"it printed nothing for {Defaults.UpdatePreflightTimeout.TotalSeconds:0} s when asked for its version");
        }

        var version = lines.LastOrDefault(l => l.Length > 0 && !l.StartsWith(ProcessRunner.StderrPrefix, StringComparison.Ordinal))?.Trim();
        if (outcome.ExitCode != 0 || version is null || !InstallLayout.IsValidVersion(version))
        {
            var said = lines.Count == 0 ? "nothing" : string.Join(" | ", lines.TakeLast(3));
            return new Preflight(null, $"`{Defaults.AgentVersionSwitch}` exited with code {outcome.ExitCode} and printed {said}");
        }

        return new Preflight(version, null);
    }

    private void Revert(string running, string? previousMarker, string runningExecutable, string newDirectory)
    {
        var repoint = ServiceControl.SetBinaryPath(runningExecutable);
        if (repoint is not null)
        {
            _log.LogError("{Pc}: could not point the service back at {Executable}: {Problem}", _link.Name, runningExecutable, repoint);
        }

        try
        {
            _layout.WriteCurrent(running);
            if (previousMarker is null)
            {
                _layout.ClearPrevious();
            }
            else
            {
                _layout.WritePrevious(previousMarker);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "{Pc}: could not restore app\\current / app\\previous: {Message}", _link.Name, ex.Message);
        }

        TryDelete(newDirectory);
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
                    await report(new JobProgress { JobId = job.Id, Percent = 100, Line = $"waiting for the new files to be released ({ex.Message}; an antivirus scan?)" });
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
