using System.Globalization;

namespace LabControl.Shared.Localization;

/// <summary>One language the product ships, named in that language for the selector.</summary>
/// <param name="Code">The neutral culture name — <c>uk</c>, <c>en</c>, <c>ru</c> — also what <c>preferences.json</c> stores.</param>
/// <param name="NativeName">The language's own name: a teacher who cannot read the current UI still finds theirs.</param>
public sealed record ProductLanguage(string Code, string NativeName)
{
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Code);
}

/// <summary>
/// The three product languages and the one rule every component uses to pick among them
/// (M7, D-67, D-86): a stored choice wins; without one, a supported OS display language;
/// anything else is English, the neutral resource that always exists.
/// </summary>
public static class ProductLanguages
{
    public static readonly ProductLanguage Ukrainian = new("uk", "Українська");

    public static readonly ProductLanguage English = new("en", "English");

    public static readonly ProductLanguage Russian = new("ru", "Русский");

    /// <summary>In the order the selector shows them (D-67 item 3).</summary>
    public static readonly IReadOnlyList<ProductLanguage> All = [Ukrainian, English, Russian];

    /// <summary>The supported language a culture belongs to — <c>uk-UA</c> is Ukrainian — or <c>null</c>.</summary>
    public static ProductLanguage? Find(CultureInfo? culture)
    {
        for (var current = culture; current is not null && !Equals(current, CultureInfo.InvariantCulture); current = current.Parent)
        {
            if (Find(current.Name) is { } language)
            {
                return language;
            }
        }

        return null;
    }

    /// <summary>The supported language with this code, case-insensitively, or <c>null</c>.</summary>
    public static ProductLanguage? Find(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : All.FirstOrDefault(language => string.Equals(language.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The language a code from the wire or a file names, English for anything else — an empty
    /// field from an older console, a newer build's language (D-87): the neutral resource
    /// always exists, so a PC never shows nothing.
    /// </summary>
    public static ProductLanguage Of(string? code) => Find(code) ?? English;

    /// <summary>
    /// The language to render in: <paramref name="stored"/> when it is a supported code, else the
    /// OS display language when supported, else English. An unknown stored code (a newer build's
    /// language) is ignored rather than refused.
    /// </summary>
    public static ProductLanguage Resolve(string? stored, CultureInfo osCulture) =>
        Find(stored) ?? Find(osCulture) ?? English;
}
