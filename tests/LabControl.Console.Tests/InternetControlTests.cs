using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// Internet control end to end (M6 portion 3, PROTOCOL "Internet policy", D-73, D-74): the
/// console's <c>InternetPolicy</c> reaches simulated PCs over the real link, each PC's journal
/// holds it and says so in <c>InternetState</c>, the tile follows that word, the refused names
/// merge across PCs and <i>Allow</i> amends every whitelist that refused one, a PC that
/// relinks re-applies its policy from its own document, and a PC with no enforcer answers with
/// a problem rather than silence.
/// </summary>
public class InternetControlTests
{
    [Fact]
    public async Task A_whitelist_reaches_every_pc_and_the_tile_and_the_refused_names_follow_what_each_pc_reports()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 3));
        var root = TestConsole.TempDirectory();
        var machines = new List<FakeMachine>();
        try
        {
            for (var n = 1; n <= 3; n++)
            {
                var store = FakeMachine.Install(Path.Combine(root, $"PC-{n:00}"), n, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
                var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger($"PC-{n:00}"));
                machines.Add(machine);
                machine.Start();
            }

            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == 3, TimeSpan.FromSeconds(30)));
            Assert.True(await Wait.UntilAsync(() => machines.All(m => console.Session.FindLinked(m.AgentId)?.InternetState is { Mode: InternetPolicy.Types.Mode.Open })));

            var all = machines.Select(m => m.AgentId).ToArray();
            var sent = console.Session.SetInternet(all, InternetPolicy.Types.Mode.Whitelist, ["*.jetbrains.com", "docs.oracle.com"], "Java docs", TimeSpan.FromMinutes(45));
            Assert.Equal(3, sent);

            Assert.True(await Wait.UntilAsync(() => machines.All(m => console.Session.FindLinked(m.AgentId)?.InternetMode == InternetPolicy.Types.Mode.Whitelist), TimeSpan.FromSeconds(15)));
            var state = console.Session.FindLinked(machines[0].AgentId)!.InternetState!;
            Assert.Equal(console.Session.Instance.InstanceId, state.SetByInstance);
            Assert.Equal(["*.jetbrains.com", "docs.oracle.com"], state.AllowedHosts);
            Assert.InRange(state.RemainingSeconds, 44 * 60, 45 * 60);
            Assert.True(state.HardLimitUnix <= DateTimeOffset.UtcNow.Add(Defaults.InternetPolicyHardLimit).ToUnixTimeSeconds() + 5);
            Assert.Equal(InternetPolicy.Types.Mode.Whitelist, machines[0].Internet!.Mode);
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "internet.restricted");

            var tile = Tile(console, machines[0].AgentId);
            Assert.True(tile.HasInternetPolicy);
            Assert.Equal("SITES: Java docs", tile.InternetBadge);
            Assert.Contains("this console", tile.InternetText);

            // Two PCs' students ask for a font host the list does not cover; the third for an allowed one.
            machines[0].Browse("fonts.gstatic.com");
            machines[1].Browse("fonts.gstatic.com");
            machines[2].Browse("resources.jetbrains.com");
            Assert.True(await Wait.UntilAsync(() => console.Session.RefusedNames().Any(r => r.Name == "fonts.gstatic.com" && r.PcCount == 2), TimeSpan.FromSeconds(15)),
                string.Join(", ", console.Session.RefusedNames()));
            Assert.DoesNotContain(console.Session.RefusedNames(), r => r.Name == "resources.jetbrains.com");

            // Allow: every whitelist that does not cover it is amended, the timing kept.
            Assert.Equal(3, console.Session.AllowRefusedName("fonts.gstatic.com"));
            Assert.True(await Wait.UntilAsync(() => machines.All(m => console.Session.FindLinked(m.AgentId)?.InternetState?.AllowedHosts.Contains("fonts.gstatic.com") == true), TimeSpan.FromSeconds(15)));
            Assert.Equal(state.SessionId, console.Session.FindLinked(machines[0].AgentId)!.InternetState!.SessionId);
            Assert.Equal(state.UntilUnix, console.Session.FindLinked(machines[0].AgentId)!.InternetState!.UntilUnix);
            Assert.DoesNotContain(console.Session.RefusedNames(), r => r.Name == "fonts.gstatic.com");

            // Open: every PC lifts, the tiles follow.
            Assert.Equal(3, console.Session.LiftInternet(all));
            Assert.True(await Wait.UntilAsync(() => machines.All(m => console.Session.FindLinked(m.AgentId)?.InternetMode == InternetPolicy.Types.Mode.Open), TimeSpan.FromSeconds(15)));
            Assert.True(await Wait.UntilAsync(() => machines.All(m => m.Internet is null)));
            Assert.False(Tile(console, machines[0].AgentId).HasInternetPolicy);
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "internet.opened");
        }
        finally
        {
            foreach (var machine in machines)
            {
                await machine.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_pc_that_restarts_under_a_block_comes_back_blocked_from_its_own_document()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var directory = Path.Combine(TestConsole.TempDirectory(), "PC-05");
        var store = FakeMachine.Install(directory, 5, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-05"));
        string agentId;
        string sessionId;
        try
        {
            machine.Start();
            agentId = machine.AgentId;
            Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(agentId), TimeSpan.FromSeconds(15)));
            console.Session.SetInternet([agentId], InternetPolicy.Types.Mode.Blocked, [], string.Empty, null);
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(agentId)?.InternetMode == InternetPolicy.Types.Mode.Blocked, TimeSpan.FromSeconds(15)));
            sessionId = console.Session.FindLinked(agentId)!.InternetState!.SessionId;
            Assert.Equal(0, console.Session.FindLinked(agentId)!.InternetState!.UntilUnix);
        }
        finally
        {
            await machine.DisposeAsync();
        }

        Assert.True(await Wait.UntilAsync(() => !console.Session.IsLinked(agentId), TimeSpan.FromSeconds(15)));
        var again = new FakeMachine(DirectoryAgentStore.Open(directory), null, TestLogging.Factory.CreateLogger("PC-05"));
        try
        {
            again.Start();
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(agentId)?.InternetMode == InternetPolicy.Types.Mode.Blocked, TimeSpan.FromSeconds(20)));
            Assert.Equal(sessionId, console.Session.FindLinked(agentId)!.InternetState!.SessionId);
            Assert.Equal(InternetPolicy.Types.Mode.Blocked, again.Internet!.Mode);
            Assert.Equal("NO INTERNET", Tile(console, agentId).InternetBadge);
        }
        finally
        {
            await again.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_pc_without_an_enforcer_answers_with_a_problem_not_silence()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var mute = TestAgent.Install(console, 9, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(mute.AgentId), TimeSpan.FromSeconds(15)));

        Assert.Equal(1, console.Session.SetInternet([mute.AgentId], InternetPolicy.Types.Mode.Blocked, [], string.Empty, TimeSpan.FromMinutes(5)));

        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(mute.AgentId)?.InternetProblem is not null, TimeSpan.FromSeconds(15)));
        Assert.Equal(InternetPolicy.Types.Mode.Open, console.Session.FindLinked(mute.AgentId)!.InternetMode);
        var tile = Tile(console, mute.AgentId);
        Assert.False(tile.HasInternetPolicy);
        Assert.NotEmpty(tile.InternetProblem);
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "internet.refused");
    }

    [Fact]
    public void The_dialog_explains_every_bad_site_and_needs_at_least_one()
    {
        var (hosts, problem) = InternetDialogModel.ParseHosts("https://docs.oracle.com/javase/\n\n*.JetBrains.com\n*.com\n10.0.0.1\nintranet");
        Assert.Equal(["docs.oracle.com", "*.jetbrains.com"], hosts);
        Assert.NotNull(problem);
        Assert.Contains("*.com", problem);
        Assert.Contains("10.0.0.1", problem);
        Assert.Contains("intranet", problem);

        var (_, none) = InternetDialogModel.ParseHosts("  \n ");
        Assert.Equal("Add at least one site, or choose “No internet”.", none);

        var (good, ok) = InternetDialogModel.ParseHosts("docs.oracle.com\ndocs.oracle.com");
        Assert.Null(ok);
        Assert.Single(good);
    }

    private static MachineTileViewModel Tile(TestConsole console, string agentId)
    {
        var machine = console.Session.Registry.Document.Machines.Single(m => m.AgentId == agentId);
        var tile = new MachineTileViewModel(machine, console.Session.Screens.Get(agentId));
        var connection = console.Session.FindLinked(agentId);
        tile.Refresh(machine, connection, connection is null ? console.Session.Ownership(machine) : null, console.Session.Now, false,
            console.Session.Instance.InstanceId, console.Session.NameOfInstance);
        return tile;
    }
}
