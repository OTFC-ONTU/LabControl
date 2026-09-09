using LabControl.Console.Localization;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>What opens a backup: a holder's passphrase or the recovery code, never both.</summary>
public sealed record BackupSecret(string? Passphrase, RecoveryCode? RecoveryCode);

/// <summary>One row of the import results dialog (M5 §5): the file, whether it became a saved lab, and why not.</summary>
public sealed record ImportFileResult(string Path, bool Ok, string Message, string? LabName)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>A file-picker filter: a label and its <c>*.ext</c> patterns.</summary>
public sealed record FileFilter(string Label, IReadOnlyList<string> Patterns);

/// <summary>
/// <i>Add labs…</i> and drag-and-drop (M5 §5): a batch of files, one result per file, and
/// nothing activated afterwards — the teacher picks a lab from the list. Each extension
/// has its own handler; portion 2 knows <c>.lcbak</c> and portion 3 registers the lab file,
/// device grant and device request handlers through <see cref="Register"/>.
/// </summary>
public sealed class LabImports
{
    private readonly ConsoleBootstrap _bootstrap;
    private readonly Func<BackupDocument, Task<BackupSecret?>> _unlock;
    private readonly Func<string, Task<BackupSecret?>>? _unlockKey;
    private readonly Func<string, LabSession?> _active;
    private readonly string _instanceName;
    private readonly ILogger _log;
    private readonly Dictionary<string, Func<string, Task<ImportFileResult>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileFilter> _filters = [];

    /// <param name="unlock">Asks the teacher for the secret that opens a backup; <c>null</c> skips that file.</param>
    /// <param name="instanceName">The name this device's new instance gets in every imported lab.</param>
    /// <param name="active">The running session for a lab id, if that lab is active: imports into it go through the session.</param>
    /// <param name="unlockKey">
    /// Asks for the secret that unlocks a saved lab's key (the reason names the request); used
    /// to approve a <c>.lcreq</c> when its lab is not active. <c>null</c> refuses requests.
    /// </param>
    public LabImports(ConsoleBootstrap bootstrap, Func<BackupDocument, Task<BackupSecret?>> unlock, string instanceName, ILogger? log = null,
        Func<string, LabSession?>? active = null, Func<string, Task<BackupSecret?>>? unlockKey = null)
    {
        _bootstrap = bootstrap;
        _unlock = unlock;
        _unlockKey = unlockKey;
        _active = active ?? (_ => null);
        _instanceName = instanceName;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Devices = new DeviceAccess(bootstrap, _active, _log);
        Register(Defaults.BackupFileExtension, Strings.Get("Backup.FileType"), ImportBackupAsync);
        Register(Defaults.LabFileExtension, Strings.Get("LabFile.FileType"), path => Task.Run(() => Devices.ImportLabFile(path, _instanceName)));
        Register(Defaults.DeviceGrantFileExtension, Strings.Get("Grant.FileType"), path => Task.Run(() => Devices.ImportGrant(path)));
        Register(Defaults.DeviceRequestFileExtension, Strings.Get("Request.FileType"), ImportRequestAsync);
    }

    /// <summary>The lab-file and device-authorization operations behind the handlers (M5, D-56).</summary>
    public DeviceAccess Devices { get; }

    /// <summary>
    /// The picker filters (M5 §5): one combined filter first, then one per known extension in
    /// registration order. The combined filter is what makes a mixed selection possible at
    /// all — both the macOS panel and the Windows common dialog apply exactly one filter at a
    /// time, so a picker offered only per-type filters can never return a <c>.lclab</c> and a
    /// <c>.lcbak</c> in the same batch, however many files the teacher selects.
    /// </summary>
    public IReadOnlyList<FileFilter> Filters =>
        [new FileFilter(Strings.Get("Import.AllDocuments"), AllPatterns()), .. _filters];

