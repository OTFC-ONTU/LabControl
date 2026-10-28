using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LabControl.Console.Localization;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Console.Views;
using LabControl.Shared;
using LabControl.Shared.Localization;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>The console's saved language and the selector (M7, D-86).</summary>
public sealed class ConsoleLanguageTests
{
    [Fact]
    public void A_saved_choice_is_read_back_and_an_unreadable_file_means_no_choice()
    {
        var directory = TestConsole.TempDirectory();
        Assert.Null(ConsoleLanguage.ReadStored(directory, out var problem));
        Assert.Null(problem);

        ConsoleLanguage.Save(directory, ProductLanguages.Ukrainian);
        Assert.Equal("uk", ConsoleLanguage.ReadStored(directory, out _));
        ConsoleLanguage.Save(directory, ProductLanguages.Russian);
        Assert.Equal("ru", ConsoleLanguage.ReadStored(directory, out _));

        File.WriteAllText(ConsoleLanguage.PreferencesPath(directory), "{ not json");
        Assert.Null(ConsoleLanguage.ReadStored(directory, out problem));
        Assert.NotNull(problem);

        // A torn file is replaced by the next choice rather than blocking it.
        ConsoleLanguage.Save(directory, ProductLanguages.English);
        Assert.Equal("en", ConsoleLanguage.ReadStored(directory, out problem));
        Assert.Null(problem);
    }

    [Fact]
    public void A_file_from_a_newer_build_is_not_overwritten()
    {
        var directory = TestConsole.TempDirectory();
        var path = ConsoleLanguage.PreferencesPath(directory);
        File.WriteAllText(path, $"{{ \"schema_version\": {Defaults.PreferencesSchemaVersion + 1}, \"language\": \"uk\" }}");

        Assert.Null(ConsoleLanguage.ReadStored(directory, out var problem));
        Assert.NotNull(problem);
        var choice = new LanguageChoiceViewModel(directory, ProductLanguages.English);
        choice.Selected = ProductLanguages.Russian;
        Assert.NotNull(choice.Problem);
        Assert.Contains("\"uk\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_saves_at_once_and_asks_for_a_restart_only_when_it_differs_from_the_screen()
    {
        var directory = TestConsole.TempDirectory();
        var choice = new LanguageChoiceViewModel(directory, ProductLanguages.English);
        Assert.Equal(ProductLanguages.English, choice.Selected);
        Assert.False(choice.NeedsRestart);

        var restarts = 0;
        choice.RestartRequested += () => restarts++;
        choice.Selected = ProductLanguages.Ukrainian;
        Assert.True(choice.NeedsRestart);
        Assert.Equal("uk", ConsoleLanguage.ReadStored(directory, out _));
        choice.RestartCommand.Execute(null);
        Assert.Equal(1, restarts);

        choice.Selected = ProductLanguages.English;
        Assert.False(choice.NeedsRestart);

        // Opened again: the selector shows the saved choice, not the screen's language.
        ConsoleLanguage.Save(directory, ProductLanguages.Russian);
        var reopened = new LanguageChoiceViewModel(directory, ProductLanguages.English);
        Assert.Equal(ProductLanguages.Russian, reopened.Selected);
        Assert.True(reopened.NeedsRestart);
    }

    [Fact]
    public void Every_key_the_console_names_exists_in_the_English_resources()
    {
        var root = RepoRoot();
        var keys = XDocument.Load(Path.Combine(root, "src", "LabControl.Console", "Localization", "Strings.resx")).Root!
            .Elements("data").Select(data => (string)data.Attribute("name")!).ToHashSet(StringComparer.Ordinal);
        var code = new Regex(@"Strings\.(?:Get|Format)\(\s*""([^""]+)""", RegexOptions.Compiled);
        var xaml = new Regex(@"loc:Localize\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);

        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "LabControl.Console"), "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".axaml", StringComparison.Ordinal))
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in code.Matches(text).Concat(xaml.Matches(text)))
            {
                // "Capture." + code is a family of keys built at run time; only whole names are checked.
                if (!match.Groups[1].Value.EndsWith('.') && !keys.Contains(match.Groups[1].Value))
                {
                    missing.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
                }
            }
        }

        Assert.Empty(missing);
    }

    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LabControl.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("LabControl.sln was not found above the test binaries.");
    }
}

[CollectionDefinition(nameof(LanguageRenderCollection), DisableParallelization = true)]
public sealed class LanguageRenderCollection;

