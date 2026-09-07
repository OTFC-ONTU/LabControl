using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;

namespace LabControl.Console.ViewModels;

/// <summary>
/// The main window: the lab view, the jobs and events panels, the banners and the
/// toolbar. It reads <see cref="LabSession"/> and never the other way round; session
/// events arrive on background threads and are marshalled by <see cref="Post"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly LabSession _session;
    private readonly ConsoleBootstrap _bootstrap;
    private readonly IDialogs _dialogs;
    private readonly Action<Action> _post;
    private readonly Dictionary<string, MachineTileViewModel> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JobRowViewModel> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScreenViewModel> _openScreens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _framesPending = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshPending;

    public MainViewModel(LabSession session, ConsoleBootstrap bootstrap, IDialogs dialogs, Action<Action> post)
    {
        _session = session;
        _bootstrap = bootstrap;
        _dialogs = dialogs;
        _post = post;

        Settings = new SettingsViewModel(session, bootstrap, dialogs, EnsureUnlockedAsync);
        Settings.BackupChanged += RefreshBanners;
        Scripts = new ScriptsViewModel(session, dialogs, () => Selected.Select(t => t.AgentId).ToArray());

        Title = Strings.Format("Main.Title", session.LabName, session.Instance.InstanceName);

        foreach (var record in session.Events.Recent)
        {
            Events.Insert(0, new EventRowViewModel(record));
        }

        session.MachinesChanged += () => Post(RefreshMachines);
        session.OtherConsolesChanged += () => Post(() => { RefreshMachines(); RefreshBanners(); Settings.Refresh(); });
        session.Events.Added += record => Post(() => AddEvent(record));
        session.Jobs.Updated += job => Post(() => UpdateJob(job));
        session.Vault.Changed += () => Post(() => { RefreshBanners(); Settings.Refresh(); });
        session.Screens.Updated += (screen, _) => OnFrame(screen.AgentId);

        RefreshMachines();
        RefreshBanners();
    }

    public LabSession Session => _session;

    public SettingsViewModel Settings { get; }

    /// <summary>The <i>Scripts</i> tab (D-31 item 4); runs on the lab view's selection.</summary>
    public ScriptsViewModel Scripts { get; }

    public string Title { get; }

    public ObservableCollection<MachineTileViewModel> Machines { get; } = [];

    public ObservableCollection<JobRowViewModel> Jobs { get; } = [];

    public ObservableCollection<EventRowViewModel> Events { get; } = [];

    public ObservableCollection<BannerViewModel> Banners { get; } = [];

    [ObservableProperty]
    public partial string StatusLine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string KeyStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUnlocked { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WakeCommand), nameof(ShutdownCommand), nameof(RebootCommand), nameof(LogoffCommand), nameof(PushBuildCommand), nameof(SendFilesCommand), nameof(RemoveSelectedCommand))]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportBatchLogsCommand))]
    public partial JobRowViewModel? SelectedJob { get; set; }

    [ObservableProperty]
    public partial double CellWidth { get; set; } = 160;

    [ObservableProperty]
    public partial double CellHeight { get; set; } = 100;

    [ObservableProperty]
    public partial double CanvasWidth { get; set; }

    [ObservableProperty]
    public partial double CanvasHeight { get; set; }

    private double _viewportWidth = 960;

    /// <summary>The view reports its width; the grid re-flows so up to 30 tiles fit (ARCHITECTURE §8).</summary>
    public void SetViewportWidth(double width)
    {
        if (width <= 0 || Math.Abs(width - _viewportWidth) < 1)
        {
            return;
        }

        _viewportWidth = width;
        Reflow();
    }

    private void Post(Action action) => _post(action);

    // ------------------------------------------------------------------ machines

    private void RefreshMachines()
    {
        var now = _session.Now;
        var others = _session.OtherConsoles;
        var layout = _session.Registry.EffectiveLayout().ToDictionary(t => t.Number);
        var machines = _session.Registry.Document.Machines.ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var machine in machines)
        {
            seen.Add(machine.AgentId);
            if (!_tiles.TryGetValue(machine.AgentId, out var tile))
            {
                tile = new MachineTileViewModel(machine, _session.Screens.Get(machine.AgentId));
                tile.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MachineTileViewModel.IsSelected))
                    {
                        SelectedCount = Machines.Count(m => m.IsSelected);
                    }
                };
                _tiles[machine.AgentId] = tile;
                Machines.Add(tile);
            }

            var connection = _session.FindLinked(machine.AgentId);
            string? heldBy = null;
            if (connection is null && others.Count > 0)
            {
                heldBy = others.FirstOrDefault(o => string.Equals(o.InstanceId, machine.LastInstanceId, StringComparison.OrdinalIgnoreCase))?.Name
                         ?? (others.Count == 1 ? others[0].Name : Strings.Get("Tile.AnotherConsole"));
            }

            tile.Refresh(machine, connection, heldBy, now, _session.Waking(machine.AgentId) is not null);

            if (layout.TryGetValue(machine.Number, out var cell))
            {
                tile.Column = cell.Column;
                tile.Row = cell.Row;
            }
        }

        foreach (var (agentId, tile) in _tiles.ToArray())
        {
            if (!seen.Contains(agentId))
            {
                _tiles.Remove(agentId);
                Machines.Remove(tile);
            }
        }

        SelectedCount = Machines.Count(m => m.IsSelected);
        Reflow();

        RefreshStatusLine();
        RefreshBanners();
    }

    private void RefreshStatusLine()
    {
        var online = _session.Linked.Count;
        var total = _session.Registry.Document.Machines.Count;
        StatusLine = Strings.Format("Main.Status", online, total, _session.Instance.InstanceName, _session.Port, _session.Screens.TotalBytesPerSecond * 8 / 1024.0);
    }

    // ------------------------------------------------------------------ screens (M3)

    /// <summary>A frame landed for a PC; one UI hop per PC per burst, whatever the frame rate.</summary>
    private void OnFrame(string agentId)
    {
        bool first;
        lock (_framesPending)
        {
            first = _framesPending.Add(agentId);
        }

        if (first)
        {
            Post(() =>
            {
                lock (_framesPending)
                {
                    _framesPending.Remove(agentId);
                }

                if (_tiles.TryGetValue(agentId, out var tile))
                {
                    tile.FrameVersion++;
                    tile.RefreshPicture(_session.Now, _session.IsLinked(agentId));
                }
            });
        }
    }

    /// <summary>Double-click on a tile (ARCHITECTURE §8): the PC's screen full size, one window per PC.</summary>
    [RelayCommand]
    private void OpenScreen(MachineTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        if (_openScreens.TryGetValue(tile.AgentId, out var open))
        {
            open.Activate();
            return;
        }

        var screen = new ScreenViewModel(_session, tile, _post);
        _openScreens[tile.AgentId] = screen;
        screen.Closed += () => _openScreens.Remove(tile.AgentId);
        _dialogs.ShowScreen(screen);
    }

    private void Reflow()
    {
        var columns = Math.Max(Defaults.DefaultTilesPerRow, Machines.Count == 0 ? 0 : Machines.Max(m => m.Column) + 1);
        var width = Math.Clamp(Math.Floor((_viewportWidth - 16) / columns), 96, 240);
        CellWidth = width;
        // A 16:9 picture plus the strip under it (M3).
        CellHeight = Math.Floor(width * 9 / 16) + 30;

        foreach (var tile in Machines)
        {
            tile.X = tile.Column * CellWidth;
            tile.Y = tile.Row * CellHeight;
        }

        var rows = Machines.Count == 0 ? 0 : Machines.Max(m => m.Row) + 1;
        CanvasWidth = columns * CellWidth;
        CanvasHeight = rows * CellHeight;
    }

    /// <summary>Drag-and-drop from the view: the tile lands on a cell; whoever was there swaps places.</summary>
    public void MoveTile(MachineTileViewModel tile, int column, int row)
    {
        column = Math.Max(0, column);
        row = Math.Max(0, row);

        var occupant = Machines.FirstOrDefault(m => m.Column == column && m.Row == row && !ReferenceEquals(m, tile));
        if (occupant is not null)
        {
            occupant.Column = tile.Column;
            occupant.Row = tile.Row;
        }

        tile.Column = column;
        tile.Row = row;

        _session.Registry.SetLayout(Machines.Select(m => new LayoutTile { Number = m.Number, Column = m.Column, Row = m.Row }));
        Reflow();
    }

    public IReadOnlyList<MachineTileViewModel> Selected => Machines.Where(m => m.IsSelected).ToArray();

    public void Select(MachineTileViewModel tile, bool toggle)
    {
        if (!toggle)
        {
            foreach (var other in Machines)
            {
                other.IsSelected = ReferenceEquals(other, tile);
            }
        }
        else
        {
            tile.IsSelected = !tile.IsSelected;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var tile in Machines)
        {
            tile.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var tile in Machines)
        {
            tile.IsSelected = false;
        }
    }

    // ------------------------------------------------------------------ actions

    private bool HasSelection => SelectedCount > 0;

    partial void OnSelectedCountChanged(int value) => Scripts.SetSelectedPcCount(value);

    /// <summary>Wake-on-LAN for the selected PCs that are off; the outcome arrives as events and on the tile (ARCHITECTURE §6).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task WakeAsync() => _session.WakeAsync(Selected.Select(t => t.AgentId).ToArray());

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Shutdown() => CreateJobs(Job.Types.Kind.Shutdown);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reboot() => CreateJobs(Job.Types.Kind.Reboot);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Logoff() => CreateJobs(Job.Types.Kind.Logoff);

    /// <summary>Development-only (D-33): a published agent build to the selected PCs, which install it side by side and restart.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task PushBuildAsync()
    {
        var build = await _dialogs.PushAgentBuildAsync(SelectedCount);
        if (build is null)
        {
            return;
        }

        var targets = Selected.Select(t => t.AgentId).ToArray();
        if (targets.Length > 0)
        {
            _session.PushAgentBuild(targets, build);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SendFilesAsync()
    {
        var targets = Selected.Select(t => t.AgentId).ToArray();
        var answer = await _dialogs.SendFilesAsync(targets.Length);
        if (answer is null) return;
        try
        {
            var jobs = await Task.Run(() => _session.SendFiles(targets, answer.Paths, answer.Open));
            StatusLine = Strings.Format("Files.Queued", jobs.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Action.SendFiles"), Strings.Format("Files.Failed", ex.Message));
        }
    }

    private void CreateJobs(Job.Types.Kind kind, IReadOnlyDictionary<string, string>? args = null, TimeSpan? timeout = null)
    {
        var targets = Selected.Select(t => t.AgentId).ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        _session.CreateJobs(targets, kind, args, timeout);
    }

    [RelayCommand]
    private Task RemoveMachineAsync(MachineTileViewModel? tile) =>
        tile is null ? Task.CompletedTask : RemoveMachinesAsync([tile]);

    /// <summary>Toolbar: remove every selected PC in one go — one confirmation, one passphrase.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RemoveSelectedAsync() => RemoveMachinesAsync(Selected);

    private async Task RemoveMachinesAsync(IReadOnlyList<MachineTileViewModel> tiles)
    {
        if (tiles.Count == 0)
        {
            return;
        }

        var title = tiles.Count == 1 ? tiles[0].Name : Strings.Format("Machine.RemoveManyTitle", tiles.Count);
        var body = tiles.Count == 1
            ? Strings.Format("Machine.RemoveConfirm", tiles[0].Name)
            : Strings.Format("Machine.RemoveManyConfirm", tiles.Count, string.Join(", ", tiles.OrderBy(t => t.Number).Select(t => t.Name)));
        if (!await _dialogs.ConfirmAsync(title, body, Strings.Get("Machine.Remove"), destructive: true))
        {
            return;
        }

        // Removing without revoking would leave a valid certificate nobody can see any more
        // (D-28): the PC would come straight back through the self-healing list, or sit in a
        // cupboard as a credential. So the certificate goes first, then the record.
        var toRevoke = tiles
            .Where(t => t.CertificateSerial.Length > 0 && !_session.Registry.Revocations.IsRevoked(t.CertificateSerial))
            .ToArray();
        if (toRevoke.Length > 0 && !await EnsureUnlockedAsync(Strings.Get("Unlock.ReasonRemove")))
        {
            return;
        }

        foreach (var tile in tiles)
        {
            if (toRevoke.Contains(tile)
                && !_session.TryRevoke(tile.CertificateSerial, Strings.Format("Revoke.RemovedReason", tile.Name), out var message))
            {
                await _dialogs.ShowMessageAsync(tile.Name, message);
                return;
            }

            _session.ForgetMachine(tile.AgentId);
        }
    }

    [RelayCommand]
    private async Task RevokeMachineAsync(MachineTileViewModel? tile)
    {
        if (tile is null || tile.CertificateSerial.Length == 0)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(tile.Name, Strings.Format("Revoke.MachineConfirm", tile.Name), Strings.Get("Revoke.Action"), destructive: true))
        {
            return;
        }

        if (!await EnsureUnlockedAsync(Strings.Get("Unlock.ReasonRevoke")))
        {
            return;
        }

        if (!_session.TryRevoke(tile.CertificateSerial, Strings.Format("Revoke.MachineReason", tile.Name), out var message))
        {
            await _dialogs.ShowMessageAsync(tile.Name, message);
        }
    }

    // ------------------------------------------------------------------ lab key

    /// <summary>Unlocks the lab key if it is locked, asking for a passphrase or the recovery code.</summary>
    public async Task<bool> EnsureUnlockedAsync(string reason)
    {
        if (_session.Vault.IsUnlocked)
        {
            _session.Vault.Peek();
            return true;
        }

        while (true)
        {
            var answer = await _dialogs.UnlockAsync(reason);
            if (answer is null)
            {
                return false;
            }

            var opened = answer.RecoveryCode is not null
                ? _session.Vault.TryUnlock(answer.RecoveryCode)
                : _session.Vault.TryUnlock(answer.Passphrase ?? string.Empty);

            if (opened)
            {
                _session.Events.Info("key.unlocked", Strings.Get("Key.Unlocked"));
                RefreshBanners();
                return true;
            }

            await _dialogs.ShowMessageAsync(Strings.Get("Unlock.Title"), Strings.Get("Unlock.Wrong"));
        }
    }

    [RelayCommand]
    private Task UnlockAsync() => EnsureUnlockedAsync(Strings.Get("Unlock.ReasonEnrol"));

    [RelayCommand]
    private void Lock() => _session.Vault.Lock();

    [RelayCommand]
    private void TakeOver() => _session.TakeOver();

    // ------------------------------------------------------------------ banners

    /// <summary>Called every second by the view, for the clock-driven parts.</summary>
    public void Tick()
    {
        RefreshBanners();
        RefreshStatusLine();

        var now = _session.Now;
        foreach (var tile in Machines)
        {
            tile.RefreshPicture(now, tile.IsOnline);
        }
    }

    private void RefreshBanners()
    {
        var vault = _session.Vault;
        IsUnlocked = vault.IsUnlocked;
        KeyStatus = vault.IsUnlocked
            ? Strings.Format("Key.UnlockedUntil", vault.LocksAt?.ToLocalTime().ToString("t", Strings.Culture) ?? string.Empty)
            : Strings.Get("Key.Locked");

        var wanted = new List<BannerViewModel>();

        foreach (var other in _session.OtherConsoles)
        {
            var held = _session.HeldElsewhere();
            var total = _session.Registry.Document.Machines.Count;
            var numbers = string.Join(", ", held.Select(m => string.Format(Strings.Culture, Defaults.MachineNameFormat, m.Number)));
            var text = other.TookOverAt is { } at && held.Count == total && total > 0
                ? Strings.Format("Banner.TookOver", other.Name, at.ToLocalTime().ToString("t", Strings.Culture), held.Count, total, numbers)
                : Strings.Format("Banner.OtherConsole", other.Name, held.Count, total, numbers);

            wanted.Add(new BannerViewModel("other:" + other.InstanceId, text, Strings.Get("Banner.TakeOver"),
                () => { _session.TakeOver(); return Task.CompletedTask; }, isWarning: false));
        }

        var renewals = _session.MachinesNeedingRenewal();
        if (renewals.Count > 0 && !vault.IsUnlocked)
        {
            wanted.Add(new BannerViewModel("renew", Strings.Format("Banner.Renewal", renewals.Count),
                Strings.Get("Banner.UnlockKey"), () => EnsureUnlockedAsync(Strings.Get("Unlock.ReasonRenewal")), isWarning: true));
        }

        var backup = _bootstrap.CheckBackup(_session.InstanceDocument);
        if (backup != BackupStatus.Current)
        {
            wanted.Add(new BannerViewModel("backup",
                Strings.Get(backup == BackupStatus.Missing ? "Banner.BackupMissing" : "Banner.BackupStale"),
                Strings.Get("Banner.ExportBackup"), () => Settings.ExportBackupCommand.ExecuteAsync(null), isWarning: true));
        }

        if (vault.IsUnlocked)
        {
            wanted.Add(new BannerViewModel("unlocked", KeyStatus, Strings.Get("Key.Lock"),
                () => { vault.Lock(); return Task.CompletedTask; }, isWarning: false));
        }

        // Reconcile in place so the banner strip does not flicker every second.
        for (var i = Banners.Count - 1; i >= 0; i--)
        {
            if (wanted.All(w => w.Key != Banners[i].Key))
            {
                Banners.RemoveAt(i);
            }
        }

        foreach (var banner in wanted)
        {
            var existing = Banners.FirstOrDefault(b => b.Key == banner.Key);
            if (existing is null)
            {
                Banners.Add(banner);
            }
            else
            {
                existing.Text = banner.Text;
            }
        }
    }

    // ------------------------------------------------------------------ jobs and events

    private bool HasJobBatch => !string.IsNullOrEmpty(SelectedJob?.Job.BatchId);

    [RelayCommand(CanExecute = nameof(HasJobBatch))]
    private async Task ExportBatchLogsAsync()
    {
        var batchId = SelectedJob?.Job.BatchId;
        if (string.IsNullOrEmpty(batchId))
        {
            return;
        }

        var destination = await _dialogs.PickSaveFileAsync(Strings.Get("Jobs.ExportBatch"),
            batchId + Defaults.JobBatchArchiveExtension, Defaults.JobBatchArchiveExtension);
        if (destination is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => _session.BatchLogs.Export(batchId, destination));
            await _dialogs.ShowMessageAsync(Strings.Get("Jobs.ExportBatch"), Strings.Format("Jobs.BatchExported", destination));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _dialogs.ShowMessageAsync(Strings.Get("Jobs.ExportBatch"), Strings.Format("Jobs.BatchExportFailed", ex.Message));
        }
    }

    private void UpdateJob(JobRecord job)
    {
        if (_jobs.TryGetValue(job.Id, out var row))
        {
            row.Refresh();
            if (ReferenceEquals(SelectedJob, row))
            {
                OnPropertyChanged(nameof(SelectedJob));
            }

            return;
        }

        var number = _session.Registry.FindByAgentId(job.AgentId)?.Number ?? 0;
        row = new JobRowViewModel(job, number);
        _jobs[job.Id] = row;
        Jobs.Insert(0, row);

        while (Jobs.Count > 500)
        {
            _jobs.Remove(Jobs[^1].Id);
            Jobs.RemoveAt(Jobs.Count - 1);
        }
    }

    private void AddEvent(EventRecord record)
    {
        Events.Insert(0, new EventRowViewModel(record));
        while (Events.Count > 1000)
        {
            Events.RemoveAt(Events.Count - 1);
        }

        if (record.Code is "machine.replaced" or "link.new_machine" or "enroll.issued" or "renew.issued")
        {
            RefreshMachines();
        }
    }

    /// <summary>Coalesces bursts of refreshes (thirty PCs connecting at once) into one per UI tick.</summary>
    public void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) == 0)
        {
            Post(() =>
            {
                Interlocked.Exchange(ref _refreshPending, 0);
                RefreshMachines();
            });
        }
    }
}
