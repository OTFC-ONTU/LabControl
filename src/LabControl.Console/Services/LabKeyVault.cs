using LabControl.Shared;
using LabControl.Shared.Identity;

namespace LabControl.Console.Services;

/// <summary>
/// Where the console keeps the lab key: the encrypted document always, the unlocked key
/// only for <see cref="Defaults.LabKeyUnlockWindow"/> after it was last used (D-26). Every
/// CA operation — enrolling, renewing, revoking, adding a holder, exporting a backup — goes
/// through <see cref="Use"/>, which is what keeps the window honest.
/// </summary>
public sealed class LabKeyVault : IDisposable
{
    private readonly LabStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();
    private LabKey? _key;
    private DateTimeOffset _lastUsed;

    public LabKeyVault(LabStore store, LabKeyDocument document, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        Document = document;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The file as loaded; its holder names are readable without unlocking.</summary>
    public LabKeyDocument Document { get; private set; }

    public string LabId => Document.LabId;

    public string LabName => Document.LabName;

    public IReadOnlyList<string> HolderNames =>
        Document.Wrappings.Where(w => w.Kind == KeyWrappingKind.Holder).Select(w => w.Name).ToArray();

    public bool IsUnlocked
    {
        get
        {
            lock (_gate)
            {
                return _key is not null;
            }
        }
    }

    /// <summary>When the key locks itself if nothing uses it; <c>null</c> while locked.</summary>
    public DateTimeOffset? LocksAt
    {
        get
        {
            lock (_gate)
            {
                return _key is null ? null : _lastUsed + Defaults.LabKeyUnlockWindow;
            }
        }
    }

    /// <summary>Raised on unlock, lock and any change to the document (holders, recovery code).</summary>
    public event Action? Changed;

    public bool TryUnlock(string passphrase)
    {
        if (!LabKey.TryUnlock(Document, passphrase, out var key))
        {
            return false;
        }

        Adopt(key);
        return true;
    }

    public bool TryUnlock(RecoveryCode recoveryCode)
    {
        if (!LabKey.TryUnlock(Document, recoveryCode, out var key))
        {
            return false;
        }

        Adopt(key);
        return true;
    }

    /// <summary>Takes an already-unlocked key — from the first-run wizard or an import.</summary>
    public void Adopt(LabKey key)
    {
        LabKey? previous;
        lock (_gate)
        {
            previous = _key;
            _key = key;
            Document = key.Document;
            _lastUsed = _clock();
        }

        previous?.Dispose();
        Changed?.Invoke();
    }

    public void Lock()
    {
        LabKey? key;
        lock (_gate)
        {
            key = _key;
            _key = null;
        }

        if (key is null)
        {
            return;
        }

        key.Dispose();
        Changed?.Invoke();
    }

    /// <summary>
    /// Runs a CA operation if the key is unlocked, extending the window; returns
    /// <c>false</c> — without calling <paramref name="action"/> — when it is locked.
    /// </summary>
    public bool Use<T>(Func<LabKey, T> action, out T result)
    {
        lock (_gate)
        {
            if (_key is null)
            {
                result = default!;
                return false;
            }

            _lastUsed = _clock();
            result = action(_key);
            return true;
        }
    }

    public bool Use(Action<LabKey> action) => Use<object?>(key => { action(key); return null; }, out _);

    /// <summary>Peeks at the unlocked key for a read-only step (issuing, signing) that the caller already guards.</summary>
    public LabKey? Peek()
    {
        lock (_gate)
        {
            if (_key is not null)
            {
                _lastUsed = _clock();
            }

            return _key;
        }
    }

    /// <summary>Writes the document after a change made through <see cref="Use"/> (holders, recovery code).</summary>
    public void Save()
    {
        lock (_gate)
        {
            _store.SaveLabKey(Document);
        }

        Changed?.Invoke();
    }

    /// <summary>Called by the session's timer; locks the key once the window has passed.</summary>
    public void Tick()
    {
        bool expired;
        lock (_gate)
        {
            expired = _key is not null && _clock() - _lastUsed > Defaults.LabKeyUnlockWindow;
        }

        if (expired)
        {
            Lock();
        }
    }

    public void Dispose() => Lock();
}
