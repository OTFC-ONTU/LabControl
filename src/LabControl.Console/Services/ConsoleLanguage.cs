using System.Globalization;
using LabControl.Console.Localization;
using LabControl.Shared;
using LabControl.Shared.Localization;
using LabControl.Shared.Persistence;

namespace LabControl.Console.Services;

/// <summary>
/// The console's interface language (M7, D-67, D-86): read from <c>preferences.json</c> once,
/// before Avalonia starts, and applied for the whole process. A new choice is saved at once
/// and takes effect on the next start — every window, view model and cached label is built
/// in one language, so nothing is left half-switched.
/// </summary>
public static class ConsoleLanguage
{
    /// <summary>The language this process renders in; English until <see cref="Apply"/>.</summary>
    public static ProductLanguage Running { get; private set; } = ProductLanguages.English;

    /// <summary>The path of <c>preferences.json</c> under a data directory.</summary>
    public static string PreferencesPath(string dataDirectory) => Path.Combine(dataDirectory, Defaults.PreferencesFileName);

    /// <summary>
    /// The stored choice, or <c>null</c> when there is none. An unreadable or newer file is
    /// treated as no choice — the console must start whatever the file holds — and the
    /// reason is returned for the log.
    /// </summary>
    public static string? ReadStored(string dataDirectory, out string? problem)
    {
        problem = null;
        try
        {
            return JsonStore.LoadIfExists<PreferencesDocument>(PreferencesPath(dataDirectory), PreferencesDocument.Migrations)?.Language;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SchemaVersionException or System.Text.Json.JsonException)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>Resolves the language for this data directory and makes it the process's UI culture.</summary>
    public static ProductLanguage Apply(string dataDirectory, out string? problem) =>
        Apply(ProductLanguages.Resolve(ReadStored(dataDirectory, out problem), CultureInfo.CurrentUICulture));

    /// <summary>
    /// Makes <paramref name="language"/> the UI culture: the console's <see cref="Strings"/>, and
    /// every thread's <see cref="CultureInfo.CurrentUICulture"/> so Shared resources (policy
    /// texts, <c>INSTALL.txt</c>) follow too. Number and date formats keep the OS region.
    /// </summary>
    public static ProductLanguage Apply(ProductLanguage language)
    {
        var culture = language.Culture;
        Strings.Culture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Running = language;
        return language;
    }

    /// <summary>Saves the teacher's choice; the rest of <c>preferences.json</c> is kept.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The data directory refuses the write.</exception>
    public static void Save(string dataDirectory, ProductLanguage language)
    {
        var path = PreferencesPath(dataDirectory);
        PreferencesDocument document;
        try
        {
            document = JsonStore.LoadIfExists<PreferencesDocument>(path, PreferencesDocument.Migrations) ?? new PreferencesDocument();
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            // A torn or foreign file holds nothing worth keeping; the choice replaces it.
            document = new PreferencesDocument();
        }

        document.Language = language.Code;
        JsonStore.Save(path, document, PreferencesDocument.Migrations);
    }
}
