namespace LabControl.Shared.Exam;

/// <summary>
/// Executable names as the exam's allowed-programs switch matches them (M6 portion 4, D-75):
/// the file name only, lower case, with <c>.exe</c> — <c>IDEA64</c>, <c>idea64.exe</c> and
/// <c>C:\Program Files\JetBrains\bin\idea64.exe</c> are the same program. Matching is by
/// name only: a renamed executable passes, and the console says so in one sentence.
/// </summary>
public static class ProgramNames
{
    /// <summary>The longest name kept; a longer one is no executable a teacher would type.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// The normalized name, or <c>false</c> when <paramref name="text"/> names no executable —
    /// empty, a bare directory, characters a file name cannot hold, or longer than
    /// <see cref="MaxLength"/>. Never throws.
    /// </summary>
    public static bool TryNormalize(string? text, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().Trim('"').Trim();
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            trimmed = trimmed[(slash + 1)..];
        }

        if (trimmed.Length == 0 || trimmed.IndexOfAny(Invalid) >= 0 || trimmed.Trim('.').Length == 0)
        {
            return false;
        }

        if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += ".exe";
        }

        if (trimmed.Length > MaxLength)
        {
            return false;
        }

        name = trimmed.ToLowerInvariant();
        return true;
    }

    /// <summary>The names a list keeps: normalized, distinct, in the teacher's order, at most <see cref="Defaults.ExamMaxAllowedPrograms"/>; any that does not normalize is dropped.</summary>
    public static List<string> NormalizeAll(IEnumerable<string?>? names)
    {
        var kept = new List<string>();
        foreach (var text in names ?? [])
        {
            if (kept.Count >= Defaults.ExamMaxAllowedPrograms)
            {
                break;
            }

            if (TryNormalize(text, out var name) && !kept.Contains(name, StringComparer.Ordinal))
            {
                kept.Add(name);
            }
        }

        return kept;
    }

    private static readonly char[] Invalid = ['<', '>', ':', '"', '|', '?', '*', '\0', '\r', '\n', '\t'];
}
