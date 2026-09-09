using LabControl.Console.Localization;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>The points at which the migration has changed something on disk, in order.</summary>
public enum MigrationStep
{
    /// <summary>Every original was copied into <c>labs/&lt;lab_id&gt;.migrating/</c>. The originals are untouched.</summary>
    Copied = 1,

    /// <summary>The staging directory was renamed to <c>labs/&lt;lab_id&gt;/</c>. The originals are untouched.</summary>
    Renamed = 2,

    /// <summary>The lab is in <c>profiles.json</c>: the commit point. The originals are untouched.</summary>
    Committed = 3,

    /// <summary>The originals are gone: every one of them matched its copy byte for byte.</summary>
    Deleted = 4,

    /// <summary>
    /// The originals no longer matched the committed copy and were moved, complete, into a
    /// <c>migration-conflict-*</c> directory at the data root instead of being deleted.
    /// </summary>
    MovedAside = 5,
}

/// <summary>
/// Moves the pre-M5 single-lab data directory — <c>lab-key.lck</c>, <c>instance.json</c> and
/// the rest flat at the root — into <c>labs/&lt;lab_id&gt;/</c> and writes the first
/// <c>profiles.json</c> (M5 §2.3, D-55). Copy, rename, commit, delete: the originals are not
/// touched until the index names the new directory, so a crash at any point leaves a
/// directory the next launch finishes from the same end state, and a pre-M5 build can still
/// be pointed at <c>labs/&lt;lab_id&gt;/</c>. The instance document moves verbatim: its key
/// reference in the OS keystore is <c>instance-&lt;instance_id&gt;</c>, which does not
/// mention a path, so this machine keeps its identity and no student PC notices anything.
/// <para>
/// Nothing at the root is deleted unless it matches its copy: a document byte for byte, a
/// directory file by file and size by size. A copy that does not match and is not yet in the
/// index is thrown away and made again from the root; one that is already committed is kept,
/// and the root files are moved complete into a <c>migration-conflict-*</c> directory for the
/// owner to look at — the case of a pre-M5 build having been run on the root again after the
/// move (README, <i>Downgrading</i>), where the root is newer than the copy.
/// </para>
/// </summary>
public sealed class ProfileMigration
{
    private static readonly string[] DocumentNames =
    [
        Defaults.LabKeyFileName,
        Defaults.InstanceFileName,
        Defaults.LabFileName,
        Defaults.EnrollmentFileName,
        Defaults.ScriptsFileName,
    ];

    /// <summary>
    /// Deletion order: the lab's own documents and directories first, the two files that
    /// mark a root as pending — the instance, then the key — last. A crash anywhere in
    /// between leaves a root the next launch still recognises as unfinished.
    /// </summary>
    private static readonly string[] DocumentDeletionOrder =
    [
        Defaults.LabFileName,
        Defaults.EnrollmentFileName,
        Defaults.ScriptsFileName,
        Defaults.InstanceFileName,
        Defaults.LabKeyFileName,
    ];

    private static readonly string[] DirectoryNames =
    [
        Defaults.PackagesDirectoryName,
        Defaults.LogsDirectoryName,
    ];

    /// <summary>An antivirus can hold a freshly written file for a moment (D-33 item 9); a rename is retried, not failed.</summary>
    private const int MoveAttempts = 5;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Headroom over the size of the originals before the copy starts.</summary>
    private const long FreeSpaceMarginBytes = 64L * 1024 * 1024;

    private readonly string _dataDirectory;
    private readonly ILogger _log;

