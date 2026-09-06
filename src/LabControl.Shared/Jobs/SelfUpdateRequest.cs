using LabControl.Shared.Files;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;

namespace LabControl.Shared.Jobs;

/// <summary>
/// The arguments of a <c>self_update</c> job (PROTOCOL, <c>self_update</c>; D-33), parsed
/// and validated once so the console builds them and the agent reads them through the same
/// names. The job itself stays tiny — it is part of the frozen subset (D-19) — and carries
/// only the version being offered and how to fetch the <c>UpdateManifest</c>; the manifest
/// names the files, and every file travels through <c>PullFile</c> under its own hash.
/// </summary>
public sealed record SelfUpdateRequest(string Version, string ManifestReference, string ManifestSha256)
{
    public const string VersionKey = "version";
    public const string ReferenceKey = "ref";
    public const string Sha256Key = "sha256";

    /// <summary>The wire form: every value spelled the one way the agent parses.</summary>
    public Dictionary<string, string> ToArgs() => new(StringComparer.Ordinal)
    {
        [VersionKey] = Version,
        [ReferenceKey] = ManifestReference,
        [Sha256Key] = ManifestSha256,
    };

    /// <summary>Reads a job's arguments; a job this build cannot apply is refused with the reason.</summary>
    public static bool TryParse(Job job, out SelfUpdateRequest request, out string error)
    {
        request = null!;
        error = string.Empty;

        if (job.Kind != Job.Types.Kind.SelfUpdate)
        {
            error = $"job {job.Id} is a {job.Kind}, not a self_update";
            return false;
        }

        if (!job.Args.TryGetValue(VersionKey, out var version) || !InstallLayout.IsValidVersion(version.Trim()))
        {
            error = $"the job has no usable '{VersionKey}' — nothing to name the version directory";
            return false;
        }

        if (!job.Args.TryGetValue(ReferenceKey, out var reference) || string.IsNullOrWhiteSpace(reference))
        {
            error = $"the job has no '{ReferenceKey}' — no manifest to pull";
            return false;
        }

        if (!job.Args.TryGetValue(Sha256Key, out var sha256) || !FileHash.LooksLikeSha256(sha256))
        {
            error = $"the job has no usable '{Sha256Key}'; the manifest cannot be verified";
            return false;
        }

        request = new SelfUpdateRequest(version.Trim(), reference.Trim(), sha256.ToLowerInvariant());
        return true;
    }
}
