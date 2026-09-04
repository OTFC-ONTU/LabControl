namespace LabControl.Shared.Persistence;

/// <summary>
/// A file was written by a build that understands a newer schema than this one. D-20
/// requires a refusal that <b>names the version</b> rather than a tolerant partial read:
/// silently dropping fields from <c>lab.json</c> loses machines, and from a backup loses
/// the lab.
/// </summary>
public sealed class SchemaVersionException : Exception
{
    public SchemaVersionException(string documentName, int fileVersion, int supportedVersion)
        : base($"'{documentName}' was written with schema version {fileVersion}; " +
               $"this build understands version {supportedVersion}. " +
               "Install a LabControl build that understands the newer version — the file is not modified.")
    {
        DocumentName = documentName;
        FileVersion = fileVersion;
        SupportedVersion = supportedVersion;
    }

    public string DocumentName { get; }

    /// <summary>The version found in the file.</summary>
    public int FileVersion { get; }

    /// <summary>The highest version this build can read.</summary>
    public int SupportedVersion { get; }
}
