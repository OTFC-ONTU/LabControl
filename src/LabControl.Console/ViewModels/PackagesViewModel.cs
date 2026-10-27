using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared.Packages;

namespace LabControl.Console.ViewModels;

/// <summary>A catalog entry in the list: name, version and kind, and whether its installer is here.</summary>
public sealed class PackageRowViewModel(CatalogItem item)
{
    public CatalogItem Item { get; } = item;

    public string Id => Item.Id;

    public string Name => Item.Entry?.Name ?? Item.Id;

    public string Details => Item.Entry is { } e
        ? string.Join(" · ", new[] { e.Version, PackagesViewModel.FamilyName(e.Family), PackagesViewModel.FormatSize(e.Size) }.Where(s => s.Length > 0))
        : Strings.Get("Packages.Unreadable");

    public string StateText => Item.Entry is null ? Strings.Format("Packages.UnreadableFormat", Item.Problem ?? string.Empty) : PackagesViewModel.StateText(Item.State);

    public bool HasProblem => Item.Entry is null || Item.State != InstallerState.Present;
}

/// <summary>One PC of the lab and where it stands with the selected package.</summary>
public sealed record PackagePcRow(string Name, PcPackageState State, string StateText, string Detail)
{
    public bool IsProblem => State is PcPackageState.Failed or PcPackageState.Missing;
}

/// <summary>
/// The <i>Packages</i> tab (M7 portions 3–4, D-88, D-89): the catalog on the left, the selected
/// entry on the right with the fields the teacher may correct — name, version, silent arguments,
/// the detect rule, the time limit — and where each PC of the lab stands with it. A new package is
/// tried on the PCs selected in the lab view first; <i>Install on all missing</i> asks before it
/// sends an installer no PC has installed yet.
/// </summary>
public sealed partial class PackagesViewModel : ObservableObject
{
    private readonly LabSession _session;
    private readonly PackageCatalog _catalog;
    private readonly IDialogs _dialogs;
    private readonly Func<IReadOnlyList<string>> _selectedAgents;
    private readonly Action _onChanged;
    private readonly Action _onStatus;

    /// <param name="post">Runs an action on the UI thread: the catalog and the PCs' answers arrive on other threads.</param>
    /// <param name="selectedAgents">The PCs selected in the lab view.</param>
    public PackagesViewModel(LabSession session, IDialogs dialogs, Action<Action> post, Func<IReadOnlyList<string>> selectedAgents)
    {
        _session = session;
        _catalog = session.Packages;
        _dialogs = dialogs;
        _selectedAgents = selectedAgents;
        _onChanged = () => post(Refresh);
        _onStatus = () => post(RefreshPcs);
        _catalog.Changed += _onChanged;
        session.PackageStatus.Changed += _onStatus;
        Refresh();
    }

    /// <summary>The session is being left (M5): stop listening to its catalog and its PCs.</summary>
    public void Detach()
    {
        _catalog.Changed -= _onChanged;
        _session.PackageStatus.Changed -= _onStatus;
    }

    public ObservableCollection<PackageRowViewModel> Packages { get; } = [];

