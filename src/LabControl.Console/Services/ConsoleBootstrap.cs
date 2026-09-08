using LabControl.Console.Localization;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>Whether the backup on record still matches the lab key on disk (D-25).</summary>
public enum BackupStatus
{
    /// <summary>No backup has ever been exported from this machine.</summary>
    Missing = 0,

    /// <summary>A backup exists but <c>lab-key.lck</c> has changed since: a holder or the recovery code.</summary>
    Stale = 1,

    Current = 2,
}

/// <summary>
/// Everything that happens before the main window: creating a lab on first run, importing
/// one from a backup (ARCHITECTURE §3.6), reopening an existing one, re-minting a console
/// leaf that is close to expiry (§3.8), and the backup bookkeeping. Pure orchestration over
/// the Shared building blocks; the wizard is only its face.
/// <para>
/// Since M5 (D-55) a device holds several labs, each in its own directory under
/// <c>labs/</c> and listed in <c>profiles.json</c>: every operation names a lab id and
/// works on that lab's <see cref="LabStore"/>. The bootstrap keeps no current lab itself.
/// </para>
/// </summary>
public sealed class ConsoleBootstrap
{
    private readonly ConsoleOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Func<ISecretProtector> _newProtector;

    /// <param name="newProtector">
    /// The keystore a freshly minted instance key is written with; the platform's own by
    /// default. Tests inject one that refuses, to check an import leaves nothing behind.
    /// </param>
    public ConsoleBootstrap(ConsoleOptions options, ILoggerFactory loggers, Func<ISecretProtector>? newProtector = null)
    {
        _options = options;
        _loggers = loggers;
        _log = loggers.CreateLogger<ConsoleBootstrap>();
        _newProtector = newProtector ?? SecretProtector.ForCurrentPlatform;
        Profiles = new ProfileStore(options.DataDirectory, loggers.CreateLogger<ProfileStore>());
    }

    /// <summary>The index of the labs saved on this device.</summary>
    public ProfileStore Profiles { get; }

    /// <summary>True when at least one saved lab can be opened on this device.</summary>
    public bool HasLab => Profiles.Profiles.Any(profile => StoreFor(profile.LabId).HasLab);

    /// <summary>The lab's own directory, whether or not it exists yet.</summary>
    public LabStore StoreFor(string labId) => new(Profiles.Directory(labId));

    /// <summary>This machine's name as the default console instance name; "Console" when the OS has none.</summary>
    public static string DefaultInstanceName()
    {
        var host = Environment.MachineName;
        return host.Length > 0 ? host : "Console";
    }

    /// <summary>
    /// The lab this device opens without asking: the last used one; failing that mark, the
    /// one used most recently, then the one added most recently. <c>null</c> when nothing is
    /// saved. The chooser (M5 portion 2) lets the teacher pick another; nobody edits the index.
    /// </summary>
    public string? DefaultLabId
    {
        get
        {
            var last = Profiles.LastUsedLabId;
            if (!string.IsNullOrEmpty(last) && Profiles.Find(last) is not null)
            {
                return last;
            }

            return Profiles.Profiles
                .OrderByDescending(profile => profile.LastUsedUnix)
                .ThenByDescending(profile => profile.AddedAtUnix)
                .FirstOrDefault()?.LabId;
        }
    }

    // ------------------------------------------------------------------ first run

    /// <summary>A brand-new lab: key, first holder, recovery code, this machine's instance, its own directory and index entry.</summary>
    public LabSession CreateLab(string labName, string holderName, string passphrase, string instanceName, out RecoveryCode recoveryCode)
    {
        var lab = LabKey.Create(labName, holderName, passphrase, out recoveryCode);
        var store = StoreFor(lab.LabId);
        store.EnsureDirectories();
        store.SaveLabKey(lab.Document);

        var instance = ConsoleInstance.Mint(lab, instanceName, _newProtector());
        store.SaveInstance(instance.Document);
        store.SaveLab(new LabDocument { LabId = lab.LabId, LabName = lab.LabName });

        Record(lab, instance.Document, ProfileSource.Created, pcCount: 0);

        var vault = new LabKeyVault(store, lab.Document);
        vault.Adopt(lab);

        return NewSession(store, vault, instance);
    }

    /// <summary>Reads a backup far enough to show its lab name and holders before asking for a secret.</summary>
    public static BackupDocument ReadBackup(string path) =>
        LabBackup.Parse(File.ReadAllText(path), Path.GetFileName(path));

