using System.IO.Compression;
using LabControl.Shared.Jobs;

namespace LabControl.Shared.Packages;

/// <summary>
/// A package that comes as a <c>.zip</c> — a driver or a program whose <c>setup.exe</c> needs the
/// files beside it (D-89 addendum). The archive is the catalog's installer, pushed and hash-checked
/// whole; the PC unpacks it into its private staging directory and runs the entry named by
/// <c>run</c>, from its own folder. Every name is checked before anything is written: an entry
/// that would land outside the folder, a link, or an archive larger than the limits is refused.
/// </summary>
public static class PackageArchive
{
    public static ReadOnlySpan<byte> Signature => "PK\u0003\u0004"u8;

    public static bool IsArchive(string fileName) => fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A path inside the archive the PC may run: forward slashes, each part a Windows leaf name,
    /// no <c>..</c>, ending in <c>.exe</c> or <c>.msi</c>.
    /// </summary>
    public static bool IsValidRun(string run)
    {
        if (string.IsNullOrWhiteSpace(run) || run.Length > 260 || run.Contains('\\') || run.StartsWith('/')) return false;
        var parts = run.Split('/');
        return parts.Length <= 8 && parts.All(p => p is not ("." or "..") && SendFileRequest.IsValidName(p))
            && (run.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || run.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every installer inside the archive, the likeliest first: nearest the top, <c>setup.exe</c> / <c>install.exe</c> before others, an MSI before an arbitrary executable.</summary>
    public static IReadOnlyList<string> Candidates(Stream archive)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        return zip.Entries
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(name => !name.StartsWith("__MACOSX/", StringComparison.Ordinal) && IsValidRun(name))
            .OrderBy(name => name.Count(c => c == '/'))
            .ThenBy(name => Path.GetFileName(name).ToLowerInvariant() switch
            {
                "setup.exe" => 0,
                "install.exe" => 1,
                _ when name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) => 2,
                _ => 3,
            })
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Copies one entry out, for the wizard to inspect; <c>false</c> when the archive has no such entry.</summary>
    public static async Task<bool> ExtractEntryAsync(string archivePath, string entryName, string destination, CancellationToken token)
    {
        await using var file = File.OpenRead(archivePath);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/') == entryName);
        if (entry is null || entry.Length > Defaults.PackageArchiveMaxBytes) return false;
        await using var source = entry.Open();
        await using var target = File.Create(destination);
        await CopyBoundedAsync(source, target, entry.Length, token);
        return true;
    }

    /// <summary>
    /// Unpacks <paramref name="archivePath"/> into <paramref name="destination"/> (created, expected
    /// empty), at most <see cref="Defaults.PackageArchiveMaxEntries"/> entries and
    /// <see cref="Defaults.PackageArchiveMaxBytes"/> in all. Throws <see cref="InvalidDataException"/>
    /// for an archive that breaks a rule and leaves the caller to remove what was written.
    /// </summary>
    public static async Task ExtractAsync(string archivePath, string destination, CancellationToken token)
    {
        var root = Path.GetFullPath(destination);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        await using var file = File.OpenRead(archivePath);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > Defaults.PackageArchiveMaxEntries)
        {
            throw new InvalidDataException($"more than {Defaults.PackageArchiveMaxEntries} entries");
        }

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (name.StartsWith('/') || parts.Any(p => p is "." or ".." || !SendFileRequest.IsValidName(p)))
            {
                throw new InvalidDataException($"unsafe name in the archive: {name}");
            }

            // Unix symbolic links keep their mode in the high word of the external attributes.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new InvalidDataException($"a link in the archive: {name}");
            }

            var target = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"unsafe name in the archive: {name}");
            }

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            total += entry.Length;
            if (total > Defaults.PackageArchiveMaxBytes)
            {
                throw new InvalidDataException("the archive unpacks to more than the limit");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, Defaults.FileChunkBytes, FileOptions.Asynchronous);
            await CopyBoundedAsync(source, output, entry.Length, token);
        }
    }

    /// <summary>Copies at most <paramref name="declared"/> bytes: an entry that inflates past its stated size is refused.</summary>
    private static async Task CopyBoundedAsync(Stream source, Stream target, long declared, CancellationToken token)
    {
        var buffer = new byte[1 << 16];
        long copied = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, token)) > 0)
        {
            copied += n;
            if (copied > declared)
            {
                throw new InvalidDataException("an entry is larger than the archive says");
            }

            await target.WriteAsync(buffer.AsMemory(0, n), token);
        }
    }
}
