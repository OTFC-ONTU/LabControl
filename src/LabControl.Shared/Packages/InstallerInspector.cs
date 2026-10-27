using System.Security.Cryptography;

namespace LabControl.Shared.Packages;

/// <summary>The installer technologies the wizard recognises (D-88); each has its own silent arguments.</summary>
public enum InstallerFamily
{
    Unknown,
    Msi,
    Nsis,
    Inno,
    InstallShield,
    WixBurn,
}

/// <summary>What <see cref="InstallerInspector"/> found in a file — proposals only; the teacher may change every one.</summary>
public sealed record InstallerInfo(
    string FileName,
    long Size,
    string Sha256,
    InstallerFamily Family,
    string? ProductName,
    string? ProductVersion,
    string? Manufacturer,
    string? ProductCode);

/// <summary>
/// Looks at a local <c>.exe</c> / <c>.msi</c> without running it (D-88): one pass computes the
/// SHA-256 and searches the first <see cref="MarkerWindow"/> bytes for the markers each
/// installer technology leaves; an MSI's <c>Property</c> table or an executable's version
/// resource gives the product's name and version. Nothing here needs Windows.
/// </summary>
public static class InstallerInspector
{
    /// <summary>How far into the file the family markers are searched; every known stub puts them near the start.</summary>
    public const int MarkerWindow = 32 * 1024 * 1024;

    private static readonly (InstallerFamily Family, byte[] Marker)[] Markers =
    [
        (InstallerFamily.Nsis, [0xEF, 0xBE, 0xAD, 0xDE, .. "NullsoftInst"u8]),
        (InstallerFamily.Inno, [.. "Inno Setup Setup Data"u8]),
        (InstallerFamily.Inno, [.. "rDlPtS02"u8]),
        (InstallerFamily.InstallShield, [.. "InstallShield"u8]),
    ];

    /// <summary>The silent arguments proposed for each family; empty when the family is unknown.</summary>
    public static string SilentArguments(InstallerFamily family) => family switch
    {
        InstallerFamily.Msi => "/qn /norestart",
        InstallerFamily.Nsis => "/S",
        InstallerFamily.Inno => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
        InstallerFamily.InstallShield => "/s /v\"/qn /norestart\"",
        InstallerFamily.WixBurn => "/quiet /norestart",
        _ => string.Empty,
    };

    public static async Task<InstallerInfo> InspectAsync(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var size = file.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        var window = new MemoryStream();
        long read = 0;
        int n;
        while ((n = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            if (read < MarkerWindow)
            {
                window.Write(buffer, 0, (int)Math.Min(n, MarkerWindow - read));
            }

            read += n;
            progress?.Report(size == 0 ? 1 : (double)read / size);
        }

        var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
        var head = window.ToArray();
        var info = new InstallerInfo(Path.GetFileName(path), read, sha, InstallerFamily.Unknown, null, null, null, null);

        if (head.AsSpan().StartsWith(CompoundFile.Signature))
        {
            try
            {
                var properties = MsiProperties.Read(file);
                string? Get(string key) => properties.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
                return info with
                {
                    Family = InstallerFamily.Msi,
                    ProductName = Get("ProductName"),
                    ProductVersion = Get("ProductVersion"),
                    Manufacturer = Get("Manufacturer"),
                    ProductCode = Get("ProductCode"),
                };
            }
            catch (InvalidDataException)
            {
                // A compound file that is not a readable database: a Word document, a damaged MSI.
                return info;
            }
        }

        var version = PeVersionInfo.Read(file);
        if (version is null)
        {
            return info;
        }

        return info with
        {
            Family = FamilyOf(version, head),
            ProductName = version.Get("ProductName") ?? version.Get("FileDescription"),
            ProductVersion = CleanVersion(version.Get("ProductVersion")) ?? CleanVersion(version.Get("FileVersion")) ?? version.FixedProductVersion,
            Manufacturer = version.Get("CompanyName"),
        };
    }

    private static InstallerFamily FamilyOf(PeVersionInfo version, byte[] head)
    {
        if (version.Sections.Contains(".wixburn", StringComparer.Ordinal))
        {
            return InstallerFamily.WixBurn;
        }

        foreach (var (family, marker) in Markers)
        {
            if (head.AsSpan().IndexOf(marker) >= 0)
            {
                return family;
            }
        }

        // The stubs also sign their version resource: Inno's "Comments", InstallShield's description.
        var text = string.Join("\n", version.Strings.Values);
        if (text.Contains("Inno Setup", StringComparison.OrdinalIgnoreCase)) return InstallerFamily.Inno;
        if (text.Contains("InstallShield", StringComparison.OrdinalIgnoreCase)) return InstallerFamily.InstallShield;
        if (text.Contains("Nullsoft", StringComparison.OrdinalIgnoreCase)) return InstallerFamily.Nsis;
        return InstallerFamily.Unknown;
    }

    /// <summary>"2026.2 (build 262.1)" → "2026.2"; a version string ends at its first space or comma.</summary>
    private static string? CleanVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        var end = trimmed.IndexOfAny([' ', ',', '\t']);
        var result = end > 0 ? trimmed[..end] : trimmed;
        return result.Any(char.IsDigit) ? result : null;
    }
}
