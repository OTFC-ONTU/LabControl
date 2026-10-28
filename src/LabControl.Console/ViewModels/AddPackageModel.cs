using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Shared.Packages;

namespace LabControl.Console.ViewModels;

/// <summary>
/// The <i>Add package</i> wizard without its window (M7 portion 3, D-88): the installer is
/// inspected, every field gets a proposal the teacher may change, and <see cref="AddAsync"/>
/// copies the file into the catalog. A package whose name is already in the catalog is a
/// new version of it and replaces it (owner decision 2026-10-27); the replaced entry's own
/// silent arguments and detect rule are proposed again, because the teacher may have
/// corrected them for this program.
/// </summary>
public sealed class AddPackageModel
{
    private readonly PackageCatalog _catalog;

    public AddPackageModel(PackageCatalog catalog) => _catalog = catalog;

    public string? SourcePath { get; private set; }

    public InstallerInfo? Info { get; private set; }

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Silent { get; set; } = string.Empty;

    public DetectKind DetectKind { get; set; } = DetectKind.UninstallName;

    public string DetectName { get; set; } = string.Empty;

    public string DetectPath { get; set; } = string.Empty;

    /// <summary>How long the installer may run on a PC (D-89); the replaced entry's own limit is kept.</summary>
    public int TimeoutMinutes { get; set; } = (int)LabControl.Shared.Defaults.PackageInstallTimeout.TotalMinutes;

    /// <summary>Reads the installer and fills every proposal; the reason when the file cannot be read, else <c>null</c>.</summary>
    public async Task<string?> InspectAsync(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        InstallerInfo info;
        try
        {
            info = await InstallerInspector.InspectAsync(path, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SourcePath = null;
            Info = null;
            return Strings.Format("Packages.ReadFailed", ex.Message);
        }

        SourcePath = path;
        Info = info;
        Propose();
        return null;
    }

    /// <summary>A <c>.zip</c> package (D-89 addendum): the teacher picked another installer inside it; the proposals follow it.</summary>
    public async Task ChooseRunAsync(string run, CancellationToken cancellationToken)
    {
        if (SourcePath is null || Info is not { IsArchive: true } info || !info.Runs!.Contains(run)) return;
        Info = await InstallerInspector.InspectArchiveEntryAsync(SourcePath, info, run, cancellationToken);
        Propose();
    }

    /// <summary>Every field from what was found; a program already in the catalog keeps the teacher's own arguments, rule and limit.</summary>
    private void Propose()
    {
        var info = Info!;
        Name = info.ProductName ?? Path.GetFileNameWithoutExtension(info.FileName);
        if (Name.Length > PackageEntry.MaxNameLength) Name = Name[..PackageEntry.MaxNameLength].Trim();
        Version = info.ProductVersion ?? string.Empty;
        Silent = InstallerInspector.SilentArguments(info.Family);
        TimeoutMinutes = (int)LabControl.Shared.Defaults.PackageInstallTimeout.TotalMinutes;
        DetectKind = DetectKind.UninstallName;
        DetectName = Name;
        DetectPath = string.Empty;
        if (Replaces() is { } existing && existing.Family == info.Family)
        {
            Silent = existing.Silent;
            TimeoutMinutes = existing.TimeoutMinutes;
            DetectKind = existing.Detect.Kind;
            if (existing.Detect.Kind == DetectKind.Path) DetectPath = existing.Detect.Value;
            else DetectName = existing.Detect.Value;
        }
    }

    /// <summary>The installers inside a <c>.zip</c> package, the chosen one first in the wizard's list; empty for a single installer.</summary>
    public IReadOnlyList<string> Runs => Info?.Runs ?? [];

    public string? Run => Info?.Run;

    /// <summary>The catalog entry the current name would replace, if any.</summary>
    public PackageEntry? Replaces() => string.IsNullOrWhiteSpace(Name) ? null : _catalog.Matching(Name);

    public string FileText => Info is null ? Strings.Get("Packages.NoInstaller") : Strings.Format("Packages.FileFormat", Info.FileName, PackagesViewModel.FormatSize(Info.Size));

    public string FamilyText => Info is null ? string.Empty : Strings.Format("Packages.DetectedFormat", PackagesViewModel.FamilyName(Info.Family));

    public string FamilyHint => Info is null ? string.Empty : Strings.Get("Packages.Hint." + Info.Family);

    public string ReplacesText => Replaces() is { } existing
        ? Strings.Format("Packages.ReplacesFormat", existing.Name, string.IsNullOrEmpty(existing.Version) ? "—" : existing.Version)
        : string.Empty;

    /// <summary>The entry as the fields describe it, and the first problem with it.</summary>
    public (PackageEntry? Entry, string? Problem) Draft()
    {
        if (Info is null || SourcePath is null)
        {
            return (null, Strings.Get("Packages.NoInstaller"));
        }

        if (Info.IsArchive && Info.Run is null)
        {
            return (null, Strings.Get("Packages.NoInstallerInArchive"));
        }

        var entry = new PackageEntry
        {
            Id = PackageEntry.IdFor(Name),
            Name = Name.Trim(),
            Version = Version.Trim(),
            Family = Info.Family,
            File = Info.FileName,
            Run = Info.Run ?? string.Empty,
            Size = Info.Size,
            Sha256 = Info.Sha256,
            Silent = Silent.Trim(),
            Detect = new DetectRule(DetectKind, (DetectKind == DetectKind.Path ? DetectPath : DetectName).Trim()),
            TimeoutMinutes = TimeoutMinutes,
        };
        var problem = entry.Validate();
        if (problem == PackageProblem.None && Info.Family == InstallerFamily.Unknown && entry.Silent.Length == 0)
        {
            return (entry, Strings.Get("Packages.Problem.SilentNeeded"));
        }

        return (entry, problem == PackageProblem.None ? null : PackagesViewModel.ProblemText(problem));
    }

    /// <summary>Copies the installer into the catalog; the entry, or the reason it was not added.</summary>
    public async Task<(PackageEntry? Entry, string? Problem)> AddAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (draft, problem) = Draft();
        if (draft is null || problem is not null)
        {
            return (null, problem);
        }

        try
        {
            var entry = await _catalog.AddAsync(SourcePath!, draft, Replaces()?.Id, progress, cancellationToken);
            return (entry, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (null, Strings.Format("Packages.AddFailed", ex.Message));
        }
    }
}
