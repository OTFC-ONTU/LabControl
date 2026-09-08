using LabControl.Shared;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabControl.Console.Services;

/// <summary>
/// The index of the labs saved on this device — <c>profiles.json</c> at the data root — and
/// where each lab's own directory is (ARCHITECTURE §4, M5 D-55). It knows nothing about a
/// lab's contents; <see cref="LabStore"/> does, one per lab directory. Every write goes
/// through <see cref="JsonStore"/>, so a crash mid-save leaves the old index or the new one.
/// </summary>
public sealed class ProfileStore
{
    private readonly ILogger _log;
    private readonly Func<ProtectedSecret, ISecretProtector> _protectors;
    private ProfilesDocument _document = new();

    /// <param name="dataDirectory">The console's data root.</param>
    /// <param name="log">Where a keystore that will not let go of an instance key is reported.</param>
    /// <param name="protectors">
    /// Resolves the protector that wrote a secret; <see cref="SecretProtector.For"/> unless a
    /// test substitutes one.
    /// </param>
    public ProfileStore(string dataDirectory, ILogger? log = null, Func<ProtectedSecret, ISecretProtector>? protectors = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        _log = log ?? NullLogger.Instance;
        _protectors = protectors ?? SecretProtector.For;
        Load();
    }

    public string DataDirectory { get; }

    public string ProfilesPath => Path.Combine(DataDirectory, Defaults.ProfilesFileName);

    public string LabsDirectory => Path.Combine(DataDirectory, Defaults.LabsDirectoryName);

    /// <summary>True once the index has been written — after the migration or the first lab.</summary>
    public bool Exists => File.Exists(ProfilesPath);

    public IReadOnlyList<ProfileRecord> Profiles => _document.Labs;

    public string? LastUsedLabId
    {
        get => _document.LastUsedLabId;
        set => _document.LastUsedLabId = value;
    }

    /// <summary>Re-reads the index from disk; an absent file is an empty index, an unreadable one throws.</summary>
    public void Load() =>
        _document = JsonStore.LoadIfExists<ProfilesDocument>(ProfilesPath, ProfilesDocument.Migrations) ?? new ProfilesDocument();

    public void Save() => JsonStore.Save(ProfilesPath, _document, ProfilesDocument.Migrations);

    public ProfileRecord? Find(string labId) =>
        _document.Labs.FirstOrDefault(p => string.Equals(p.LabId, labId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The lab's directory as an absolute path. A lab that is not in the index yet gets the
    /// default <c>labs/&lt;lab_id&gt;</c>, which is where <see cref="Upsert"/> will record it.
    /// </summary>
    public string Directory(string labId)
    {
        var relative = Find(labId)?.Directory;
        if (string.IsNullOrEmpty(relative))
        {
            relative = RelativeDirectoryFor(labId);
        }

        var full = Path.GetFullPath(Path.Combine(DataDirectory, relative));
        if (!full.StartsWith(DataDirectory + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException($"'{Defaults.ProfilesFileName}' points lab {labId} outside the data directory ('{relative}').");
        }

        return full;
    }

    /// <summary>The index entry's directory for a new lab: <c>labs/&lt;lab_id&gt;</c>, forward slashes, relative to the data root.</summary>
    public static string RelativeDirectoryFor(string labId)
    {
        EnsureSafeLabId(labId);
        return $"{Defaults.LabsDirectoryName}/{labId}";
    }

    /// <summary>Records that the lab was opened now; the PC count is what the chooser shows while the lab is closed.</summary>
    public void Touch(string labId, DateTimeOffset now, int pcCount)
    {
        var record = Find(labId) ?? throw new InvalidDataException($"Lab {labId} is not in '{Defaults.ProfilesFileName}'.");
        record.LastUsedUnix = now.ToUnixTimeSeconds();
        record.PcCount = pcCount;
        _document.LastUsedLabId = record.LabId;
        Save();
    }

    /// <summary>Adds or replaces the entry for the record's lab id and saves.</summary>
    public void Upsert(ProfileRecord record)
    {
        EnsureSafeLabId(record.LabId);
        if (string.IsNullOrEmpty(record.Directory))
        {
            record.Directory = RelativeDirectoryFor(record.LabId);
        }

        var index = _document.Labs.FindIndex(p => string.Equals(p.LabId, record.LabId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _document.Labs.Add(record);
        }
        else
        {
            _document.Labs[index] = record;
        }

        Save();
    }

    /// <summary>
    /// Forgets a lab on this device. With <paramref name="deleteData"/> the lab directory goes
    /// too, and the instance key is removed from the OS keystore first: deleting
    /// <c>instance.json</c> alone would leave a <c>instance-&lt;id&gt;</c> item behind in the
    /// Keychain or keyring. Nothing here touches a student PC or another console.
    /// </summary>
    public void Remove(string labId, bool deleteData)
    {
        var record = Find(labId);
        if (record is null)
        {
            return;
        }

        if (deleteData)
        {
            var directory = Directory(labId);
            var store = new LabStore(directory);
            InstanceDocument? instance = null;
            try
            {
                instance = store.LoadInstance();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SchemaVersionException)
            {
                // An unreadable instance document has no key reference to forget.
            }

            if (instance is not null)
            {
                try
                {
                    _protectors(instance.PrivateKey).Forget(instance.PrivateKey);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                               or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The Keychain, DPAPI or secret-tool refused; an item nobody can open is
                    // inert once its document is gone, so the lab is still removed.
                    _log.LogWarning(ex, "The instance key '{Reference}' of lab {LabId} could not be removed from the keystore; the lab is removed anyway",
                        instance.PrivateKey.Reference, labId);
                }
            }

            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }

        _document.Labs.Remove(record);
        if (string.Equals(_document.LastUsedLabId, record.LabId, StringComparison.OrdinalIgnoreCase))
        {
            _document.LastUsedLabId = null;
        }

        Save();
    }

    /// <summary>Windows paths differ in case only on paper; everywhere else case is identity.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// A lab id names a directory, so it must be a plain UUID-like token: no separators,
    /// no dots, nothing a path could interpret. Lab ids are minted as GUIDs; anything else
    /// in the index is corruption, not input.
    /// </summary>
    private static void EnsureSafeLabId(string labId)
    {
        if (string.IsNullOrWhiteSpace(labId) || labId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new InvalidDataException($"'{labId}' is not a lab id.");
        }
    }
}
