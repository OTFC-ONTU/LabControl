using LabControl.Shared.Exam;
using LabControl.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>What <see cref="ExamPresetLibrary.Save"/> did: the stored copy, or why nothing was saved.</summary>
public sealed record ExamPresetSaveResult(ExamPreset? Saved, string? NameProblem)
{
    public bool Ok => Saved is not null;
}

/// <summary>
/// The teacher's named exam presets (M6 portion 4, D-76): <c>exam-presets.json</c> loaded once,
/// created with one example on the very first run, saved on every change. Modelled on
/// <see cref="InternetPresetLibrary"/>; callers only ever see copies.
/// </summary>
public sealed class ExamPresetLibrary
{
    public const string SeedName = "Practical (IntelliJ IDEA)";

    public const int SeedTimerMinutes = 80;

    public static readonly IReadOnlyList<string> SeedPrograms = ["idea64.exe", "fsnotifier.exe", "jcef_helper.exe", "java.exe", "javaw.exe", "cmd.exe", "msedge.exe"];

    private readonly LabStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();
    private readonly ExamPresetsDocument _document;

    /// <param name="internetPresets">Where the example's whitelist comes from: the <i>Java docs</i> internet preset, if the lab has it.</param>
    public ExamPresetLibrary(LabStore store, string labId, Func<DateTimeOffset> clock, InternetPresetLibrary internetPresets, ILogger<ExamPresetLibrary> log)
    {
        _store = store;
        _clock = clock;

        var document = store.LoadExamPresets();
        if (document is null)
        {
            var now = clock().ToUnixTimeSeconds();
            document = new ExamPresetsDocument { LabId = labId, SeedImportedAtUnix = now };
            var javaDocs = internetPresets.Presets.FirstOrDefault(p => string.Equals(p.Name, InternetPresetLibrary.SeedName, StringComparison.OrdinalIgnoreCase));
            document.Presets.Add(new ExamPreset
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = SeedName,
                TimerMinutes = SeedTimerMinutes,
                RestrictPrograms = true,
                AllowedPrograms = ProgramNames.NormalizeAll(SeedPrograms),
                InternetMode = javaDocs is null ? ExamInternetModes.Untouched : ExamInternetModes.Whitelist,
                InternetPresetName = javaDocs?.Name ?? string.Empty,
                InternetHosts = javaDocs?.Hosts.ToList() ?? [],
                CreatedAtUnix = now,
                ModifiedAtUnix = now,
            });
            store.SaveExamPresets(document);
            log.LogInformation("exam preset library created with {Count} seed preset(s)", document.Presets.Count);
        }

        _document = document;
    }

    /// <summary>Raised after every successful change, on the caller's thread, outside the lock.</summary>
    public event Action? Changed;

    /// <summary>The document as it stands, a copy; for the backup.</summary>
    public ExamPresetsDocument Document
    {
        get
        {
            lock (_gate)
            {
                return new ExamPresetsDocument
                {
                    LabId = _document.LabId,
                    SeedImportedAtUnix = _document.SeedImportedAtUnix,
                    Presets = _document.Presets.Select(p => p.Clone()).ToList(),
                };
            }
        }
    }

    /// <summary>Copies, by name; the caller may edit them freely.</summary>
    public IReadOnlyList<ExamPreset> Presets
    {
        get
        {
            lock (_gate)
            {
                return _document.Presets
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => p.Clone())
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Adds a preset (an unknown or null id) or replaces one. The name is trimmed and must be
    /// unique ignoring case; the program names are normalized, the ones that do not normalize
    /// dropped. Nothing is saved when the name is refused.
    /// </summary>
    public ExamPresetSaveResult Save(string? id, ExamPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var trimmed = (preset.Name ?? string.Empty).Trim();
        ExamPreset saved;
        lock (_gate)
        {
            if (trimmed.Length == 0)
            {
                return new ExamPresetSaveResult(null, InternetPresetLibrary.NameProblems.Empty);
            }

            if (_document.Presets.Any(p => !string.Equals(p.Id, id, StringComparison.Ordinal) && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return new ExamPresetSaveResult(null, InternetPresetLibrary.NameProblems.Taken);
            }

            var before = Snapshot();
            var now = _clock().ToUnixTimeSeconds();
            saved = preset.Clone();
            saved.Name = trimmed;
            saved.Message ??= string.Empty;
            saved.TimerMinutes = Math.Clamp(saved.TimerMinutes, 0, (int)Shared.Defaults.ExamHardLimit.TotalMinutes);
            saved.AllowedPrograms = ProgramNames.NormalizeAll(saved.AllowedPrograms);
            saved.InternetPresetName ??= string.Empty;
            saved.InternetHosts = saved.InternetMode == ExamInternetModes.Whitelist ? saved.InternetHosts.ToList() : [];
            saved.CollectFolder = Shared.Jobs.CollectRequest.TryNormalize(saved.CollectFolder, out var collectFolder)
                ? collectFolder
                : Shared.Jobs.CollectRequest.DefaultFolder;
            saved.ModifiedAtUnix = now;
            var index = id is null ? -1 : _document.Presets.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                saved.Id = Guid.NewGuid().ToString("n");
                saved.CreatedAtUnix = now;
                _document.Presets.Add(saved);
            }
            else
            {
                saved.Id = _document.Presets[index].Id;
                saved.CreatedAtUnix = _document.Presets[index].CreatedAtUnix;
                _document.Presets[index] = saved;
            }

            SaveOrRestore(before);
            saved = saved.Clone();
        }

        Changed?.Invoke();
        return new ExamPresetSaveResult(saved, null);
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var before = Snapshot();
            if (_document.Presets.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal)) == 0)
            {
                return false;
            }

            SaveOrRestore(before);
        }

        Changed?.Invoke();
        return true;
    }

    private List<ExamPreset> Snapshot() => _document.Presets.Select(p => p.Clone()).ToList();

    /// <summary>
    /// Writes the library; when the disk refuses (full, read-only), puts the presets back as they
    /// were and throws, so what the dialog shows is what is on disk (M7 robustness pass).
    /// </summary>
    private void SaveOrRestore(List<ExamPreset> before)
    {
        try
        {
            _store.SaveExamPresets(_document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _document.Presets = before;
            throw;
        }
    }
}

/// <summary>What the teacher asked the <i>Exam</i> dialog to start (M6 portion 4, D-75).</summary>
/// <param name="Timer">The countdown; <c>null</c> = no timer.</param>
/// <param name="InternetMode"><c>OPEN</c> or unspecified = internet untouched; otherwise whitelist or blocked for the exam's lifetime.</param>
public sealed record ExamRequest(
    string Message,
    TimeSpan? Timer,
    bool RestrictPrograms,
    IReadOnlyList<string> AllowedPrograms,
    LabControl.Shared.Protocol.InternetPolicy.Types.Mode InternetMode,
    IReadOnlyList<string> InternetHosts,
    string InternetPresetName,
    string PresetName,
    string? CollectFolder = null,
    string? CollectDestination = null);
