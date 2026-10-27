using LabControl.Shared.Internet;
using LabControl.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>What <see cref="InternetPresetLibrary.Save"/> did: the stored copy, or why nothing was saved.</summary>
public sealed record InternetPresetSaveResult(InternetPreset? Saved, string? NameProblem, IReadOnlyList<(string Input, string Problem)> HostProblems)
{
    public bool Ok => Saved is not null;
}

/// <summary>
/// The teacher's named whitelist presets (M6, D-73): <c>internet-presets.json</c> loaded once,
/// created with one seed preset on the very first run (owner decision 2026-10-17), saved on
/// every change. Modelled on <see cref="ScriptLibrary"/>; callers only ever see copies.
/// </summary>
public sealed class InternetPresetLibrary
{
    /// <summary>The stable reasons <see cref="Save"/> refuses a name.</summary>
    public static class NameProblems
    {
        public const string Empty = "preset.name_empty";

        public const string Taken = "preset.name_taken";
    }

    public const string SeedName = "Java docs";

    public static readonly IReadOnlyList<string> SeedHosts = ["*.jetbrains.com", "docs.oracle.com"];

    private readonly LabStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();
    private readonly InternetPresetsDocument _document;

    public InternetPresetLibrary(LabStore store, string labId, Func<DateTimeOffset> clock, ILogger<InternetPresetLibrary> log)
    {
        _store = store;
        _clock = clock;

        var document = store.LoadInternetPresets();
        if (document is null)
        {
            var now = clock().ToUnixTimeSeconds();
            document = new InternetPresetsDocument { LabId = labId, SeedImportedAtUnix = now };
            var hosts = new List<string>();
            foreach (var host in SeedHosts)
            {
                if (HostPattern.TryNormalize(host, out var pattern, out _) && !hosts.Contains(pattern, StringComparer.Ordinal))
                {
                    hosts.Add(pattern);
                }
            }

            document.Presets.Add(new InternetPreset { Id = Guid.NewGuid().ToString("n"), Name = SeedName, Hosts = hosts, CreatedAtUnix = now, ModifiedAtUnix = now });
            store.SaveInternetPresets(document);
            log.LogInformation("internet preset library created with {Count} seed preset(s)", document.Presets.Count);
        }

        _document = document;
    }

    /// <summary>Raised after every successful change, on the caller's thread, outside the lock.</summary>
    public event Action? Changed;

    /// <summary>Copies, by name; the caller may edit them freely.</summary>
    public IReadOnlyList<InternetPreset> Presets
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

    public InternetPreset? Find(string id)
    {
        lock (_gate)
        {
            return _document.Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))?.Clone();
        }
    }

    /// <summary>
    /// Adds a preset (<paramref name="id"/> null or unknown) or replaces one. The name is trimmed
    /// and must be unique ignoring case; every non-blank host must normalize, blanks and
    /// duplicates are dropped. Nothing is saved when anything is refused.
    /// </summary>
    public InternetPresetSaveResult Save(string? id, string name, IEnumerable<string> hosts)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var patterns = new List<string>();
        var problems = new List<(string Input, string Problem)>();
        foreach (var input in hosts ?? [])
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (!HostPattern.TryNormalize(input, out var pattern, out var problem))
            {
                problems.Add((input, problem));
            }
            else if (!patterns.Contains(pattern, StringComparer.Ordinal))
            {
                patterns.Add(pattern);
            }
        }

        InternetPreset saved;
        lock (_gate)
        {
            string? nameProblem = null;
            if (trimmed.Length == 0)
            {
                nameProblem = NameProblems.Empty;
            }
            else if (_document.Presets.Any(p => !string.Equals(p.Id, id, StringComparison.Ordinal) && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                nameProblem = NameProblems.Taken;
            }

            if (nameProblem is not null || problems.Count > 0)
            {
                return new InternetPresetSaveResult(null, nameProblem, problems);
            }

            var before = Snapshot();
            var now = _clock().ToUnixTimeSeconds();
            var index = id is null ? -1 : _document.Presets.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                saved = new InternetPreset { Id = Guid.NewGuid().ToString("n"), Name = trimmed, Hosts = patterns, CreatedAtUnix = now, ModifiedAtUnix = now };
                _document.Presets.Add(saved);
            }
            else
            {
                saved = _document.Presets[index];
                saved.Name = trimmed;
                saved.Hosts = patterns;
                saved.ModifiedAtUnix = now;
            }

            SaveOrRestore(before);
            saved = saved.Clone();
        }

        Changed?.Invoke();
        return new InternetPresetSaveResult(saved, null, []);
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

    private List<InternetPreset> Snapshot() => _document.Presets.Select(p => p.Clone()).ToList();

    /// <summary>
    /// Writes the library; when the disk refuses (full, read-only), puts the presets back as they
    /// were and throws, so what the dialog shows is what is on disk (M7 robustness pass).
    /// </summary>
    private void SaveOrRestore(List<InternetPreset> before)
    {
        try
        {
            _store.SaveInternetPresets(_document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _document.Presets = before;
            throw;
        }
    }
}
