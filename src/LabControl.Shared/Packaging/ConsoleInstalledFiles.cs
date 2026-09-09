using System.IO.Compression;
using System.Security.Cryptography;

namespace LabControl.Shared.Packaging;

/// <summary>One line of <c>installed-files.txt</c> that was refused, and why (D-59 item 1).</summary>
public sealed record ConsoleManifestRefusal(string Line, string Reason)
{
    public override string ToString() => "\"" + Line + "\" (" + Reason + ")";
}

/// <summary>
/// What the installer believes it owns inside its install directory. <see cref="FromFallback"/>
/// says the manifest was missing or unusable and the two known program files were assumed
/// instead, so uninstall still removes the executables rather than reporting success over a
/// directory it never touched.
/// </summary>
public sealed record ConsoleInstalledManifest(
    IReadOnlyList<string> Owned,
    IReadOnlyList<ConsoleManifestRefusal> Refused,
    bool FromFallback);

/// <summary>The outcome of removing what the installer owns.</summary>
public sealed record ConsoleFileRemoval(
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Kept,
    IReadOnlyList<ConsoleManifestRefusal> Refused,
    bool FromFallback,
    bool DirectoryRemoved);

/// <summary>
/// The files the per-user console installer owns inside one install directory (D-59 item 1),
/// and the only code allowed to turn a line of <c>installed-files.txt</c> into a path.
///
/// The manifest lives in a directory the signed-in user can write, so it is never trusted:
/// a line that is rooted, that names a drive, that climbs out with <c>..</c> or that lands
/// anywhere but inside this directory is refused and reported, never deleted. The same guard
/// resolves every entry of the embedded payload, so a zip cannot write outside either.
///
/// The install root is a parameter rather than the machine's own, so all of this is exercised
/// against a temporary directory on any operating system.
/// </summary>
public sealed class ConsoleInstalledFiles
{
    private readonly string _root;
    private readonly string _prefix;

    public ConsoleInstalledFiles(string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        _prefix = _root + Path.DirectorySeparatorChar;
    }

    /// <summary>The install directory, as a full path without a trailing separator.</summary>
    public string Directory => _root;

    public string ManifestPath => Path.Combine(_root, Defaults.ConsoleInstallManifestFileName);

    /// <summary>
    /// What uninstall removes when the manifest is missing or holds nothing usable (S1):
    /// the two program files this installer always writes. Without this an interrupted or
    /// hand-edited installation would report a clean removal while both executables — and a
    /// Start-menu shortcut pointing at them — stayed behind with no Installed-apps entry left
    /// to retry from.
    /// </summary>
    public static IReadOnlyList<string> FallbackFiles { get; } =
        [Defaults.ConsoleExecutableName, Defaults.ConsoleSetupExecutableName];

    /// <summary>
    /// Why this relative path may not be used, or <c>null</c> when it is a plain name inside
    /// the install directory. Windows-shaped paths are refused on every operating system, so
    /// the rule is the same one whether it runs on the teacher's PC or in a test on the Mac.
    /// </summary>
    public static string? RefuseReason(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return "the line is empty";
        }

        if (relative.Contains('\0', StringComparison.Ordinal))
        {
            return "it contains a null character";
        }

        if (relative[0] is '/' or '\\' || Path.IsPathRooted(relative))
        {
            return "it names an absolute path";
        }

        if (relative.Length >= 2 && relative[1] == ':' && char.IsAsciiLetter(relative[0]))
        {
            return "it names a drive";
        }

        foreach (var segment in relative.Split(['/', '\\'], StringSplitOptions.None))
        {
            if (segment is "..")
            {
                return "it climbs out of the install directory";
            }
        }

