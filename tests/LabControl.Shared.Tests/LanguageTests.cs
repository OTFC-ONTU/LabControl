using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabControl.Shared.Localization;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>The language rule every component shares (M7, D-67 item 3, D-86).</summary>
public class ProductLanguagesTests
{
    [Fact]
    public void The_selector_lists_the_three_languages_under_their_own_names()
    {
        Assert.Equal(["uk", "en", "ru"], ProductLanguages.All.Select(language => language.Code));
        Assert.Equal(["Українська", "English", "Русский"], ProductLanguages.All.Select(language => language.NativeName));
    }

    [Theory]
    [InlineData("ru", "uk-UA", "ru")]
    [InlineData("UK", "en-US", "uk")]
    [InlineData(null, "uk-UA", "uk")]
    [InlineData(null, "ru-UA", "ru")]
    [InlineData(null, "en-GB", "en")]
    [InlineData(null, "de-DE", "en")]
    [InlineData("", "ru-RU", "ru")]
    [InlineData("pl", "uk", "uk")]
    [InlineData("pl", "fr-FR", "en")]
    public void A_stored_choice_wins_then_a_supported_OS_language_then_English(string? stored, string os, string expected) =>
        Assert.Equal(expected, ProductLanguages.Resolve(stored, CultureInfo.GetCultureInfo(os)).Code);

    [Fact]
    public void The_invariant_culture_is_English() =>
        Assert.Equal("en", ProductLanguages.Resolve(null, CultureInfo.InvariantCulture).Code);
}

/// <summary>
/// Completeness of every translated resource set (M7, D-67 item 4): each neutral
/// <c>*.resx</c> under <c>src/</c> has a Ukrainian and a Russian sibling with exactly its keys,
/// the same placeholders in every string, and a value wherever English has one.
/// </summary>
public class ResourceTranslationTests
{
    private static readonly string[] Translations = ["uk", "ru"];

    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})", RegexOptions.Compiled);

    public static TheoryData<string, string> Sets()
    {
        var data = new TheoryData<string, string>();
        foreach (var neutral in NeutralFiles())
        {
            foreach (var culture in Translations)
            {
                data.Add(Path.GetRelativePath(RepoRoot(), neutral), culture);
            }
        }

        return data;
    }

    [Fact]
    public void Every_product_resource_set_is_found()
    {
        var names = NeutralFiles().Select(Path.GetFileName).ToList();
        Assert.Contains("Strings.resx", names);
        Assert.Contains("SetupStrings.resx", names);
        Assert.Contains("OverlayStrings.resx", names);
        Assert.Contains("PolicyStrings.resx", names);
        Assert.Contains("UsbInstallerStrings.resx", names);
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void A_translation_has_every_key_and_the_same_placeholders(string neutralPath, string culture)
    {
        var neutral = Read(Path.Combine(RepoRoot(), neutralPath));
        var translatedPath = Path.Combine(RepoRoot(), Path.ChangeExtension(neutralPath, culture + ".resx"));
        Assert.True(File.Exists(translatedPath), $"{translatedPath} is missing.");
        var translated = Read(translatedPath);

        Assert.Equal(neutral.Keys.Order(StringComparer.Ordinal), translated.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, english) in neutral)
        {
            var value = translated[key];
            Assert.True(english.Length == 0 || value.Trim().Length > 0, $"{culture} {key} is empty.");
            Assert.True(Placeholders(english).SequenceEqual(Placeholders(value)), $"{culture} {key}: placeholders differ from English.");
            var arguments = Enumerable.Repeat<object?>(1, ArgumentCount(english)).ToArray();
            Assert.True(Format(english, arguments) is null || Format(value, arguments) is not null, $"{culture} {key} does not format like English.");
        }
    }

    private static string? Format(string template, object?[] arguments)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, arguments);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static int ArgumentCount(string text) =>
        Placeholder.Matches(text).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + 1).DefaultIfEmpty(0).Max();

    private static List<string> Placeholders(string text) =>
        [.. Placeholder.Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal)];

    private static Dictionary<string, string> Read(string path) =>
        XDocument.Load(path).Root!.Elements("data").ToDictionary(
            data => (string)data.Attribute("name")!,
            data => (string?)data.Element("value") ?? string.Empty,
            StringComparer.Ordinal);

    private static IEnumerable<string> NeutralFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.resx", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => Path.GetFileNameWithoutExtension(path).IndexOf('.') < 0)
            .Order(StringComparer.Ordinal);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LabControl.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("LabControl.sln was not found above the test binaries.");
    }
}
