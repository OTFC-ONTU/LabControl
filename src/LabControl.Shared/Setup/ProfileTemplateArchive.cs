using System.IO.Compression;
using LabControl.Shared.Jobs;

namespace LabControl.Shared.Setup;

public sealed record ProfileTemplateFile(string RelativePath, byte[] Content);

/// <summary>A classroom document seed, never an import of registry hives, credentials,
/// shell extensions, executables or a complete personal profile.</summary>
public static class ProfileTemplateArchive
{
    public static IReadOnlyList<ProfileTemplateFile> Read(Stream source)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > Defaults.ProfileTemplateMaxEntries)
            throw new InvalidDataException("The profile template contains too many entries.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ProfileTemplateFile>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var type = (entry.ExternalAttributes >> 16) & 0xf000;
            if (type is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Profile template links and special files are unsupported.");
            var isDirectory = entry.FullName.EndsWith('/');
            var path = NormalizePath(entry.FullName.TrimEnd('/'), isDirectory);
            if (!seen.Add(path)) throw new InvalidDataException("Profile template paths must be unique, including case.");
            if (isDirectory)
            {
                if (entry.Length != 0) throw new InvalidDataException("A template directory contains file data.");
                continue;
            }
            if (entry.Length > Defaults.ProfileTemplateMaxFileBytes || entry.Length < 0
                || total + entry.Length > Defaults.ProfileTemplateMaxTotalBytes)
                throw new InvalidDataException("The profile template exceeds its file or total size limit.");
            using var content = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[Defaults.FileChunkBytes];
            int read;
            while ((read = content.Read(buffer)) > 0)
            {
                if (output.Length + read > entry.Length) throw new InvalidDataException("A template entry exceeds its declared size.");
                output.Write(buffer, 0, read);
            }
            if (output.Length != entry.Length) throw new InvalidDataException("A template entry is incomplete.");
            total += output.Length;
            files.Add(new(path, output.ToArray()));
        }
        var fileNames = files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var parent = file.RelativePath;
            while ((parent.LastIndexOf('/')) is var slash && slash >= 0)
            {
                parent = parent[..slash];
                if (fileNames.Contains(parent)) throw new InvalidDataException("A template file cannot also be a directory.");
            }
        }
        if (files.Count == 0) throw new InvalidDataException("The profile template contains no supported documents.");
        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    public static string NormalizePath(string path, bool directory = false)
    {
        if (string.IsNullOrEmpty(path) || path.Length > Defaults.ProfileTemplateMaxPathCharacters || path.Contains('\\'))
            throw new InvalidDataException("Template paths must be relative Desktop/ or Documents/ paths.");
        var parts = path.Split('/');
        if (parts.Any(part => !SendFileRequest.IsValidName(part)))
            throw new InvalidDataException("A profile template path is not a safe Windows path.");
        if (parts[0].Equals(Defaults.ProfileTemplateDesktopDirectoryName, StringComparison.OrdinalIgnoreCase))
            parts[0] = Defaults.ProfileTemplateDesktopDirectoryName;
        else if (parts[0].Equals(Defaults.ProfileTemplateDocumentsDirectoryName, StringComparison.OrdinalIgnoreCase))
            parts[0] = Defaults.ProfileTemplateDocumentsDirectoryName;
        else throw new InvalidDataException("Only Desktop/ and Documents/ are supported in profile templates.");
        if (!directory && (parts.Length < 2 || !new SendFileRequest("", "", parts[^1], true).MayOpen))
            throw new InvalidDataException("Profile templates support document and image files only; no hives, programs or shortcuts.");
        return string.Join('/', parts);
    }
}
