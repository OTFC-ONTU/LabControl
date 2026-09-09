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
    private readonly Action _onMachinesChanged;
    private readonly Action _onOtherConsolesChanged;
    private readonly Action<EventRecord> _onEventAdded;
    private readonly Action<JobRecord> _onJobUpdated;
    private readonly Action _onVaultChanged;
    private readonly Action<AgentScreen, FrameOutcome> _onScreenUpdated;
    private readonly NetworkReadiness _network;
    private readonly Action _onNetworkChanged;
    private int _refreshPending;
    private volatile bool _detached;

    /// <param name="network">
    /// The LAN-access check behind the firewall banner (M5, D-59 item 2). Windows only; on
    /// macOS and Linux it reports <see cref="NetworkReadinessState.NotApplicable"/> and no
    /// banner is ever shown.
    /// </param>
    public MainViewModel(LabSession session, ConsoleBootstrap bootstrap, IDialogs dialogs, Action<Action> post,
        NetworkReadiness? network = null)
    {
        _session = session;
        _bootstrap = bootstrap;
        _dialogs = dialogs;
        _post = post;
        _network = network ?? NetworkReadiness.ForThisMachine();

        Settings = new SettingsViewModel(session, bootstrap, dialogs, EnsureUnlockedAsync);
        Settings.BackupChanged += RefreshBanners;
        Scripts = new ScriptsViewModel(session, dialogs, () => Selected.Select(t => t.AgentId).ToArray());

        Title = Strings.Format("Main.Title", session.LabName, session.Instance.InstanceName);
        Generation = session.Generation;
        // A missing profile record never grants more than a teacher sees (M5 §5).
        AccessLabel = AccessLabels.For(bootstrap.Profiles.Find(session.LabId)?.Access ?? ProfileAccess.Teacher);

        foreach (var record in session.Events.Recent)
        {
            Events.Insert(0, new EventRowViewModel(record));
        }

        // Jobs the session already holds before this view model exists: the rows it brought
        // back from jobs-inflight.json when the lab was opened (M5, D-57 item 4). Without this
        // the teacher would be told N jobs are still running and see an empty panel.
        foreach (var job in session.Jobs.All())
        {
            UpdateJob(job);
        }

        // Kept as fields so Detach can unsubscribe every one of them (M5, D-57 item 2).
        _onMachinesChanged = () => Post(RefreshMachines);
        _onOtherConsolesChanged = () => Post(() => { RefreshMachines(); RefreshBanners(); Settings.Refresh(); });
        _onEventAdded = record => Post(() => AddEvent(record));
        _onJobUpdated = job => Post(() => UpdateJob(job));
        _onVaultChanged = () => Post(() => { RefreshBanners(); Settings.Refresh(); });
        _onScreenUpdated = (screen, _) => OnFrame(screen.AgentId);
        _onNetworkChanged = () => Post(RefreshBanners);
        _network.Changed += _onNetworkChanged;
        session.MachinesChanged += _onMachinesChanged;
        session.OtherConsolesChanged += _onOtherConsolesChanged;
        session.Events.Added += _onEventAdded;
        session.Jobs.Updated += _onJobUpdated;
        if (session.Vault is { } vault)
        {
            vault.Changed += _onVaultChanged;
        }

        session.Screens.Updated += _onScreenUpdated;

        RefreshMachines();
        RefreshBanners();

        // A lab has just been activated: this is the moment the LAN has to be able to
        // reach the console (D-59 item 2). The read is COM on Windows, so it is not done
        // on this thread; everywhere else it answers "not applicable" and stops.
        _ = _network.CheckAsync();
    }

    public LabSession Session => _session;

    public SettingsViewModel Settings { get; }

    /// <summary>The <i>Scripts</i> tab (D-31 item 4); runs on the lab view's selection.</summary>
    public ScriptsViewModel Scripts { get; }

    public string Title { get; }

    /// <summary>The session this view model was built for; a posted callback from an older one is dropped (M5).</summary>
    public long Generation { get; }

    /// <summary>"Administrator" or "Teacher", from the profile (M5 §5).</summary>
    public string AccessLabel { get; }

    /// <summary>True when this session holds the lab key (D-56 item 5); administrator-only actions are hidden otherwise.</summary>
    public bool IsAdministrator => _session.IsAdministrator;

    /// <summary>The one-line explanation shown in place of the administrator-only actions on a teacher console.</summary>
    public string AccessHint => IsAdministrator ? string.Empty : Strings.Get("Access.TeacherHint");

    /// <summary>The toolbar chip next to <i>Disconnect</i>: lab name and access, and "Connecting…" until the server serves.</summary>
    public string LabChip => Strings.Format(IsConnecting ? "Main.LabChipConnecting" : "Main.LabChip", _session.LabName, AccessLabel);

    /// <summary>
    /// True while the window shows the cached mosaic of a lab whose server has not started
    /// yet (D-57 item 1): every tile offline, no action enabled. <see cref="MarkConnected"/>
    /// ends it once the controller reports <c>Active</c>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabChip), nameof(IsToolbarEnabled))]
    public partial bool IsConnecting { get; set; }

    /// <summary>True once <see cref="Detach"/> ran: this view model belongs to a lab the console has left.</summary>
    public bool IsDetached => _detached;

    /// <summary>The toolbar acts only on a live, connected session: never on one still connecting or already left.</summary>
    public bool IsToolbarEnabled => !_detached && !IsConnecting;

    /// <summary>The server is up: the mosaic becomes live and the toolbar wakes.</summary>
    public void MarkConnected()
    {
        if (_detached)
        {
            return;
        }

        IsConnecting = false;
        RefreshMachines();
        RefreshBanners();
        _ = _network.CheckAsync();
    }

    /// <summary>The teacher pressed <i>Disconnect</i>; the app shows the departure report and releases the lab.</summary>
    public event Action? DisconnectRequested;

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

    /// <summary>Marshals to the UI thread; once detached, nothing from the old session runs.</summary>
    private void Post(Action action) => _post(() =>
    {
        if (!_detached)
        {
            action();
        }
    });

    /// <summary>
    /// Cuts this view model off from its session before the console leaves the lab (M5,
    /// D-57 item 2): every subscription is removed, every single-PC window is closed, and a
    /// callback already posted from the old session is dropped when it runs. The next lab
    /// gets a fresh view model; nothing here is reused.
    /// </summary>
    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        OnPropertyChanged(nameof(IsDetached));
        OnPropertyChanged(nameof(IsToolbarEnabled));
        _session.MachinesChanged -= _onMachinesChanged;
        _session.OtherConsolesChanged -= _onOtherConsolesChanged;
        _session.Events.Added -= _onEventAdded;
        _session.Jobs.Updated -= _onJobUpdated;
        if (_session.Vault is { } vault)
        {
            vault.Changed -= _onVaultChanged;
        }

        _session.Screens.Updated -= _onScreenUpdated;
        _network.Changed -= _onNetworkChanged;
        Scripts.Detach();

        foreach (var screen in _openScreens.Values.ToArray())
        {
            screen.RequestClose();
        }

        _openScreens.Clear();
    }

    [RelayCommand]
    private void Disconnect() => DisconnectRequested?.Invoke();

    // ------------------------------------------------------------------ machines

    private void RefreshMachines()
    {
        var now = _session.Now;
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

            // What this console knows, not what it can guess (M5 §4.6, D-58): a PC that is
            // simply not linked here shows offline, or "not seen since" while another
            // console is live, and is credited to that console only when it was observed
            // leaving for it.
            var ownership = connection is null ? _session.Ownership(machine) : null;

            tile.Refresh(machine, connection, ownership, now, _session.Waking(machine.AgentId) is not null);

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

    private bool CanPushBuild => HasSelection && IsAdministrator;

    /// <summary>Signs and pushes a published agent build to selected PCs for side-by-side installation; administrator only (D-56 item 5).</summary>
    [RelayCommand(CanExecute = nameof(CanPushBuild))]
    private async Task PushBuildAsync()
    {
        var build = await _dialogs.PushAgentBuildAsync(SelectedCount);
        if (build is null)
        {
            return;
        }

        if (!await EnsureUnlockedAsync(Strings.Get("Unlock.ReasonUpdate"))) return;
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
        // A teacher console cannot revoke (D-56 item 5): it only forgets the record; the
        // administrator revokes when the PC must never come back.
        var toRevoke = IsAdministrator
            ? tiles.Where(t => t.CertificateSerial.Length > 0 && !_session.Registry.Revocations.IsRevoked(t.CertificateSerial)).ToArray()
            : [];
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
        if (_session.Vault is not { } vault)
        {
            // A teacher console has no key to unlock (D-56 item 5): say so, once, in place of the prompt.
            await _dialogs.ShowMessageAsync(Strings.Get("Unlock.Title"), Strings.Get("Access.AdministratorNeeded"));
            return false;
        }

        if (vault.IsUnlocked)
        {
            vault.Peek();
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
                ? vault.TryUnlock(answer.RecoveryCode)
                : vault.TryUnlock(answer.Passphrase ?? string.Empty);

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
    private void Lock() => _session.Vault?.Lock();

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
        IsUnlocked = vault is { IsUnlocked: true };
        KeyStatus = vault is null
            ? Strings.Get("Access.TeacherStatus")
            : vault.IsUnlocked
                ? Strings.Format("Key.UnlockedUntil", vault.LocksAt?.ToLocalTime().ToString("t", Strings.Culture) ?? string.Empty)
                : Strings.Get("Key.Locked");

        var wanted = new List<BannerViewModel>();

        // LAN access (D-59 item 2). Non-blocking: the console serves either way, and a
        // teacher who cannot elevate gets the two commands rather than a dead end.
        if (_network.HasBanner)
        {
            // The key carries the state, so moving from "Allow…" to the netsh diagnostic
            // replaces the banner instead of only rewriting its text.
            var key = "network:" + _network.State;
            var lines = string.Join("  ", _network.Diagnostic);
            wanted.Add(_network.State == NetworkReadinessState.Missing
                ? new BannerViewModel(key, Strings.Get("Network.Blocked"), Strings.Get("Network.Allow"),
                    () => _network.AllowAsync(), isWarning: true)
                : new BannerViewModel(key,
                    Strings.Format(_network.State == NetworkReadinessState.Denied ? "Network.Denied" : "Network.Failed", lines),
                    Strings.Get("Network.Recheck"), () => _network.CheckAsync(), isWarning: true));
        }

        var now = _session.Now;
        foreach (var other in _session.OtherConsoles)
        {
            // Only the PCs this console watched leave for that machine are counted, and the
            // wording says "at least" because the ones it knows nothing about may be
            // anywhere — including switched off (M5 §4.6, D-58).
            var observed = _session.ObservedElsewhere(other.InstanceId);
            var total = _session.Registry.Document.Machines.Count;
            var numbers = string.Join(", ", observed.Select(m => string.Format(Strings.Culture, Defaults.MachineNameFormat, m.Number)));
            var text = OtherConsoleText(other, observed.Count, total, numbers, now);

            wanted.Add(new BannerViewModel("other:" + other.InstanceId, text, Strings.Get("Banner.TakeOver"),
                () => { _session.TakeOver(); return Task.CompletedTask; }, isWarning: false));
        }

        var renewals = _session.MachinesNeedingRenewal();
        if (renewals.Count > 0 && vault is { IsUnlocked: false })
        {
            wanted.Add(new BannerViewModel("renew", Strings.Format("Banner.Renewal", renewals.Count),
                Strings.Get("Banner.UnlockKey"), () => EnsureUnlockedAsync(Strings.Get("Unlock.ReasonRenewal")), isWarning: true));
        }

        var backup = _bootstrap.CheckBackup(_session.InstanceDocument);
        if (backup is not BackupStatus.Current and not BackupStatus.NotApplicable)
        {
            wanted.Add(new BannerViewModel("backup",
                Strings.Get(backup == BackupStatus.Missing ? "Banner.BackupMissing" : "Banner.BackupStale"),
                Strings.Get("Banner.ExportBackup"), () => Settings.ExportBackupCommand.ExecuteAsync(null), isWarning: true));
        }

        if (vault is { IsUnlocked: true })
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

    /// <summary>
    /// What one other-console banner says (ARCHITECTURE §3.7.2). Two things are said at
    /// most: that the machine is also running this lab, and — while its press is still what
    /// explains what this console sees — that it took the lab over, with the time it did.
    /// <para>
    /// "… took over the lab at 10:32" is news, and news goes stale (D-58). It is worded that
    /// way while the press this console <i>saw</i> is younger than the observations it
    /// produced (<see cref="Defaults.OwnershipObservationLifetime"/>), not for the rest of a
    /// two-console day; and pressing <i>Take over</i> here clears the press outright, so a
    /// console that has just taken the room back never claims to have lost it. The count is
    /// only what was positively observed, which is why it says "at least".
    /// </para>
    /// </summary>
    public static string OtherConsoleText(OtherConsole other, int observed, int total, string numbers, DateTimeOffset now)
    {
        var tookOver = other.TookOverAt is { } at && other.TookOverSeenAt is { } seen &&
                       now - seen <= Defaults.OwnershipObservationLifetime
            ? at
            : (DateTimeOffset?)null;
        var when = tookOver?.ToLocalTime().ToString("t", Strings.Culture) ?? string.Empty;

        return tookOver is not null
            ? observed > 0
                ? Strings.Format("Banner.TookOver", other.Name, when, observed, total, numbers)
                : Strings.Format("Banner.TookOverUnknown", other.Name, when)
            : observed > 0
                ? Strings.Format("Banner.OtherConsole", other.Name, observed, total, numbers)
                : Strings.Format("Banner.OtherConsoleUnknown", other.Name);
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
