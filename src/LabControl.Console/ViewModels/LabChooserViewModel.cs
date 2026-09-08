using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.ViewModels;

/// <summary>One saved lab in the chooser (M5 §5): metadata from <c>profiles.json</c>, nothing live.</summary>
public sealed partial class LabRowViewModel : ObservableObject
{
    public LabRowViewModel(ProfileRecord record, bool highlighted, bool active, DateTimeOffset now, bool leafIsTeacher = false)
    {
        Record = record;
        IsAdministrator = record.Access == ProfileAccess.Administrator;
        NeedsRenewal = DeviceAccess.NeedsRenewal(record, now);
        CanAuthorize = IsAdministrator || DeviceAccess.CanRequest(record, now);
        AuthorizeLabel = IsAdministrator
            ? Strings.Get("Chooser.AuthorizeRequests")
            : record.Authorization == ProfileAuthorization.Authorized ? Strings.Get("Chooser.RequestRenewal") : Strings.Get("Chooser.Authorize");
        CanRenewCertificate = IsAdministrator && leafIsTeacher;
        OpenRefusal = IsAdministrator ? null : record.Authorization switch
        {
            ProfileAuthorization.Authorized when record.AccessExpiresUnix > 0 && DateTimeOffset.FromUnixTimeSeconds(record.AccessExpiresUnix) <= now => Strings.Get("Access.OpenExpired"),
            ProfileAuthorization.Authorized => null,
            ProfileAuthorization.RequestPending => Strings.Get("Access.OpenRequestPending"),
            ProfileAuthorization.Expired => Strings.Get("Access.OpenExpired"),
            ProfileAuthorization.Revoked => Strings.Get("Access.OpenRevoked"),
            _ => Strings.Get("Access.OpenNeedsAuthorization"),
        };
        LabId = record.LabId;
        Name = record.LabName;
        AccessLabel = AccessLabels.For(record.Access);
        PcCountText = record.PcCount switch
        {
            0 => Strings.Get("Chooser.NoPcs"),
            1 => Strings.Get("Chooser.PcCountOne"),
            var n => Strings.Format("Chooser.PcCount", n),
        };
        LastUsedText = record.LastUsedUnix > 0
            ? Strings.Format("Chooser.LastUsed", DateTimeOffset.FromUnixTimeSeconds(record.LastUsedUnix).ToLocalTime().ToString("g", Strings.Culture))
            : Strings.Get("Chooser.NeverUsed");
        IsHighlighted = highlighted;
        IsActive = active;
        StatusText = active ? Strings.Get("Chooser.Status.Active") : StatusOf(record, now);
    }

    public ProfileRecord Record { get; }

    public string LabId { get; }

    public string Name { get; }

    public string AccessLabel { get; }

    public string PcCountText { get; }

    public string StatusText { get; }

    public string LastUsedText { get; }

    /// <summary>The last-used lab: preselected, never opened by itself.</summary>
    public bool IsHighlighted { get; }

    public bool IsActive { get; }

    public bool IsAdministrator { get; }

    /// <summary>A teacher device inside the renewal lead time (D-56 item 4): the chooser offers a renewal request.</summary>
    public bool NeedsRenewal { get; }

    /// <summary><i>Authorize…</i> applies: a teacher profile that can write a request, or an administrator profile that can approve them.</summary>
    public bool CanAuthorize { get; }

    public string AuthorizeLabel { get; }

    /// <summary>An administrator profile whose leaf still says <c>OU=LabControl Teacher</c> (upgraded from a backup, D-56 item 3).</summary>
    public bool CanRenewCertificate { get; }

    /// <summary>Why <i>Open</i> is refused, or <c>null</c> when the lab can be opened (D-56 item 3).</summary>
    public string? OpenRefusal { get; }

    public bool CanOpen => OpenRefusal is null;

    /// <summary>The second line of the row, for the template.</summary>
    public string Details => $"{AccessLabel} · {PcCountText} · {StatusText} · {LastUsedText}";

