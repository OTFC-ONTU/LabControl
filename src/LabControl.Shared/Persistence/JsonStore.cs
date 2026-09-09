using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LabControl.Shared.Persistence;

/// <summary>
/// Reads and writes every LabControl document: snake_case JSON, <c>schema_version</c>
/// first, migrated forward on load (D-20), written atomically so a crash mid-save can
/// never leave a truncated <c>lab.json</c>.
/// </summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Serializes a document, stamping it with the schema version this build writes.</summary>
    public static string Serialize<T>(T document, SchemaMigrations migrations)
        where T : class, ISchemaVersioned
    {
        document.SchemaVersion = migrations.CurrentVersion;
        return JsonSerializer.Serialize(document, Options);
    }

    /// <summary>
    /// The suffix of the file <see cref="Save{T}"/> writes before the atomic replace. One left
    /// behind is a save that never finished; the document beside it is the last good one.
    /// </summary>
    public const string TemporarySuffix = ".tmp";

    /// <summary>
    /// The temporary <see cref="Save{T}"/> writes when nothing occupies it. A save that finds it
    /// taken — by a leftover, or by another thread saving the same document at this very moment —
    /// picks a unique name of its own instead, so two savers can never write one file between them.
    /// </summary>
    public static string TemporaryPathFor(string path) => path + TemporarySuffix;

    /// <summary>Every temporary a save of this document may have left behind, the fixed old name included.</summary>
    public static IEnumerable<string> TemporaryPathsFor(string path)
    {
        yield return TemporaryPathFor(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*" + TemporarySuffix))
        {
            yield return file;
        }
    }

    /// <summary>
    /// SHA-256 of a document's text, lowercase hex. Used to tell whether the backup on record
    /// still matches <c>lab-key.lck</c> (D-25).
    /// </summary>
    public static string Fingerprint(string json) => Fingerprint(System.Text.Encoding.UTF8.GetBytes(json));

    /// <summary>SHA-256 of a file's exact bytes, lowercase hex: what "the same document" means on disk.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary><see cref="Fingerprint(ReadOnlySpan{byte})"/> of a file on disk.</summary>
    public static string FingerprintFile(string path) => Fingerprint(File.ReadAllBytes(path));

    /// <summary>Parses a document from text, migrating it forward or refusing it.</summary>
    /// <exception cref="SchemaVersionException">Written by a newer build.</exception>
    /// <exception cref="InvalidDataException">Not a LabControl document.</exception>
    public static T Parse<T>(string json, string documentName, SchemaMigrations migrations)
        where T : class, ISchemaVersioned
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                   ?? throw new InvalidDataException($"'{documentName}' is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{documentName}' is not valid JSON: {ex.Message}", ex);
        }

        root = migrations.Upgrade(documentName, root);

        return root.Deserialize<T>(Options)
               ?? throw new InvalidDataException($"'{documentName}' deserialized to nothing.");
    }

    /// <summary>Loads a document from disk.</summary>
    public static T Load<T>(string path, SchemaMigrations migrations)
        where T : class, ISchemaVersioned =>
        Parse<T>(File.ReadAllText(path), Path.GetFileName(path), migrations);

    /// <summary>
    /// Loads a document, or returns <c>null</c> if it does not exist yet. A file that
    /// exists but cannot be read still throws — an unreadable lab is not an empty lab.
    /// </summary>
    public static T? LoadIfExists<T>(string path, SchemaMigrations migrations)
        where T : class, ISchemaVersioned =>
        File.Exists(path) ? Load<T>(path, migrations) : null;

    /// <summary>
    /// Writes a document atomically: a temporary file in the same directory, then a
    /// replace. A power cut leaves either the old file or the new one, never half of one.
    /// </summary>
    /// <param name="ownerOnly">
    /// Restrict the file to its owner (0600). Used for anything holding key material.
    /// </param>
    public static void Save<T>(string path, T document, SchemaMigrations migrations, bool ownerOnly = false)
        where T : class, ISchemaVersioned
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Every saver claims its own temporary. Several threads may save the same document at
        // the same moment — thirty PCs finishing one batch, the job book following each of them
        // — and a shared temporary name means two of them interleave their bytes and move a
        // torn file into place. The usual name is taken first, so a crash still leaves the
        // familiar "<document>.tmp" beside the document; whoever finds it taken names its own.
        var text = Serialize(document, migrations);
        var temporary = TemporaryPathFor(path);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(text);
                }

                break;
            }
            catch (IOException) when (attempt < 16 && !Directory.Exists(temporary))
            {
                // Another saver holds this name. The retry cannot be conditioned on the file
                // still being there: the holder may already have moved its temporary onto the
                // document, and the saver that lost the race would then rethrow a collision
                // that has resolved itself. Take a name nobody can be holding instead. A
                // directory sitting on the name is not a race but broken storage, and is
                // reported rather than worked around.
                temporary = $"{path}.{Guid.NewGuid():n}{TemporarySuffix}";
            }
        }

        try
        {
            RestrictPermissions(temporary, ownerOnly);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 0600 on macOS and Linux. Windows inherits the ACL of the per-user data directory,
    /// which is already private, and has no equivalent one-call form.
    /// </summary>
    private static void RestrictPermissions(string path, bool ownerOnly)
    {
        if (!ownerOnly || OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
