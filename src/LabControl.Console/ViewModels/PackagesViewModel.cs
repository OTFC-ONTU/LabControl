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

/// <summary>
/// The <i>Packages</i> tab (M7 portion 3, D-88): the catalog on the left, the selected entry
/// on the right with the fields the teacher may correct — name, version, silent arguments,
/// the detect rule — and <i>Add package…</i>, the wizard. Installing comes with portion 4.
/// </summary>
public sealed partial class PackagesViewModel : ObservableObject
{
    private readonly PackageCatalog _catalog;
    private readonly IDialogs _dialogs;
    private readonly Action _onChanged;

    /// <param name="post">Runs an action on the UI thread: the catalog says it changed from whichever thread finished the copy.</param>
    public PackagesViewModel(PackageCatalog catalog, IDialogs dialogs, Action<Action> post)
    {
        _catalog = catalog;
        _dialogs = dialogs;
        _onChanged = () => post(Refresh);
        catalog.Changed += _onChanged;
        Refresh();
    }

    /// <summary>The session is being left (M5): stop listening to its catalog.</summary>
    public void Detach() => _catalog.Changed -= _onChanged;

    public ObservableCollection<PackageRowViewModel> Packages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand), nameof(DeleteCommand))]
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
    public partial string Facts { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StateLine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

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
        Facts = entry is null ? string.Empty : string.Join("\n",
            Strings.Format("Packages.FactKind", FamilyName(entry.Family)),
            Strings.Format("Packages.FileFormat", entry.File, FormatSize(entry.Size)),
            Strings.Format("Packages.FactHash", entry.Sha256),
            Strings.Format("Packages.FactChanged", entry.ChangedBy,
                DateTimeOffset.FromUnixTimeSeconds(entry.ChangedAtUnix).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        StateLine = entry is null ? Selected?.StateText ?? string.Empty : StateText(_catalog.StateOf(entry));
    }

    private bool CanEdit() => Selected?.Item.Entry is not null;

    private bool CanDelete() => Selected is not null;

    [RelayCommand]
    private async Task AddAsync()
    {
        var added = await _dialogs.AddPackageAsync(new AddPackageModel(_catalog));
        if (added is not null)
        {
            Refresh();
            Selected = Packages.FirstOrDefault(p => p.Id == added.Id) ?? Selected;
            Status = Strings.Format("Packages.Added", added.Name);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Save()
    {
        if (Selected?.Item.Entry is not { } entry) return;
        var edited = entry with
        {
            Name = Name,
            Version = Version,
            Silent = Silent,
            Detect = new DetectRule(DetectIndex == 1 ? DetectKind.Path : DetectKind.UninstallName, DetectValue),
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
            Status = Strings.Format("Packages.Deleted", row.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Strings.Format("Packages.DeleteFailed", ex.Message);
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
