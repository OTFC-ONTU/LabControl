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
/// </summary>
public sealed class ConsoleBootstrap
{
    private readonly ConsoleOptions _options;
    private readonly ILoggerFactory _loggers;

    public ConsoleBootstrap(ConsoleOptions options, ILoggerFactory loggers)
    {
        _options = options;
        _loggers = loggers;
        Store = new LabStore(options.DataDirectory);
    }

    public LabStore Store { get; }

    public bool HasLab => Store.HasLab;

    // ------------------------------------------------------------------ first run

    /// <summary>A brand-new lab: key, first holder, recovery code, this machine's instance.</summary>
    public LabSession CreateLab(string labName, string holderName, string passphrase, string instanceName, out RecoveryCode recoveryCode)
    {
        Store.EnsureDirectories();

        var lab = LabKey.Create(labName, holderName, passphrase, out recoveryCode);
        Store.SaveLabKey(lab.Document);

        var instance = ConsoleInstance.Mint(lab, instanceName, SecretProtector.ForCurrentPlatform());
        Store.SaveInstance(instance.Document);
        Store.SaveLab(new LabDocument { LabId = lab.LabId, LabName = lab.LabName });

        var vault = new LabKeyVault(Store, lab.Document);
        vault.Adopt(lab);

        return new LabSession(_options, Store, vault, instance, instance.Document, _loggers);
    }

    /// <summary>Reads a backup far enough to show its lab name and holders before asking for a secret.</summary>
    public static BackupDocument ReadBackup(string path) =>
        LabBackup.Parse(File.ReadAllText(path), Path.GetFileName(path));

    /// <summary>
    /// Imports a lab from a backup (ARCHITECTURE §3.6 steps 1–3): opens the key with a
    /// passphrase or the recovery code, restores the machine list, revocations, layout and
    /// catalog, mints this machine's own instance and records the backup as current.
    /// </summary>
    public LabSession ImportBackup(BackupDocument backup, string? passphrase, RecoveryCode? recoveryCode, string instanceName)
    {
        LabKey lab;
        var opened = recoveryCode is not null
            ? LabKey.TryUnlock(backup.LabKey, recoveryCode, out lab)
            : LabKey.TryUnlock(backup.LabKey, passphrase ?? string.Empty, out lab);

        if (!opened)
        {
            throw new UnauthorizedAccessException(recoveryCode is not null
                ? "The recovery code does not open this backup."
                : "The passphrase does not open this backup.");
        }

        var payload = LabBackup.Open(backup, lab);

        Store.EnsureDirectories();
        Store.SaveLabKey(lab.Document);

        // The machine list and layout come from the backup; the instance list keeps the
        // other teacher machines it knew, and this one is added when the session starts.
        payload.Lab.LabId = lab.LabId;
        payload.Lab.LabName = lab.LabName;
        foreach (var record in payload.Lab.Instances)
        {
            record.IsThisMachine = false;
        }

        Store.SaveLab(payload.Lab);
        Store.WriteCatalog(payload.Catalog);
        if (payload.Enrollment is not null)
        {
            // The codes on sticks written by the old machine keep working here (D-28).
            payload.Enrollment.LabId = lab.LabId;
            Store.SaveEnrollment(payload.Enrollment);
        }

        var instance = ConsoleInstance.Mint(lab, instanceName, SecretProtector.ForCurrentPlatform());
        instance.Document.BackupExportedAtUnix = backup.ExportedAtUnix;
        instance.Document.BackupLocation = "imported from a backup";
        instance.Document.BackupFingerprint = JsonStore.Fingerprint(JsonStore.Serialize(lab.Document, LabKeyDocument.Migrations));
        instance.Document.RecoveryCodeAcknowledged = true;
        Store.SaveInstance(instance.Document);

        var vault = new LabKeyVault(Store, lab.Document);
        vault.Adopt(lab);

        return new LabSession(_options, Store, vault, instance, instance.Document, _loggers);
    }

    // ------------------------------------------------------------------ every later run

    /// <summary>
    /// Reopens the lab on this machine. The key stays locked; the instance leaf is checked
    /// for expiry and <see cref="NeedsRemint"/> tells the caller to ask for a passphrase.
    /// </summary>
    public OpenedLab OpenExisting()
    {
        var keyDocument = Store.LoadLabKey();
        var instanceDocument = Store.LoadInstance()
                               ?? throw new InvalidDataException($"'{Defaults.InstanceFileName}' is missing; import the lab key to mint a new console instance.");

        if (!string.Equals(keyDocument.LabId, instanceDocument.LabId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"'{Defaults.InstanceFileName}' belongs to lab {instanceDocument.LabId} but '{Defaults.LabKeyFileName}' is lab {keyDocument.LabId}.");
        }

        var instance = ConsoleInstance.Open(instanceDocument);
        var vault = new LabKeyVault(Store, keyDocument);

        return new OpenedLab(vault, instance, instanceDocument, instance.NeedsRemint(DateTimeOffset.UtcNow));
    }

    /// <summary>Replaces an expiring console leaf (§3.8); the vault must be unlocked.</summary>
    public ConsoleInstance Remint(LabKeyVault vault, InstanceDocument existing)
    {
        if (!vault.Use(lab => ConsoleInstance.Remint(lab, existing, SecretProtector.ForCurrentPlatform()), out var instance))
        {
            throw new InvalidOperationException("The lab key is locked.");
        }

        Store.SaveInstance(instance.Document);
        return instance;
    }

    public LabSession Start(OpenedLab opened, ConsoleInstance instance) =>
        new(_options, Store, opened.Vault, instance, instance.Document, _loggers);

    // ------------------------------------------------------------------ backup

    public BackupStatus CheckBackup(InstanceDocument instance)
    {
        if (instance.BackupExportedAtUnix == 0 || string.IsNullOrEmpty(instance.BackupFingerprint))
        {
            return BackupStatus.Missing;
        }

        if (!string.Equals(instance.BackupFingerprint, Store.LabKeyFingerprint(), StringComparison.Ordinal))
        {
            return BackupStatus.Stale;
        }

        // A stick written after the last backup holds codes only this machine knows; a
        // replacement console restored from that backup would refuse every PC installed from
        // the stick (D-28). So the backup is stale until it is exported again.
        var codesWrittenSince = Store.LoadEnrollment(instance.LabId).Codes
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
        session.SaveLab();

        if (!session.Vault.Use(lab => LabBackup.Serialize(LabBackup.Export(lab, session.Registry.Document, Store.ReadCatalog(), session.Instance.InstanceName, now, session.Enrollment.Document)), out var json))
        {
            error = "The lab key is locked; unlock it to export a backup.";
            return false;
        }

        try
        {
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not write the backup: {ex.Message}";
            return false;
        }

        session.InstanceDocument.BackupExportedAtUnix = now.ToUnixTimeSeconds();
        session.InstanceDocument.BackupLocation = path;
        session.InstanceDocument.BackupFingerprint = Store.LabKeyFingerprint();
        Store.SaveInstance(session.InstanceDocument);
        session.Events.Info("backup.exported", $"Backup exported to {path}.");

        error = string.Empty;
        return true;
    }

    public void SaveInstance(InstanceDocument document) => Store.SaveInstance(document);
}

public sealed record OpenedLab(LabKeyVault Vault, ConsoleInstance Instance, InstanceDocument Document, bool NeedsRemint);