        return null;
    }

    /// <summary>
    /// Turns one manifest or payload entry into a full path inside this directory, or refuses
    /// it. Both the shape of the line and the resolved path are checked, so nothing outside
    /// the install directory can ever be opened, cleared of its read-only flag or deleted.
    /// </summary>
    public bool TryResolve(string? relative, out string fullPath, out string? refusal)
    {
        fullPath = "";
        refusal = RefuseReason(relative);
        if (refusal is not null)
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(_root, relative!));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            refusal = "it is not a usable path";
            return false;
        }

        if (!candidate.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            refusal = "it lands outside the install directory";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>The same guard, for callers that treat an escape as a broken package rather than a bad line.</summary>
    public string Resolve(string relative, string what)
    {
        if (TryResolve(relative, out var full, out var refusal))
        {
            return full;
        }

        throw new IOException(what + " contains a path outside the install directory: " + relative + " — " + refusal);
    }

    /// <summary>
    /// Reads <c>installed-files.txt</c>. Hostile or unusable lines are reported rather than
    /// obeyed; a missing, unreadable or entirely refused manifest falls back to
    /// <see cref="FallbackFiles"/>.
    /// </summary>
    public ConsoleInstalledManifest ReadManifest()
    {
        string[] lines;
        try
        {
            lines = File.Exists(ManifestPath) ? File.ReadAllLines(ManifestPath) : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new ConsoleInstalledManifest(FallbackFiles, [], true);
        }

        var owned = new List<string>();
        var refused = new List<ConsoleManifestRefusal>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!TryResolve(line, out _, out var reason))
            {
                refused.Add(new ConsoleManifestRefusal(line, reason!));
                continue;
            }

            owned.Add(line);
        }

        return owned.Count > 0
            ? new ConsoleInstalledManifest(owned, refused, false)
            : new ConsoleInstalledManifest(FallbackFiles, refused, true);
    }

    /// <summary>Writes the manifest: one relative path per line, sorted, nothing else.</summary>
    public void WriteManifest(IEnumerable<string> owned)
    {
        System.IO.Directory.CreateDirectory(_root);
        File.WriteAllLines(ManifestPath, owned
            .Where(name => RefuseReason(name) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Writes every entry of <paramref name="archive"/> into the install directory, skipping
    /// the ones already there byte for byte, and returns how many files it replaced. A freshly
    /// written file can still be held open by an antivirus scan (D-33 item 9), so each write
    /// is retried.
    /// </summary>
    public int ExtractArchive(ZipArchive archive)
    {
        var written = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Resolve(relative, "The embedded console payload");

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (SameContent(target, bytes))
            {
                continue;
            }

            WriteWithRetry(target, bytes);
            written++;
        }

        return written;
    }

    /// <summary>The relative names of every entry of a payload, in the order they are written.</summary>
    public static IReadOnlyList<string> EntriesOf(ZipArchive archive) =>
    [
        .. archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => entry.FullName.Replace('/', Path.DirectorySeparatorChar))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// Deletes exactly the named files — an upgrade's leftovers — and prunes the directories
    /// they emptied. The manifest and the install directory itself are left alone.
    /// </summary>
    public ConsoleFileRemoval Prune(IEnumerable<string> relatives, string? keepFullPath = null)
    {
        var removal = Delete(relatives, keepFullPath, false);
        PruneEmptyDirectories(_root, false);
        return removal;
    }

    /// <summary>
    /// Removes everything the manifest lists (or, when there is no usable manifest, the two
    /// known program files), then the manifest, then the install directory once it is empty.
    /// A file the caller must keep — the running executable — is reported in
    /// <see cref="ConsoleFileRemoval.Kept"/> and the manifest survives for the temporary copy
    /// that finishes the job. Nothing that is not owned is ever deleted, and no directory is
    /// ever removed recursively.
    /// </summary>
    public ConsoleFileRemoval RemoveAll(string? keepFullPath = null)
    {
        if (!System.IO.Directory.Exists(_root))
        {
            return new ConsoleFileRemoval([], [], [], false, true);
        }

        var manifest = ReadManifest();
        var removal = Delete(manifest.Owned, keepFullPath, manifest.FromFallback);
        var refused = manifest.Refused.Concat(removal.Refused).ToArray();

        if (removal.Kept.Count == 0)
        {
            DeleteFile(ManifestPath);
        }

        PruneEmptyDirectories(_root, true);
        return removal with
        {
            Refused = refused,
            FromFallback = manifest.FromFallback,
            DirectoryRemoved = !System.IO.Directory.Exists(_root),
        };
    }

    /// <summary>
    /// Whether two paths name the same directory. The temporary copy that finishes an
    /// uninstall compares its argument with the layout's own install directory before it
    /// deletes anything (D-59 item 1): a path it was not built to remove is refused.
    /// </summary>
    public static bool IsTheSameDirectory(string? one, string? other)
    {
        if (string.IsNullOrWhiteSpace(one) || string.IsNullOrWhiteSpace(other))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(one)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private ConsoleFileRemoval Delete(IEnumerable<string> relatives, string? keepFullPath, bool fromFallback)
    {
        var keep = keepFullPath is null ? null : SafeFullPath(keepFullPath);
        var removed = new List<string>();
        var kept = new List<string>();
        var refused = new List<ConsoleManifestRefusal>();

        foreach (var relative in relatives)
        {
            if (!TryResolve(relative, out var target, out var reason))
            {
                refused.Add(new ConsoleManifestRefusal(relative ?? "", reason!));
                continue;
            }

            if (keep is not null && string.Equals(target, keep, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(relative);
                continue;
            }

            if (DeleteFile(target))
            {
                removed.Add(relative);
            }
        }

        return new ConsoleFileRemoval(removed, kept, refused, fromFallback, false);
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Deletes one file this installer owns; a read-only flag is cleared first, a missing file is success.</summary>
    private static bool DeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The delete below reports the real problem.
        }

        File.Delete(path);
        return true;
    }

    private void PruneEmptyDirectories(string directory, bool includeRoot)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return;
        }

        foreach (var child in System.IO.Directory.GetDirectories(directory))
        {
            PruneEmptyDirectories(child, true);
        }

        if ((includeRoot || !string.Equals(directory, _root, StringComparison.OrdinalIgnoreCase))
            && System.IO.Directory.GetFileSystemEntries(directory).Length == 0)
        {
            try
            {
                System.IO.Directory.Delete(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Something appeared in it between the check and the delete; leave it.
            }
        }
    }

    private static bool SameContent(string path, byte[] bytes)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var existing = File.OpenRead(path);
            if (existing.Length != bytes.Length)
            {
                return false;
            }

            return SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(bytes));
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void WriteWithRetry(string target, byte[] bytes)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
            {
                // Nothing to clear; the write below reports the real problem.
            }

            try
            {
                File.WriteAllBytes(target, bytes);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * attempt));
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }
    }
}

