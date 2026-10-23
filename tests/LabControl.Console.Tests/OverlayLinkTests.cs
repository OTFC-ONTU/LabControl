using System.Reflection;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Link;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// <see cref="AgentLink"/>'s own overlay delivery over the real link (M6 portion 1, D-69
/// item 7, M3 of the foundation review), with no enforcer in the way: the gate's refusal
/// reaches the console with <c>problem</c> set and no <c>kind</c> while the link's latest
/// state is untouched; the handler gets the validated instance id and its exception is
/// swallowed; a link with no handler answers rather than leaving the console waiting; and
/// the latest state is re-sent after every <c>Welcome</c>.
/// </summary>
public class OverlayLinkTests
{
    [Fact]
    public async Task A_refusal_at_the_gate_reaches_the_wire_with_a_problem_and_no_kind_and_leaves_the_latest_state_alone()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 11, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(pc.AgentId), TimeSpan.FromSeconds(20)));
        var connection = console.Session.FindLinked(pc.AgentId)!;

        // The enforcer's truth, as it would be after a restart: a lock is showing.
        var shown = new OverlayState { Kind = Overlay.Types.Kind.Lock, SessionId = "held", SetByInstance = "someone", HardLimitUnix = 5 };
        pc.Link.PublishOverlayState(shown);
        Assert.True(await Wait.UntilAsync(() => connection.OverlayLocked, TimeSpan.FromSeconds(10)));

        // This console's instance is withdrawn in the PC's own set (the LockTests shortcut).
        var lab = console.Session.Vault!.Peek()!;
        var entry = RevocationSet.Create(lab, LabCertificates.InstanceSerial(console.Session.Instance.InstanceId), "withdrawn in a test", DateTimeOffset.UtcNow);
        var revocations = (RevocationSet)typeof(AgentLink).GetField("_revocations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pc.Link)!;
        Assert.Single(revocations.Merge(console.Authority, [entry]));

        var delivered = 0;
        pc.Link.OverlayReceived += (_, _) => Interlocked.Increment(ref delivered);
        Assert.True(connection.TrySend(new ConsoleMessage { Overlay = new Overlay { Kind = Overlay.Types.Kind.Unlock } }));

        Assert.True(await Wait.UntilAsync(() => connection.OverlayProblem is not null, TimeSpan.FromSeconds(10)));
        Assert.Equal(PolicyEvents.RefusedWithdrawn, connection.OverlayProblem);
        Assert.Equal(0, delivered);

        // The wire carried no kind: the console still shows the lock the PC last described.
        Assert.True(connection.OverlayLocked);
        Assert.Equal("held", connection.OverlayState!.SessionId);
        Assert.Same(shown, pc.Link.LatestOverlayState);
        Assert.True(console.Session.IsLinked(pc.AgentId));
    }

    [Fact]
    public async Task The_handler_gets_the_validated_instance_id_and_its_exception_is_swallowed_without_costing_the_link()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 12, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(pc.AgentId), TimeSpan.FromSeconds(20)));
        var connection = console.Session.FindLinked(pc.AgentId)!;

        var seen = new List<(string Kind, string Instance)>();
        pc.Link.OverlayReceived += (overlay, instance) =>
        {
            lock (seen)
            {
                seen.Add((overlay.SessionId, instance));
            }

            if (overlay.SessionId == "boom")
            {
                throw new InvalidOperationException("the enforcer fell over");
            }

            pc.Link.PublishOverlayState(new OverlayState { Kind = Overlay.Types.Kind.Lock, SessionId = overlay.SessionId, SetByInstance = instance });
        };

        Assert.True(connection.TrySend(new ConsoleMessage { Overlay = new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "boom" } }));
        Assert.True(await Wait.UntilAsync(() => { lock (seen) { return seen.Count == 1; } }, TimeSpan.FromSeconds(10)));
        Assert.Equal(console.Session.Instance.InstanceId, seen[0].Instance);

        // The link is intact: the next overlay is delivered and answered.
        Assert.True(connection.TrySend(new ConsoleMessage { Overlay = new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "after" } }));
        Assert.True(await Wait.UntilAsync(() => connection.OverlayState is { SessionId: "after" }, TimeSpan.FromSeconds(10)));
        Assert.Equal(console.Session.Instance.InstanceId, connection.OverlayState!.SetByInstance);
        Assert.Null(connection.OverlayProblem);
        Assert.True(console.Session.IsLinked(pc.AgentId));
        Assert.Equal(LinkState.Linked, pc.Link.State);
    }

    [Fact]
    public async Task A_link_with_nothing_to_show_overlays_answers_so_the_console_does_not_wait_forever()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 13, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(pc.AgentId), TimeSpan.FromSeconds(20)));
        var connection = console.Session.FindLinked(pc.AgentId)!;

        Assert.Equal(1, console.Session.Lock([pc.AgentId], "nobody home", null));
        Assert.True(await Wait.UntilAsync(() => connection.OverlayProblem is not null, TimeSpan.FromSeconds(10)));
        Assert.False(connection.OverlayLocked);
        Assert.Null(pc.Link.LatestOverlayState);
    }

    [Fact]
    public async Task The_latest_state_is_resent_after_every_welcome()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 14, console.IssueCodes(1)[0]);

        // Published before the first dial, as the enforcer does at service start.
        pc.Link.PublishOverlayState(new OverlayState { Kind = Overlay.Types.Kind.Lock, SessionId = "before-dial", SetByInstance = "earlier" });
        pc.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId)?.OverlayState is { SessionId: "before-dial" }, TimeSpan.FromSeconds(20)));
        var first = console.Session.FindLinked(pc.AgentId)!;

        // The console drops the stream; the PC dials again and the next Welcome is followed by the same state.
        pc.Link.PublishOverlayState(new OverlayState { Kind = Overlay.Types.Kind.Lock, SessionId = "before-relink", SetByInstance = "earlier" });
        Assert.True(await Wait.UntilAsync(() => first.OverlayState is { SessionId: "before-relink" }, TimeSpan.FromSeconds(10)));
        first.Close("test: relink");
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId) is { } again && !ReferenceEquals(again, first) && again.OverlayState is { SessionId: "before-relink" }, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task A_resent_state_counts_its_remaining_time_down_to_the_moment_of_sending()
    {
        // VM drill 2026-10-23: a relinked PC repeated an exam's remaining time as it was minutes
        // earlier, so the badge was behind and Extend added those minutes to the exam.
        var ahead = TimeSpan.Zero;
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 15, console.IssueCodes(1)[0], options: new AgentLinkOptions { Clock = () => DateTimeOffset.UtcNow + ahead }).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(pc.AgentId), TimeSpan.FromSeconds(20)));
        var first = console.Session.FindLinked(pc.AgentId)!;

        pc.Link.PublishOverlayState(new OverlayState { Kind = Overlay.Types.Kind.Lock, SessionId = "lock", SetByInstance = "earlier", RemainingSeconds = 3000 });
        pc.Link.PublishInternetState(new InternetState { Mode = InternetPolicy.Types.Mode.Blocked, SessionId = "net", RemainingSeconds = 3000 });
        pc.Link.PublishExamState(new ExamState { Active = true, SessionId = "exam", RemainingSeconds = 3000 });
        Assert.True(await Wait.UntilAsync(() => first.ExamState is { SessionId: "exam" } && first.InternetState is { SessionId: "net" } && first.OverlayState is { SessionId: "lock" }, TimeSpan.FromSeconds(10)));

        ahead = TimeSpan.FromMinutes(10);
        first.Close("test: relink");
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId) is { ExamState.SessionId: "exam" } again && !ReferenceEquals(again, first), TimeSpan.FromSeconds(30)));
        var again = console.Session.FindLinked(pc.AgentId)!;
        Assert.True(await Wait.UntilAsync(() => again.InternetState is { SessionId: "net" } && again.OverlayState is { SessionId: "lock" }, TimeSpan.FromSeconds(10)));
        Assert.InRange(again.ExamState!.RemainingSeconds, 2390, 2400);
        Assert.InRange(again.InternetState!.RemainingSeconds, 2390, 2400);
        Assert.InRange(again.OverlayState!.RemainingSeconds, 2390, 2400);
        Assert.Equal(3000, pc.Link.LatestExamState!.RemainingSeconds);
    }
}
