namespace LabControl.Shared.Persistence;

/// <summary>
/// <c>preferences.json</c> at the console's data root (M7, D-86): what the person at this
/// computer chose for the console itself, independent of any lab.
/// </summary>
public sealed class PreferencesDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.PreferencesSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.PreferencesSchemaVersion;

    /// <summary>
    /// <c>uk</c>, <c>en</c> or <c>ru</c> (<see cref="Localization.ProductLanguages"/>); <c>null</c>
    /// until the teacher chooses, so the OS display language decides.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>The last *Text / Video* choice next to *Show my screen* (M7 portion 6, D-91); <c>null</c> = *Text*.</summary>
    public bool? BroadcastVideo { get; set; }

    /// <summary>The last *Text / Video* choice in a PC's full-view window (D-91); <c>null</c> = *Text*.</summary>
    public bool? FullViewVideo { get; set; }
}