/// <summary>
/// The copies of itself the uninstaller leaves in the temp directory (D-59 item 1). One is
/// made on every uninstall, because a running executable cannot delete itself; each one is
/// ~60 MB, each one is a working installer, and none of them may be left lying about to be
/// double-clicked months later. The copy removes itself when it is done, and every run of the
/// installer sweeps the ones an interrupted uninstall left behind.
/// </summary>
public static class ConsoleTempCopies
{
    public const string Prefix = "LabControl.ConsoleSetup.";
    public const string Suffix = ".exe";

    /// <summary>How long a copy may sit in the temp directory before a later run sweeps it.</summary>
    public static TimeSpan Stale => TimeSpan.FromHours(1);

    public static string SearchPattern => Prefix + "*" + Suffix;

    public static string NameFor(Guid id) => Prefix + id.ToString("N") + Suffix;

    /// <summary>
    /// Only this installer's own temporary copies: the prefix, 32 hex digits and the suffix.
    /// The installed <c>LabControl.ConsoleSetup.exe</c> itself never matches.
    /// </summary>
    public static bool IsTemporaryCopy(string? fileName)
    {
        // The length first, and exactly: "LabControl.ConsoleSetup.exe" — the installed
        // uninstaller — both starts with the prefix and ends with the suffix, and they overlap,
        // so measuring the middle before knowing there is one asks for a negative length.
        if (fileName is null
            || fileName.Length != Prefix.Length + 32 + Suffix.Length
            || !fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var character in fileName.AsSpan(Prefix.Length, 32))
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deletes every temporary copy in <paramref name="directory"/> that is older than
    /// <paramref name="olderThan"/> and is not <paramref name="keepFullPath"/> — the copy that
    /// is running right now. Returns what it removed; a copy that is still held open by a
    /// running uninstall is simply left for the next run.
    /// </summary>
    public static IReadOnlyList<string> Sweep(string directory, string? keepFullPath, TimeSpan olderThan, DateTime nowUtc)
    {
        var removed = new List<string>();
        if (!Directory.Exists(directory))
        {
            return removed;
        }

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory, SearchPattern);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return removed;
        }

        var keep = keepFullPath is null ? null : TryFullPath(keepFullPath);
        foreach (var candidate in candidates)
        {
            if (!IsTemporaryCopy(Path.GetFileName(candidate))
                || (keep is not null && string.Equals(TryFullPath(candidate), keep, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                if (nowUtc - File.GetLastWriteTimeUtc(candidate) < olderThan)
                {
                    continue;
                }

                File.Delete(candidate);
                removed.Add(Path.GetFileName(candidate));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Still running, or not ours to delete. The next run tries again.
            }
        }

        return removed;
    }

    private static string? TryFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