    private static string StatusOf(ProfileRecord record, DateTimeOffset now) => record.Authorization switch
    {
        ProfileAuthorization.NeedsAuthorization => Strings.Get("Chooser.Status.NeedsAuthorization"),
        ProfileAuthorization.RequestPending => Strings.Get("Chooser.Status.RequestPending"),
        ProfileAuthorization.Expired => Strings.Get("Chooser.Status.Expired"),
        ProfileAuthorization.Revoked => Strings.Get("Chooser.Status.Revoked"),
        _ when record.AccessExpiresUnix > 0 && DateTimeOffset.FromUnixTimeSeconds(record.AccessExpiresUnix) <= now => Strings.Get("Chooser.Status.Expired"),
        _ when record.AccessExpiresUnix > 0 && DeviceAccess.NeedsRenewal(record, now) => Strings.Format("Chooser.Status.RenewalDue", DateTimeOffset.FromUnixTimeSeconds(record.AccessExpiresUnix).ToLocalTime().ToString("d", Strings.Culture)),
        _ when record.AccessExpiresUnix > 0 => Strings.Format("Chooser.Status.Expires", DateTimeOffset.FromUnixTimeSeconds(record.AccessExpiresUnix).ToLocalTime().ToString("d", Strings.Culture)),
        _ => Strings.Get("Chooser.Status.Ready"),
    };
}

/// <summary>
/// <i>My labs</i> (M5 §5, D-53): the labs saved on this device, from the index only — no
/// beacon is heard, no PC is contacted until <i>Open</i>. The last-used lab is highlighted
/// and never opened by itself. <i>Add labs…</i> and a drop import files and activate
/// nothing; <i>Create a lab…</i> runs the first-run wizard's create path into a new
/// profile; <i>Remove from this device</i> forgets a lab here and touches nothing else.
/// </summary>
public sealed partial class LabChooserViewModel : ObservableObject
{
    private readonly ConsoleBootstrap _bootstrap;
    private readonly ActiveLabController _controller;
    private readonly IDialogs _dialogs;
    private readonly Action<Action> _post;
    private readonly Action<ActivationStatus> _onStatus;
    private readonly ILogger? _log;

    /// <summary>One import batch at a time, whoever asked for it (D-59 item 5).</summary>
    private readonly SemaphoreSlim _imports = new(1, 1);

    /// <summary>How many file flows are open; the busy flag drops when the last one ends, not the first.</summary>
    private int _fileFlows;
    private bool _detached;

    public LabChooserViewModel(ConsoleBootstrap bootstrap, ActiveLabController controller, IDialogs dialogs, Action<Action> post, ILogger? log = null)
    {
        _bootstrap = bootstrap;
        _controller = controller;
        _dialogs = dialogs;
        _post = post;
        _log = log;
        Imports = new LabImports(bootstrap, UnlockBackupAsync, ConsoleBootstrap.DefaultInstanceName(), log,
            labId => controller.Active is { IsDisposed: false } active && string.Equals(active.LabId, labId, StringComparison.OrdinalIgnoreCase) ? active : null,
            UnlockKeyAsync);

        _onStatus = status => _post(() =>
        {
            if (!_detached)
            {
                Apply(status);
            }
        });
        controller.StatusChanged += _onStatus;

        Refresh();
        Apply(controller.Status);
    }

    /// <summary>The batch importer behind <i>Add labs…</i> and the drop target; portion 3 registers more handlers on it.</summary>
    public LabImports Imports { get; }

