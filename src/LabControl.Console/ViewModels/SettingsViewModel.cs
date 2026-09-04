using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;

namespace LabControl.Console.ViewModels;

/// <summary>A key holder row in Settings.</summary>
public sealed record HolderRowViewModel(string Name, string Since);

/// <summary>A teacher machine row in Settings (ARCHITECTURE §3.7.1).</summary>
public sealed partial class InstanceRowViewModel : ObservableObject
{
    public InstanceRowViewModel(InstanceRecord record, bool isThisMachine)
    {
        InstanceId = record.InstanceId;
        Name = record.Name.Length > 0 ? record.Name : record.InstanceId;
        CertificateSerial = record.CertificateSerial;
        IsThisMachine = isThisMachine;
        FirstSeen = record.FirstSeenUnix == 0 ? string.Empty : DateTimeOffset.FromUnixTimeSeconds(record.FirstSeenUnix).ToLocalTime().ToString("d", Strings.Culture);
        LastSeen = record.LastSeenUnix == 0 ? string.Empty : DateTimeOffset.FromUnixTimeSeconds(record.LastSeenUnix).ToLocalTime().ToString("g", Strings.Culture);
    }

    public string InstanceId { get; }

    public string Name { get; }

    public string CertificateSerial { get; }

    public bool IsThisMachine { get; }

    public string FirstSeen { get; }

    public string LastSeen { get; }

    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial bool IsRevoked { get; set; }

    public bool CanRevoke => !IsThisMachine && !IsRevoked && CertificateSerial.Length > 0;
}

/// <summary>An enrollment code row in Settings.</summary>
public sealed record CodeRowViewModel(string Code, string Batch, string Status);

