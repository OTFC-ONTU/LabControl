using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared.Lab;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;
using LabControl.Shared.Setup;

namespace LabControl.Console.ViewModels;

/// <summary>A key holder row in Settings.</summary>
/// <summary>One key holder: <paramref name="Name"/> as stored, <paramref name="Display"/> as the teacher reads it (a remembering computer is not a person, D-82).</summary>
public sealed record HolderRowViewModel(string Name, string Since, string Display);

/// <summary>
/// One console device of the lab in Settings (ARCHITECTURE §3.7.1, M5 D-56): this machine,
/// another administrator machine, or a teacher device — everything <c>lab.json</c> lists,
/// with what it was authorized as, when, and whether its access was withdrawn.
/// </summary>
public sealed partial class DeviceRowViewModel : ObservableObject
{
    public DeviceRowViewModel(InstanceRecord record, bool isThisMachine, ProfileAccess thisAccess)
    {
        InstanceId = record.InstanceId;
        Name = record.Name.Length > 0 ? record.Name : record.InstanceId;
        CertificateSerial = record.CertificateSerial;
        IsThisMachine = isThisMachine;
        var access = isThisMachine && record.Access == ProfileAccess.Unknown ? thisAccess : record.Access;
        AccessLabel = access == ProfileAccess.Unknown ? Strings.Get("Access.Unknown") : AccessLabels.For(access);
        Authorized = record.AuthorizedAtUnix == 0 ? string.Empty : Strings.Format("Device.AuthorizedOn", DateTimeOffset.FromUnixTimeSeconds(record.AuthorizedAtUnix).ToLocalTime().ToString("d", Strings.Culture));
        Withdrawn = record.RevokedAtUnix == 0 ? string.Empty : Strings.Format("Device.WithdrawnOn", DateTimeOffset.FromUnixTimeSeconds(record.RevokedAtUnix).ToLocalTime().ToString("d", Strings.Culture));
        FirstSeen = record.FirstSeenUnix == 0 ? string.Empty : DateTimeOffset.FromUnixTimeSeconds(record.FirstSeenUnix).ToLocalTime().ToString("d", Strings.Culture);
        LastSeen = record.LastSeenUnix == 0 ? string.Empty : DateTimeOffset.FromUnixTimeSeconds(record.LastSeenUnix).ToLocalTime().ToString("g", Strings.Culture);
    }

    public string InstanceId { get; }

    public string Name { get; }

    public string CertificateSerial { get; }

    public bool IsThisMachine { get; }

    public string AccessLabel { get; }

    public string Authorized { get; }

    public string Withdrawn { get; }

    public string FirstSeen { get; }

    public string LastSeen { get; }

    /// <summary>The first line's tail: "this machine", "live now".</summary>
    public string Marks => string.Join(" ", new[]
    {
        IsThisMachine ? Strings.Get("Device.ThisMachine") : string.Empty,
        IsLive ? Strings.Get("Settings.LiveNow") : string.Empty,
    }.Where(m => m.Length > 0));

