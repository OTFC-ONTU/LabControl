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
    /// SHA-256 of a document's text, lowercase hex. Used to tell whether the backup on record
    /// still matches <c>lab-key.lck</c> (D-25).
    /// </summary>
    public static string Fingerprint(string json) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));

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

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Serialize(document, migrations));
        RestrictPermissions(temporary, ownerOnly);

        File.Move(temporary, path, overwrite: true);
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
