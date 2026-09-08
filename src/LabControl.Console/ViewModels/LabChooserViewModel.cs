using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared.Persistence;

namespace LabControl.Console.ViewModels;

/// <summary>One saved lab in the chooser (M5 §5): metadata from <c>profiles.json</c>, nothing live.</summary>
public sealed partial class LabRowViewModel : ObservableObject
{
    public LabRowViewModel(ProfileRecord record, bool highlighted, bool active, DateTimeOffset now)
    {
        Record = record;
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

    /// <summary>The second line of the row, for the template.</summary>
    public string Details => $"{AccessLabel} · {PcCountText} · {StatusText} · {LastUsedText}";

    private static string StatusOf(ProfileRecord record, DateTimeOffset now) => record.Authorization switch
    {
        ProfileAuthorization.NeedsAuthorization => Strings.Get("Chooser.Status.NeedsAuthorization"),
        ProfileAuthorization.RequestPending => Strings.Get("Chooser.Status.RequestPending"),
        ProfileAuthorization.Expired => Strings.Get("Chooser.Status.Expired"),
        ProfileAuthorization.Revoked => Strings.Get("Chooser.Status.Revoked"),
        _ when record.AccessExpiresUnix > 0 && DateTimeOffset.FromUnixTimeSeconds(record.AccessExpiresUnix) <= now => Strings.Get("Chooser.Status.Expired"),
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
    private bool _detached;

    public LabChooserViewModel(ConsoleBootstrap bootstrap, ActiveLabController controller, IDialogs dialogs, Action<Action> post)
    {
        _bootstrap = bootstrap;
        _controller = controller;
        _dialogs = dialogs;
        _post = post;
        Imports = new LabImports(bootstrap, UnlockBackupAsync, ConsoleBootstrap.DefaultInstanceName());

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
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(RemoveCommand))]
    public partial LabRowViewModel? Selected { get; set; }

    /// <summary>What is going on: "Opening …", or empty.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>The last failure, in red, until the next attempt.</summary>
    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    /// <summary>An activation is under way: buttons wait, the list stays.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(RemoveCommand), nameof(AddLabsCommand), nameof(CreateLabCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasLabs { get; set; }

    /// <summary>The lab whose last activation failed: <i>Retry</i> opens it again, whatever row is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial string? FailedLabId { get; set; }

    public bool HasFailure => FailedLabId is not null;

    /// <summary>The window runs the create wizard; the view model only asks.</summary>
    public event Action? CreateRequested;

    public event Action? QuitRequested;

    /// <summary>Re-reads the index; the highlighted row is the last-used lab, or the most recently used one.</summary>
    public void Refresh()
    {
        _bootstrap.Profiles.Load();
        var highlighted = _bootstrap.DefaultLabId;
        var active = _controller.Active is { IsDisposed: false } session ? session.LabId : null;
        var now = DateTimeOffset.UtcNow;
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
                now));
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
                IsBusy = true;
                Error = string.Empty;
                FailedLabId = null;
                Status = Strings.Format("Chooser.Opening", NameOf(status.LabId));
                break;

            case ActivationState.Deactivating:
                IsBusy = true;
                Status = Strings.Format("Chooser.Leaving", NameOf(status.LabId));
                break;

            case ActivationState.Failed:
                IsBusy = false;
                Status = string.Empty;
                Error = Strings.Format("Chooser.OpenFailed", NameOf(status.LabId), status.Error ?? string.Empty);
                FailedLabId = status.LabId;
                Refresh();
                break;

            default:
                IsBusy = false;
                Status = string.Empty;
                FailedLabId = null;
                Refresh();
                break;
        }
    }

    private string NameOf(string? labId) =>
        labId is null ? string.Empty : _bootstrap.Profiles.Find(labId)?.LabName ?? labId;

    private bool CanOpen => !IsBusy && Selected is not null;

    /// <summary>The active lab cannot be forgotten while it is open: leave it first.</summary>
    private bool CanRemove => !IsBusy && Selected is { IsActive: false };

    private bool CanAct => !IsBusy;

    private bool CanRetry => !IsBusy && FailedLabId is not null;

    /// <summary>Open (button, double-click, Enter): the selected lab becomes the active one; the app shows the main window on <see cref="ActivationState.Active"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync(LabRowViewModel? row)
    {
        var target = row ?? Selected;
        if (target is null || IsBusy)
        {
            return;
        }

        Error = string.Empty;
        await ActivateAsync(target.LabId);
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

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task AddLabsAsync()
    {
        var paths = await _dialogs.PickOpenFilesAsync(Strings.Get("Chooser.AddLabsTitle"), Imports.Filters);
        if (paths.Count > 0)
        {
            await ImportFilesAsync(paths);
        }
    }

    /// <summary>Imports a batch (the picker or a drop), shows one row per file and refreshes the list. Nothing activates.</summary>
    public async Task<IReadOnlyList<ImportFileResult>> ImportFilesAsync(IEnumerable<string> paths)
    {
        var results = await Imports.ImportAsync(paths);
        Refresh();
        if (results.Count > 0)
        {
            await _dialogs.ShowImportResultsAsync(results);
        }

        return results;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void CreateLab() => CreateRequested?.Invoke();

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