    /// <summary>
    /// Imports a lab from a backup (ARCHITECTURE §3.6 steps 1–3) into a new lab directory:
    /// opens the key with a passphrase or the recovery code, restores the machine list,
    /// revocations, layout and catalog, mints this machine's own instance and records the
    /// backup as current. A lab that is already saved on this device is refused rather than
    /// overwritten; upgrading a saved lab from a backup is M5 portion 3.
    /// </summary>
    public LabSession ImportBackup(BackupDocument backup, string? passphrase, RecoveryCode? recoveryCode, string instanceName)
    {
        var imported = ImportBackupProfile(backup, passphrase, recoveryCode, instanceName);
        var store = StoreFor(imported.LabId);
        var vault = new LabKeyVault(store, imported.Key.Document);
        vault.Adopt(imported.Key);
        return NewSession(store, vault, imported.Instance);
    }

    /// <summary>
    /// The profile half of <see cref="ImportBackup"/> (M5 portion 2): everything is written
    /// and indexed, nothing is started. The chooser's <i>Add labs…</i> uses it so a batch of
    /// backups lands as saved labs and nothing activates. The caller disposes the result.
    /// </summary>
    public ImportedLab ImportBackupProfile(BackupDocument backup, string? passphrase, RecoveryCode? recoveryCode, string instanceName)
    {
        LabKey lab;
        var opened = recoveryCode is not null
            ? LabKey.TryUnlock(backup.LabKey, recoveryCode, out lab)
            : LabKey.TryUnlock(backup.LabKey, passphrase ?? string.Empty, out lab);

        if (!opened)
        {
            throw new UnauthorizedAccessException(Strings.Get(recoveryCode is not null ? "Bootstrap.WrongRecoveryCode" : "Bootstrap.WrongPassphrase"));
        }

        // Atomic per file (M5 portion 2 review): the directory this import creates and the
        // instance key it mints are both undone when anything after them fails, so a refused
        // keystore or a full disk never leaves labs/<id>/lab-key.lck without an index entry
        // (which would refuse every later import as "occupied" and hide the lab from Remove)
        // or an orphaned item in the keystore.
        var createdDirectory = false;
        string? directory = null;
        ConsoleInstance? instance = null;
        ISecretProtector? protector = null;
        var recording = false;
        try
        {
            var payload = LabBackup.Open(backup, lab);

            var existing = Profiles.Find(lab.LabId);
            var store = StoreFor(lab.LabId);
            if (existing is not null)
            {
                throw new InvalidDataException(Strings.Format("Bootstrap.LabAlreadySaved", existing.LabName));
            }

            if (Directory.Exists(store.Directory))
            {
                if (store.HasLabKey || File.Exists(store.InstancePath))
                {
                    // Files of this lab that the index does not list: a directory someone put back
                    // by hand, or an index that was replaced. Not ours to overwrite.
                    throw new InvalidDataException(Strings.Format("Bootstrap.LabDirectoryOccupied", store.Directory, lab.LabName, Defaults.ProfilesFileName));
                }

                // Neither the key nor an instance: a leftover — an import that died before it wrote
                // anything that matters, or a removal that did not finish. It holds no identity.
                _log.LogWarning("Removing the leftover directory {Directory}: it holds no key and no instance and is not in {Index}", store.Directory, Defaults.ProfilesFileName);
                Directory.Delete(store.Directory, recursive: true);
            }

            directory = store.Directory;
            store.EnsureDirectories();
            createdDirectory = true;
            store.SaveLabKey(lab.Document);

            // The machine list and layout come from the backup; the instance list keeps the
            // other teacher machines it knew, and this one is added when the session starts.
            payload.Lab.LabId = lab.LabId;
            payload.Lab.LabName = lab.LabName;
            foreach (var record in payload.Lab.Instances)
            {
                record.IsThisMachine = false;
            }

            store.SaveLab(payload.Lab);
            store.WriteCatalog(payload.Catalog);
            if (payload.Scripts is not null)
            {
                // The library moves with the lab (D-31 item 4); the seed is imported only when nothing came.
                payload.Scripts.LabId = lab.LabId;
                store.SaveScripts(payload.Scripts);
            }

            if (payload.Enrollment is not null)
            {
                // The codes on sticks written by the old machine keep working here (D-28).
                payload.Enrollment.LabId = lab.LabId;
                store.SaveEnrollment(payload.Enrollment);
            }

            protector = _newProtector();
            instance = ConsoleInstance.Mint(lab, instanceName, protector);
            instance.Document.BackupExportedAtUnix = backup.ExportedAtUnix;
            instance.Document.BackupLocation = "imported from a backup";
            instance.Document.BackupFingerprint = JsonStore.Fingerprint(JsonStore.Serialize(lab.Document, LabKeyDocument.Migrations));
            instance.Document.RecoveryCodeAcknowledged = true;
            store.SaveInstance(instance.Document);

            recording = true;
            Record(lab, instance.Document, ProfileSource.Backup, payload.Lab.Machines.Count);

            var imported = new ImportedLab(lab.LabId, lab.LabName, payload.Lab.Machines.Count, lab, instance);
            instance = null;
            return imported;
        }
        catch (Exception ex)
        {
            if (recording)
            {
                // The index entry was added in memory and its write failed: what is on disk is
                // the truth, so the index is re-read and the unsaved entry is gone with it —
                // otherwise the same file would be refused as "already saved" until a restart.
                try
                {
                    Profiles.Load();
                }
                catch (Exception reload) when (reload is IOException or InvalidDataException or SchemaVersionException or UnauthorizedAccessException)
                {
                    _log.LogWarning(reload, "The lab index could not be re-read after the failed import of lab {LabId}", lab.LabId);
                }
            }

            if (instance is not null)
            {
                // The minted key was stored before the failure: take it back out of the same
                // keystore that holds it (the injected one, in tests), not one looked up by name.
                try
                {
                    (protector ?? SecretProtector.For(instance.Document.PrivateKey)).Forget(instance.Document.PrivateKey);
                }
                catch (Exception forget) when (forget is InvalidDataException or IOException or InvalidOperationException)
                {
                    _log.LogWarning(forget, "The instance key minted by the failed import of lab {LabId} could not be removed from the keystore", lab.LabId);
                }

                instance.Dispose();
            }

            if (createdDirectory && directory is not null)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception delete) when (delete is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(delete, "The directory written by the failed import of lab {LabId} could not be removed: {Directory}", lab.LabId, directory);
                }
            }

