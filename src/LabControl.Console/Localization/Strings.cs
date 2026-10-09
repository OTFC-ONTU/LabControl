using System.Globalization;
using System.Reflection;
using System.Resources;

namespace LabControl.Console.Localization;

/// <summary>
/// Every user-visible string comes from here — never from a literal in code or XAML.
/// English is the only culture today; M7 adds Ukrainian and Russian satellite
/// resources next to <c>Strings.resx</c> and an explicit persistent selector (D-67).
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Resources =
        new("LabControl.Console.Localization.Strings", Assembly.GetExecutingAssembly());

    /// <summary>The culture the UI renders in. Changing it re-resolves every lookup.</summary>
    public static CultureInfo Culture { get; set; } = CultureInfo.CurrentUICulture;

    /// <summary>
    /// Resolves a resource key. A missing key returns the key itself wrapped in markers
    /// rather than throwing: a half-translated build must still be usable, and the marker
    /// makes the gap obvious on screen.
    /// </summary>
    public static string Get(string key) =>
        Resources.GetString(key, Culture) ?? $"!{key}!";

    public static string Format(string key, params object?[] args) =>
        string.Format(Culture, Get(key), args);
}
