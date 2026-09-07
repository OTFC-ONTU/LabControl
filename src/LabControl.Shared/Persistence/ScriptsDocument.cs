using LabControl.Shared.Jobs;

namespace LabControl.Shared.Persistence;

/// <summary>
/// One script in the teacher's library (D-31 item 4, D-38): everything a <c>run_script</c>
/// job needs except the file reference, which the console mints when the script is run.
/// <see cref="Shell"/> and <see cref="RunAs"/> use the job's own vocabulary
/// (<see cref="RunScriptRequest"/>), so the document and the wire never disagree.
/// </summary>
public sealed class ScriptRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>One line, shown next to the name in the list.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary><c>powershell</c> or <c>cmd</c>.</summary>
    public string Shell { get; set; } = RunScriptRequest.PowerShellValue;

    /// <summary><c>system</c> or <c>user</c>.</summary>
    public string RunAs { get; set; } = RunScriptRequest.SystemValue;

    public int TimeoutSeconds { get; set; } = (int)Defaults.ScriptDefaultTimeout.TotalSeconds;

    public string Text { get; set; } = string.Empty;

    public long CreatedAtUnix { get; set; }

    public long UpdatedAtUnix { get; set; }

    /// <summary>The seed file this came from, or null for one the teacher wrote.</summary>
    public string? SeedFile { get; set; }

    public ScriptShell ShellKind => ParseShell(Shell);

    public ScriptRunAs RunAsKind => ParseRunAs(RunAs);

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds > 0 ? TimeoutSeconds : Defaults.ScriptDefaultTimeout.TotalSeconds);

    public static ScriptShell ParseShell(string value) =>
        string.Equals(value, RunScriptRequest.CmdValue, StringComparison.OrdinalIgnoreCase) ? ScriptShell.Cmd : ScriptShell.PowerShell;

    public static ScriptRunAs ParseRunAs(string value) =>
        string.Equals(value, RunScriptRequest.UserValue, StringComparison.OrdinalIgnoreCase) ? ScriptRunAs.User : ScriptRunAs.System;

    public static string ShellValue(ScriptShell shell) => shell == ScriptShell.Cmd ? RunScriptRequest.CmdValue : RunScriptRequest.PowerShellValue;

    public static string RunAsValue(ScriptRunAs runAs) => runAs == ScriptRunAs.User ? RunScriptRequest.UserValue : RunScriptRequest.SystemValue;

    /// <summary>A copy that shares nothing with this record, for editing.</summary>
    public ScriptRecord Clone() => (ScriptRecord)MemberwiseClone();
}

/// <summary>
/// <c>scripts.json</c> beside <c>lab.json</c> — the script library (D-31 item 4). Part of
/// the backup, so it moves with the lab to the next teacher machine.
/// </summary>
public sealed class ScriptsDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.ScriptsSchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.ScriptsSchemaVersion;

    public string LabId { get; set; } = string.Empty;

    /// <summary>When the repository's seed was imported; the seed is read once and never again (D-31 item 4).</summary>
    public long SeedImportedAtUnix { get; set; }

    public List<ScriptRecord> Scripts { get; set; } = [];
}
