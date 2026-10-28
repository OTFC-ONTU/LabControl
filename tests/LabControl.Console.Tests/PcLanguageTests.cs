using LabControl.Console.Services;
using LabControl.FakeAgent;
using LabControl.Shared.Link;
using LabControl.Shared.Localization;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// The console's language travels to the PC (M7 portion 2, D-87): <c>Welcome.language</c> for what
/// the PC writes back, remembered in <c>agent.json</c>, and the language of each lock and exam for
/// the stock words the student sees. In the language collection: what a PC writes is process-wide.
/// </summary>
[Collection(nameof(LanguageRenderCollection))]
public sealed class PcLanguageTests
{
    [Fact]
    public async Task A_russian_consoles_lock_and_exam_reach_the_pc_in_russian_and_the_pc_remembers_the_language()
    {
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.Language = ProductLanguages.Russian;
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-04"), 4, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        try
        {
            await using var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-04"));
            machine.Start();
            Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(machine.AgentId), TimeSpan.FromSeconds(20)));

            Assert.Equal("ru", store.Config.ConsoleLanguage);
            Assert.Equal(ProductLanguages.Russian, PcLanguage.Current);

            Assert.Equal(1, console.Session.Lock([machine.AgentId], "Смотрите на доску", null));
            Assert.True(await Wait.UntilAsync(() => machine.Lock is not null, TimeSpan.FromSeconds(10)));
            Assert.Equal("ru", machine.Lock!.Language);
            console.Session.Unlock([machine.AgentId]);
            Assert.True(await Wait.UntilAsync(() => machine.Lock is null, TimeSpan.FromSeconds(10)));

            var request = new ExamRequest("Вариант 2", TimeSpan.FromMinutes(40), false, [], InternetPolicy.Types.Mode.Open, [], string.Empty, "Practical");
            console.Session.StartExam([machine.AgentId], request, wholeLab: false);
            Assert.True(await Wait.UntilAsync(() => machine.Exam is not null, TimeSpan.FromSeconds(10)));
            Assert.Equal("ru", machine.Exam!.Language);
        }
        finally
        {
            PcLanguage.Set(null);
        }
    }
}
