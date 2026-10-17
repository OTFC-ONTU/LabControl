using System.Globalization;
using System.Net;

namespace LabControl.Shared.Internet;

/// <summary>
/// The whitelist's site names (M6 portion 3, D-74): what a teacher typed or pasted turned into
/// a pattern the resolver can match — a plain host (<c>docs.oracle.com</c>) or one leading
/// wildcard (<c>*.jetbrains.com</c>) — and the match itself. Hostnames only: with HTTPS a URL
/// path never reaches the machine, so a pasted URL keeps its host and loses the rest
/// (ARCHITECTURE §6.2). A refusal is a stable code from <see cref="Problems"/> the console puts
/// into words; nothing here is a sentence.
/// </summary>
public static class HostPattern
{
    /// <summary>The stable reasons <see cref="TryNormalize"/> refuses an entry.</summary>
    public static class Problems
    {
        /// <summary>Nothing left once spaces, a scheme and a path are taken away.</summary>
        public const string Empty = "host.empty";

        /// <summary>Not a hostname: a bad character, an empty or over-long label, an over-long name.</summary>
        public const string Invalid = "host.invalid";

        /// <summary>An IPv4 or IPv6 address; the whitelist names sites, the lab's subnet is exempt anyway.</summary>
        public const string IpLiteral = "host.ip_literal";

        /// <summary><c>*</c> alone or a wildcard over a single label (<c>*.com</c>): the whole internet, near enough.</summary>
        public const string WildcardTooBroad = "host.wildcard_too_broad";

        /// <summary>A <c>*</c> anywhere but as the whole first label (<c>docs.*.com</c>, <c>*docs.com</c>).</summary>
        public const string WildcardMisplaced = "host.wildcard_misplaced";

        /// <summary>A name of one label (<c>localhost</c>, <c>intranet</c>), which the internet never resolves.</summary>
        public const string SingleLabel = "host.single_label";
    }

    /// <summary>The prefix of a wildcard pattern: the site itself and everything under it.</summary>
    public const string WildcardPrefix = "*.";