            _log.LogWarning(ex, "Import of lab {LabId} failed; nothing of it is kept", lab.LabId);
            lab.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ every later run

    /// <summary>Reopens <see cref="DefaultLabId"/> — today's single-lab behaviour.</summary>
    public OpenedLab OpenExisting()
    {
        var labId = DefaultLabId ?? throw new InvalidDataException(Strings.Format("App.NoLabSaved", Profiles.DataDirectory));
        return OpenExisting(labId);
    }

    /// <summary>
    /// Reopens a saved lab on this machine. The key stays locked; the instance leaf is checked
    /// for expiry and <see cref="OpenedLab.NeedsRemint"/> tells the caller to ask for a passphrase.
    /// </summary>
    public OpenedLab OpenExisting(string labId)
    {
        if (Profiles.Find(labId) is null)
        {
            throw new InvalidDataException(Strings.Format("Bootstrap.LabNotSaved", labId));
        }

        var store = StoreFor(labId);
        // packages/ and logs/ may be missing from a lab that never had them; every writer expects them.
        store.EnsureDirectories();
        var keyDocument = store.LoadLabKey();
        var instanceDocument = store.LoadInstance()
                               ?? throw new InvalidDataException(Strings.Format("Bootstrap.InstanceMissing", Defaults.InstanceFileName));

        if (!string.Equals(keyDocument.LabId, instanceDocument.LabId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Strings.Format("Bootstrap.KeyInstanceMismatch",
                Defaults.InstanceFileName, instanceDocument.LabId, Defaults.LabKeyFileName, keyDocument.LabId));
        }

        var instance = ConsoleInstance.Open(instanceDocument);
        var vault = new LabKeyVault(store, keyDocument);

        return new OpenedLab(store, vault, instance, instanceDocument, instance.NeedsRemint(DateTimeOffset.UtcNow));
    }

    /// <summary>Replaces an expiring console leaf (§3.8); the vault must be unlocked.</summary>
    public ConsoleInstance Remint(LabKeyVault vault, InstanceDocument existing)
    {
        if (!vault.Use(lab => ConsoleInstance.Remint(lab, existing, SecretProtector.ForCurrentPlatform()), out var instance))
        {
            throw new InvalidOperationException("The lab key is locked.");
        }

        StoreFor(existing.LabId).SaveInstance(instance.Document);
        return instance;
    }

    /// <summary>Builds the session for an opened lab and marks the lab as last used.</summary>
    public LabSession Start(OpenedLab opened, ConsoleInstance instance)
    {
        var session = Build(opened, instance);
        Profiles.Touch(session.LabId, session.Now, session.Registry.Document.Machines.Count);
        return session;
    }

