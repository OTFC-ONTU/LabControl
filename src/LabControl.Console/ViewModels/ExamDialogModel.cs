using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared.Exam;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;

namespace LabControl.Console.ViewModels;

/// <summary>
/// The live side of the <i>Exam</i> dialog (M6 portion 4, D-75, D-76), free of Avalonia: the
/// exam presets it edits, the internet presets it borrows a whitelist from, and the
/// program-list validation in the teacher's words.
/// </summary>
public sealed class ExamDialogModel
{
    private readonly LabSession? _session;
    private readonly IReadOnlyList<ExamPreset>? _examPresets;
    private readonly IReadOnlyList<InternetPreset>? _internetPresets;

    public ExamDialogModel(LabSession session, int pcCount, bool wholeLab)
    {
        _session = session;
        PcCount = pcCount;
        WholeLab = wholeLab;
    }

    /// <summary>A model over fixed lists, for the renders.</summary>
    public ExamDialogModel(IReadOnlyList<ExamPreset> examPresets, IReadOnlyList<InternetPreset> internetPresets, int pcCount, bool wholeLab)
    {
        _examPresets = examPresets;
        _internetPresets = internetPresets;
        PcCount = pcCount;
        WholeLab = wholeLab;
    }

    public int PcCount { get; }

    /// <summary>Started for the whole lab: a PC that links before the end joins it.</summary>
    public bool WholeLab { get; }

    public IReadOnlyList<ExamPreset> Presets => _session?.ExamPresets.Presets ?? _examPresets ?? [];

    public IReadOnlyList<InternetPreset> InternetPresets => _session?.InternetPresets.Presets ?? _internetPresets ?? [];

    /// <summary>Saves (or, with an id, updates) a preset; the problem in words, or <c>null</c> when saved.</summary>
    public (ExamPreset? Saved, string? Problem) SavePreset(string? id, ExamPreset preset)
    {
        if (_session is null)
        {
            return (preset, null);
        }

        ExamPresetSaveResult result;
        try
        {
            result = _session.ExamPresets.Save(id, preset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, Strings.Format("Store.SaveFailed", ex.Message));
        }

        return result.Ok
            ? (result.Saved, null)
            : (null, Strings.Get("Internet.Preset." + result.NameProblem!.Replace("preset.", string.Empty, StringComparison.Ordinal)));
    }

    /// <summary>Deletes a preset; false with the problem in words when the library could not be written.</summary>
    public bool DeletePreset(string id, out string? problem)
    {
        problem = null;
        try
        {
            return _session?.ExamPresets.Delete(id) ?? false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = Strings.Format("Store.SaveFailed", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The program list as typed — one per line, blanks ignored — normalized, or the problems in
    /// words. With the switch on, an empty list is allowed: then only Windows' own programs and
    /// the shell stay open.
    /// </summary>
    /// <summary>The browsers a teacher would allow for the exam's sites; with none of them on the list the sites cannot be opened.</summary>
    public static IReadOnlyList<string> Browsers { get; } = ["msedge.exe", "chrome.exe", "firefox.exe", "opera.exe", "brave.exe", "browser.exe"];

    /// <summary>
    /// True when the exam allows sites but restricts programs to a list with no browser on it:
    /// the whitelist would close the browser the moment it starts (found on a lab PC, 2026-10-23).
    /// </summary>
    public static bool SitesWithoutBrowser(bool restrictPrograms, bool whitelist, string? programsText) =>
        restrictPrograms && whitelist && !ParsePrograms(programsText).Programs.Any(p => Browsers.Contains(p, StringComparer.Ordinal));

    public static (IReadOnlyList<string> Programs, string? Problem) ParsePrograms(string? text)
    {
        var programs = new List<string>();
        var problems = new List<string>();
        foreach (var line in (text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (ProgramNames.TryNormalize(line, out var name))
            {
                if (!programs.Contains(name, StringComparer.Ordinal))
                {
                    programs.Add(name);
                }
            }
            else
            {
                problems.Add(Strings.Format("Exam.ProgramProblem", line));
            }
        }

        if (programs.Count > LabControl.Shared.Defaults.ExamMaxAllowedPrograms)
        {
            problems.Add(Strings.Format("Exam.TooManyPrograms", LabControl.Shared.Defaults.ExamMaxAllowedPrograms));
        }

        return (programs, problems.Count > 0 ? string.Join("\n", problems) : null);
    }

    /// <summary>The preset a dialog state amounts to, for <i>Save as preset</i>.</summary>
    public static ExamPreset ToPreset(string name, ExamRequest request) => new()
    {
        Name = name,
        Message = request.Message,
        TimerMinutes = request.Timer is { } timer ? (int)Math.Ceiling(timer.TotalMinutes) : 0,
        RestrictPrograms = request.RestrictPrograms,
        AllowedPrograms = request.AllowedPrograms.ToList(),
        InternetMode = request.InternetMode switch
        {
            InternetPolicy.Types.Mode.Blocked => ExamInternetModes.Blocked,
            InternetPolicy.Types.Mode.Whitelist => ExamInternetModes.Whitelist,
            _ => ExamInternetModes.Untouched,
        },
        InternetPresetName = request.InternetPresetName,
        InternetHosts = request.InternetHosts.ToList(),
        CollectWork = request.CollectFolder is not null,
        CollectFolder = request.CollectFolder ?? LabControl.Shared.Jobs.CollectRequest.DefaultFolder,
    };

    /// <summary>A folder on the PCs as typed, normalized, or the problem in words (M6 portion 5).</summary>
    public static (string? Folder, string? Problem) ParseCollectFolder(string? text) =>
        LabControl.Shared.Jobs.CollectRequest.TryNormalize(text, out var folder) ? (folder, null) : (null, Strings.Get("Collect.FolderInvalid"));
}
