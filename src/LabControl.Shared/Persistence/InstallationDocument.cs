namespace LabControl.Shared.Persistence;

/// <summary>Local ownership evidence, independent of enrollment and updates (D-45).
/// Contains no passwords, sign-in secrets or profile paths.</summary>
public sealed class InstallationDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.InstallationSchemaVersion);
    public int SchemaVersion { get; set; } = Defaults.InstallationSchemaVersion;
    public string InstallationId { get; set; } = string.Empty;
    // Nullable so a malformed/older document cannot silently enable the fresh default.
    public bool? CreateStudentAccount { get; set; }
    public bool StudentCreationPending { get; set; }
    public string? CreatedStudentSid { get; set; }
}
