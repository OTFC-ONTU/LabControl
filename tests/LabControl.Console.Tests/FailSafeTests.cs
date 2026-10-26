using LabControl.Console.Services;
using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// The fail-safe drill in miniature (M6 portion 6, D-79 item 5): the drill on a real PC needs
/// an explicitly short hard limit, because the real ones are hours long, and a separate proof
/// that a far limit is clamped to the agent's ceiling — the short one alone would never show
/// that the ceiling holds. Both run here over the real link with simulated PCs.
/// </summary>
public class FailSafeTests
{
    [Fact]
    public async Task With_the_console_gone_every_pc_restores_itself_at_a_short_hard_limit()
    {
        var console = await TestConsole.CreateLabAsync();
        var disposed = false;
        var machines = new List<FakeMachine>();
        try
        {
            console.Session.LockLimit = TimeSpan.FromSeconds(4);
            console.Session.InternetLimit = TimeSpan.FromSeconds(4);
            console.Session.ExamLimit = TimeSpan.FromSeconds(4);
            var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 2));
            var root = TestConsole.TempDirectory();
            for (var n = 1; n <= 2; n++)
            {
                var store = FakeMachine.Install(Path.Combine(root, $"PC-{n:00}"), n, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
                var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger($"PC-{n:00}"));
                machines.Add(machine);
                machine.Start();
            }

            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == 2, TimeSpan.FromSeconds(20)));
            var (exam, policies) = (machines[0], machines[1]);

            // An exam with the internet blocked and the whitelist on, its timer far beyond the limit;
            // on the other PC a lock and a whitelist, both "until lifted".
            var (_, sent) = console.Session.StartExam([exam.AgentId], new ExamRequest("Test", TimeSpan.FromMinutes(40), true, ["idea64.exe"],
                InternetPolicy.Types.Mode.Blocked, [], string.Empty, "Drill"), wholeLab: false);
            Assert.Equal(1, sent);
            Assert.Equal(1, console.Session.Lock([policies.AgentId], "Wait", null));
            Assert.Equal(1, console.Session.SetInternet([policies.AgentId], InternetPolicy.Types.Mode.Whitelist, ["docs.oracle.com"], "Drill", null));
            Assert.True(await Wait.UntilAsync(() => exam.Exam is not null && exam.Internet is not null && policies.Lock is not null && policies.Internet is not null,
                TimeSpan.FromSeconds(10)));

            // The console's laptop closes.
            await console.DisposeAsync();
            disposed = true;

            Assert.True(await Wait.UntilAsync(() => exam.Exam is null && exam.Internet is null && policies.Lock is null && policies.Internet is null,
                TimeSpan.FromSeconds(20)), $"exam {exam.Exam is not null}, its internet {exam.Internet is not null}, lock {policies.Lock is not null}, internet {policies.Internet is not null}");
        }
        finally
        {
            foreach (var machine in machines)
            {
                await machine.DisposeAsync();
            }

            if (!disposed)
            {
                await console.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_far_hard_limit_is_clamped_to_the_pcs_own_ceiling()
    {
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.LockLimit = TimeSpan.FromDays(30);
        console.Session.InternetLimit = TimeSpan.FromDays(30);
        console.Session.ExamLimit = TimeSpan.FromDays(30);
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 2));
        var root = TestConsole.TempDirectory();
        var stores = Enumerable.Range(1, 2).Select(n =>
            FakeMachine.Install(Path.Combine(root, $"PC-{n:00}"), n, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false)).ToArray();
        await using var first = new FakeMachine(stores[0], null, TestLogging.Factory.CreateLogger("PC-01"));
        await using var second = new FakeMachine(stores[1], null, TestLogging.Factory.CreateLogger("PC-02"));
        first.Start();
        second.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == 2, TimeSpan.FromSeconds(20)));

        console.Session.Lock([first.AgentId], "far", null);
        console.Session.SetInternet([first.AgentId], InternetPolicy.Types.Mode.Blocked, [], string.Empty, null);
        console.Session.StartExam([second.AgentId], new ExamRequest("Test", null, false, [], InternetPolicy.Types.Mode.Open, [], string.Empty, string.Empty), wholeLab: false);
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(first.AgentId) is { OverlayLocked: true, InternetMode: InternetPolicy.Types.Mode.Blocked }
            && console.Session.FindLinked(second.AgentId) is { InExam: true }, TimeSpan.FromSeconds(15)));

        var now = DateTimeOffset.UtcNow;
        var pc1 = console.Session.FindLinked(first.AgentId)!;
        var pc2 = console.Session.FindLinked(second.AgentId)!;
        Assert.InRange(pc1.OverlayState!.HardLimitUnix, now.ToUnixTimeSeconds(), now.Add(Defaults.LockHardLimit).ToUnixTimeSeconds() + 5);
        Assert.InRange(pc1.InternetState!.HardLimitUnix, now.ToUnixTimeSeconds(), now.Add(Defaults.InternetPolicyHardLimit).ToUnixTimeSeconds() + 5);
        Assert.InRange(pc2.ExamState!.HardLimitUnix, now.ToUnixTimeSeconds(), now.Add(Defaults.ExamHardLimit).ToUnixTimeSeconds() + 5);
    }
}
