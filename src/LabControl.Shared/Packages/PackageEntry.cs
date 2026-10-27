using System.Text;
using LabControl.Shared.Files;
using LabControl.Shared.Jobs;

namespace LabControl.Shared.Packages;

/// <summary>How a PC decides a package is installed (D-88).</summary>
public enum DetectKind
{
    /// <summary>An entry in Windows' installed-programs list whose display name starts with the value, ignoring case.</summary>
    UninstallName,

    /// <summary>A file at a fixed Windows path, environment variables such as <c>%ProgramFiles%</c> allowed.</summary>
    Path,
}

public sealed record DetectRule(DetectKind Kind, string Value);

/// <summary>Why an entry cannot be saved — stable codes the console puts into words.</summary>
public enum PackageProblem
{
    None,
    NameMissing,
    NameTooLong,
    VersionInvalid,
    FileNameInvalid,
    HashInvalid,
    SilentInvalid,
    DetectMissing,
    DetectPathInvalid,
    MsiFileExpected,
    TimeoutInvalid,
    RunInvalid,
}

/// <summary>
/// One entry of the package catalog — <c>packages/&lt;id&gt;.yaml</c> on the console, its
/// installer beside it (D-07, D-88). The id is fixed when the entry is created and names
/// the files; everything else the teacher may change. The installer is always run silently
/// as SYSTEM (D-23), so there is no run-as choice.
/// </summary>
public sealed record PackageEntry
{
    public const int MaxNameLength = 80;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = string.Empty;
    public InstallerFamily Family { get; init; }
    public required string File { get; init; }

    /// <summary>For a <c>.zip</c> package, the installer inside it to run (<c>setup.exe</c>, <c>Driver/setup.exe</c>); empty otherwise (D-89 addendum).</summary>
    public string Run { get; init; } = string.Empty;

    /// <summary>The file the PC actually starts: <see cref="Run"/> inside an archive, else <see cref="File"/>.</summary>
    public string Started => Run.Length > 0 ? Path.GetFileName(Run) : File;
    public long Size { get; init; }
    public required string Sha256 { get; init; }
    public string Silent { get; init; } = string.Empty;
    public required DetectRule Detect { get; init; }

    /// <summary>How long the installer may run on a PC before it is ended as stuck (D-89).</summary>
    public int TimeoutMinutes { get; init; } = (int)Defaults.PackageInstallTimeout.TotalMinutes;
    public string ChangedBy { get; init; } = string.Empty;
    public long ChangedAtUnix { get; init; }

    public PackageProblem Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return PackageProblem.NameMissing;
        if (Name.Length > MaxNameLength || Name.Any(char.IsControl)) return PackageProblem.NameTooLong;
        if (Version.Length > 64 || Version.Any(char.IsControl)) return PackageProblem.VersionInvalid;
        if (!SendFileRequest.IsValidName(File)) return PackageProblem.FileNameInvalid;
        if (PackageArchive.IsArchive(File) ? !PackageArchive.IsValidRun(Run) : Run.Length > 0) return PackageProblem.RunInvalid;
        if (Family == InstallerFamily.Msi && !Started.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) return PackageProblem.MsiFileExpected;
        if (!FileHash.LooksLikeSha256(Sha256)) return PackageProblem.HashInvalid;
        if (Silent.Length > 1024 || Silent.Any(char.IsControl)) return PackageProblem.SilentInvalid;
        if (string.IsNullOrWhiteSpace(Detect.Value) || Detect.Value.Length > 260 || Detect.Value.Any(char.IsControl)) return PackageProblem.DetectMissing;
        if (Detect.Kind == DetectKind.Path && !IsWindowsPath(Detect.Value.Trim())) return PackageProblem.DetectPathInvalid;
        if (TimeoutMinutes < 1 || TimeoutMinutes > Defaults.PackageInstallTimeoutMax.TotalMinutes) return PackageProblem.TimeoutInvalid;
        return PackageProblem.None;
    }

    /// <summary><c>C:\…</c> or <c>%Variable%\…</c>: an absolute local path as SYSTEM sees it.</summary>
    public static bool IsWindowsPath(string path)
    {
        if (path.Contains('/') || path.Contains("..", StringComparison.Ordinal) || path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) return false;
        if (path.Length >= 4 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\') return true;
        if (path.StartsWith('%'))
        {
            var close = path.IndexOf('%', 1);
            return close > 1 && close + 2 < path.Length && path[close + 1] == '\\' && path[1..close].All(c => char.IsAsciiLetterOrDigit(c) || c is '(' or ')' or '_');
        }

        return false;
    }

    /// <summary>
    /// The id a name gets: lower-case ASCII letters and digits joined by single hyphens,
    /// at most 48 characters; a name with none of them (Cyrillic only) gets <c>package</c>.
    /// </summary>
    public static string IdFor(string name)
    {
        var result = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                result.Append(char.ToLowerInvariant(c));
            }
            else if (result.Length > 0 && result[^1] != '-')
            {
                result.Append('-');
            }

            if (result.Length >= 48) break;
        }

        var id = result.ToString().Trim('-');
        return id.Length == 0 ? "package" : id;
    }

    public static bool IsValidId(string id) =>
        id.Length is > 0 and <= 48 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !id.StartsWith('-') && !id.EndsWith('-');
}
