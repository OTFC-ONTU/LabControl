namespace LabControl.Shared.Persistence;

/// <summary>
/// One named whitelist preset (M6, D-73): the host patterns a teacher picks with one click
/// when the internet switch allows only a list. Every entry in <see cref="Hosts"/> is already
/// normalized by <see cref="Internet.HostPattern.TryNormalize"/>.
/// </summary>
public sealed class InternetPreset
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> Hosts { get; set; } = [];

    public long CreatedAtUnix { get; set; }

    public long ModifiedAtUnix { get; set; }

    /// <summary>A copy that shares nothing with this preset, the host list included.</summary>
    public InternetPreset Clone()
    {
        var copy = (InternetPreset)MemberwiseClone();
        copy.Hosts = [.. Hosts];
        return copy;
    }
}

/// <summary>
/// <c>internet-presets.json</c> beside <c>lab.json</c> — the named whitelist presets (D-73),
/// persisted like the script library (D-31 item 4): part of the backup and of the routine lab
/// file, so a restored or a second console has them.
/// </summary>
public sealed class InternetPresetsDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.InternetPresetsSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.InternetPresetsSchemaVersion;

    public string LabId { get; set; } = string.Empty;

    /// <summary>When the seed preset was created; it is created once and never again, even after the teacher deletes it.</summary>
    public long SeedImportedAtUnix { get; set; }

    public List<InternetPreset> Presets { get; set; } = [];
}