    public ProfileMigration(string dataDirectory, ILogger log)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _log = log;
    }

    /// <summary>Raised after each step has reached disk; tests use it to stop the process mid-way.</summary>
    public event Action<MigrationStep>? StepCompleted;

    /// <summary>
    /// True while a single-lab layout is still at the root, complete or half-moved: the key or
    /// the instance is there, or the index already exists and any other lab file or directory
    /// — <c>lab.json</c>, <c>enrollment.json</c>, <c>scripts.json</c>, <c>packages/</c>, a
    /// <c>logs/</c> with anything but the app's own log files — was left behind by a delete
    /// that did not finish.
    /// </summary>
    public static bool IsPending(string dataDirectory) =>
        HasSentinel(dataDirectory)
        || (File.Exists(Path.Combine(dataDirectory, Defaults.ProfilesFileName)) && HasOtherLabData(dataDirectory));

    /// <summary>
    /// Runs or resumes the migration. Returns the lab id that was moved, or <c>null</c> when
    /// the root holds no single-lab layout — a fresh directory or an already migrated one.
    /// </summary>
    /// <exception cref="InvalidDataException">The root is not a lab this build can move: key and instance from different labs, an instance without a key, a key naming no lab.</exception>
    /// <exception cref="IOException">Not enough free space, or a file could not be copied or renamed.</exception>
    public string? Run(DateTimeOffset? now = null)
    {
        if (!IsPending(_dataDirectory))
        {
            return null;
        }

        var when = now ?? DateTimeOffset.UtcNow;
        var profiles = new ProfileStore(_dataDirectory, _log);
        var labId = ResolveLabId(profiles);
        if (labId is null)
        {
            return null;
        }

        var target = profiles.Directory(labId);
        var staging = target + Defaults.MigratingDirectorySuffix;
        var committed = profiles.Find(labId) is not null;

        _log.LogInformation("Moving the single lab {LabId} from the data root into {Target}", labId, target);

        if (Directory.Exists(target))
        {
            if (Directory.Exists(staging))
            {
                // The rename happened; a staging directory beside it is a leftover of a copy that
                // was itself interrupted before the discard below ran.
                _log.LogInformation("Discarding the stray {Staging}", staging);
                Directory.Delete(staging, recursive: true);
            }

            if (!committed)
            {
                var differences = Compare(target);
                if (differences.Count > 0)
                {
                    // The copy is not in the index yet, so the root is still the only truth:
                    // an older copy, or one a pre-M5 build ran beside, is worth nothing.
                    _log.LogWarning("{Target} exists but differs from the data root ({Differences}); it is not in {Index} yet and is copied again",
                        target, string.Join(", ", differences), Defaults.ProfilesFileName);
                    Directory.Delete(target, recursive: true);
                }
            }
        }

        if (!Directory.Exists(target))
        {
            if (Directory.Exists(staging))
            {
                // An earlier attempt stopped mid-copy; a partial copy is worth nothing.
                _log.LogInformation("Discarding the unfinished copy at {Staging}", staging);
                Directory.Delete(staging, recursive: true);
            }

            EnsureFreeSpace();
            CopyOriginals(staging);
            _log.LogInformation("Copied the lab into {Staging}", staging);
            StepCompleted?.Invoke(MigrationStep.Copied);

            MoveDirectory(staging, target);
            _log.LogInformation("Renamed {Staging} to {Target}", staging, target);
            StepCompleted?.Invoke(MigrationStep.Renamed);
        }

        if (!committed)
        {
            profiles.Upsert(DescribeMigrated(labId, new LabStore(target), when));
            profiles.LastUsedLabId ??= labId;
            profiles.Save();
            _log.LogInformation("Recorded lab {LabId} in {Index}", labId, Defaults.ProfilesFileName);
            StepCompleted?.Invoke(MigrationStep.Committed);
        }

        MoveAppLogsToRoot();

        var conflicts = Compare(target);
        if (conflicts.Count > 0)
        {
            // The committed copy is what the console opens from now on; the root is newer
            // than it in some way and is kept, complete, where the owner can find it.
            var conflict = Path.Combine(_dataDirectory, Defaults.MigrationConflictDirectoryPrefix + when.ToString("yyyyMMdd-HHmmss"));
            MoveOriginalsTo(conflict);
            _log.LogWarning("The files at the data root no longer match the saved lab {LabId} ({Differences}); they were moved to {Conflict} instead of being deleted. The console opens the saved lab.",
                labId, string.Join(", ", conflicts), conflict);
            StepCompleted?.Invoke(MigrationStep.MovedAside);
            return labId;
        }

        DeleteOriginals(target);
        _log.LogInformation("Removed the single-lab files from the data root; the lab now lives in {Target}", target);
        StepCompleted?.Invoke(MigrationStep.Deleted);

        return labId;
    }

    // ------------------------------------------------------------------ what is at the root

    private static bool HasSentinel(string dataDirectory) =>
        File.Exists(Path.Combine(dataDirectory, Defaults.LabKeyFileName))
        || File.Exists(Path.Combine(dataDirectory, Defaults.InstanceFileName));

    private static bool HasOtherLabData(string dataDirectory)
    {
        if (File.Exists(Path.Combine(dataDirectory, Defaults.LabFileName))
            || File.Exists(Path.Combine(dataDirectory, Defaults.EnrollmentFileName))
            || File.Exists(Path.Combine(dataDirectory, Defaults.ScriptsFileName))
            || Directory.Exists(Path.Combine(dataDirectory, Defaults.PackagesDirectoryName)))
        {
            return true;
        }

        var logs = Path.Combine(dataDirectory, Defaults.LogsDirectoryName);
        return Directory.Exists(logs) && Directory.EnumerateFileSystemEntries(logs).Any(entry => Directory.Exists(entry) || !IsAppLog(entry));
    }

    /// <summary>
    /// Which lab the root holds. The key says so; the instance must agree. An instance without
    /// a key is a pre-M5 installation that lost its key, not a lab — unless the index already
    /// names it, in which case an older build's delete stopped between the two files.
    /// </summary>
    private string? ResolveLabId(ProfileStore profiles)
    {
        var keyPath = Path.Combine(_dataDirectory, Defaults.LabKeyFileName);
        var instancePath = Path.Combine(_dataDirectory, Defaults.InstanceFileName);

        var key = JsonStore.LoadIfExists<LabKeyDocument>(keyPath, LabKeyDocument.Migrations);
        var instance = JsonStore.LoadIfExists<InstanceDocument>(instancePath, InstanceDocument.Migrations);

        if (key is not null && instance is not null
            && !string.Equals(key.LabId, instance.LabId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Strings.Format("Bootstrap.KeyInstanceMismatch",
                Defaults.InstanceFileName, instance.LabId, Defaults.LabKeyFileName, key.LabId));
        }

        if (key is not null)
        {
            if (string.IsNullOrWhiteSpace(key.LabId))
            {
                throw new InvalidDataException(Strings.Format("Migration.KeyNamesNoLab", Defaults.LabKeyFileName));
            }

            return key.LabId;
        }

        if (instance is not null)
        {
            if (!string.IsNullOrWhiteSpace(instance.LabId) && profiles.Find(instance.LabId) is not null)
            {
                return instance.LabId;
            }

            throw new InvalidDataException(Strings.Format("Migration.InstanceWithoutKey",
                Defaults.InstanceFileName, _dataDirectory, Defaults.LabKeyFileName));
        }

        // Neither sentinel: the index exists and a delete stopped before it finished. The
        // lab document says which lab; without it, or for a lab the index does not know,
        // the leftovers are nobody's and are left where they are.
        var lab = JsonStore.LoadIfExists<LabDocument>(Path.Combine(_dataDirectory, Defaults.LabFileName), LabDocument.Migrations);
        if (lab is not null && !string.IsNullOrWhiteSpace(lab.LabId) && profiles.Find(lab.LabId) is not null)
        {
            return lab.LabId;
        }

        _log.LogWarning("Lab files remain at the data root {Data} but they belong to no lab in {Index}; they were left alone",
            _dataDirectory, Defaults.ProfilesFileName);
        return null;
    }

    // ------------------------------------------------------------------ copy

    private void EnsureFreeSpace()
    {
        var total = OriginalsSize();
        _log.LogInformation("The single-lab files at the data root take {Bytes} bytes", total);

        long free;
        try
        {
            var root = OperatingSystem.IsWindows() ? Path.GetPathRoot(_dataDirectory) ?? _dataDirectory : _dataDirectory;
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read the free space of the volume holding {Data}; copying without the check", _dataDirectory);
            return;
        }

        if (free < total + FreeSpaceMarginBytes)
        {
            throw new IOException(Strings.Format("Migration.NotEnoughSpace",
                ToMegabytes(total + FreeSpaceMarginBytes), _dataDirectory, ToMegabytes(free)));
        }
    }

    private static long ToMegabytes(long bytes) => (bytes + (1024 * 1024) - 1) / (1024 * 1024);

    private long OriginalsSize()
    {
        long total = 0;
        foreach (var name in DocumentNames)
        {
            var path = Path.Combine(_dataDirectory, name);
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        foreach (var name in DirectoryNames)
        {
            var directory = Path.Combine(_dataDirectory, name);
            if (Directory.Exists(directory))
            {
                total += ListTree(directory, skipAppLogs: name == Defaults.LogsDirectoryName).Values.Sum();
            }
        }

        return total;
    }

    private void CopyOriginals(string staging)
    {
        Directory.CreateDirectory(staging);

        foreach (var name in DocumentNames)
        {
            var source = Path.Combine(_dataDirectory, name);
            if (!File.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(staging, name);
            CopyFile(source, destination);
            if (name is Defaults.LabKeyFileName or Defaults.InstanceFileName)
            {
                RestrictToOwner(destination);
            }
        }

        foreach (var name in DirectoryNames)
        {
            var source = Path.Combine(_dataDirectory, name);
            if (Directory.Exists(source))
            {
                CopyTree(source, Path.Combine(staging, name), skipAppLogs: name == Defaults.LogsDirectoryName);
            }
        }
    }

    private void CopyTree(string source, string destination, bool skipAppLogs)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (IsLink(entry))
            {
                // A symlink or junction is not lab data, and following one could loop forever
                // or copy something far outside the data directory.
                _log.LogWarning("Skipping the link {Path} -> {Target}; links are not copied", entry.FullName, entry.LinkTarget);
                continue;
            }

            if (entry is DirectoryInfo directory)
            {
                CopyTree(directory.FullName, Path.Combine(destination, directory.Name), skipAppLogs: false);
            }
            else if (!(skipAppLogs && IsAppLog(entry.FullName)))
            {
                CopyFile(entry.FullName, Path.Combine(destination, entry.Name));
            }
        }
    }

    private static void CopyFile(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);

        // A read-only original would make its copy read-only too, and the copy must be
        // writable: the console saves over it from now on.
        var attributes = File.GetAttributes(destination);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(destination, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static bool IsLink(FileSystemInfo entry) =>
        entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0;

    private void MoveDirectory(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts && ex is (IOException or UnauthorizedAccessException))
            {
                _log.LogWarning(ex, "Renaming {Source} to {Destination} failed (attempt {Attempt} of {Attempts}); retrying",
                    source, destination, attempt, MoveAttempts);
                Thread.Sleep(MoveRetryDelay);
            }
        }
    }

    // ------------------------------------------------------------------ compare

    /// <summary>
    /// What at the root does not match its copy under <paramref name="target"/>: a document
    /// whose bytes differ, a file under <c>packages/</c> or <c>logs/</c> that the copy lacks
    /// or has at another size. Root files that are already gone count as matching — a delete
    /// that stopped half-way is the expected way to get here — and extra files in the copy
    /// are the copy's business.
    /// </summary>
    private IReadOnlyList<string> Compare(string target)
    {
        var differences = new List<string>();

        foreach (var name in DocumentNames)
        {
            var source = Path.Combine(_dataDirectory, name);
            if (!File.Exists(source))
            {
                continue;
            }

            var copy = Path.Combine(target, name);
            if (!File.Exists(copy) || !string.Equals(JsonStore.FingerprintFile(source), JsonStore.FingerprintFile(copy), StringComparison.Ordinal))
            {
                differences.Add(name);
            }
        }

        foreach (var name in DirectoryNames)
        {
            var source = Path.Combine(_dataDirectory, name);
            if (!Directory.Exists(source))
            {
                continue;
            }

            var copy = Path.Combine(target, name);
            var copied = Directory.Exists(copy) ? ListTree(copy, skipAppLogs: false) : new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (relative, size) in ListTree(source, skipAppLogs: name == Defaults.LogsDirectoryName))
            {
                if (!copied.TryGetValue(relative, out var copiedSize) || copiedSize != size)
                {
                    differences.Add($"{name}/{relative}");
                }
            }
        }

        return differences;
    }

    /// <summary>Every regular file under a directory, by relative path with forward slashes, and its size. Links are not files.</summary>
    private static Dictionary<string, long> ListTree(string root, bool skipAppLogs)
    {
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        Walk(new DirectoryInfo(root), string.Empty, skipAppLogs);
        return files;

        void Walk(DirectoryInfo directory, string prefix, bool skipLogs)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (IsLink(entry))
                {
                    continue;
                }

                if (entry is DirectoryInfo child)
                {
                    Walk(child, prefix + child.Name + "/", skipLogs: false);
                }
                else if (entry is FileInfo file && !(skipLogs && IsAppLog(file.FullName)))
                {
                    files[prefix + file.Name] = file.Length;
                }
            }
        }
    }

    // ------------------------------------------------------------------ retire the originals

    /// <summary>
    /// Deletes the originals, last of all the two files that mark the root as pending. Each
    /// document is checked against its copy once more right before it goes: a file whose copy
    /// does not match byte for byte is never deleted.
    /// </summary>
    private void DeleteOriginals(string target)
    {
        foreach (var name in DocumentDeletionOrder)
        {
            if (name is Defaults.InstanceFileName)
            {
                DeleteLabDirectories();
            }

            var source = Path.Combine(_dataDirectory, name);
            if (File.Exists(source))
            {
                var copy = Path.Combine(target, name);
                if (!File.Exists(copy) || !string.Equals(JsonStore.FingerprintFile(source), JsonStore.FingerprintFile(copy), StringComparison.Ordinal))
                {
                    throw new IOException($"'{name}' at {_dataDirectory} changed while the lab was being moved; nothing more was deleted.");
                }

                File.Delete(source);
            }

            // A leftover of an atomic save that never finished; the document beside it was the good one.
            foreach (var temporary in JsonStore.TemporaryPathsFor(source))
            {
                File.Delete(temporary);
            }
        }
    }

    private void DeleteLabDirectories()
    {
        var packages = Path.Combine(_dataDirectory, Defaults.PackagesDirectoryName);
        if (Directory.Exists(packages))
        {
            Directory.Delete(packages, recursive: true);
        }

        var logs = Path.Combine(_dataDirectory, Defaults.LogsDirectoryName);
        if (Directory.Exists(logs))
        {
            foreach (var file in Directory.EnumerateFiles(logs))
            {
                if (!IsAppLog(file))
                {
                    File.Delete(file);
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(logs))
            {
                Directory.Delete(directory, recursive: true);
            }

            if (!Directory.EnumerateFileSystemEntries(logs).Any())
            {
                Directory.Delete(logs);
            }
        }
    }

    /// <summary>Moves every original, and any temporary file beside one, into the conflict directory, in the deletion order.</summary>
    private void MoveOriginalsTo(string conflict)
    {
        Directory.CreateDirectory(conflict);

        foreach (var name in DocumentDeletionOrder)
        {
            if (name is Defaults.InstanceFileName)
            {
                MoveLabDirectoriesTo(conflict);
            }

            foreach (var file in JsonStore.TemporaryPathsFor(Path.Combine(_dataDirectory, name)).Prepend(Path.Combine(_dataDirectory, name)).ToArray())
            {
                if (File.Exists(file))
                {
                    File.Move(file, Path.Combine(conflict, Path.GetFileName(file)), overwrite: true);
                }
            }
        }
    }

    private void MoveLabDirectoriesTo(string conflict)
    {
        var packages = Path.Combine(_dataDirectory, Defaults.PackagesDirectoryName);
        if (Directory.Exists(packages))
        {
            MoveDirectory(packages, Path.Combine(conflict, Defaults.PackagesDirectoryName));
        }

        var logs = Path.Combine(_dataDirectory, Defaults.LogsDirectoryName);
        if (Directory.Exists(logs))
        {
            var movedLogs = Path.Combine(conflict, Defaults.LogsDirectoryName);
            Directory.CreateDirectory(movedLogs);
            foreach (var file in Directory.EnumerateFiles(logs))
            {
                if (!IsAppLog(file))
                {
                    File.Move(file, Path.Combine(movedLogs, Path.GetFileName(file)), overwrite: true);
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(logs))
            {
                MoveDirectory(directory, Path.Combine(movedLogs, Path.GetFileName(directory)));
            }

            if (!Directory.EnumerateFileSystemEntries(logs).Any())
            {
                Directory.Delete(logs);
            }
        }
    }

    /// <summary>
    /// The app-level <c>console-*.log</c> files a pre-M5 console wrote under <c>logs/</c> go to
    /// the data root, where Serilog writes them now, so the retention limit applies to them
    /// too. One that already has a namesake at the root stays where it is.
    /// </summary>
    private void MoveAppLogsToRoot()
    {
        var logs = Path.Combine(_dataDirectory, Defaults.LogsDirectoryName);
        if (!Directory.Exists(logs))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(logs).Where(IsAppLog).ToArray())
        {
            var destination = Path.Combine(_dataDirectory, Path.GetFileName(file));
            if (File.Exists(destination))
            {
                _log.LogInformation("Leaving the old app log {Path} under logs/: the data root already has one of that name", file);
                continue;
            }

            File.Move(file, destination);
        }
    }

    // ------------------------------------------------------------------ the index entry

    private static ProfileRecord DescribeMigrated(string labId, LabStore store, DateTimeOffset now)
    {
        var key = store.HasLabKey ? store.LoadLabKey() : null;
        var instance = store.LoadInstance();
        var lab = JsonStore.LoadIfExists<LabDocument>(store.LabPath, LabDocument.Migrations);

        return new ProfileRecord
        {
            LabId = labId,
            LabName = key?.LabName ?? lab?.LabName ?? string.Empty,
            AuthorityFingerprint = key is null ? string.Empty : ProfileRecord.AuthorityFingerprintOf(key.Authority),
            // A pre-M5 directory always held the lab key: it was the only way to have a lab.
            Access = key is null ? ProfileAccess.Unknown : ProfileAccess.Administrator,
            Authorization = ProfileAuthorization.Authorized,
            InstanceId = instance?.InstanceId ?? string.Empty,
            InstanceName = instance?.InstanceName ?? string.Empty,
            PcCount = lab?.Machines.Count ?? 0,
            AddedAtUnix = now.ToUnixTimeSeconds(),
            LastUsedUnix = now.ToUnixTimeSeconds(),
            Source = ProfileSource.Migrated,
        };
    }

    private static bool IsAppLog(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(Defaults.ConsoleLogFilePrefix, StringComparison.Ordinal)
               && name.EndsWith(Defaults.ConsoleLogFileExtension, StringComparison.Ordinal);
    }

    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
