namespace LabControl.Shared.Persistence;

/// <summary>
/// <c>setup.json</c> on the USB stick (INSTALLER.md, "Building the USB payload"). Carries
/// no secret: the lab id, the single-use enrollment codes, the student account defaults
/// and the numbering convention. <c>ca.crt</c> sits beside it.
/// </summary>
public sealed class SetupPayloadDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.SetupPayloadSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.SetupPayloadSchemaVersion;

    public string LabId { get; set; } = string.Empty;

    public string LabName { get; set; } = string.Empty;

    /// <summary>Optional pin for a network that drops broadcasts (ARCHITECTURE §3.4).</summary>
    public string? ConsoleHost { get; set; }

    public int ConsolePort { get; set; } = Defaults.ConsolePort;

    public StudentAccountSettings Student { get; set; } = new();

    public string Naming { get; set; } = Defaults.MachineNameFormat;

    /// <summary>The default offered by the PC-number prompt; Setup bumps it after each PC.</summary>
    public int NextNumber { get; set; } = 1;

    /// <summary>Single-use, one per PC plus spares (D-14). Setup removes the one it used.</summary>
    public List<string> EnrollmentCodes { get; set; } = [];

    /// <summary>Which console wrote this stick and when, so a stale stick can be told apart.</summary>
    public string WrittenBy { get; set; } = string.Empty;

    public long WrittenAtUnix { get; set; }
}

public sealed class StudentAccountSettings
{
    public string Name { get; set; } = Defaults.StudentAccountName;

    public string Password { get; set; } = Defaults.StudentDefaultPassword;
}
