namespace LabControl.Shared.Persistence;

/// <summary>
/// <c>device-key.json</c> in a lab's profile directory (D-82): how this computer opens the lab
/// key without a passphrase. The secret itself is in the OS store (<see cref="ProtectedSecret"/>,
/// the same protector as the console instance's own key); the key document carries a holder
/// named <see cref="HolderName"/> wrapped under it. Kept apart from <c>instance.json</c> so a
/// rewrite of the instance from an older copy can never drop it, and never in a backup or a
/// lab file: it opens nothing anywhere else.
/// </summary>
public sealed class DeviceKeyDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.DeviceKeySchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.DeviceKeySchemaVersion;

    public string LabId { get; set; } = string.Empty;

    /// <summary>The key holder this secret opens: <see cref="Defaults.DeviceHolderPrefix"/> plus the instance id.</summary>
    public string HolderName { get; set; } = string.Empty;

    public long RememberedAtUnix { get; set; }

    public ProtectedSecret Secret { get; set; } = new();
}
