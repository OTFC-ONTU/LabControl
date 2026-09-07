namespace LabControl.Shared.Persistence;

/// <summary>Only encrypted bytes reach disk, including in the temporary file (D-47).</summary>
public sealed class SetupSettingsDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.SetupSettingsSchemaVersion);
    public int SchemaVersion { get; set; } = Defaults.SetupSettingsSchemaVersion;
    public byte[] Payload { get; set; } = [];
}