    /// <summary>The second line: id, serial, dates.</summary>
    public string Details => string.Join("   ", new[] { AccessLabel, Authorized, Withdrawn, CertificateSerial, LastSeen }.Where(d => d.Length > 0));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Marks))]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWithdraw))]
    public partial bool IsRevoked { get; set; }

    /// <summary>"delivered to 11 of 14 PCs; pending on PC-03 (offline since …)" — empty unless withdrawn (D-56 item 6).</summary>
    [ObservableProperty]
    public partial string Delivery { get; set; } = string.Empty;

    public bool CanWithdraw => !IsThisMachine && !IsRevoked;
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
    private readonly DeviceAccess _devices;

    public SettingsViewModel(LabSession session, ConsoleBootstrap bootstrap, IDialogs dialogs, Func<string, Task<bool>> ensureUnlocked)
    {
        _session = session;
        _bootstrap = bootstrap;
        _dialogs = dialogs;
        _ensureUnlocked = ensureUnlocked;
        _devices = new DeviceAccess(bootstrap, labId => string.Equals(labId, session.LabId, StringComparison.OrdinalIgnoreCase) ? session : null);

        IsAdministrator = session.IsAdministrator;
        AccessLabel = AccessLabels.For(session.Access);
        AccessHint = session.IsAdministrator ? string.Empty : Strings.Get("Access.TeacherHint");
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

    /// <summary>True when this session holds the lab key (D-56 item 5); the key, holder, backup and enrolment panels show only then.</summary>
    public bool IsAdministrator { get; }

    public string AccessLabel { get; }

    /// <summary>One line in place of the administrator-only panels on a teacher console.</summary>
    public string AccessHint { get; }

    public ObservableCollection<HolderRowViewModel> Holders { get; } = [];

    /// <summary>Every console device of the lab (M5 §3.5): the one panel that replaced <i>Other teacher machines</i>.</summary>
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    /// <summary>Codes that arrived in an imported backup and wait for activation (D-60).</summary>
    [ObservableProperty]
    public partial int DormantCodes { get; set; }

    /// <summary>Which sticks those codes are from and who wrote them.</summary>
    [ObservableProperty]
    public partial string DormantHint { get; set; } = string.Empty;

    public ObservableCollection<CodeRowViewModel> Codes { get; } = [];

    [ObservableProperty]
    public partial string KeyStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUnlocked { get; set; }

    /// <summary>This computer opens the lab key without a passphrase (D-82).</summary>
    [ObservableProperty]
    public partial bool IsRemembered { get; set; }

    /// <summary>The key could be remembered here: there is a memory and it is not remembered yet.</summary>
    [ObservableProperty]
    public partial bool CanRemember { get; set; }

    [ObservableProperty]
    public partial string BackupStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool BackupIsCurrent { get; set; }

    [ObservableProperty]
    public partial int UnusedCodes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PayloadHint))]
    public partial int PayloadPcCount { get; set; } = Defaults.MaxStudentPcs / 2;

    /// <summary>
    /// Void the unused codes of earlier sticks when writing this one (D-28). On by default;
    /// off while PCs installed from the earlier stick are still waiting to enrol.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PayloadHint))]
    public partial bool VoidEarlierCodes { get; set; } = true;

    /// <summary>Show used and voided codes too. Off by default: the list is for the current stick.</summary>
    [ObservableProperty]
    public partial bool ShowCodeHistory { get; set; }

    [ObservableProperty]
    public partial int HistoryCodes { get; set; }

    partial void OnShowCodeHistoryChanged(bool value) => Refresh();

    /// <summary>What the next <i>Write USB payload</i> will do, in numbers (D-28).</summary>
    public string PayloadHint
    {
        get
        {
            var pcs = Math.Clamp(PayloadPcCount, 1, Defaults.MaxStudentPcs);
            var codes = Strings.Format("Enroll.HintFormat", pcs + Defaults.SpareEnrollmentCodes, pcs, Defaults.SpareEnrollmentCodes);
            if (UnusedCodes == 0)
            {
                return codes;
            }

            return codes + " " + (VoidEarlierCodes
                ? Strings.Format("Enroll.VoidHintOn", UnusedCodes)
                : Strings.Format("Enroll.VoidHintOff", UnusedCodes));
        }
    }

    [ObservableProperty]
    public partial DeviceRowViewModel? SelectedDevice { get; set; }

    [ObservableProperty]
    public partial string LastMessage { get; set; } = string.Empty;

    public void Refresh()
    {
        var vault = _session.Vault;
        IsUnlocked = vault is { IsUnlocked: true } or { IsRemembered: true };
        IsRemembered = vault is { IsRemembered: true };
        OnPropertyChanged(nameof(CanMoveToNewKey));
        CanRemember = vault is { Memory: not null, IsRemembered: false };
        KeyStatus = MainViewModel.KeyStatusText(vault);

        Holders.Clear();
        if (vault is not null)
        {
            foreach (var wrapping in vault.Document.Wrappings.Where(w => w.Kind == KeyWrappingKind.Holder))
            {
                Holders.Add(new HolderRowViewModel(wrapping.Name,
                    DateTimeOffset.FromUnixTimeSeconds(wrapping.CreatedAtUnix).ToLocalTime().ToString("d", Strings.Culture),
                    HolderDisplayName(wrapping.Name)));
            }
        }

        var status = _bootstrap.CheckBackup(_session.InstanceDocument);
        BackupIsCurrent = status is Services.BackupStatus.Current or Services.BackupStatus.NotApplicable;
        BackupStatus = status switch
        {
            Services.BackupStatus.Current => Strings.Format("Backup.Current",
                DateTimeOffset.FromUnixTimeSeconds(_session.InstanceDocument.BackupExportedAtUnix).ToLocalTime().ToString("g", Strings.Culture),
                _session.InstanceDocument.BackupLocation ?? string.Empty),
            Services.BackupStatus.Stale => Strings.Get("Backup.Stale"),
            Services.BackupStatus.NotApplicable => Strings.Get("Access.AdministratorNeeded"),
            _ => Strings.Get("Backup.Missing"),
        };

        RefreshDevices();

        Codes.Clear();
        HistoryCodes = _session.Enrollment.Document.Codes.Count(c => !c.IsOutstanding);
        foreach (var code in _session.Enrollment.Document.Codes.OrderByDescending(c => c.CreatedAtUnix))
        {
            if (!code.IsOutstanding && !ShowCodeHistory)
            {
                continue;
            }

            Codes.Add(new CodeRowViewModel(
                Shared.Identity.Base32Text.Group(code.Code, size: 4),
                code.Batch,
                code.IsBurned
                    ? Strings.Format(
                        code.UsedByAgentId is not null && _session.Registry.FindByAgentId(code.UsedByAgentId) is null ? "Code.UsedByRemoved" : "Code.UsedBy",
                        string.Format(Strings.Culture, Defaults.MachineNameFormat, code.UsedByNumber))
                    : code.IsVoided
                        ? Strings.Format("Code.Voided", DateTimeOffset.FromUnixTimeSeconds(code.VoidedAtUnix).ToLocalTime().ToString("d", Strings.Culture))
                        : code.IsDormant
                            ? Strings.Get("Code.Dormant")
                            : Strings.Get("Code.Unused")));
        }

        UnusedCodes = _session.Enrollment.UnusedCodeCount;
        DormantCodes = _session.Enrollment.DormantCodeCount;
        DormantHint = DormantCodes == 0
            ? string.Empty
            : Strings.Format("Enroll.DormantHint", DormantCodes, string.Join(", ", _session.Enrollment.DormantBatches.Select(b =>
                b.IssuedByInstanceName is { Length: > 0 } issuer ? $"{b.Batch} ({issuer})" : b.Batch)));
        OnPropertyChanged(nameof(PayloadHint));
    }

    /// <summary>The device list and, for each withdrawn device, how far the revocation has travelled (D-56 item 6).</summary>
    public void RefreshDevices()
    {
        var selected = SelectedDevice?.InstanceId;
        Devices.Clear();
        var live = _session.OtherConsoles.ToDictionary(o => o.InstanceId, StringComparer.OrdinalIgnoreCase);
        InstanceRecord[] records = [];
        _session.Registry.Persist(document => records = document.Instances.ToArray());

        foreach (var record in records.OrderByDescending(r => r.IsThisMachine).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var isThisMachine = string.Equals(record.InstanceId, _session.Instance.InstanceId, StringComparison.OrdinalIgnoreCase);
            var instanceSerial = Shared.Identity.LabCertificates.InstanceSerial(record.InstanceId);
            var revoked = record.RevokedAtUnix > 0
                          || _session.Registry.Revocations.IsRevoked(instanceSerial)
                          || (record.CertificateSerial.Length > 0 && _session.Registry.Revocations.IsRevoked(record.CertificateSerial));

            var row = new DeviceRowViewModel(record, isThisMachine, _session.Access)
            {
                IsLive = live.ContainsKey(record.InstanceId),
                IsRevoked = revoked,
            };

            if (revoked)
            {
                var delivery = _session.DeliveryOf(_session.Registry.Revocations.IsRevoked(instanceSerial) ? instanceSerial : record.CertificateSerial);
                var pendingNames = string.Join(", ", delivery.Pending.Select(m =>
                    m.LastSeenUnix == 0
                        ? Strings.Format("Device.PendingNeverSeen", string.Format(Strings.Culture, Defaults.MachineNameFormat, m.Number))
                        : Strings.Format("Device.PendingOffline", string.Format(Strings.Culture, Defaults.MachineNameFormat, m.Number),
                            DateTimeOffset.FromUnixTimeSeconds(m.LastSeenUnix).ToLocalTime().ToString("g", Strings.Culture))));
                var cannotHold = delivery.CannotHold.Count == 0
                    ? string.Empty
                    : " " + Strings.Format("Device.DeliveryCannotHold",
                        string.Join(", ", delivery.CannotHold.Select(m => string.Format(Strings.Culture, Defaults.MachineNameFormat, m.Number))));
                row.Delivery = delivery.Total == 0
                    ? Strings.Get("Device.DeliveryNoPcs")
                    : delivery.IsComplete
                        ? Strings.Format("Device.DeliveredAll", delivery.Total)
                        : delivery.Pending.Count == 0
                            ? Strings.Format("Device.DeliveredCount", delivery.Delivered, delivery.Total) + cannotHold
                            : Strings.Format("Device.DeliveryPending", delivery.Delivered, delivery.Total, pendingNames) + cannotHold;
            }

            Devices.Add(row);
        }

        SelectedDevice = Devices.FirstOrDefault(d => d.InstanceId == selected);
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
        _session.Vault?.Lock();
        Refresh();
    }

    /// <summary>Remembers the lab key on this computer (D-82); asks for the passphrase once if the key is locked.</summary>
    [RelayCommand]
    private async Task RememberKeyAsync()
    {
        if (_session.Vault is not { } vault || !await _ensureUnlocked(Strings.Get("Unlock.ReasonRemember")))
        {
            return;
        }

        if (!vault.Remember())
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Settings.LabKey"), Strings.Get("Key.RememberFailed"));
        }

        _session.Events.Info("key.remembered", Strings.Get("Key.RememberedEvent"));
        Refresh();
    }

    /// <summary>Forgets the lab key on this computer: the passphrase is needed here again (D-82).</summary>
    [RelayCommand]
    private async Task ForgetKeyAsync()
    {
        if (_session.Vault is not { } vault
            || !await _dialogs.ConfirmAsync(Strings.Get("Settings.LabKey"), Strings.Get("Key.ForgetConfirm"), Strings.Get("Key.Forget"), destructive: true))
        {
            return;
        }

        vault.Forget();
        _session.Events.Info("key.forgotten", Strings.Get("Key.ForgottenEvent"));
        Refresh();
    }

    /// <summary>"This computer" for the holder this console added, "Another computer" for another console's, the name otherwise.</summary>
    private string HolderDisplayName(string name) =>
        !LabKeyMemory.IsDeviceHolder(name) ? name
        : string.Equals(name, LabKeyMemory.HolderNameOf(_session.Instance.InstanceId), StringComparison.Ordinal)
            ? Strings.Get("Key.HolderThisComputer")
            : Strings.Format("Key.HolderOtherComputer", _session.NameOfInstance(name[Defaults.DeviceHolderPrefix.Length..]));

    /// <summary>Raised with the new lab's id once a lab was moved to a new key and the teacher chose to switch to it (D-82).</summary>
    public event Action<string>? SwitchLabRequested;

    /// <summary>The move is offered on an administrator console of a lab not yet moved (D-82, owner decision 1).</summary>
    public bool CanMoveToNewKey => _session.Vault is not null && _bootstrap.Profiles.Find(_session.LabId)?.MovedToLabId is null;

    /// <summary>
    /// <i>Lost the passphrase? Move this lab to a new key…</i> (D-82 item 3): explains, asks for the
    /// new key's holder and passphrase, creates the new lab remembered on this computer, makes
    /// sure the new recovery code is written down, sends every linked PC its move, waits for
    /// their answers and offers to switch. PCs that were off move when they link to this lab later.
    /// </summary>
    [RelayCommand]
    private async Task MoveToNewKeyAsync()
    {
        if (!CanMoveToNewKey
            || !await _dialogs.ConfirmAsync(Strings.Get("Move.Title"), Strings.Format("Move.Explain", _session.LabName), Strings.Get("Common.Continue")))
        {
            return;
        }

        var answer = await _dialogs.AddHolderAsync(Strings.Get("Move.NewKeyHeading"), Strings.Get("Move.NewKeyHint"));
        if (answer is null)
        {
            return;
        }

        LabMoveTarget target;
        RecoveryCode recovery;
        try
        {
            (target, recovery) = await Task.Run(() =>
            {
                var created = _bootstrap.CreateLabForMove(_session, answer.Name, answer.Passphrase, out var code);
                return (created, code);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Move.Title"), Strings.Format("Move.CreateFailed", ex.Message));
            return;
        }

        _session.Events.Info("lab.moved", Strings.Format("Event.LabMoved", _session.LabName));
        await _dialogs.ShowRecoveryCodeAsync(recovery);

        _session.MoveTarget = target;
        var linked = _session.Linked.Select(c => c.AgentId).ToArray();
        var jobs = _session.MoveToNewKey(linked, target);
        LastMessage = Strings.Format("Move.Sending", jobs.Count);

        // The scripts only write a payload and schedule a task: their answers come within seconds.
        var deadline = DateTimeOffset.UtcNow + Defaults.LabMoveScriptTimeout;
        while (DateTimeOffset.UtcNow < deadline && jobs.Any(j => _session.Jobs.Find(j.Id) is { IsFinished: false }))
        {
            await Task.Delay(500);
        }

        var finished = jobs.Select(j => _session.Jobs.Find(j.Id)).Where(j => j is not null).ToArray();
        var moved = finished.Count(j => j!.Output.Any(line => line.Contains(LabMoveScript.ScheduledMarker, StringComparison.Ordinal)));
        var refused = finished.Count(j => j!.Output.Any(line => line.Contains(LabMoveScript.RefusedMarker, StringComparison.Ordinal)));
        var offline = _session.Registry.Document.Machines.Count - linked.Length;
        var summary = Strings.Format("Move.Summary", moved, refused, jobs.Count - moved - refused, Math.Max(0, offline), target.LabName);
        LastMessage = summary;
        Refresh();

        if (await _dialogs.ConfirmAsync(Strings.Get("Move.Title"), summary + "\n\n" + Strings.Get("Move.SwitchQuestion"), Strings.Get("Move.Switch")))
        {
            SwitchLabRequested?.Invoke(target.LabId);
        }
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
            if (_session.Vault is not { } vault || !vault.Use(lab => lab.AddHolder(answer.Name, answer.Passphrase)))
            {
                return;
            }

            vault.Save();
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
            if (_session.Vault is { } vault && vault.Use(lab => lab.RemoveHolder(holder.Name)))
            {
                vault.Save();
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

        if (_session.Vault is not { } vault || !vault.Use(lab => lab.ResetRecoveryCode(), out var code))
        {
            return;
        }

        vault.Save();
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

    // ------------------------------------------------------------------ teacher devices (M5, D-56)

    /// <summary>
    /// <i>Authorize requests…</i>: several <c>.lcreq</c> files at once; each grant is
    /// written beside its request. Needs the key, so it lives on administrator profiles only.
    /// </summary>
    [RelayCommand]
    private async Task AuthorizeRequestsAsync()
    {
        if (!IsAdministrator)
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Settings.TeacherDevices"), Strings.Get("Access.AdministratorNeeded"));
            return;
        }

        var paths = await _dialogs.PickOpenFilesAsync(Strings.Get("Device.AuthorizeTitle"),
            [new FileFilter(Strings.Get("Request.FileType"), ["*" + Defaults.DeviceRequestFileExtension])]);
        if (paths.Count == 0)
        {
            return;
        }

        if (!await _ensureUnlocked(Strings.Get("Unlock.ReasonAuthorize")))
        {
            return;
        }

        var results = new List<ImportFileResult>();
        foreach (var path in paths)
        {
            try
            {
                if (_session.Vault is not { } vault || !vault.Use(lab => _devices.ApproveRequest(path, lab), out var approved))
                {
                    results.Add(new ImportFileResult(path, false, Strings.Get("Bootstrap.KeyLocked"), _session.LabName));
                    continue;
                }

                results.Add(new ImportFileResult(path, true, Strings.Format("Import.RequestApproved", approved.InstanceName, _session.LabName, approved.GrantPath), _session.LabName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SchemaVersionException or InvalidOperationException
                                           or System.Security.Cryptography.CryptographicException or ArgumentException or System.Formats.Asn1.AsnContentException)
            {
                // A hostile or damaged request file — an unreadable CSR, subject or key — fails
                // as its own row; it never takes the whole batch down.
                results.Add(new ImportFileResult(path, false, ex.Message, _session.LabName));
            }
        }

        await _dialogs.ShowImportResultsAsync(results);
        var granted = results.Count(r => r.Ok);
        if (granted > 0)
        {
            LastMessage = Strings.Format("Device.Authorized", granted);
        }

        Refresh();
    }

    /// <summary>
    /// <i>Withdraw access…</i>: revokes the device's leaf serial and its <c>instance:</c>
    /// pseudo-serial (D-56 item 6), pushed to the linked PCs at once; the row then shows
    /// how far the entry has travelled — never "revoked everywhere".
    /// </summary>
    [RelayCommand]
    private async Task WithdrawDeviceAsync(DeviceRowViewModel? device)
    {
        device ??= SelectedDevice;
        if (device is null || !device.CanWithdraw)
        {
            return;
        }

        if (!IsAdministrator)
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Settings.TeacherDevices"), Strings.Get("Access.AdministratorNeeded"));
            return;
        }

        if (!await _dialogs.ConfirmAsync(Strings.Get("Settings.TeacherDevices"),
                Strings.Format("Device.WithdrawConfirm", device.Name), Strings.Get("Device.Withdraw"), destructive: true))
        {
            return;
        }

        if (!await _ensureUnlocked(Strings.Get("Unlock.ReasonRevoke")))
        {
            return;
        }

        if (!_session.TryWithdrawDevice(device.InstanceId, Strings.Format("Device.WithdrawReason", device.Name), out var message))
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Settings.TeacherDevices"), message);
        }
        else
        {
            LastMessage = message;
        }

        Refresh();
    }

    /// <summary><i>Export lab file…</i> (D-56 item 2): the routine file for teacher devices; only from here, never as a side effect.</summary>
    [RelayCommand]
    private async Task ExportLabFileAsync()
    {
        if (!IsAdministrator)
        {
            await _dialogs.ShowMessageAsync(Strings.Get("LabFile.Export"), Strings.Get("Access.AdministratorNeeded"));
            return;
        }

        if (!await _ensureUnlocked(Strings.Get("Unlock.ReasonLabFile")))
        {
            return;
        }

        var path = await _dialogs.PickSaveFileAsync(Strings.Get("LabFile.Export"),
            Shared.Lab.LabFile.SuggestFileName(_session.LabName, _session.Now), Defaults.LabFileExtension);
        if (path is null)
        {
            return;
        }

        if (!_bootstrap.TryExportLabFile(_session, path, out var error))
        {
            await _dialogs.ShowMessageAsync(Strings.Get("LabFile.Export"), error);
        }
        else
        {
            LastMessage = Strings.Format("LabFile.Exported", path);
        }

        Refresh();
    }

    /// <summary><i>Use codes from the imported backup</i> (D-60): the explicit activation of dormant codes on this profile.</summary>
    [RelayCommand]
    private async Task ActivateDormantCodesAsync()
    {
        if (DormantCodes == 0)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(Strings.Get("Settings.Enrolment"), Strings.Format("Enroll.ActivateConfirm", DormantCodes), Strings.Get("Enroll.Activate")))
        {
            return;
        }

        var activated = _session.Enrollment.ActivateDormant();
        _session.SaveEnrollment();
        _session.Events.Info("enroll.codes_activated", $"{activated} enrollment code(s) from the imported backup activated on this console.");
        LastMessage = Strings.Format("Enroll.Activated", activated);
        Refresh();
    }

    // ------------------------------------------------------------------ enrolment

    [RelayCommand]
    private async Task BuildUsbInstallerAsync()
    {
        var buildFolder = await _dialogs.PickFolderAsync(Strings.Get("Enroll.BuildFolder"));
        if (buildFolder is null) return;
        try
        {
            var builder = new UsbInstallerBuilder(buildFolder, Version);
            var folder = await _dialogs.PickFolderAsync(Strings.Get("Enroll.BuildUsb"));
            if (folder is null) return;
            builder.ValidateDestination(Path.Combine(folder, Defaults.PayloadDirectoryName));
            var target = _session.WritePayload(folder, Math.Clamp(PayloadPcCount, 1, Defaults.MaxStudentPcs), VoidEarlierCodes);
            await Task.Run(() => builder.Build(target));
            LastMessage = Strings.Format("Enroll.InstallerWritten", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Enroll.BuildUsb"), ex.Message);
        }
        Refresh();
    }

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
            var target = _session.WritePayload(folder, count, VoidEarlierCodes);
            LastMessage = Strings.Format("Enroll.PayloadWritten", target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Enroll.WritePayload"), ex.Message);
        }

        Refresh();
    }
}
