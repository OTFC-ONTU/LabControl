using System.Security.Cryptography;
using LabControl.Shared.Jobs;

namespace LabControl.Shared.Files;

/// <summary>Atomic handout placement. Windows callers must impersonate the managed
/// student: reparse checks alone cannot isolate a user-writable directory from races.</summary>
public static class HandoutDelivery
{
    public static long Deliver(Stream source, string materialsDirectory, SendFileRequest request, CancellationToken token)
    {
        if (!SendFileRequest.IsValidName(request.Name)) throw new InvalidDataException("Invalid handout name.");
        RejectReparseAncestors(materialsDirectory);
        Directory.CreateDirectory(materialsDirectory);
        RejectReparseAncestors(materialsDirectory);
        var temporary = Path.Combine(materialsDirectory, Guid.NewGuid().ToString("N") + ".partial");
        var target = Path.Combine(materialsDirectory, request.Name);
        try
        {
            long count = 0;
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[Defaults.FileChunkBytes];
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                    destination.Write(buffer, 0, read);
                    count += read;
                }
                if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), request.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The staged handout hash changed; the existing file was preserved.");
                destination.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            RejectReparseAncestors(materialsDirectory);
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The handout destination is a reparse point.");
            File.Move(temporary, target, overwrite: true);
            return count;
        }
        finally
        {
            // This cleanup also runs under the user's token on Windows.
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void RejectReparseAncestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            try
            {
                if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Handout directories cannot contain reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