    /// <summary>
    /// Builds the session for an opened lab without touching the index: the controller marks
    /// the lab as used only once the session actually serves (M5, D-57 item 1).
    /// </summary>
    public LabSession Build(OpenedLab opened, ConsoleInstance instance) => NewSession(opened.Store, opened.Vault, instance);

    private LabSession NewSession(LabStore store, LabKeyVault vault, ConsoleInstance instance) =>
        new(_options, store, vault, instance, instance.Document, _loggers, seedScripts: SeedScripts.Embedded())
        {
            // A PC of another saved lab is refused by name (D-57 item 2); the index is the only source.
            LabNameResolver = labId => Profiles.Find(labId)?.LabName,
        };

    // ------------------------------------------------------------------ backup

    public BackupStatus CheckBackup(InstanceDocument instance)
    {
        if (instance.BackupExportedAtUnix == 0 || string.IsNullOrEmpty(instance.BackupFingerprint))
        {
            return BackupStatus.Missing;
        }

        var store = StoreFor(instance.LabId);
        if (!string.Equals(instance.BackupFingerprint, store.LabKeyFingerprint(), StringComparison.Ordinal))
        {
            return BackupStatus.Stale;
        }

        // A stick written after the last backup holds codes only this machine knows; a
        // replacement console restored from that backup would refuse every PC installed from
        // the stick (D-28). So the backup is stale until it is exported again.
        var codesWrittenSince = store.LoadEnrollment(instance.LabId).Codes
            .Any(code => code.IsUsable && code.CreatedAtUnix > instance.BackupExportedAtUnix);

        return codesWrittenSince ? BackupStatus.Stale : BackupStatus.Current;
    }

    /// <summary>
    /// True when the first-run wizard was closed before its last two steps (ARCHITECTURE §3.7):
    /// the lab exists on disk, but the recovery code was never acknowledged or no backup was
    /// ever exported. The next launch resumes the wizard instead of opening the main window.
    /// </summary>
    public bool SetupIsUnfinished(InstanceDocument instance) =>
        !instance.RecoveryCodeAcknowledged || CheckBackup(instance) == BackupStatus.Missing;

    /// <summary>Writes the archive and records it as the current backup on this machine. The vault must be unlocked.</summary>
    public bool TryExportBackup(LabSession session, string path, out string error)
    {
        var now = session.Now;
        var store = session.Store;
        session.SaveLab();

        if (!session.Vault.Use(lab => LabBackup.Serialize(LabBackup.Export(lab, session.Registry.Document, store.ReadCatalog(), session.Instance.InstanceName, now, session.Enrollment.Document, session.Scripts.Document)), out var json))
        {
            error = Strings.Get("Bootstrap.KeyLocked");
            return false;
        }

        try
        {
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = Strings.Format("Bootstrap.BackupWriteFailed", ex.Message);
            return false;
        }

        session.InstanceDocument.BackupExportedAtUnix = now.ToUnixTimeSeconds();
        session.InstanceDocument.BackupLocation = path;
        session.InstanceDocument.BackupFingerprint = store.LabKeyFingerprint();
        store.SaveInstance(session.InstanceDocument);
        session.Events.Info("backup.exported", $"Backup exported to {path}.");

        error = string.Empty;
        return true;
    }

    public void SaveInstance(InstanceDocument document) => StoreFor(document.LabId).SaveInstance(document);

    // ------------------------------------------------------------------ index

    /// <summary>An administrator entry for a lab this device holds the key of.</summary>
    private void Record(LabKey lab, InstanceDocument instance, ProfileSource source, int pcCount)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Profiles.Upsert(new ProfileRecord
        {
            LabId = lab.LabId,
            LabName = lab.LabName,
            AuthorityFingerprint = ProfileRecord.AuthorityFingerprintOf(lab.Document.Authority),
            Access = ProfileAccess.Administrator,
            Authorization = ProfileAuthorization.Authorized,
            InstanceId = instance.InstanceId,
            InstanceName = instance.InstanceName,
            PcCount = pcCount,
            AddedAtUnix = now,
            LastUsedUnix = now,
            Source = source,
        });
        Profiles.LastUsedLabId = lab.LabId;
        Profiles.Save();
    }
}

public sealed record OpenedLab(LabStore Store, LabKeyVault Vault, ConsoleInstance Instance, InstanceDocument Document, bool NeedsRemint);

/// <summary>A lab just written from a backup (M5): its identity, the unlocked key and the minted instance, both owned by the holder.</summary>
public sealed record ImportedLab(string LabId, string LabName, int PcCount, LabKey Key, ConsoleInstance Instance) : IDisposable
{
    public void Dispose()
    {
        Key.Dispose();
        Instance.Dispose();
    }
}
