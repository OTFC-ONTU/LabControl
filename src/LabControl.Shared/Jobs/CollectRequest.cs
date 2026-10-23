using LabControl.Shared.Protocol;

namespace LabControl.Shared.Jobs;

/// <summary>Where a <c>collect_files</c> folder starts: a known folder of the student, or the profile itself.</summary>
public enum CollectRoot
{
    Profile,
    Desktop,
    Documents,
    Downloads,
}

/// <summary>
/// A <c>collect_files</c> job (M6 portion 5, D-77): one folder inside the student's profile,
/// written the way the teacher types it (<c>Desktop\Exam</c>, <c>Documents\Java\Lab3</c>). A
/// first part of <c>Desktop</c>, <c>Documents</c> or <c>Downloads</c> means that known folder,
/// wherever Windows keeps it; anything else is relative to the profile. Never absolute, never
/// <c>..</c>: the console cannot name a place outside the student's own files.
/// </summary>
public sealed record CollectRequest(string Folder)
{
    /// <summary>The owner's default (2026-10-23): a folder named Exam on the student's desktop.</summary>
    public const string DefaultFolder = @"Desktop\Exam";

    public const int MaxParts = 8;
    public const int MaxLength = 200;

    public Dictionary<string, string> ToArgs() => new(StringComparer.Ordinal) { ["folder"] = Folder };

    public static bool TryParse(Job job, out CollectRequest request, out string error)
    {
        request = null!;
        error = "Invalid folder to collect.";
        if (job.Kind != Job.Types.Kind.CollectFiles || !job.Args.TryGetValue("folder", out var folder)
            || !TryNormalize(folder, out var normalized))
        {
            return false;
        }

        request = new CollectRequest(normalized);
        error = string.Empty;
        return true;
    }

    /// <summary>The folder in its one form — parts joined by <c>\</c> — or <c>false</c> when it names no folder inside the profile.</summary>
    public static bool TryNormalize(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength || text.Contains(':') || text.StartsWith('\\') || text.StartsWith('/'))
        {
            return false;
        }

        var parts = text.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > MaxParts || parts.Any(p => p is "." or ".." || !SendFileRequest.IsValidName(p)))
        {
            return false;
        }

        normalized = string.Join('\\', parts);
        return true;
    }

    /// <summary>The known folder the path starts in, and the parts below it.</summary>
    public (CollectRoot Root, string[] Below) Split()
    {
        var parts = Folder.Split('\\');
        var root = parts[0].ToUpperInvariant() switch
        {
            "DESKTOP" => CollectRoot.Desktop,
            "DOCUMENTS" => CollectRoot.Documents,
            "DOWNLOADS" => CollectRoot.Downloads,
            _ => CollectRoot.Profile,
        };
        return root == CollectRoot.Profile ? (root, parts) : (root, parts[1..]);
    }
}

/// <summary>Light: empty Desktop, Documents and Downloads. Full: delete the profile and reboot into a new one (D-78).</summary>
public enum ResetMode
{
    Light,
    Full,
}

/// <summary>A <c>reset_profile</c> job (M6 portion 5, D-78).</summary>
public sealed record ResetProfileRequest(ResetMode Mode)
{
    public Dictionary<string, string> ToArgs() => new(StringComparer.Ordinal) { ["mode"] = Mode == ResetMode.Full ? "full" : "light" };

    public static bool TryParse(Job job, out ResetProfileRequest request, out string error)
    {
        request = null!;
        error = "Invalid profile reset mode.";
        if (job.Kind != Job.Types.Kind.ResetProfile || !job.Args.TryGetValue("mode", out var mode))
        {
            return false;
        }

        ResetMode? parsed = mode switch { "light" => ResetMode.Light, "full" => ResetMode.Full, _ => null };
        if (parsed is null)
        {
            return false;
        }

        request = new ResetProfileRequest(parsed.Value);
        error = string.Empty;
        return true;
    }
}
