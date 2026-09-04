using System.Text.Json.Nodes;

namespace LabControl.Shared.Persistence;

/// <summary>One forward-only step, from <paramref name="FromVersion"/> to the next version.</summary>
/// <param name="FromVersion">The version the step upgrades <i>from</i>.</param>
/// <param name="Apply">Rewrites the parsed object; must not depend on anything outside it.</param>
public sealed record SchemaMigration(int FromVersion, Func<JsonObject, JsonObject> Apply);

/// <summary>
/// The forward-only migration chain for one document type (D-20). It is empty today, on
/// purpose: the machinery is built in M1 while there is exactly one version, because it
/// cannot be retrofitted honestly afterwards.
/// </summary>
public sealed class SchemaMigrations
{
    private readonly Dictionary<int, SchemaMigration> _steps;

    public SchemaMigrations(int currentVersion, params SchemaMigration[] steps)
    {
        if (currentVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(currentVersion), currentVersion, "Schema versions start at 1.");
        }

        CurrentVersion = currentVersion;
        _steps = steps.ToDictionary(step => step.FromVersion);
    }

    /// <summary>The version this build writes and can migrate up to.</summary>
    public int CurrentVersion { get; }

    /// <summary>
    /// Brings a parsed document up to <see cref="CurrentVersion"/>, or refuses it.
    /// </summary>
    /// <exception cref="SchemaVersionException">The file is newer than this build.</exception>
    /// <exception cref="InvalidDataException">The file has no usable <c>schema_version</c>.</exception>
    public JsonObject Upgrade(string documentName, JsonObject root)
    {
        var version = ReadVersion(documentName, root);

        if (version > CurrentVersion)
        {
            throw new SchemaVersionException(documentName, version, CurrentVersion);
        }

        while (version < CurrentVersion)
        {
            if (!_steps.TryGetValue(version, out var step))
            {
                // A gap here is a bug in this build, not bad input: some release bumped the
                // version without writing the step that reads what the previous one wrote.
                throw new InvalidOperationException(
                    $"No migration from schema version {version} to {version + 1} for '{documentName}'.");
            }

            root = step.Apply(root);
            version++;
            root[Defaults.SchemaVersionFieldName] = version;
        }

        return root;
    }

    private static int ReadVersion(string documentName, JsonObject root)
    {
        if (!root.TryGetPropertyValue(Defaults.SchemaVersionFieldName, out var node) || node is null)
        {
            throw new InvalidDataException(
                $"'{documentName}' has no '{Defaults.SchemaVersionFieldName}' — it was not written by LabControl.");
        }

        if (node is not JsonValue value || !value.TryGetValue<int>(out var version))
        {
            throw new InvalidDataException(
                $"'{documentName}' has a '{Defaults.SchemaVersionFieldName}' that is not a whole number.");
        }

        if (version < 1)
        {
            throw new InvalidDataException(
                $"'{documentName}' has {Defaults.SchemaVersionFieldName} {version}; versions start at 1.");
        }

        return version;
    }
}