/// <summary>
/// The representative windows rendered in each product language (M7, D-67 item 4): every
/// format string runs in that language, no key is missing, and with <c>LABCONTROL_UI_SHOTS</c>
/// set the PNGs (<c>lang-uk-…</c>) are what the owner checks for clipping and wrapping. Runs
/// alone: the interface language is process-wide.
/// </summary>
[Collection(nameof(LanguageRenderCollection))]
public sealed class LanguageRenderTests
{
    [Theory]
    [InlineData("uk", "Мої лабораторії")]
    [InlineData("ru", "Мои лаборатории")]
    [InlineData("en", "My labs")]
    public async Task The_main_windows_and_dialogs_render_in_each_language(string code, string chooserTitle)
    {
        await using var console = await TestConsole.CreateLabAsync("MacBook");
        var codes = console.IssueCodes(3);
        var agents = new List<TestAgent>();
        try
        {
            for (var n = 1; n <= 3; n++)
            {
                agents.Add(TestAgent.Install(console, n, codes[n - 1]).Start());
            }

            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == 3, TimeSpan.FromSeconds(15)));
            agents[1].Link.PublishOverlayState(new OverlayState { Kind = Overlay.Types.Kind.Lock, SessionId = "l2", SetByInstance = console.Session.Instance.InstanceId, RemainingSeconds = 600 });
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(agents[1].AgentId)?.OverlayLocked == true));
            var bootstrap = new ConsoleBootstrap(console.Session.Options, TestLogging.Factory);
            var directory = TestConsole.TempDirectory();
            using var room = SavedLab.Save(directory, "Room 214");
            var chooserBootstrap = new ConsoleBootstrap(new ConsoleOptions { DataDirectory = directory, Port = 0, BindAddress = System.Net.IPAddress.Loopback, BeaconPort = TestConsole.BeaconPort }, TestLogging.Factory);
            await using var controller = new ActiveLabController(chooserBootstrap, TestLogging.Factory);

            var language = ProductLanguages.Find(code)!;
            var done = await UiTests.Session.Dispatch(async () =>
            {
                ConsoleLanguage.Apply(language);
                try
                {
                    var shots = new List<Window>();

                    var chooser = new LabChooserWindow();
                    chooser.DataContext = new LabChooserViewModel(chooserBootstrap, controller, chooser, action => Dispatcher.UIThread.Post(action))
                    {
                        Language = new LanguageChoiceViewModel(directory, language),
                    };
                    chooser.Show();
                    await UiTests.Render(chooser, $"lang-{code}-chooser");
                    Assert.StartsWith(chooserTitle, chooser.Title, StringComparison.Ordinal);
                    shots.Add(chooser);

                    var main = new MainWindow();
                    var vm = new MainViewModel(console.Session, bootstrap, main, action => Dispatcher.UIThread.Post(action));
                    vm.Settings.Language = new LanguageChoiceViewModel(directory, language);
                    main.DataContext = vm;
                    main.Show();
                    await UiTests.Render(main, $"lang-{code}-main");
                    var tabs = main.GetVisualDescendants().OfType<TabControl>().First();
                    for (var tab = 1; tab < tabs.ItemCount; tab++)
                    {
                        tabs.SelectedIndex = tab;
                        await UiTests.Render(main, $"lang-{code}-main-tab{tab}");
                        AssertNoMissingKey(main);
                    }

                    shots.Add(main);

                    Window[] dialogs =
                    [
                        new LockScreensDialog(3),
                        new SendFilesDialog(3),
                        new InternetDialog(new InternetDialogModel(console.Session, 3)),
                        new ExamDialog(new ExamDialogModel(console.Session, 3, wholeLab: true)),
                        new CollectFilesDialog(3),
                        new AddPackageDialog(new AddPackageModel(console.Session.Packages)),
                        new DepartureDialog("Room 214", new DepartureReport([], QueuedJobs: 2, UploadsInProgress: 0, ProbationPcs: [2], PendingWakes: [])),
                    ];
                    foreach (var dialog in dialogs)
                    {
                        dialog.Show();
                        await UiTests.Render(dialog, $"lang-{code}-{dialog.GetType().Name}");
                        shots.Add(dialog);
                    }

                    foreach (var window in shots)
                    {
                        AssertNoMissingKey(window);
                        window.Close();
                    }

                    return true;
                }
                finally
                {
                    ConsoleLanguage.Apply(ProductLanguages.English);
                    CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en");
                }
            }, TestContext.Current.CancellationToken);
            Assert.True(done);
        }
        finally
        {
            foreach (var agent in agents)
            {
                await agent.DisposeAsync();
            }
        }
    }

    private static void AssertNoMissingKey(Window window)
    {
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text)
            .Concat(window.GetVisualDescendants().OfType<ContentControl>().Select(control => control.Content as string))
            .Append(window.Title)
            .Where(text => !string.IsNullOrEmpty(text));
        Assert.DoesNotContain(texts, text => Regex.IsMatch(text!, @"![A-Za-z]+\.[A-Za-z.]+!"));
    }
}