    private const int MaxNameLength = 253;
    private const int MaxLabelLength = 63;

    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true, AllowUnassigned = false };

    /// <summary>
    /// Turns what the teacher entered into a pattern: trimmed and lowercased, a pasted URL cut
    /// down to its host (scheme, user, port, path, query and fragment dropped), one trailing dot
    /// removed, an international name spelled in punycode, then checked label by label.
    /// </summary>
    /// <param name="input">The entry as typed or pasted.</param>
    /// <param name="pattern">The normalized pattern; empty when refused.</param>
    /// <param name="problemCode">A <see cref="Problems"/> code; empty on success.</param>
    public static bool TryNormalize(string? input, out string pattern, out string problemCode)
    {
        pattern = string.Empty;
        problemCode = string.Empty;

        var text = (input ?? string.Empty).Trim().ToLowerInvariant();

        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }

        var pathStart = text.IndexOfAny(['/', '?', '#', '\\']);
        if (pathStart >= 0)
        {
            text = text[..pathStart];
        }

        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            text = text[(at + 1)..];
        }

        if (text.StartsWith('['))
        {
            // A bracketed IPv6 literal, with or without a port.
            problemCode = Problems.IpLiteral;
            return false;
        }

        var colons = text.Count(c => c == ':');
        if (colons > 1)
        {
            problemCode = IPAddress.TryParse(text, out _) ? Problems.IpLiteral : Problems.Invalid;
            return false;
        }

        if (colons == 1)
        {
            var port = text[(text.IndexOf(':') + 1)..];
            if (port.Length > 0 && !port.All(char.IsAsciiDigit))
            {
                problemCode = Problems.Invalid;
                return false;
            }

            text = text[..text.IndexOf(':')];
        }

        if (text.EndsWith('.'))
        {
            text = text[..^1];
        }

        if (text.Length == 0)
        {
            problemCode = Problems.Empty;
            return false;
        }

        if (text == "*" || text == WildcardPrefix)
        {
            problemCode = Problems.WildcardTooBroad;
            return false;
        }

        var wildcard = text.StartsWith(WildcardPrefix, StringComparison.Ordinal);
        var name = wildcard ? text[WildcardPrefix.Length..] : text;

        if (name.Contains('*'))
        {
            problemCode = Problems.WildcardMisplaced;
            return false;
        }

        if (IsIpLiteral(name))
        {
            problemCode = Problems.IpLiteral;
            return false;
        }

        if (!name.All(char.IsAscii))
        {
            try
            {
                name = Idn.GetAscii(name).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                problemCode = Problems.Invalid;
                return false;
            }
        }

        if (!IsLdhName(name, out var labels))
        {
            problemCode = Problems.Invalid;
            return false;
        }

        if (labels == 1)
        {
            problemCode = wildcard ? Problems.WildcardTooBroad : Problems.SingleLabel;
            return false;
        }

        pattern = wildcard ? WildcardPrefix + name : name;
        return true;
    }

    /// <summary>
    /// Whether a normalized <paramref name="pattern"/> covers the query <paramref name="name"/>:
    /// a plain pattern only the same name, <c>*.x</c> the name <c>x</c> itself and every name
    /// under it — but never <c>evilx</c>, which merely ends with the same letters.
    /// </summary>
    public static bool Matches(string pattern, string name)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(name);

        var candidate = name.EndsWith('.') ? name.AsSpan(0, name.Length - 1) : name.AsSpan();
        if (candidate.Length == 0 || pattern.Length == 0)
        {
            return false;
        }

        if (!pattern.StartsWith(WildcardPrefix, StringComparison.Ordinal))
        {
            return candidate.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }

        var apex = pattern.AsSpan(WildcardPrefix.Length);
        if (apex.Length == 0)
        {
            return false;
        }

        if (candidate.Equals(apex, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.Length > apex.Length + 1
            && candidate[candidate.Length - apex.Length - 1] == '.'
            && candidate[(candidate.Length - apex.Length)..].Equals(apex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether any of the normalized <paramref name="patterns"/> covers <paramref name="name"/>.</summary>
    public static bool AnyMatches(IReadOnlyList<string> patterns, string name)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        for (var i = 0; i < patterns.Count; i++)
        {
            if (Matches(patterns[i], name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A query name as the resolver compares it — lowercased, without the trailing dot — or
    /// <c>null</c> when it is not a name at all (empty, the root, an over-long or empty label,
    /// a character outside letters, digits, hyphen and underscore). Underscores are allowed
    /// because service lookups (<c>_ldap._tcp…</c>) are real queries, not garbage.
    /// </summary>
    public static string? NormalizeName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var text = name.EndsWith('.') ? name[..^1] : name;
        if (text.Length == 0 || text.Length > MaxNameLength)
        {
            return null;
        }

        var labelLength = 0;
        foreach (var c in text)
        {
            if (c == '.')
            {
                if (labelLength == 0)
                {
                    return null;
                }

                labelLength = 0;
                continue;
            }

            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_') || ++labelLength > MaxLabelLength)
            {
                return null;
            }
        }

        return labelLength == 0 ? null : text.ToLowerInvariant();
    }

    private static bool IsIpLiteral(string name)
    {
        if (name.Contains(':'))
        {
            return IPAddress.TryParse(name, out _);
        }

        // Only dotted digits count as IPv4; IPAddress.TryParse would also take "10" or "1.2".
        return name.Length > 0
            && name.All(c => char.IsAsciiDigit(c) || c == '.')
            && name.Count(c => c == '.') == 3
            && IPAddress.TryParse(name, out _);
    }

    private static bool IsLdhName(string name, out int labels)
    {
        labels = 0;
        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            return false;
        }

        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0 || label.Length > MaxLabelLength || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            if (!label.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            {
                return false;
            }

            labels++;
        }

        // An all-digit top-level label is never a real name (and "1.2.3.4.5" is no site).
        return !name[(name.LastIndexOf('.') + 1)..].All(char.IsAsciiDigit);
    }
}
