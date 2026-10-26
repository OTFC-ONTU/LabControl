using System.Globalization;
using System.Reflection;
using System.Resources;

namespace LabControl.Console.Localization;

/// <summary>
/// Every user-visible string comes from here — never from a literal in code or XAML.
/// English is the neutral resource; Ukrainian and Russian are satellites beside it
/// (<c>Strings.uk.resx</c>, <c>Strings.ru.resx</c>), and <see cref="Services.ConsoleLanguage"/>
/// picks one at start (D-67, D-86).
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Resources =
        new("LabControl.Console.Localization.Strings", Assembly.GetExecutingAssembly());

    /// <summary>
    /// The culture the UI renders in. Changing it re-resolves every later lookup; English until
    /// <see cref="Services.ConsoleLanguage.Apply(LabControl.Shared.Localization.ProductLanguage)"/>
    /// sets the chosen one, so a host that never chose (the headless tests) is deterministic.
    /// </summary>
    public static CultureInfo Culture { get; set; } = CultureInfo.GetCultureInfo("en");

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
