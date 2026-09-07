using LabControl.Shared.Files;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Jobs;

/// <summary>A handout name is a Windows leaf name even when the console runs on macOS.</summary>
public sealed record SendFileRequest(string Reference, string Sha256, string Name, bool Open)
{
    public Dictionary<string, string> ToArgs() => new(StringComparer.Ordinal)
    {
        ["ref"] = Reference, ["sha256"] = Sha256, ["name"] = Name,
        ["open"] = Open ? "true" : "false",
    };

    public static bool TryParse(Job job, out SendFileRequest request, out string error)
    {
        request = null!;
        error = "Invalid handout reference, hash, name or open option.";
        if (job.Kind != Job.Types.Kind.SendFile
            || !job.Args.TryGetValue("ref", out var reference) || !FileHash.LooksLikeSha256(reference)
            || !job.Args.TryGetValue("sha256", out var hash) || !FileHash.LooksLikeSha256(hash)
            || !job.Args.TryGetValue("name", out var name) || !IsValidName(name)) return false;
        var open = false;
        if (job.Args.TryGetValue("open", out var value) && !bool.TryParse(value, out open)) return false;
        request = new(reference, hash, name, open);
        error = string.Empty;
        return true;
    }

    public static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))) return false;
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                 && "123456789¹²³".Contains(stem[3]));
    }

    /// <summary>Opening is limited to document formats; scripts, executables and shortcuts remain files.</summary>
    public bool MayOpen => Open && Path.GetExtension(Name).ToLowerInvariant() is
        ".pdf" or ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" or ".txt" or ".png" or ".jpg" or ".jpeg";
}
