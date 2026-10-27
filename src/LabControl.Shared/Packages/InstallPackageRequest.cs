using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Packages;

/// <summary>What an <c>install_package</c> job asks of a PC (D-89).</summary>
public enum InstallMode
{
    /// <summary>Run the installer whatever the detect rule says — the test install, a new version.</summary>
    Install,

    /// <summary>Run it only when the detect rule finds nothing — <i>Install on all missing</i>.</summary>
    Missing,

    /// <summary>Only evaluate the detect rule; nothing is pulled or run.</summary>
    Check,
}

/// <summary>
/// The arguments of an <c>install_package</c> job (PROTOCOL, <i>Files</i>; D-23, D-89): the
/// installer by reference and hash, how to run it and how to tell it worked. Everything the PC
/// needs travels in the job, so the agent never reads the catalog. Validated on both sides with
/// the catalog entry's own rules.
/// </summary>
public sealed record InstallPackageRequest(
    string Package,
    string Version,
    string Reference,
    string Sha256,
    string File,
    InstallerFamily Family,
    string Silent,
    DetectRule Detect,
    TimeSpan Timeout,
    InstallMode Mode)
{
    public static InstallPackageRequest For(PackageEntry entry, InstallMode mode) => new(
        entry.Id, entry.Version, entry.Sha256, entry.Sha256, entry.File, entry.Family, entry.Silent, entry.Detect,
        TimeSpan.FromMinutes(entry.TimeoutMinutes), mode);

    public Dictionary<string, string> ToArgs() => new(StringComparer.Ordinal)
    {
        ["package"] = Package,
        ["version"] = Version,
        ["ref"] = Reference,
        ["sha256"] = Sha256,
        ["file"] = File,
        ["family"] = FamilyCode(Family),
        ["silent"] = Silent,
        ["detect_kind"] = Detect.Kind == DetectKind.Path ? "path" : "uninstall_name",
        ["detect"] = Detect.Value,
        ["timeout_seconds"] = ((int)Timeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["mode"] = Mode switch { InstallMode.Missing => "missing", InstallMode.Check => "check", _ => "install" },
    };

    public static bool TryParse(Job job, out InstallPackageRequest request, out string error)
    {
        request = null!;
        error = "Invalid package, installer reference, hash, arguments or detect rule.";
        string Arg(string key) => job.Args.TryGetValue(key, out var value) ? value : string.Empty;
        if (job.Kind != Job.Types.Kind.InstallPackage) return false;
        if (!FileHash.LooksLikeSha256(Arg("ref")) || !FileHash.LooksLikeSha256(Arg("sha256"))) return false;
        if (!int.TryParse(Arg("timeout_seconds"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return false;
        DetectKind? kind = Arg("detect_kind") switch { "path" => DetectKind.Path, "uninstall_name" => DetectKind.UninstallName, _ => null };
        InstallMode? mode = Arg("mode") switch { "install" => InstallMode.Install, "missing" => InstallMode.Missing, "check" => InstallMode.Check, _ => null };
        if (kind is null || mode is null || !SendFileRequest.IsValidName(Arg("file"))) return false;

        var candidate = new InstallPackageRequest(Arg("package"), Arg("version"), Arg("ref"), Arg("sha256").ToLowerInvariant(), Arg("file"),
            ParseFamily(Arg("family")), Arg("silent"), new DetectRule(kind.Value, Arg("detect")), TimeSpan.FromSeconds(seconds), mode.Value);
        var asEntry = new PackageEntry
        {
            Id = candidate.Package, Name = candidate.Package, Version = candidate.Version, Family = candidate.Family, File = candidate.File,
            Sha256 = candidate.Sha256, Silent = candidate.Silent, Detect = candidate.Detect, TimeoutMinutes = Math.Max(1, seconds / 60),
        };
        if (!PackageEntry.IsValidId(candidate.Package) || asEntry.Validate() != PackageProblem.None
            || seconds < 60 || seconds > Defaults.PackageInstallTimeoutMax.TotalSeconds)
        {
            return false;
        }

        request = candidate;
        error = string.Empty;
        return true;
    }

    /// <summary>The catalog's YAML and the job share these names.</summary>
    public static string FamilyCode(InstallerFamily family) => family switch
    {
        InstallerFamily.Msi => "msi",
        InstallerFamily.Nsis => "nsis",
        InstallerFamily.Inno => "inno",
        InstallerFamily.InstallShield => "installshield",
        InstallerFamily.WixBurn => "burn",
        _ => "exe",
    };

    public static InstallerFamily ParseFamily(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "msi" => InstallerFamily.Msi,
        "nsis" => InstallerFamily.Nsis,
        "inno" => InstallerFamily.Inno,
        "installshield" => InstallerFamily.InstallShield,
        "burn" => InstallerFamily.WixBurn,
        _ => InstallerFamily.Unknown,
    };

    /// <summary>
    /// The command line: <c>msiexec.exe /i "&lt;file&gt;" &lt;silent&gt;</c> for an MSI, the
    /// installer itself with the silent arguments as written for everything else. The arguments
    /// are passed verbatim — Windows programs parse their own command line.
    /// </summary>
    public static (string Executable, string Arguments) CommandLine(InstallerFamily family, string stagedPath, string silent, string systemDirectory) =>
        family == InstallerFamily.Msi
            ? (Path.Combine(systemDirectory, "msiexec.exe"), $"/i \"{stagedPath}\" {silent}".TrimEnd())
            : (stagedPath, silent);
}