    /// <summary>
    /// Every pattern the combined filter carries: the four document types of
    /// <see cref="Defaults.ConsoleDocumentExtensions"/> first, in their order, and then
    /// anything a later <see cref="Register"/> added that is not among them.
    /// </summary>
    private IReadOnlyList<string> AllPatterns()
    {
        var patterns = new List<string>();
        foreach (var extension in Defaults.ConsoleDocumentExtensions)
        {
            if (_handlers.ContainsKey(extension))
            {
                patterns.Add("*" + extension);
            }
        }

        foreach (var filter in _filters)
        {
            foreach (var pattern in filter.Patterns)
            {
                if (!patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                {
                    patterns.Add(pattern);
                }
            }
        }

        return patterns;
    }

    public IReadOnlyCollection<string> KnownExtensions => _handlers.Keys;

    /// <summary>Adds (or replaces) the handler for one extension — the extension point for portion 3's lab files.</summary>
    public void Register(string extension, string label, Func<string, Task<ImportFileResult>> handler)
    {
        _handlers[extension] = handler;
        _filters.RemoveAll(f => f.Patterns.Contains("*" + extension, StringComparer.OrdinalIgnoreCase));
        _filters.Add(new FileFilter(label, ["*" + extension]));
    }

    /// <summary>
    /// Imports every file in turn and reports each one. A file that fails — wrong passphrase,
    /// unreadable, already saved — fails alone; the others still land.
    /// </summary>
    public async Task<IReadOnlyList<ImportFileResult>> ImportAsync(IEnumerable<string> paths)
    {
        var results = new List<ImportFileResult>();
        foreach (var path in paths)
        {
            var extension = Path.GetExtension(path);
            if (!_handlers.TryGetValue(extension, out var handler))
            {
                results.Add(new ImportFileResult(path, false, Strings.Format("Import.Unsupported", extension.Length == 0 ? "?" : extension), null));
                continue;
            }

            try
            {
                results.Add(await handler(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SchemaVersionException or InvalidOperationException
                                           or System.Security.Cryptography.CryptographicException or ArgumentException or System.Formats.Asn1.AsnContentException)
            {
                // InvalidOperationException is the keystore refusing to hold the new instance
                // key (Keychain, secret-tool); ArgumentException, CryptographicException and
                // AsnContentException are a hostile or damaged file's certificate, CSR or
                // subject: that file fails alone, like a wrong passphrase.
                results.Add(new ImportFileResult(path, false, ex.Message, null));
            }
        }

        foreach (var result in results)
        {
            _log.LogInformation("Import of {File}: {Outcome} — {Message}", result.FileName, result.Ok ? "added" : "not added", result.Message);
        }

        return results;
    }

    private async Task<ImportFileResult> ImportBackupAsync(string path)
    {
        // Off the UI thread: reading and parsing is a file the console did not write, and the
        // window may not freeze while a slow disk (or a stick pulled mid-read) answers.
        var backup = await Task.Run(() => ConsoleBootstrap.ReadBackup(path));

        // An administrator profile is refused before asking for a secret; a teacher profile
        // is upgraded from the backup (D-56 item 3), keeping its instance and history.
        var existing = _bootstrap.Profiles.Find(backup.LabKey.LabId);
        if (existing is { Access: ProfileAccess.Administrator })
        {
            return new ImportFileResult(path, false, Strings.Format("Bootstrap.LabAlreadySaved", existing.LabName), existing.LabName);
        }

        var secret = await _unlock(backup);
        if (secret is null)
        {
            return new ImportFileResult(path, false, Strings.Get("Import.Cancelled"), backup.LabName);
        }

        // PBKDF2 takes a moment; the caller is the UI thread.
        if (existing is not null)
        {
            var active = _active(existing.LabId);
            using var upgraded = await Task.Run(() => _bootstrap.UpgradeFromBackup(backup, secret.Passphrase, secret.RecoveryCode, _instanceName, active));
            return new ImportFileResult(path, true, Strings.Format("Import.Upgraded", upgraded.LabName), upgraded.LabName);
        }

        using var imported = await Task.Run(() => _bootstrap.ImportBackupProfile(backup, secret.Passphrase, secret.RecoveryCode, _instanceName));
        return new ImportFileResult(path, true, Strings.Format("Import.Added", imported.LabName, imported.PcCount), imported.LabName);
    }

    /// <summary>
    /// A request is approved only where the lab's key is (D-56 item 4): through the running
    /// session's vault when that lab is active, otherwise by unlocking the saved key once.
    /// </summary>
    private async Task<ImportFileResult> ImportRequestAsync(string path)
    {
        var request = await Task.Run(() => DeviceAccess.ReadRequest(path));
        var profile = _bootstrap.Profiles.Find(request.LabId);
        if (profile is null || !Devices.CanApprove(request.LabId))
        {
            return new ImportFileResult(path, false, Strings.Format("Import.RequestNoAdministrator", request.LabName), request.LabName);
        }

        // The profile's own name from here on: the file's outer lab_name is unverified text.
        var labName = profile.LabName;
        var active = _active(request.LabId);
        if (active is { IsDisposed: false, Vault: { } vault })
        {
            if (!vault.IsUnlocked)
            {
                var secret = _unlockKey is null ? null : await _unlockKey(Strings.Format("Import.UnlockForRequest", labName, Path.GetFileName(path)));
                if (secret is null)
                {
                    return new ImportFileResult(path, false, Strings.Get("Import.Cancelled"), labName);
                }

                var opened = await Task.Run(() => secret.RecoveryCode is not null ? vault.TryUnlock(secret.RecoveryCode) : vault.TryUnlock(secret.Passphrase ?? string.Empty));
                if (!opened)
                {
                    return new ImportFileResult(path, false, Strings.Get(secret.RecoveryCode is not null ? "Bootstrap.WrongRecoveryCode" : "Bootstrap.WrongPassphrase"), labName);
                }
            }

            if (!vault.Use(lab => Devices.ApproveRequest(path, lab), out var approved))
            {
                return new ImportFileResult(path, false, Strings.Get("Bootstrap.KeyLocked"), labName);
            }

            return new ImportFileResult(path, true, Strings.Format("Import.RequestApproved", approved.InstanceName, labName, approved.GrantPath), labName);
        }

        var keyDocument = _bootstrap.StoreFor(request.LabId).LoadLabKey();
        var answer = _unlockKey is null ? null : await _unlockKey(Strings.Format("Import.UnlockForRequest", labName, Path.GetFileName(path)));
        if (answer is null)
        {
            return new ImportFileResult(path, false, Strings.Get("Import.Cancelled"), labName);
        }

        return await Task.Run(() =>
        {
            LabKey lab;
            var unlocked = answer.RecoveryCode is not null
                ? LabKey.TryUnlock(keyDocument, answer.RecoveryCode, out lab)
                : LabKey.TryUnlock(keyDocument, answer.Passphrase ?? string.Empty, out lab);
            if (!unlocked)
            {
                return new ImportFileResult(path, false, Strings.Get(answer.RecoveryCode is not null ? "Bootstrap.WrongRecoveryCode" : "Bootstrap.WrongPassphrase"), labName);
            }

            using (lab)
            {
                var approved = Devices.ApproveRequest(path, lab);
                return new ImportFileResult(path, true, Strings.Format("Import.RequestApproved", approved.InstanceName, labName, approved.GrantPath), labName);
            }
        });
    }
}
