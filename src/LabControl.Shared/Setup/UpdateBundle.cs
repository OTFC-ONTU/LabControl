using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Setup;

/// <summary>
/// The agent's reading of a pulled <c>UpdateManifest</c> (PROTOCOL, <c>self_update</c>;
/// D-33): parsed and checked against the job before a single binary is pulled, so a
/// manifest that lies about its version, names a path instead of a file, or lacks the
/// executables a version directory must hold is refused with the reason and nothing is
/// written. Shared by the Windows agent and the simulator so both refuse the same things.
/// </summary>
public static class UpdateBundle
{
    /// <summary>The files every version directory must hold; a bundle without them is refused.</summary>
    public static readonly IReadOnlyList<string> RequiredFiles = [Defaults.AgentExecutableName, Defaults.SessionExecutableName];

    /// <summary>Authenticates before parsing or authorizing any executable download.</summary>
    public static bool TryReadVerified(ReadOnlySpan<byte> manifestBytes, SelfUpdateRequest request,
        X509Certificate2 authority, string installedVersion, out UpdateManifest manifest, out string error)
    {
        manifest = null!;
        error = string.Empty;
        if (!UpdateManifestSignature.Verify(authority, manifestBytes, request.ManifestSignature))
        {
            error = "the manifest has no valid signature from this lab's pinned authority";
            return false;
        }
        if (!TryRead(manifestBytes, request, out var parsed, out error)) return false;
        if (!TryVersion(parsed.MinInstalledVersion, out var minimum) || !TryVersion(installedVersion, out var installed))
        {
            error = "the minimum or installed version is not a supported numeric version";
            return false;
        }
        if (installed < minimum)
        {
            error = $"this update requires installed version {parsed.MinInstalledVersion} or newer";
            return false;
        }
        // A lab-signed older build remains available as an intentional recovery update.
        // The minimum describes compatibility, not an anti-downgrade policy.
        manifest = parsed;
        return true;
    }

    private static bool TryVersion(string text, out Version version)
    {
        version = null!;
        if (!InstallLayout.IsValidVersion(text) || !Version.TryParse(InstallLayout.BaseVersionOf(text), out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    /// <summary>Structural parsing only. Installation callers must use TryReadVerified.</summary>
    public static bool TryRead(ReadOnlySpan<byte> manifestBytes, SelfUpdateRequest request, out UpdateManifest manifest, out string error)
    {
        manifest = null!;
        error = string.Empty;

        UpdateManifest parsed;
        try
        {
            parsed = UpdateManifest.Parser.ParseFrom(manifestBytes);
        }
        catch (InvalidProtocolBufferException ex)
        {
            error = $"the manifest is not an UpdateManifest: {ex.Message}";
            return false;
        }

        if (!string.Equals(parsed.Version, request.Version, StringComparison.Ordinal))
        {
            error = $"the manifest is for version '{parsed.Version}' but the job says '{request.Version}'";
            return false;
        }

        if (parsed.Files.Count == 0)
        {
            error = "the manifest names no files";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in parsed.Files)
        {
            if (!IsPlainFileName(file.RelativePath))
            {
                error = $"'{file.RelativePath}' is not a plain file name; a bundle holds files, not paths";
                return false;
            }

            if (!seen.Add(file.RelativePath))
            {
                error = $"'{file.RelativePath}' is listed twice";
                return false;
            }

            if (file.Size <= 0)
            {
                error = $"'{file.RelativePath}' has no size";
                return false;
            }

            if (!FileHash.LooksLikeSha256(file.Sha256))
            {
                error = $"'{file.RelativePath}' has no usable SHA-256";
                return false;
            }
        }

        foreach (var required in RequiredFiles)
        {
            if (!seen.Contains(required))
            {
                error = $"the bundle has no {required}";
                return false;
            }
        }

        manifest = parsed;
        return true;
    }

    /// <summary>Letters, digits, dash, underscore, dot — and nothing that walks directories or hides as one.</summary>
    public static bool IsPlainFileName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 128
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && name.Trim('.') == name;
}
