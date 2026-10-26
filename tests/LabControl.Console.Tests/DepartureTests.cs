using LabControl.Console.Services;
using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Jobs;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// The departure contract for classroom control (M6 portion 6, D-79): the report names what
/// the PCs hold by their own word — a broadcast that ends, a lock and a standalone internet
/// policy that stay until a console ends them or their limit passes, an exam that continues,
/// exam work that will not be collected — and <i>Unlock, open the internet and leave</i> ends
/// the first two and waits for the PCs to confirm, leaving the exam and its own internet switch
/// alone. An agent below <see cref="Defaults.ClassroomControlProtocolVersion"/> is never
/// <i>set</i> anything, yet is still sent every end.
/// </summary>
public class DepartureTests
{
    [Fact]
    public async Task The_report_names_every_policy_and_ending_them_leaves_the_exam_to_its_timer()
    {
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.BroadcastSource = () => new FakeScreen(90, 1280, 720, idle: false);
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

            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == 3, TimeSpan.FromSeconds(20)));
            var (pc1, pc2, pc3) = (machines[0].AgentId, machines[1].AgentId, machines[2].AgentId);
            Assert.True(console.Session.DescribeDeparture().IsEmpty);

            // PC-01: a lock and a standalone block. PC-02: an exam with its own internet switch,
            // collecting its work at the end. PC-03: shown the teacher's screen.
            Assert.Equal(1, console.Session.Lock([pc1], "Look at the board", TimeSpan.FromMinutes(10)));
            Assert.Equal(1, console.Session.SetInternet([pc1], InternetPolicy.Types.Mode.Blocked, [], string.Empty, null));
            console.Session.StartExam([pc2], new ExamRequest("Test", TimeSpan.FromMinutes(40), false, [], InternetPolicy.Types.Mode.Blocked, [], string.Empty, "Practical",
                CollectFolder: CollectRequest.DefaultFolder, CollectDestination: TestConsole.TempDirectory()), wholeLab: false);
            Assert.Equal(1, console.Session.StartBroadcast([pc3]));

            Assert.True(await Wait.UntilAsync(() =>
                console.Session.FindLinked(pc1) is { OverlayLocked: true, InternetMode: InternetPolicy.Types.Mode.Blocked }
                && console.Session.FindLinked(pc2) is { InExam: true, InternetMode: InternetPolicy.Types.Mode.Blocked }
                && console.Session.FindLinked(pc3) is { OverlayBroadcasting: true }, TimeSpan.FromSeconds(20)));

            var now = DateTimeOffset.UtcNow;
            var report = console.Session.DescribeDeparture();
            Assert.False(report.IsEmpty);
            Assert.True(report.CanEndPolicies);
            Assert.Equal([3], report.BroadcastPcs);
            Assert.Equal([1], report.Locks.Pcs);
            Assert.InRange(report.Locks.EndsBy!.Value, now.AddMinutes(9), now.AddMinutes(11));

            // The exam's internet switch is the exam's: it is not a standalone policy to lift.
            Assert.Equal([1], report.Internet.Pcs);
            Assert.InRange(report.Internet.EndsBy!.Value, now.Add(Defaults.InternetPolicyHardLimit).AddMinutes(-1), now.Add(Defaults.InternetPolicyHardLimit).AddMinutes(1));
            Assert.Equal([2], report.Exams.Pcs);
            Assert.InRange(report.Exams.EndsBy!.Value, now.AddMinutes(39), now.AddMinutes(41));
            Assert.Equal([2], report.UncollectedExamPcs);

            // Ending them waits for the PCs' own word and returns nobody still holding one.
            Assert.Empty(await console.Session.EndLocksAndInternetAsync(Defaults.DeparturePolicyEndBudget));
            var linked1 = console.Session.FindLinked(pc1)!;
            Assert.False(linked1.OverlayLocked);
            Assert.Equal(InternetPolicy.Types.Mode.Open, linked1.InternetMode);
            Assert.Null(machines[0].Lock);

            // The exam continues, its internet switch with it, and the broadcast is not touched here.
            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            Assert.True(console.Session.FindLinked(pc2) is { InExam: true, InternetMode: InternetPolicy.Types.Mode.Blocked });
            var after = console.Session.DescribeDeparture();
            Assert.False(after.CanEndPolicies);
            Assert.True(after.Locks.IsEmpty && after.Internet.IsEmpty);
            Assert.Equal([2], after.Exams.Pcs);
            Assert.Equal([3], after.BroadcastPcs);
            Assert.DoesNotContain(console.Session.Events.Recent, e => e.Code == "departure.policies_held");
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
    public async Task A_pc_that_never_confirms_is_named_and_the_wait_is_bounded()
    {
        await using var console = await TestConsole.CreateLabAsync();

        // The mute agent has no enforcer, so its state can only be what the test makes it.
        await using var mute = TestAgent.Install(console, 4, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(mute.AgentId), TimeSpan.FromSeconds(15)));
        var connection = console.Session.FindLinked(mute.AgentId)!;
        connection.ApplyOverlayState(new OverlayState { Kind = Overlay.Types.Kind.Lock, SessionId = "s", RemainingSeconds = 600 });

        var started = DateTimeOffset.UtcNow;
        Assert.Equal([4], await console.Session.EndLocksAndInternetAsync(TimeSpan.FromSeconds(1)));
        Assert.InRange(DateTimeOffset.UtcNow - started, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(5));
        var held = Assert.Single(console.Session.Events.Recent, e => e.Code == "departure.policies_held");
        Assert.Contains("PC-04", held.Message);
    }

    [Fact]
    public async Task An_agent_of_protocol_one_is_never_set_a_policy_but_is_still_sent_every_end()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var old = TestAgent.Install(console, 9, console.IssueCodes(1)[0],
            options: new AgentLinkOptions { ProtocolVersion = Defaults.ClassroomControlProtocolVersion - 1 }).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(old.AgentId), TimeSpan.FromSeconds(15)));

        var connection = console.Session.FindLinked(old.AgentId)!;
        Assert.False(connection.IsOutdated);
        Assert.False(connection.SupportsClassroomControl);
        var warned = Assert.Single(console.Session.Events.Recent, e => e.Code == "link.no_classroom_control");
        Assert.Contains("PC-09", warned.Message);

        Assert.Equal(0, console.Session.Lock([old.AgentId], "no", null));
        Assert.Equal(0, console.Session.SetInternet([old.AgentId], InternetPolicy.Types.Mode.Blocked, [], string.Empty, null));
        Assert.Equal(0, console.Session.StartExam([old.AgentId], new ExamRequest("Test", null, false, [], InternetPolicy.Types.Mode.Open, [], string.Empty, string.Empty), wholeLab: false).Sent);

        // An M6 build from before the bump may hold one: it is still sent the end.
        Assert.Equal(1, console.Session.Unlock([old.AgentId]));
        Assert.Equal(1, console.Session.LiftInternet([old.AgentId]));
    }

    [Fact]
    public void A_collection_in_progress_is_not_collected_and_an_empty_report_asks_nothing()
    {
        Assert.Equal(DepartureConsequence.NotCollected, DepartureReport.ConsequenceOf(Job.Types.Kind.CollectFiles));
        Assert.Equal(DepartureConsequence.ContinuesOnPc, DepartureReport.ConsequenceOf(Job.Types.Kind.ResetProfile));
        Assert.True(DepartureReport.Empty.IsEmpty);
        Assert.False(DepartureReport.Empty.CanEndPolicies);
        Assert.False((DepartureReport.Empty with { Exams = new DeparturePolicy([3], null) }).IsEmpty);
        Assert.False((DepartureReport.Empty with { Exams = new DeparturePolicy([3], null) }).CanEndPolicies);
        Assert.True((DepartureReport.Empty with { Internet = new DeparturePolicy([3], null) }).CanEndPolicies);
        Assert.False((DepartureReport.Empty with { UncollectedExamPcs = [5] }).IsEmpty);
    }
}
