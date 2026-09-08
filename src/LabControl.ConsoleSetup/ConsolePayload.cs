using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace LabControl.ConsoleSetup;

/// <summary>
/// The published <c>win-x64</c> console, embedded as one zip by <c>tools/package-windows.sh</c>
/// (D-59 item 1). The project compiles without it — so an ordinary <c>dotnet build</c> on any
/// machine still builds this executable — and an executable built that way refuses to install
/// and says so, instead of writing an empty program directory.
/// </summary>
internal sealed class ConsolePayload
{
    private const string ResourceName = "LabControl.ConsoleSetup.Payload.zip";

    private ConsolePayload(IReadOnlyList<string> entries) => Entries = entries;

    /// <summary>The relative paths inside the install directory, in the order they are written.</summary>
    public IReadOnlyList<string> Entries { get; }

    public static bool IsPresent => typeof(ConsolePayload).Assembly.GetManifestResourceInfo(ResourceName) is not null;

    public static ConsolePayload Open()
    {
        using var archive = OpenArchive();
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => entry.FullName.Replace('/', Path.DirectorySeparatorChar))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ConsolePayload(entries);
    }

    /// <summary>
    /// Writes every entry into <paramref name="directory"/>, skipping the ones already there
    /// byte for byte, and returns how many files it actually replaced. A freshly written file
    /// can still be held open by an antivirus scan (D-33 item 9), so each write is retried.
    /// </summary>
    public int ExtractTo(string directory)
    {
        using var archive = OpenArchive();
        var written = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(directory, relative));
            if (!target.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new IOException("The embedded console payload contains a path outside the install directory: " + entry.FullName);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
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

    private static ZipArchive OpenArchive()
    {
        var stream = typeof(ConsolePayload).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                "This installer was built without the console program files. Build it with tools/package-windows.sh, "
                + "which publishes the console and embeds it, and run that executable instead.");
        return new ZipArchive(stream, ZipArchiveMode.Read);
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
