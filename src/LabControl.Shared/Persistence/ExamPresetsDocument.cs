namespace LabControl.Shared.Persistence;

/// <summary>
/// One named exam preset (M6 portion 4, D-76): the four switches a teacher composes once and
/// starts with one click — <i>Written test</i>, <i>Practical with the IDE</i>. The internet
/// switch keeps a copy of the hosts it was given, so a preset still means what it meant after
/// the internet preset it came from is edited or deleted.
/// </summary>
public sealed class ExamPreset
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>The timer in minutes; 0 = no timer.</summary>
    public int TimerMinutes { get; set; }

    public bool RestrictPrograms { get; set; }

    /// <summary>Executable names, normalized by <see cref="Exam.ProgramNames"/>.</summary>
    public List<string> AllowedPrograms { get; set; } = [];

    /// <summary><c>untouched</c>, <c>blocked</c> or <c>whitelist</c> (<see cref="ExamInternetModes"/>).</summary>
    public string InternetMode { get; set; } = ExamInternetModes.Untouched;

    /// <summary>The internet preset the hosts were taken from, for the badge; empty when none.</summary>
    public string InternetPresetName { get; set; } = string.Empty;

    public List<string> InternetHosts { get; set; } = [];

    public long CreatedAtUnix { get; set; }

    public long ModifiedAtUnix { get; set; }

    /// <summary>A copy that shares nothing with this preset.</summary>
    public ExamPreset Clone()
    {
        var copy = (ExamPreset)MemberwiseClone();
        copy.AllowedPrograms = [.. AllowedPrograms];
        copy.InternetHosts = [.. InternetHosts];
        return copy;
    }
}

/// <summary>The values <see cref="ExamPreset.InternetMode"/> takes on disk.</summary>
public static class ExamInternetModes
{
    public const string Untouched = "untouched";

    public const string Blocked = "blocked";

    public const string Whitelist = "whitelist";
}

/// <summary>
/// <c>exam-presets.json</c> beside <c>lab.json</c> — the named exam presets (D-76). Part of
/// the backup like the script library, but <b>not</b> of the routine lab file: an exam preset
/// is the administrator's teaching material, not what a teacher device needs to run the room.
/// </summary>
public sealed class ExamPresetsDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.ExamPresetsSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.ExamPresetsSchemaVersion;

    public string LabId { get; set; } = string.Empty;

    /// <summary>When the seed preset was created; it is created once and never again, even after the teacher deletes it.</summary>
    public long SeedImportedAtUnix { get; set; }

    public List<ExamPreset> Presets { get; set; } = [];
}