/// <summary>
/// Settings: the lab key (holders, recovery code, backup), the teacher machines, the
/// enrollment codes and this console's identity (ARCHITECTURE §8).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly LabSession _session;
    private readonly ConsoleBootstrap _bootstrap;
    private readonly IDialogs _dialogs;
    private readonly Func<string, Task<bool>> _ensureUnlocked;

    public SettingsViewModel(LabSession session, ConsoleBootstrap bootstrap, IDialogs dialogs, Func<string, Task<bool>> ensureUnlocked)
    {
        _session = session;
        _bootstrap = bootstrap;
        _dialogs = dialogs;
        _ensureUnlocked = ensureUnlocked;

        LabName = session.LabName;
        LabId = session.LabId;
        InstanceName = session.Instance.InstanceName;
        InstanceId = session.Instance.InstanceId;
        InstanceSerial = session.Instance.CertificateSerial;
        InstanceExpires = session.Instance.ExpiresAt.ToLocalTime().ToString("d", Strings.Culture);
        DataDirectory = session.Store.Directory;
        Endpoint = $"{session.Options.BindAddress}:{session.Port}";
        Version = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? string.Empty;

        Refresh();
    }

    public string LabName { get; }

    public string LabId { get; }

    public string InstanceName { get; }

    public string InstanceId { get; }

    public string InstanceSerial { get; }

    public string InstanceExpires { get; }

    public string DataDirectory { get; }

    public string Endpoint { get; }

    public string Version { get; }

    public ObservableCollection<HolderRowViewModel> Holders { get; } = [];

    public ObservableCollection<InstanceRowViewModel> OtherMachines { get; } = [];

    public ObservableCollection<CodeRowViewModel> Codes { get; } = [];

    [ObservableProperty]
    public partial string KeyStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUnlocked { get; set; }

    [ObservableProperty]
    public partial string BackupStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool BackupIsCurrent { get; set; }

    [ObservableProperty]
    public partial int UnusedCodes { get; set; }

    [ObservableProperty]
    public partial int PayloadPcCount { get; set; } = Defaults.MaxStudentPcs / 2;

    [ObservableProperty]
    public partial InstanceRowViewModel? SelectedMachine { get; set; }

    [ObservableProperty]
    public partial string LastMessage { get; set; } = string.Empty;

    public void Refresh()
    {
        var vault = _session.Vault;
        IsUnlocked = vault.IsUnlocked;
        KeyStatus = vault.IsUnlocked
            ? Strings.Format("Key.UnlockedUntil", vault.LocksAt?.ToLocalTime().ToString("t", Strings.Culture) ?? string.Empty)
            : Strings.Get("Key.Locked");

        Holders.Clear();
        foreach (var wrapping in vault.Document.Wrappings.Where(w => w.Kind == KeyWrappingKind.Holder))
        {
            Holders.Add(new HolderRowViewModel(wrapping.Name,
                DateTimeOffset.FromUnixTimeSeconds(wrapping.CreatedAtUnix).ToLocalTime().ToString("d", Strings.Culture)));
        }

        var status = _bootstrap.CheckBackup(_session.InstanceDocument);
        BackupIsCurrent = status == Services.BackupStatus.Current;
        BackupStatus = status switch
        {
            Services.BackupStatus.Current => Strings.Format("Backup.Current",
                DateTimeOffset.FromUnixTimeSeconds(_session.InstanceDocument.BackupExportedAtUnix).ToLocalTime().ToString("g", Strings.Culture),
                _session.InstanceDocument.BackupLocation ?? string.Empty),
            Services.BackupStatus.Stale => Strings.Get("Backup.Stale"),
            _ => Strings.Get("Backup.Missing"),
        };

        var selected = SelectedMachine?.InstanceId;
        OtherMachines.Clear();
        var live = _session.OtherConsoles.ToDictionary(o => o.InstanceId, StringComparer.OrdinalIgnoreCase);
        foreach (var record in _session.Registry.OtherTeacherMachines(_session.Instance.InstanceId))
        {
            OtherMachines.Add(new InstanceRowViewModel(record, isThisMachine: false)
            {
                IsLive = live.ContainsKey(record.InstanceId),
                IsRevoked = record.CertificateSerial.Length > 0 && _session.Registry.Revocations.IsRevoked(record.CertificateSerial),
            });
        }

        SelectedMachine = OtherMachines.FirstOrDefault(m => m.InstanceId == selected);

        Codes.Clear();
        foreach (var code in _session.Enrollment.Document.Codes.OrderByDescending(c => c.CreatedAtUnix))
        {
            Codes.Add(new CodeRowViewModel(
                Shared.Identity.Base32Text.Group(code.Code, size: 4),
                code.Batch,
                code.IsBurned
                    ? Strings.Format("Code.UsedBy", string.Format(Strings.Culture, Defaults.MachineNameFormat, code.UsedByNumber))
                    : Strings.Get("Code.Unused")));
        }

        UnusedCodes = _session.Enrollment.UnusedCodeCount;
    }

    // ------------------------------------------------------------------ lab key

    [RelayCommand]
    private async Task UnlockAsync()
    {
        await _ensureUnlocked(Strings.Get("Unlock.ReasonSettings"));
        Refresh();
    }

    [RelayCommand]
    private void Lock()
    {
        _session.Vault.Lock();
        Refresh();
    }

    [RelayCommand]
    private async Task AddHolderAsync()
    {
        if (!await _ensureUnlocked(Strings.Get("Unlock.ReasonHolders")))
        {
            return;
        }

        var answer = await _dialogs.AddHolderAsync();
        if (answer is null)
        {
            return;
        }

        try
        {
            if (!_session.Vault.Use(lab => lab.AddHolder(answer.Name, answer.Passphrase)))
            {
                return;
            }

            _session.Vault.Save();
            _session.Events.Info("key.holder_added", Strings.Format("Key.HolderAdded", answer.Name));
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Settings.Holders"), ex.Message);
        }

        Refresh();
    }

    [RelayCommand]
    private async Task RemoveHolderAsync(HolderRowViewModel? holder)
    {
        if (holder is null || !await _ensureUnlocked(Strings.Get("Unlock.ReasonHolders")))
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Strings.Get("Settings.Holders"), Strings.Format("Key.RemoveHolderConfirm", holder.Name),
                Strings.Get("Key.RemoveHolder"), destructive: true))
        {
            return;
        }

        try
        {
            if (_session.Vault.Use(lab => lab.RemoveHolder(holder.Name)))
            {
                _session.Vault.Save();
                _session.Events.Warning("key.holder_removed", Strings.Format("Key.HolderRemoved", holder.Name));
            }
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Settings.Holders"), ex.Message);
        }

        Refresh();
    }

    [RelayCommand]
    private async Task ReprintRecoveryCodeAsync()
    {
        if (!await _ensureUnlocked(Strings.Get("Unlock.ReasonRecovery")))
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Strings.Get("Settings.RecoveryCode"), Strings.Get("Key.ReprintConfirm"), Strings.Get("Key.Reprint")))
        {
            return;
        }

        if (!_session.Vault.Use(lab => lab.ResetRecoveryCode(), out var code))
        {
            return;
        }

        _session.Vault.Save();
        _session.Events.Warning("key.recovery_reprinted", Strings.Get("Key.RecoveryReprinted"));
        await _dialogs.ShowRecoveryCodeAsync(code);
        Refresh();
    }

    // ------------------------------------------------------------------ backup

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        if (!await _ensureUnlocked(Strings.Get("Unlock.ReasonBackup")))
        {
            return;
        }

        var path = await _dialogs.PickSaveFileAsync(Strings.Get("Backup.Export"),
            Shared.Lab.LabBackup.SuggestFileName(_session.LabName, _session.Now), Defaults.BackupFileExtension);
        if (path is null)
        {
            return;
        }

        if (!_bootstrap.TryExportBackup(_session, path, out var error))
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Backup.Export"), error);
        }
        else
        {
            LastMessage = Strings.Format("Backup.Exported", path);
        }

        Refresh();
        BackupChanged?.Invoke();
    }

    public event Action? BackupChanged;

    // ------------------------------------------------------------------ teacher machines

    [RelayCommand]
    private async Task RevokeMachineAsync(InstanceRowViewModel? machine)
    {
        if (machine is null || !machine.CanRevoke)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Strings.Get("Settings.TeacherMachines"),
                Strings.Format("Revoke.InstanceConfirm", machine.Name), Strings.Get("Revoke.Action"), destructive: true))
        {
            return;
        }

        if (!await _ensureUnlocked(Strings.Get("Unlock.ReasonRevoke")))
        {
            return;
        }

        if (!_session.TryRevoke(machine.CertificateSerial, Strings.Format("Revoke.InstanceReason", machine.Name), out var message))
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Settings.TeacherMachines"), message);
        }

        Refresh();
    }

    // ------------------------------------------------------------------ enrolment

    [RelayCommand]
    private async Task WritePayloadAsync()
    {
        var folder = await _dialogs.PickFolderAsync(Strings.Get("Enroll.WritePayload"));
        if (folder is null)
        {
            return;
        }

        var count = Math.Clamp(PayloadPcCount, 1, Defaults.MaxStudentPcs);
        try
        {
            var target = _session.WritePayload(folder, count);
            LastMessage = Strings.Format("Enroll.PayloadWritten", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Enroll.WritePayload"), ex.Message);
        }

        Refresh();
    }
}