    public ObservableCollection<LabRowViewModel> Labs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(RemoveCommand), nameof(AuthorizeCommand), nameof(RenewCertificateCommand))]
    public partial LabRowViewModel? Selected { get; set; }

    /// <summary>What is going on: "Opening …", or empty.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>The last failure, in red, until the next attempt.</summary>
    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    /// <summary>An activation is under way: buttons wait, the list stays.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(RemoveCommand), nameof(AddLabsCommand), nameof(CreateLabCommand), nameof(AuthorizeCommand), nameof(RenewCertificateCommand))]
    public partial bool IsActivating { get; set; }

    /// <summary>
    /// A file flow owns the chooser: an import batch (the picker, a drop, or files a second
    /// launch forwarded) or the create wizard. Held across all of them so a second batch
    /// cannot start a second import — two unlock dialogs over one profile store — while the
    /// first is still asking the teacher for a passphrase.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(RemoveCommand), nameof(AddLabsCommand), nameof(CreateLabCommand), nameof(AuthorizeCommand), nameof(RenewCertificateCommand))]
    public partial bool IsBusyWithFiles { get; set; }

    /// <summary>The chooser is working: an activation, an import batch or the create wizard.</summary>
    public bool IsBusy => IsActivating || IsBusyWithFiles;

    /// <summary>The chooser finished an import batch or the create wizard: whatever queued behind it may go now.</summary>
    public event Action? BecameIdle;

    /// <summary>A file flow starts: the picker, a batch, the create wizard. Flows nest, so the flag is counted.</summary>
    private void EnterFileFlow()
    {
        _fileFlows++;
        IsBusyWithFiles = true;
    }

    private void LeaveFileFlow()
    {
        if (--_fileFlows <= 0)
        {
            _fileFlows = 0;
            IsBusyWithFiles = false;
        }
    }

    partial void OnIsBusyWithFilesChanged(bool value)
    {
        if (!value)
        {
            BecameIdle?.Invoke();
        }
    }

    [ObservableProperty]
    public partial bool HasLabs { get; set; }

    /// <summary>The lab whose last activation failed: <i>Retry</i> opens it again, whatever row is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial string? FailedLabId { get; set; }

    public bool HasFailure => FailedLabId is not null;

    /// <summary>The window runs the create wizard; the view model only asks, and stays busy until it is done.</summary>
    public event Func<Task>? CreateRequested;

    public event Action? QuitRequested;

    /// <summary>Re-reads the index; the highlighted row is the last-used lab, or the most recently used one.</summary>
    public void Refresh()
    {
        _bootstrap.Profiles.Load();
        var now = DateTimeOffset.UtcNow;
        _bootstrap.RefreshExpiry(now);
        var highlighted = _bootstrap.DefaultLabId;
        var active = _controller.Active is { IsDisposed: false } session ? session.LabId : null;
        var selectedId = Selected?.LabId;

        Labs.Clear();
        foreach (var record in _bootstrap.Profiles.Profiles
                     .OrderByDescending(p => p.LastUsedUnix)
                     .ThenByDescending(p => p.AddedAtUnix)
                     .ThenBy(p => p.LabName, StringComparer.CurrentCultureIgnoreCase))
        {
            Labs.Add(new LabRowViewModel(record,
                highlighted: string.Equals(record.LabId, highlighted, StringComparison.OrdinalIgnoreCase),
                active: string.Equals(record.LabId, active, StringComparison.OrdinalIgnoreCase),
                now,
                leafIsTeacher: LeafIsTeacher(record)));
        }

        HasLabs = Labs.Count > 0;
        Selected = Labs.FirstOrDefault(l => string.Equals(l.LabId, selectedId, StringComparison.OrdinalIgnoreCase))
                   ?? Labs.FirstOrDefault(l => l.IsHighlighted)
                   ?? Labs.FirstOrDefault();
    }

    private void Apply(ActivationStatus status)
    {
        switch (status.State)
        {
            case ActivationState.Activating:
                IsActivating = true;
                Error = string.Empty;
                FailedLabId = null;
                Status = Strings.Format("Chooser.Opening", NameOf(status.LabId));
                break;

            case ActivationState.Deactivating:
                IsActivating = true;
                Status = Strings.Format("Chooser.Leaving", NameOf(status.LabId));
                break;

            case ActivationState.Failed:
                IsActivating = false;
                Status = string.Empty;
                Error = Strings.Format("Chooser.OpenFailed", NameOf(status.LabId), status.Error ?? string.Empty);
                FailedLabId = status.LabId;
                Refresh();
                break;

            default:
                IsActivating = false;
                Status = string.Empty;
                FailedLabId = null;
                Refresh();
                break;
        }
    }

    private string NameOf(string? labId) =>
        labId is null ? string.Empty : _bootstrap.Profiles.Find(labId)?.LabName ?? labId;

    /// <summary>An administrator profile upgraded from a backup keeps its teacher leaf until <i>Renew this device's certificate</i> (D-56 item 3).</summary>
    private bool LeafIsTeacher(ProfileRecord record)
    {
        if (record.Access != ProfileAccess.Administrator)
        {
            return false;
        }

        try
        {
            if (_bootstrap.StoreFor(record.LabId).LoadInstance() is not { Certificate.Length: > 0 } instance)
            {
                return false;
            }

            using var leaf = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(instance.Certificate);
            return LabName.AccessOf(leaf) == ConsoleAccess.Teacher;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SchemaVersionException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private bool CanOpen => !IsBusy && Selected is not null;

    /// <summary>The active lab cannot be forgotten while it is open: leave it first.</summary>
    private bool CanRemove => !IsBusy && Selected is { IsActive: false };

    private bool CanAct => !IsBusy;

    private bool CanRetry => !IsBusy && FailedLabId is not null;

    private bool CanAuthorize => !IsBusy && Selected is { CanAuthorize: true };

    private bool CanRenewCertificate => !IsBusy && Selected is { CanRenewCertificate: true };

    /// <summary>Open (button, double-click, Enter): the selected lab becomes the active one; the app shows the main window on <see cref="ActivationState.Active"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync(LabRowViewModel? row)
    {
        var target = row ?? Selected;
        if (target is null || IsBusy)
        {
            return;
        }

        if (target.OpenRefusal is { } refusal)
        {
            // A teacher profile without a current grant cannot serve (D-56 item 3): the row says what to do next.
            Error = Strings.Format("Chooser.OpenRefused", target.Name, refusal);
            return;
        }

        Error = string.Empty;
        await ActivateAsync(target.LabId);
    }

    /// <summary>
    /// <i>Authorize…</i> (M5 §5): on a teacher profile, writes the device request (a re-run
    /// regenerates the same file); on an administrator profile, opens the approval flow for
    /// one or more requests.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAuthorize))]
    private async Task AuthorizeAsync(LabRowViewModel? row)
    {
        var target = row ?? Selected;
        if (target is null || !target.CanAuthorize || IsBusy)
        {
            return;
        }

        if (target.IsAdministrator)
        {
            EnterFileFlow();
            try
            {
                var requests = await _dialogs.PickOpenFilesAsync(Strings.Get("Device.AuthorizeTitle"),
                    [new FileFilter(Strings.Get("Request.FileType"), ["*" + Shared.Defaults.DeviceRequestFileExtension])]);
                if (requests.Count > 0)
                {
                    await RunImportAsync(requests);
                }
            }
            finally
            {
                LeaveFileFlow();
            }

            return;
        }

        string suggested;
        try
        {
            suggested = Imports.Devices.SuggestRequestFileName(target.LabId);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Error = Strings.Format("Chooser.RequestFailed", target.Name, ex.Message);
            return;
        }

        var path = await _dialogs.PickSaveFileAsync(Strings.Get("Chooser.RequestTitle"), suggested, Shared.Defaults.DeviceRequestFileExtension);
        if (path is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => Imports.Devices.WriteRequest(target.LabId, path));
            Error = string.Empty;
            Status = Strings.Format("Chooser.RequestWritten", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SchemaVersionException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException)
        {
            Error = Strings.Format("Chooser.RequestFailed", target.Name, ex.Message);
        }

        Refresh();
    }

    /// <summary>
    /// <i>Renew this device's certificate</i>: an administrator profile upgraded from a backup
    /// still serves with its teacher leaf until the teacher asks, with the key, for an
    /// administrator one (D-56 item 3). Same instance id, new key.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRenewCertificate))]
    private async Task RenewCertificateAsync(LabRowViewModel? row)
    {
        var target = row ?? Selected;
        if (target is null || !target.CanRenewCertificate || IsBusy)
        {
            return;
        }

        var store = _bootstrap.StoreFor(target.LabId);
        LabKeyDocument keyDocument;
        InstanceDocument existing;
        try
        {
            keyDocument = store.LoadLabKey();
            existing = store.LoadInstance() ?? throw new InvalidDataException(Strings.Format("Bootstrap.InstanceMissing", Shared.Defaults.InstanceFileName));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SchemaVersionException or UnauthorizedAccessException)
        {
            Error = Strings.Format("Chooser.RenewFailed", target.Name, ex.Message);
            return;
        }

        while (true)
        {
            var answer = await _dialogs.UnlockAsync(Strings.Format("Unlock.ReasonRenewDevice", target.Name));
            if (answer is null)
            {
                return;
            }

            var lab = await Task.Run(() =>
            {
                var unlocked = answer.RecoveryCode is not null
                    ? LabKey.TryUnlock(keyDocument, answer.RecoveryCode, out var key)
                    : LabKey.TryUnlock(keyDocument, answer.Passphrase ?? string.Empty, out key);
                return unlocked ? key : null;
            });
            if (lab is null)
            {
                await _dialogs.ShowMessageAsync(Strings.Get("Unlock.Title"), Strings.Get("Unlock.Wrong"));
                continue;
            }

            try
            {
                using (lab)
                {
                    using var reminted = ConsoleInstance.Remint(lab, existing, _bootstrap.NewProtector());
                    store.SaveInstance(reminted.Document);
                }

                Error = string.Empty;
                Status = Strings.Format("Chooser.Renewed", target.Name);
                _log?.LogInformation("Console leaf of lab {LabId} re-minted as administrator from the chooser", target.LabId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                           or System.Security.Cryptography.CryptographicException)
            {
                Error = Strings.Format("Chooser.RenewFailed", target.Name, ex.Message);
            }

            Refresh();
            return;
        }
    }

    /// <summary><i>Retry</i> after a failure: the same lab again, whatever row is selected now.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        if (FailedLabId is { } labId && !IsBusy)
        {
            Error = string.Empty;
            await ActivateAsync(labId);
        }
    }

    private async Task ActivateAsync(string labId)
    {
        try
        {
            await _controller.ActivateAsync(labId);
        }
        catch (OperationCanceledException)
        {
            // The console is shutting down while the lab was opening; the chooser goes with it.
        }
    }

    /// <summary><i>Add labs…</i>: the picker and the batch it chose are one busy stretch, so nothing else imports underneath.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task AddLabsAsync()
    {
        EnterFileFlow();
        try
        {
            var paths = await _dialogs.PickOpenFilesAsync(Strings.Get("Chooser.AddLabsTitle"), Imports.Filters);
            if (paths.Count > 0)
            {
                await RunImportAsync(paths);
            }
        }
        finally
        {
            LeaveFileFlow();
        }
    }

    /// <summary>
    /// Imports a batch (the picker, a drop, or files a second launch forwarded), shows one row
    /// per file and refreshes the list. Nothing activates. Batches are serialized here: an
    /// import asks for passphrases, and a second batch that started underneath the first would
    /// stack two unlock dialogs over one profile store.
    /// </summary>
    public async Task<IReadOnlyList<ImportFileResult>> ImportFilesAsync(IEnumerable<string> paths)
    {
        EnterFileFlow();
        try
        {
            return await RunImportAsync(paths);
        }
        finally
        {
            LeaveFileFlow();
        }
    }

    /// <summary>One batch, alone: the caller owns <see cref="IsBusyWithFiles"/>, the semaphore owns the order.</summary>
    private async Task<IReadOnlyList<ImportFileResult>> RunImportAsync(IEnumerable<string> paths)
    {
        await _imports.WaitAsync();
        try
        {
            var results = await Imports.ImportAsync(paths);
            Refresh();
            if (results.Count > 0)
            {
                await _dialogs.ShowImportResultsAsync(results);
            }

            return results;
        }
        finally
        {
            _imports.Release();
        }
    }

    /// <summary><i>Create a lab…</i>: the app runs the wizard, and the chooser is busy — no import may start behind it — until it returns.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task CreateLabAsync()
    {
        if (CreateRequested is not { } create || IsBusy)
        {
            return;
        }

        EnterFileFlow();
        try
        {
            await create();
        }
        finally
        {
            LeaveFileFlow();
        }
    }

    [RelayCommand]
    private void Quit() => QuitRequested?.Invoke();

    /// <summary>
    /// Forgets a lab on this device (design §5): the profile entry, <c>labs/&lt;id&gt;/</c> and
    /// the instance key in the keystore. An administrator profile gets a second question
    /// naming the last backup, because <c>lab-key.lck</c> goes with it. Nothing on a student
    /// PC, in the classroom or in anyone's access changes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync(LabRowViewModel? row)
    {
        var target = row ?? Selected;
        if (target is null || target.IsActive)
        {
            return;
        }

        var name = target.Name;
        if (!await _dialogs.ConfirmAsync(Strings.Format("Chooser.RemoveTitle", name), Strings.Format("Chooser.RemoveConfirm", name), Strings.Get("Chooser.Remove"), destructive: true))
        {
            return;
        }

        var store = _bootstrap.StoreFor(target.LabId);
        if (target.Record.Access == ProfileAccess.Administrator && store.HasLabKey)
        {
            var backupText = Strings.Get("Chooser.NoBackup");
            try
            {
                if (store.LoadInstance() is { BackupExportedAtUnix: > 0 } instance)
                {
                    backupText = Strings.Format("Chooser.LastBackup",
                        DateTimeOffset.FromUnixTimeSeconds(instance.BackupExportedAtUnix).ToLocalTime().ToString("g", Strings.Culture),
                        string.IsNullOrEmpty(instance.BackupLocation) ? "?" : instance.BackupLocation);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SchemaVersionException or UnauthorizedAccessException)
            {
                // An unreadable instance document cannot name a backup; the warning stays the strong one.
            }

            if (!await _dialogs.ConfirmAsync(Strings.Format("Chooser.RemoveKeyTitle", name),
                    Strings.Format("Chooser.RemoveKeyConfirm", name, Shared.Defaults.LabKeyFileName, backupText),
                    Strings.Get("Chooser.RemoveKey"), destructive: true))
            {
                return;
            }
        }

        try
        {
            _bootstrap.Profiles.Remove(target.LabId, deleteData: true);
            Error = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Error = Strings.Format("Chooser.RemoveFailed", name, ex.Message);
        }

        Refresh();
    }

    private async Task<BackupSecret?> UnlockKeyAsync(string reason)
    {
        var answer = await _dialogs.UnlockAsync(reason);
        return answer is null ? null : new BackupSecret(answer.Passphrase, answer.RecoveryCode);
    }

    private async Task<BackupSecret?> UnlockBackupAsync(BackupDocument backup)
    {
        var exported = DateTimeOffset.FromUnixTimeSeconds(backup.ExportedAtUnix).ToLocalTime().ToString("g", Strings.Culture);
        var answer = await _dialogs.UnlockAsync(Strings.Format("Import.UnlockReason", backup.LabName, exported, backup.ExportedBy));
        return answer is null ? null : new BackupSecret(answer.Passphrase, answer.RecoveryCode);
    }

    /// <summary>The window closed: stop following the controller.</summary>
    public void Detach()
    {
        _detached = true;
        _controller.StatusChanged -= _onStatus;
    }
}
