using System.Text.RegularExpressions;
using System.Xml.Linq;
using LabControl.Shared.Link;
using LabControl.Shared.Localization;
using LabControl.Shared.Persistence;
using LabControl.Shared.Policy;
using LabControl.Shared.Protection;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LabControl.Shared.Tests;

[CollectionDefinition(nameof(PcLanguageCollection), DisableParallelization = true)]
public sealed class PcLanguageCollection;

/// <summary>
/// What a PC writes to its teacher follows the console's language (M7, D-87): <see cref="PcText"/>
/// and the policy texts read <see cref="PcLanguage.Current"/>, which a link takes from the last
/// console's word kept in <c>agent.json</c>. Runs alone: the language is process-wide, and every
/// test hands it back as English.
/// </summary>
[Collection(nameof(PcLanguageCollection))]
public sealed class PcLanguageTests : IDisposable
{
    public void Dispose() => PcLanguage.Set("en");

    [Fact]
    public void Messages_follow_the_language_the_console_announced()
    {
        PcLanguage.Set("en");
        Assert.Equal("Could not deliver notes.pdf: disk full", PcText.Get("HandoutFailed", "notes.pdf", "disk full"));

        PcLanguage.Set("uk");
        Assert.Equal("Не вдалося доставити notes.pdf: disk full", PcText.Get("HandoutFailed", "notes.pdf", "disk full"));
        Assert.StartsWith("відмовлено", PcText.Get("RefusedTeacherAccess"), StringComparison.Ordinal);

        PcLanguage.Set("ru");
        Assert.Equal("Не удалось доставить notes.pdf: disk full", PcText.Get("HandoutFailed", "notes.pdf", "disk full"));
        Assert.Equal("Текущая версия стабильна.", new Setup.UpdateTerminalReport().Read(() => null, "1.0.0")!.Message);
    }

    [Fact]
    public void An_unknown_or_empty_language_is_English_and_a_missing_key_is_its_name()
    {
        PcLanguage.Set("de");
        Assert.Equal(ProductLanguages.English, PcLanguage.Current);
        PcLanguage.Set(string.Empty);
        Assert.Equal("Rebooting (simulated).", PcText.Get("SimReboot"));
        Assert.Equal("NoSuchKey", PcText.Get("NoSuchKey", 1, 2));
    }

    [Fact]
    public void Every_message_formats_in_every_language()
    {
        var neutral = XDocument.Load(Path.Combine(RepoRoot(), "src", "LabControl.Shared", "Localization", "PcStrings.resx"));
        foreach (var language in ProductLanguages.All)
        {
            PcLanguage.Set(language.Code);
            foreach (var data in neutral.Root!.Elements("data"))
            {
                var name = (string)data.Attribute("name")!;
                var count = Regex.Matches(data.Element("value")!.Value, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value) + 1).DefaultIfEmpty(0).Max();
                var text = PcText.Get(name, Enumerable.Range(0, count).Select(i => (object)("arg" + i)).ToArray());
                Assert.NotEqual(name, text);
                Assert.DoesNotContain("{", text.Replace("{0}", string.Empty), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task A_link_starts_in_the_language_its_last_console_left_in_agent_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), "labcontrol-tests", Guid.NewGuid().ToString("n"));
        using var lab = TestLab.Create();
        var config = new AgentConfigDocument
        {
            LabId = lab.LabId,
            AgentId = Guid.NewGuid().ToString("d"),
            Number = 7,
            Hostname = "PC-07",
            Mac = "02:00:5E:00:00:07",
            ConsoleLanguage = "uk",
        };

        using (var store = DirectoryAgentStore.Install(directory, config, lab.Authority, new KeyProtection(b => b, b => b)))
        {
            PcLanguage.Set("en");
            await using var link = new AgentLink(store, new NoBehaviour(), NullLogger.Instance);
            Assert.Equal(ProductLanguages.Ukrainian, PcLanguage.Current);
            Assert.Equal("На цьому ПК триває іспит; завершіть його перед скиданням профілю, інакше роботу буде втрачено.", PcText.Get("ExamBlocksReset"));
            Assert.Equal("Час іспиту вичерпано; екран заблоковано, доки викладач його не розблокує.", PolicyEvents.Text("ExamTimeUp"));
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LabControl.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("LabControl.sln was not found above the test binaries.");
    }

    private sealed class NoBehaviour : IAgentBehaviour
    {
        public void Describe(Hello hello)
        {
        }

        public Inventory? DescribeInventory() => null;

        public Task<JobResult> RunJobAsync(Job job, Func<JobProgress, Task> report, CancellationToken token) =>
            Task.FromResult(new JobResult { JobId = job.Id, Ok = true });
    }
}
