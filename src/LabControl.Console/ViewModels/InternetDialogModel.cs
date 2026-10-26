using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared.Internet;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;

namespace LabControl.Console.ViewModels;

/// <summary>
/// What the teacher asked the <i>Internet</i> dialog for (M6 portion 3, D-73): a whitelist or a
/// block with its hosts, preset name and duration (<c>null</c> = until lifted), or
/// <see cref="InternetPolicy.Types.Mode.Open"/> to lift the policy.
/// </summary>
public sealed record InternetAnswer(InternetPolicy.Types.Mode Mode, IReadOnlyList<string> Hosts, string PresetName, TimeSpan? Duration);

/// <summary>
/// The live side of the <i>Internet</i> dialog, free of Avalonia: the presets it edits, the
/// names the PCs' whitelists refused lately and the <i>Allow</i> that acts at once (owner
/// decision 2026-10-17, D-74), and the host-list validation in the teacher's words.
/// </summary>
public sealed class InternetDialogModel
{
    private readonly LabSession _session;

    public InternetDialogModel(LabSession session, int pcCount, IReadOnlyList<int>? examPcs = null)
    {
        _session = session;
        PcCount = pcCount;
        ExamPcs = examPcs ?? [];
    }

    public int PcCount { get; }

    /// <summary>
    /// The chosen PCs in an exam that switches the internet: the exam's setting wins there until
    /// it ends, so whatever is chosen here only takes effect after it (a lab, 2026-10-26).
    /// </summary>
    public IReadOnlyList<int> ExamPcs { get; }

    public IReadOnlyList<InternetPreset> Presets => _session.InternetPresets.Presets;

    /// <summary>The refused names across the linked PCs, most widespread first.</summary>
    public IReadOnlyList<RefusedName> Refused() => _session.RefusedNames();

    /// <summary>Adds the name to every whitelist that refused it, at once; returns how many PCs were asked.</summary>
    public int Allow(string name) => _session.AllowRefusedName(name);

    /// <summary>Saves (or, with an id, updates) a preset; the problems in words, or <c>null</c> when saved.</summary>
    public (InternetPreset? Saved, string? Problem) SavePreset(string? id, string name, IEnumerable<string> hosts)
    {
        var result = _session.InternetPresets.Save(id, name, hosts);
        if (result.Ok)
        {
            return (result.Saved, null);
        }

        var lines = new List<string>();
        if (result.NameProblem is { } nameProblem)
        {
            lines.Add(Strings.Get("Internet.Preset." + nameProblem.Replace("preset.", string.Empty, StringComparison.Ordinal)));
        }

        lines.AddRange(result.HostProblems.Select(p => HostProblem(p.Input, p.Problem)));
        return (null, string.Join("\n", lines));
    }

    public bool DeletePreset(string id) => _session.InternetPresets.Delete(id);

    /// <summary>
    /// The host list as typed — one per line, blanks ignored — normalized, or the problems in
    /// words. A whitelist needs at least one site: without any it would block everything, which
    /// is what <i>No internet</i> is for.
    /// </summary>
    public static (IReadOnlyList<string> Hosts, string? Problem) ParseHosts(string? text)
    {
        var hosts = new List<string>();
        var problems = new List<string>();
        foreach (var line in (text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (HostPattern.TryNormalize(line, out var pattern, out var code))
            {
                if (!hosts.Contains(pattern, StringComparer.Ordinal))
                {
                    hosts.Add(pattern);
                }
            }
            else
            {
                problems.Add(HostProblem(line, code));
            }
        }

        if (problems.Count > 0)
        {
            return (hosts, string.Join("\n", problems));
        }

        return hosts.Count == 0 ? (hosts, Strings.Get("Internet.NoHosts")) : (hosts, null);
    }

    private static string HostProblem(string input, string code) =>
        Strings.Format("Internet.HostProblemFormat", input, Strings.Get("Internet.Host." + code.Replace("host.", string.Empty, StringComparison.Ordinal)));
}
