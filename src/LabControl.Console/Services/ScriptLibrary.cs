using LabControl.Shared.Jobs;
using LabControl.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>
/// The teacher's script library (D-31 item 4, D-38): <c>scripts.json</c> loaded once,
/// seeded from the repository's <c>scripts/library/</c> on the very first run, saved on
/// every change. The view edits copies and hands them back through <see cref="Save"/>;
/// nothing here touches the wire — <see cref="LabSession.RunScript"/> does that.
/// </summary>
public sealed class ScriptLibrary
{
    private readonly LabStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _log;
    private readonly Lock _gate = new();

    public ScriptLibrary(LabStore store, string labId, IReadOnlyList<SeedScript> seed, Func<DateTimeOffset> clock, ILogger log)
    {
        _store = store;
        _clock = clock;
        _log = log;

        var document = store.LoadScripts();
        if (document is null)
        {
            var now = clock();
            document = new ScriptsDocument { LabId = labId, SeedImportedAtUnix = now.ToUnixTimeSeconds() };
            foreach (var file in seed.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase))
            {
                document.Scripts.Add(ScriptSeed.ToRecord(file, now));
            }

            store.SaveScripts(document);
            log.LogInformation("script library created with {Count} seed script(s)", document.Scripts.Count);
        }

        Document = document;
    }

    public ScriptsDocument Document { get; }

    /// <summary>Raised after every change, on the caller's thread.</summary>
    public event Action? Changed;

    /// <summary>Copies, by name; the caller may edit them freely.</summary>
    public IReadOnlyList<ScriptRecord> Scripts
    {
        get
        {
            lock (_gate)
            {
                return Document.Scripts
                    .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(s => s.Clone())
                    .ToArray();
            }
        }
    }

    public ScriptRecord? Find(string id)
    {
        lock (_gate)
        {
            return Document.Scripts.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal))?.Clone();
        }
    }

    /// <summary>A blank script with an id and a unique name, not yet in the library.</summary>
    public ScriptRecord NewScript()
    {
        var now = _clock().ToUnixTimeSeconds();
        var baseName = "new-script";
        var name = baseName;
        lock (_gate)
        {
            for (var n = 2; Document.Scripts.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            {
                name = $"{baseName}-{n}";
            }
        }

        return new ScriptRecord { Id = Guid.NewGuid().ToString("d"), Name = name, CreatedAtUnix = now, UpdatedAtUnix = now };
    }

    /// <summary>Adds or replaces a script; the name must be non-empty and unique.</summary>
    public bool TrySave(ScriptRecord script, out string error)
    {
        error = string.Empty;
        var name = script.Name.Trim();
        if (name.Length == 0)
        {
            error = "The script needs a name.";
            return false;
        }

        if (script.TimeoutSeconds <= 0)
        {
            error = "The timeout must be a number of seconds above zero.";
            return false;
        }

        lock (_gate)
        {
            if (Document.Scripts.Any(s => !string.Equals(s.Id, script.Id, StringComparison.Ordinal) && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"Another script is already called \"{name}\".";
                return false;
            }

            var stored = script.Clone();
            stored.Name = name;
            stored.Description = script.Description.Trim();
            stored.UpdatedAtUnix = _clock().ToUnixTimeSeconds();
            if (stored.CreatedAtUnix == 0)
            {
                stored.CreatedAtUnix = stored.UpdatedAtUnix;
            }

            var index = Document.Scripts.FindIndex(s => string.Equals(s.Id, script.Id, StringComparison.Ordinal));
            if (index < 0)
            {
                Document.Scripts.Add(stored);
            }
            else
            {
                Document.Scripts[index] = stored;
            }

            _store.SaveScripts(Document);
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Explicitly add missing built-ins; preserve existing seed edits and name collisions.</summary>
    public int AddBuiltIns(IReadOnlyList<SeedScript> seed)
    {
        var added = 0;
        lock (_gate)
        {
            foreach (var file in seed)
            {
                var record = ScriptSeed.ToRecord(file, _clock());
                if (Document.Scripts.Any(s => string.Equals(s.SeedFile, file.FileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Name, record.Name, StringComparison.OrdinalIgnoreCase))) continue;
                Document.Scripts.Add(record);
                added++;
            }
            if (added > 0) _store.SaveScripts(Document);
        }
        if (added > 0) Changed?.Invoke();
        return added;
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (Document.Scripts.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal)) == 0)
            {
                return false;
            }

            _store.SaveScripts(Document);
        }

        Changed?.Invoke();
        return true;
    }
}

/// <summary>The seed scripts compiled into the console from <c>scripts/library/</c> (D-38).</summary>
public static class SeedScripts
{
    private const string Prefix = "LabControl.Console.Seed.";

    public static IReadOnlyList<SeedScript> Embedded()
    {
        var assembly = typeof(SeedScripts).Assembly;
        var list = new List<SeedScript>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            list.Add(new SeedScript(resource[Prefix.Length..], reader.ReadToEnd()));
        }

        return list;
    }
}
