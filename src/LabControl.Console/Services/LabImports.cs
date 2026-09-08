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
    private readonly string _instanceName;
    private readonly ILogger _log;
    private readonly Dictionary<string, Func<string, Task<ImportFileResult>>> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileFilter> _filters = [];

    /// <param name="unlock">Asks the teacher for the secret that opens a backup; <c>null</c> skips that file.</param>
    /// <param name="instanceName">The name this device's new instance gets in every imported lab.</param>
    public LabImports(ConsoleBootstrap bootstrap, Func<BackupDocument, Task<BackupSecret?>> unlock, string instanceName, ILogger? log = null)
    {
        _bootstrap = bootstrap;
        _unlock = unlock;
        _instanceName = instanceName;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Register(Defaults.BackupFileExtension, Strings.Get("Backup.FileType"), ImportBackupAsync);
    }

    /// <summary>The picker filters, one per known extension, in registration order.</summary>
    public IReadOnlyList<FileFilter> Filters => _filters;

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SchemaVersionException or InvalidOperationException)
            {
                // InvalidOperationException is the keystore refusing to hold the new instance
                // key (Keychain, secret-tool): that file fails alone, like a wrong passphrase.
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
        var backup = ConsoleBootstrap.ReadBackup(path);

        // Refused before asking for a secret: the portion-1 rule, until portion 3 adds the
        // upgrade of a saved lab from a backup.
        if (_bootstrap.Profiles.Find(backup.LabKey.LabId) is { } existing)
        {
            return new ImportFileResult(path, false, Strings.Format("Bootstrap.LabAlreadySaved", existing.LabName), existing.LabName);
        }

        var secret = await _unlock(backup);
        if (secret is null)
        {
            return new ImportFileResult(path, false, Strings.Get("Import.Cancelled"), backup.LabName);
        }

        // PBKDF2 takes a moment; the caller is the UI thread.
        using var imported = await Task.Run(() => _bootstrap.ImportBackupProfile(backup, secret.Passphrase, secret.RecoveryCode, _instanceName));
        return new ImportFileResult(path, true, Strings.Format("Import.Added", imported.LabName, imported.PcCount), imported.LabName);
    }
}
