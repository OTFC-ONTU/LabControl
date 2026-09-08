using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;

namespace LabControl.Console.ViewModels;

public enum SetupStep
{
    Choose = 0,
    Create = 1,
    Import = 2,
    RecoveryCode = 3,
    Backup = 4,
    Done = 5,
}

/// <summary>
/// The first-run wizard (ROADMAP M1): create a new lab, or import one from a backup. It
/// will not finish until a backup has been exported and the recovery code acknowledged
/// (ARCHITECTURE §3.7.3). Import skips both: the backup being imported <i>is</i> the backup.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly ConsoleBootstrap _bootstrap;
    private readonly IDialogs _dialogs;
    private BackupDocument? _backup;
    private RecoveryCode? _recoveryCode;

    /// <param name="startAtCreate">
    /// Skip the choose step (M5 chooser's <i>Create a lab…</i>): the lab list already offers
    /// <i>Add labs…</i> for backups, so the wizard opens straight on the form.
    /// </param>
    public SetupViewModel(ConsoleBootstrap bootstrap, IDialogs dialogs, bool startAtCreate = false)
    {
        _bootstrap = bootstrap;
        _dialogs = dialogs;
        InstanceName = ConsoleBootstrap.DefaultInstanceName();
        if (startAtCreate)
        {
            Step = SetupStep.Create;
        }
    }

    /// <summary>The running session once the wizard has finished.</summary>
    public LabSession? Session { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoose), nameof(IsCreate), nameof(IsImport), nameof(IsRecoveryCode), nameof(IsBackup), nameof(IsDone))]
    public partial SetupStep Step { get; set; }

    public bool IsChoose => Step == SetupStep.Choose;

    public bool IsCreate => Step == SetupStep.Create;

    public bool IsImport => Step == SetupStep.Import;

    public bool IsRecoveryCode => Step == SetupStep.RecoveryCode;

    public bool IsBackup => Step == SetupStep.Backup;

    public bool IsDone => Step == SetupStep.Done;

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    // ------------------------------------------------------------------ create

    [ObservableProperty]
    public partial string LabName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HolderName { get; set; } = Environment.UserName;

    [ObservableProperty]
    public partial string Passphrase { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PassphraseAgain { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InstanceName { get; set; }

    [ObservableProperty]
    public partial string RecoveryCodeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RecoveryCodeAcknowledged { get; set; }

    [ObservableProperty]
    public partial string BackupPath { get; set; } = string.Empty;

    /// <summary>The wizard was reopened on a later launch to finish the steps that were skipped.</summary>
    [ObservableProperty]
    public partial bool IsResumed { get; set; }

    // ------------------------------------------------------------------ import

    [ObservableProperty]
    public partial string BackupFile { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackupSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseRecoveryCode { get; set; }

    [ObservableProperty]
    public partial string ImportSecret { get; set; } = string.Empty;

    [RelayCommand]
    private void ChooseCreate()
    {
        Error = string.Empty;
        Step = SetupStep.Create;
    }

    [RelayCommand]
    private void ChooseImport()
    {
        Error = string.Empty;
        Step = SetupStep.Import;
    }

    [RelayCommand]
    private void Back()
    {
        Error = string.Empty;
        Step = SetupStep.Choose;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        Error = string.Empty;

        if (string.IsNullOrWhiteSpace(LabName))
        {
            Error = Strings.Get("Setup.NeedLabName");
            return;
        }

        if (string.IsNullOrWhiteSpace(HolderName))
        {
            Error = Strings.Get("Setup.NeedHolderName");
            return;
        }

        if (Passphrase.Length < 8)
        {
            Error = Strings.Get("Setup.PassphraseTooShort");
            return;
        }

        if (!string.Equals(Passphrase, PassphraseAgain, StringComparison.Ordinal))
        {
            Error = Strings.Get("Setup.PassphraseMismatch");
            return;
        }

        if (string.IsNullOrWhiteSpace(InstanceName))
        {
            Error = Strings.Get("Setup.NeedInstanceName");
            return;
        }

        IsBusy = true;
        try
        {
            var labName = LabName.Trim();
            var holder = HolderName.Trim();
            var passphrase = Passphrase;
            var instance = InstanceName.Trim();

            // PBKDF2 at 600 000 iterations takes a moment; keep the window responsive.
            RecoveryCode recovery = null!;
            Session = await Task.Run(() => _bootstrap.CreateLab(labName, holder, passphrase, instance, out recovery));
            _recoveryCode = recovery;
            RecoveryCodeText = recovery.ToPrintableString();
            Step = SetupStep.RecoveryCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Passphrase = string.Empty;
            PassphraseAgain = string.Empty;
        }
    }

    /// <summary>
    /// Picks the wizard up where an earlier launch abandoned it. The lab key must be unlocked:
    /// a recovery code that was never acknowledged is gone for good, so a fresh one is
    /// printed and the old one voided; otherwise only the backup step is left.
    /// </summary>
    public void Resume(LabSession session)
    {
        Session = session;
        IsResumed = true;
        Error = string.Empty;

        if (session.InstanceDocument.RecoveryCodeAcknowledged)
        {
            Step = SetupStep.Backup;
            return;
        }

        // The wizard only ever runs on a lab created or imported here, which holds the key.
        var vault = session.Vault ?? throw new InvalidOperationException("The wizard needs the lab key.");
        if (!vault.Use(lab => lab.ResetRecoveryCode(), out var code))
        {
            throw new InvalidOperationException("The lab key is locked.");
        }

        vault.Save();
        session.Events.Warning("key.recovery_reprinted", Strings.Get("Key.RecoveryReprinted"));
        _recoveryCode = code;
        RecoveryCodeText = code.ToPrintableString();
        Step = SetupStep.RecoveryCode;
    }

    [RelayCommand]
    private void AcknowledgeRecoveryCode()
    {
        if (!RecoveryCodeAcknowledged || Session is null)
        {
            Error = Strings.Get("Setup.AcknowledgeFirst");
            return;
        }

        Error = string.Empty;
        Session.InstanceDocument.RecoveryCodeAcknowledged = true;
        _bootstrap.SaveInstance(Session.InstanceDocument);
        RecoveryCodeText = string.Empty;
        _recoveryCode = null;
        Step = SetupStep.Backup;
    }

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        if (Session is null)
        {
            return;
        }

        Error = string.Empty;
        var path = await _dialogs.PickSaveFileAsync(Strings.Get("Backup.Export"),
            Shared.Lab.LabBackup.SuggestFileName(Session.LabName, Session.Now), Shared.Defaults.BackupFileExtension);
        if (path is null)
        {
            return;
        }

        if (!_bootstrap.TryExportBackup(Session, path, out var error))
        {
            Error = error;
            return;
        }

        BackupPath = path;
        Step = SetupStep.Done;
    }

    // ------------------------------------------------------------------ import

    [RelayCommand]
    private async Task PickBackupAsync()
    {
        Error = string.Empty;
        var file = await _dialogs.PickOpenFileAsync(Strings.Get("Setup.PickBackup"), Shared.Defaults.BackupFileExtension);
        if (file is null)
        {
            return;
        }

        try
        {
            _backup = ConsoleBootstrap.ReadBackup(file);
            BackupFile = file;
            var holders = _backup.LabKey.Wrappings.Where(w => w.Kind == KeyWrappingKind.Holder).Select(w => w.Name);
            BackupSummary = Strings.Format("Setup.BackupSummary",
                _backup.LabName,
                DateTimeOffset.FromUnixTimeSeconds(_backup.ExportedAtUnix).ToLocalTime().ToString("g", Strings.Culture),
                _backup.ExportedBy,
                string.Join(", ", holders));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SchemaVersionException or UnauthorizedAccessException)
        {
            _backup = null;
            BackupSummary = string.Empty;
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        Error = string.Empty;
        if (_backup is null)
        {
            Error = Strings.Get("Setup.NeedBackupFile");
            return;
        }

        if (string.IsNullOrWhiteSpace(InstanceName))
        {
            Error = Strings.Get("Setup.NeedInstanceName");
            return;
        }

        RecoveryCode? recovery = null;
        if (UseRecoveryCode && !RecoveryCode.TryParse(ImportSecret, out recovery))
        {
            Error = Strings.Get("Setup.BadRecoveryCode");
            return;
        }

        IsBusy = true;
        try
        {
            var backup = _backup;
            var secret = ImportSecret;
            var instance = InstanceName.Trim();
            Session = await Task.Run(() => _bootstrap.ImportBackup(backup, UseRecoveryCode ? null : secret, recovery, instance));
            Step = SetupStep.Done;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidDataException or IOException or SchemaVersionException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            ImportSecret = string.Empty;
        }
    }
}
