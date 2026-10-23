using System.IO.Compression;

namespace LabControl.Shared.Files;

/// <summary>What one collected folder held, as the PC zipped it.</summary>
/// <param name="Files">Files in the archive.</param>
/// <param name="Bytes">Their uncompressed size.</param>
/// <param name="InUse">Files that could not be read (open in another program, or refused), at most a handful named.</param>
/// <param name="Links">Symbolic links and junctions left out: a link is never followed.</param>
/// <param name="Skipped">Entries whose name or depth the console could not store, at most a handful named.</param>
public sealed record CollectSummary(int Files, long Bytes, IReadOnlyList<string> InUse, int Links, IReadOnlyList<string> Skipped);

/// <summary>The folder holds more than a collection may (D-77).</summary>
public sealed class CollectTooLargeException(string message) : IOException(message);

/// <summary>
/// Zips a collected folder on the PC and unpacks it on the console (M6 portion 5, D-77). The
/// PC side never follows a symbolic link or junction — the agent reads as SYSTEM-opened output
/// over the student's files, and a link to somewhere else is not the student's work — and
/// refuses a folder past the caps instead of sending part of it. The console side trusts
/// nothing in the archive: names are checked part by part, nothing lands outside the PC's
/// folder, and sizes are counted as they are written, not read from the headers.
/// </summary>
public static class WorkArchive
{
    private const int NamedInUse = 10;

    /// <summary>How many folders deep a collected file may be.</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// A name the console can store on macOS, Windows and Linux: no control characters, none of
    /// <c>&lt;&gt;:"/\|?*</c>, not <c>.</c> or <c>..</c>, no trailing dot or space. Reserved device
    /// names such as <c>Aux.java</c> are allowed — Windows 11 accepts them and a Java class may be named so.
    /// </summary>
    public static bool IsPortableName(string name) =>
        name.Length is > 0 and <= 255 && name is not ("." or "..") && !name.EndsWith('.') && !name.EndsWith(' ')
        && !name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c));

    /// <summary>Zips <paramref name="folder"/> into <paramref name="output"/> (left open).</summary>
    public static CollectSummary Create(string folder, Stream output, long maxBytes, int maxFiles, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(output);
        var root = new DirectoryInfo(folder);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The folder to collect is a link; nothing was collected.");
        }

        var inUse = new List<string>();
        var inUseCount = 0;
        var skipped = new List<string>();
        var skippedCount = 0;
        var links = 0;
        var files = 0;
        long bytes = 0;
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var pending = new Stack<(DirectoryInfo Directory, string Prefix)>();
            pending.Push((root, string.Empty));
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var (directory, prefix) = pending.Pop();
                var empty = true;
                foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
                {
                    empty = false;
                    var name = prefix + entry.Name;
                    if (!IsPortableName(entry.Name) || name.Count(c => c == '/') >= MaxDepth)
                    {
                        // Left out and named, the same rule the console unpacks by: one odd name never costs the rest.
                        if (skippedCount++ < NamedInUse)
                        {
                            skipped.Add(name);
                        }

                        continue;
                    }

                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        links++;
                        continue;
                    }

                    if (entry is DirectoryInfo child)
                    {
                        pending.Push((child, name + "/"));
                        continue;
                    }

                    if (++files > maxFiles)
                    {
                        throw new CollectTooLargeException($"The folder holds more than {maxFiles} files; nothing was collected.");
                    }

                    FileStream source;
                    try
                    {
                        source = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        files--;
                        if (inUseCount++ < NamedInUse)
                        {
                            inUse.Add(name);
                        }

                        continue;
                    }

                    using (source)
                    {
                        if (bytes + source.Length > maxBytes)
                        {
                            throw new CollectTooLargeException($"The folder holds more than {maxBytes / (1024 * 1024)} MB; nothing was collected.");
                        }

                        var zipEntry = archive.CreateEntry(name, CompressionLevel.Fastest);
                        zipEntry.LastWriteTime = entry.LastWriteTime;
                        using var target = zipEntry.Open();
                        var copied = CopyBounded(source, target, maxBytes - bytes, token);
                        bytes += copied;
                    }
                }

                if (empty && prefix.Length > 0)
                {
                    archive.CreateEntry(prefix);
                }
            }
        }

        if (inUseCount > inUse.Count)
        {
            inUse.Add($"… {inUseCount - inUse.Count} more");
        }

        if (skippedCount > skipped.Count)
        {
            skipped.Add($"… {skippedCount - skipped.Count} more");
        }

        return new CollectSummary(files, bytes, inUse, links, skipped);
    }

    /// <summary>
    /// Unpacks <paramref name="archive"/> into <paramref name="destination"/>, which must not
    /// exist yet: everything goes to a sibling <c>.partial</c> directory first and is renamed
    /// into place only when all of it was written, so a half-extracted PC never looks done.
    /// Returns the files written.
    /// </summary>
    public static int Extract(Stream archive, string destination, long maxBytes, int maxFiles, CancellationToken token)
    {
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException("The destination of collected work already exists.");
        }

        var partial = destination + ".partial";
        if (Directory.Exists(partial))
        {
            Directory.Delete(partial, recursive: true);
        }

        Directory.CreateDirectory(partial);
        var root = partial + Path.DirectorySeparatorChar;
        var files = 0;
        long bytes = 0;
        try
        {
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (zip.Entries.Count > maxFiles * 2 + 16)
                {
                    throw new InvalidDataException("The collected archive holds too many entries.");
                }

                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var parts = SafeParts(entry.FullName);
                    var target = Path.GetFullPath(Path.Combine([partial, .. parts]));
                    if (!target.StartsWith(root, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("A collected file named a place outside its folder.");
                    }

                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    if (++files > maxFiles)
                    {
                        throw new InvalidDataException("The collected archive holds too many files.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var source = entry.Open();
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var copied = CopyBounded(source, output, maxBytes - bytes, token);
                    bytes += copied;
                }
            }

            Directory.Move(partial, destination);
            return files;
        }
        catch
        {
            try
            {
                Directory.Delete(partial, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    /// <summary>An archive name's parts, each a plain Windows file name; anything else is refused, never repaired.</summary>
    public static string[] SafeParts(string fullName)
    {
        fullName ??= string.Empty;
        var parts = fullName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (fullName.StartsWith('/') || fullName.StartsWith('\\') || parts.Length == 0 || parts.Length > MaxDepth + 1 || parts.Any(p => !IsPortableName(p)))
        {
            throw new InvalidDataException("A collected file has a name that is not a plain file name.");
        }

        return parts;
    }

    private static long CopyBounded(Stream source, Stream target, long allowed, CancellationToken token)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            copied += read;
            if (copied > allowed)
            {
                throw new CollectTooLargeException("The collected work is larger than a collection may be.");
            }

            target.Write(buffer, 0, read);
        }

        return copied;
    }
}
