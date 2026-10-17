namespace LabControl.Shared.Internet;

/// <summary>
/// The names the whitelist resolver refused lately (M6 portion 3, D-74), so the console can
/// show "the missing CDN host is a click away" (ARCHITECTURE §6.2): each name once, the most
/// recently refused first, only those within <see cref="Defaults.WhitelistRefusedWindow"/>,
/// at most <see cref="Defaults.WhitelistRefusedMax"/>. <see cref="Version"/> moves whenever the
/// visible list changes, so the agent sends an update only when there is something new.
/// Thread-safe: the resolver notes from many queries at once while the agent reads.
/// </summary>
public sealed class RefusedNames
{
    private readonly object _gate = new();
    private readonly List<(string Name, DateTimeOffset At)> _names = [];
    private readonly TimeSpan _window;
    private readonly int _max;
    private long _version;

    /// <param name="window">How long a refusal stays visible; <see cref="Defaults.WhitelistRefusedWindow"/> by default.</param>
    /// <param name="max">How many names are kept; <see cref="Defaults.WhitelistRefusedMax"/> by default.</param>
    public RefusedNames(TimeSpan? window = null, int? max = null)
    {
        _window = window ?? Defaults.WhitelistRefusedWindow;
        _max = Math.Max(1, max ?? Defaults.WhitelistRefusedMax);
    }

    /// <summary>Changes whenever the list <see cref="Recent"/> returns changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Records that <paramref name="name"/> was refused at <paramref name="now"/>.</summary>
    public void Note(string name, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        lock (_gate)
        {
            var changed = Prune(now);
            if (_names.Count > 0 && string.Equals(_names[0].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                _names[0] = (_names[0].Name, now);
            }
            else
            {
                var existing = _names.FindIndex(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0)
                {
                    _names.RemoveAt(existing);
                }

                _names.Insert(0, (name, now));
                if (_names.Count > _max)
                {
                    _names.RemoveRange(_max, _names.Count - _max);
                }

                changed = true;
            }

            if (changed)
            {
                Interlocked.Increment(ref _version);
            }
        }
    }

    /// <summary>The names refused within the window before <paramref name="now"/>, most recent first.</summary>
    public IReadOnlyList<string> Recent(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (Prune(now))
            {
                Interlocked.Increment(ref _version);
            }

            return _names.Select(n => n.Name).ToArray();
        }
    }

    /// <summary>Forgets everything, as when a whitelist policy ends.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_names.Count > 0)
            {
                _names.Clear();
                Interlocked.Increment(ref _version);
            }
        }
    }

    private bool Prune(DateTimeOffset now)
    {
        // The list is ordered by time, newest first, so the stale ones are a tail.
        var cutoff = now - _window;
        var keep = _names.FindIndex(n => n.At < cutoff);
        if (keep < 0)
        {
            return false;
        }

        _names.RemoveRange(keep, _names.Count - keep);
        return true;
    }
}