    public ObservableCollection<PackagePcRow> Pcs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand), nameof(DeleteCommand), nameof(InstallSelectedCommand), nameof(InstallAllMissingCommand), nameof(CheckPcsCommand))]
    public partial PackageRowViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial bool HasEntry { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string LastChanged { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Version { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Silent { get; set; } = string.Empty;

    /// <summary>0 = Windows' program list, 1 = a file — the order of the combo box.</summary>
    [ObservableProperty]
    public partial int DetectIndex { get; set; }

    [ObservableProperty]
    public partial string DetectValue { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TimeoutText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Facts { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StateLine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(InstallSelectedLabel))]
    public partial int PcCount { get; set; }

    public string InstallSelectedLabel => PcCount == 0 ? Strings.Get("Packages.InstallSelectedNone") : Strings.Format("Packages.InstallSelected", PcCount);

    /// <summary>The lab view tells us how many PCs are selected; the button says so.</summary>
    public void SetSelectedPcCount(int count) => PcCount = count;

    partial void OnSelectedChanged(PackageRowViewModel? value) => Load(value?.Item.Entry);

    private void Refresh()
    {
        var selectedId = Selected?.Id;
        Packages.Clear();
        foreach (var item in _catalog.List())
        {
            Packages.Add(new PackageRowViewModel(item));
        }

        IsEmpty = Packages.Count == 0;
        LastChanged = _catalog.LastChange() is { } last
            ? Strings.Format("Packages.LastChanged", last.By, last.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : string.Empty;
        Selected = Packages.FirstOrDefault(p => p.Id == selectedId) ?? Packages.FirstOrDefault();
    }

    private void Load(PackageEntry? entry)
    {
        HasEntry = entry is not null;
        Error = string.Empty;
        Name = entry?.Name ?? string.Empty;
        Version = entry?.Version ?? string.Empty;
        Silent = entry?.Silent ?? string.Empty;
        DetectIndex = entry?.Detect.Kind == DetectKind.Path ? 1 : 0;
        DetectValue = entry?.Detect.Value ?? string.Empty;
        TimeoutText = entry?.TimeoutMinutes.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        Facts = entry is null ? string.Empty : string.Join("\n",
            Strings.Format("Packages.FactKind", FamilyName(entry.Family)),
            Strings.Format("Packages.FileFormat", entry.File, FormatSize(entry.Size)),
            Strings.Format("Packages.FactHash", entry.Sha256),
            Strings.Format("Packages.FactChanged", entry.ChangedBy,
                DateTimeOffset.FromUnixTimeSeconds(entry.ChangedAtUnix).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        StateLine = entry is null ? Selected?.StateText ?? string.Empty : StateText(_catalog.StateOf(entry));
        RefreshPcs();
    }

    /// <summary>Every PC of the lab, by number, with its latest answer about the selected entry.</summary>
    private void RefreshPcs()
    {
        Pcs.Clear();
        Summary = string.Empty;
        if (Selected?.Item.Entry is not { } entry) return;
        var counts = new Dictionary<PcPackageState, int>();
        foreach (var machine in _session.Machines())
        {
            var state = _session.PackageStatus.StateOf(entry, machine.AgentId);
            counts[state] = counts.GetValueOrDefault(state) + 1;
            var last = _session.PackageStatus.Last(entry.Id, machine.AgentId);
            var detail = state is PcPackageState.Unknown or PcPackageState.Busy || last is null ? string.Empty : last.Message;
            Pcs.Add(new PackagePcRow(PcName(machine), state, Strings.Get("Packages.PcState." + state), detail));
        }

        Summary = Strings.Format("Packages.Summary",
            counts.GetValueOrDefault(PcPackageState.Installed) + counts.GetValueOrDefault(PcPackageState.RebootRequired),
            counts.GetValueOrDefault(PcPackageState.Missing),
            counts.GetValueOrDefault(PcPackageState.Failed),
            counts.GetValueOrDefault(PcPackageState.Unknown) + counts.GetValueOrDefault(PcPackageState.Busy));

        static string PcName(Shared.Persistence.MachineRecord machine) =>
            string.Format(Strings.Culture, Shared.Defaults.MachineNameFormat, machine.Number);
    }

    private bool CanEdit() => Selected?.Item.Entry is not null;

    private bool CanDelete() => Selected is not null;

    private bool CanInstallSelected() => CanEdit() && PcCount > 0;

    [RelayCommand]
    private async Task AddAsync()
    {
        var added = await _dialogs.AddPackageAsync(new AddPackageModel(_catalog));
        if (added is not null)
        {
            Refresh();
            Selected = Packages.FirstOrDefault(p => p.Id == added.Id) ?? Selected;
            Status = Strings.Format("Packages.Added", added.Name) + " " + Strings.Get("Packages.TestHint");
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Save()
    {
        if (Selected?.Item.Entry is not { } entry) return;
        if (!int.TryParse(TimeoutText, NumberStyles.None, CultureInfo.CurrentCulture, out var minutes))
        {
            Error = ProblemText(PackageProblem.TimeoutInvalid);
            return;
        }

        var edited = entry with
        {
            Name = Name,
            Version = Version,
            Silent = Silent,
            Detect = new DetectRule(DetectIndex == 1 ? DetectKind.Path : DetectKind.UninstallName, DetectValue),
            TimeoutMinutes = minutes,
        };
        var problem = _catalog.Update(edited);
        if (problem != PackageProblem.None)
        {
            Error = ProblemText(problem);
            return;
        }

        Status = Strings.Format("Packages.Saved", edited.Name.Trim());
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Revert()
    {
        Load(Selected?.Item.Entry);
        Status = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row) return;
        if (!await _dialogs.ConfirmAsync(Strings.Get("Packages.Delete"), Strings.Format("Packages.DeleteConfirm", row.Name), Strings.Get("Packages.Delete"), destructive: true))
        {
            return;
        }

        try
        {
            _catalog.Delete(row.Id);
            _session.PackageStatus.Forget(row.Id);
            Status = Strings.Format("Packages.Deleted", row.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Strings.Format("Packages.DeleteFailed", ex.Message);
        }
    }

    /// <summary>The test install, and a new version on PCs that have the old one: the installer runs whatever the rule finds.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private void InstallSelected()
    {
        if (Selected?.Item.Entry is not { } entry) return;
        Send(entry, () => _session.InstallPackage(_selectedAgents(), entry, InstallMode.Install));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task InstallAllMissingAsync()
    {
        if (Selected?.Item.Entry is not { } entry) return;
        var pending = _session.Machines().Count(m =>
            _session.PackageStatus.StateOf(entry, m.AgentId) is not (PcPackageState.Installed or PcPackageState.RebootRequired or PcPackageState.Busy));
        if (pending == 0)
        {
            Error = string.Empty;
            Status = Strings.Format("Packages.NothingMissing", entry.Name);
            return;
        }

        if (!_session.PackageStatus.EverInstalled(entry)
            && !await _dialogs.ConfirmAsync(Strings.Get("Packages.UntestedTitle"), Strings.Format("Packages.UntestedConfirm", entry.Name, pending), Strings.Get("Packages.InstallAnyway")))
        {
            return;
        }

        Send(entry, () => _session.InstallPackageOnAllMissing(entry));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void CheckPcs()
    {
        if (Selected?.Item.Entry is not { } entry) return;
        Send(entry, () => _session.CheckPackage(entry));
    }

    private void Send(PackageEntry entry, Func<IReadOnlyList<Shared.Lab.JobRecord>> send)
    {
        Error = string.Empty;
        try
        {
            var jobs = send();
            Status = Strings.Format("Packages.Sent", entry.Name, jobs.Count);
            RefreshPcs();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = Strings.Format("Packages.InstallFailed", ex.Message);
        }
    }

    // ------------------------------------------------------------------ words

    public static string FamilyName(InstallerFamily family) => Strings.Get("Packages.Family." + family);

    public static string StateText(InstallerState state) => Strings.Get("Packages.State." + state);

    public static string ProblemText(PackageProblem problem) => Strings.Get("Packages.Problem." + problem);

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => string.Empty,
        < 1024 * 1024 => Strings.Format("Packages.SizeKb", Math.Max(1, bytes / 1024)),
        < 1024L * 1024 * 1024 => Strings.Format("Packages.SizeMb", (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.CurrentCulture)),
        _ => Strings.Format("Packages.SizeGb", (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.CurrentCulture)),
    };
}
