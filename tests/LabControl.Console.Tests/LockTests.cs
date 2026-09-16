using System.Reflection;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Discovery;
using LabControl.Shared.Identity;
using LabControl.Shared.Link;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// The lock end to end (M6 portion 1, PROTOCOL "Overlay", D-69, D-70): the console's
/// <c>Overlay</c> reaches simulated PCs over the real link, each PC's own journal decides
/// what it shows and says so in <c>OverlayState</c>, the tile follows that word and not the
/// request, a PC that restarts re-locks itself from its own <c>policy.json</c>, the hard
/// limit ends a lock on the PC's own clock with the console silent, a console the PC
/// holds revoked is refused at the gate, a lock outlives the console's departure, a second
/// console of the lab sees whose lock it is and may end it, a torn <c>policy.json</c> or a
/// machine that throws still links, a busy mutex is answered on the wire, and a PC that is
/// unlinked or outdated is skipped rather than queued.
/// </summary>
public class LockTests
{
    /// <summary>A full lab: twenty-nine simulated PCs with the real journal, and one that never answers.</summary>
    private const int Simulated = Defaults.MaxStudentPcs - 1;

    [Fact]
    public async Task Thirty_pcs_lock_and_unlock_and_the_tile_follows_what_each_pc_reports_not_the_request()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: Simulated));
        var root = TestConsole.TempDirectory();
        var machines = new List<FakeMachine>();
        try
        {
            for (var n = 1; n <= Simulated; n++)
            {
                var store = FakeMachine.Install(Path.Combine(root, $"PC-{n:00}"), n, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
                var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger($"PC-{n:00}"));
                machines.Add(machine);
                machine.Start();
            }

            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == Simulated, TimeSpan.FromSeconds(30)),
                $"{console.Session.Linked.Count} of {Simulated} linked");

            // Every PC said what it shows right after Welcome: nothing, with a hard limit of nothing.
            Assert.True(await Wait.UntilAsync(() => machines.All(m => console.Session.FindLinked(m.AgentId)?.OverlayState is { Kind: Overlay.Types.Kind.Unspecified })));

            // The thirtieth PC has no enforcer: its link answers every Overlay with a problem
            // and no kind ("not in this build"), so its tile carries a LockProblem but no lock.
            await using var mute = TestAgent.Install(console, Defaults.MaxStudentPcs, console.IssueCodes(1)[0]).Start();
            Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(mute.AgentId), TimeSpan.FromSeconds(15)));

            var all = machines.Select(m => m.AgentId).Append(mute.AgentId).ToArray();
            var sent = console.Session.Lock(all, "Дивіться на дошку", TimeSpan.FromMinutes(5));
            Assert.Equal(Defaults.MaxStudentPcs, sent);

            // The tile follows OverlayState: twenty-nine lock, the mute one never does.
            Assert.True(await Wait.UntilAsync(() => machines.All(m => console.Session.FindLinked(m.AgentId)?.OverlayLocked == true), TimeSpan.FromSeconds(20)),
                $"{machines.Count(m => console.Session.FindLinked(m.AgentId)?.OverlayLocked == true)} locked");
            var muteTile = Tile(console, mute.AgentId);
            Assert.False(muteTile.IsLocked);
            Assert.Equal(string.Empty, muteTile.LockText);

            var first = machines[0];
            var state = console.Session.FindLinked(first.AgentId)!.OverlayState!;
            Assert.Equal(console.Session.Instance.InstanceId, state.SetByInstance);
            Assert.True(state.InputBlocked);
            Assert.NotEmpty(state.SessionId);
            Assert.True(state.UntilUnix > 0);
            Assert.True(state.HardLimitUnix <= DateTimeOffset.UtcNow.Add(Defaults.LockHardLimit).ToUnixTimeSeconds() + 5);
            Assert.All(machines, m => Assert.Equal(state.SessionId, console.Session.FindLinked(m.AgentId)!.OverlayState!.SessionId));

            var tile = Tile(console, first.AgentId);
            Assert.True(tile.IsLocked);
            Assert.Equal("locked by this console", tile.LockText);
            Assert.Equal(string.Empty, tile.LockProblem);

            // The PC's own journal holds it, on disk, and its desktop draws the picture.
            Assert.NotNull(first.Lock);
            Assert.Equal("Дивіться на дошку", first.Lock!.Message);
            Assert.True(File.Exists(first.Policy.DocumentPath));
            Assert.True(await Wait.UntilAsync(() => first.Desktop?.Overlay is not null, TimeSpan.FromSeconds(10)));
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "overlay.locked");

            // Unlock: every PC lifts, the tiles follow, the journals are empty.
            Assert.Equal(Defaults.MaxStudentPcs, console.Session.Unlock(all));
            Assert.True(await Wait.UntilAsync(() => machines.All(m => console.Session.FindLinked(m.AgentId)?.OverlayLocked == false), TimeSpan.FromSeconds(20)));
            Assert.True(await Wait.UntilAsync(() => machines.All(m => m.Lock is null)));
            Assert.False(Tile(console, first.AgentId).IsLocked);
            Assert.True(await Wait.UntilAsync(() => first.Desktop?.Overlay is null, TimeSpan.FromSeconds(10)));
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "overlay.unlocked");
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
    public async Task A_pc_that_relinks_mid_lock_is_locked_again_from_its_own_document()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var directory = Path.Combine(TestConsole.TempDirectory(), "PC-07");
        var store = FakeMachine.Install(directory, 7, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        string agentId;
        string sessionId;

        var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-07"));
        try
        {
            machine.Start();
            agentId = machine.AgentId;
            Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(agentId), TimeSpan.FromSeconds(20)));

            Assert.Equal(1, console.Session.Lock([agentId], "Test in progress", null));
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(agentId)?.OverlayLocked == true, TimeSpan.FromSeconds(10)));
            sessionId = console.Session.FindLinked(agentId)!.OverlayState!.SessionId;
            Assert.Equal(0, console.Session.FindLinked(agentId)!.OverlayState!.UntilUnix);
        }
        finally
        {
            // The service stops (OnStop restores, the document stays) — a reboot mid-lock.
            await machine.DisposeAsync();
        }

        Assert.True(await Wait.UntilAsync(() => !console.Session.IsLinked(agentId), TimeSpan.FromSeconds(15)));
        Assert.True(File.Exists(Path.Combine(directory, Defaults.PolicyFileName)));

        var returned = new FakeMachine(DirectoryAgentStore.Open(directory), null, TestLogging.Factory.CreateLogger("PC-07"));
        try
        {
            // Nothing was asked of the console: the PC re-applied the lock from policy.json
            // before it dialled, so the state after Welcome already shows it.
            returned.Start();
            Assert.NotNull(returned.Lock);
            Assert.Equal(sessionId, returned.Lock!.SessionId);
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(agentId)?.OverlayLocked == true, TimeSpan.FromSeconds(20)));
            Assert.Equal(sessionId, console.Session.FindLinked(agentId)!.OverlayState!.SessionId);
            Assert.True(await Wait.UntilAsync(() => console.Session.Events.Recent.Any(e => e.Code == PolicyEvents.Reapplied), TimeSpan.FromSeconds(10)));
            Assert.True(Tile(console, agentId).IsLocked);

            // Any console of the lab may end it (owner decision 4), and this one does.
            console.Session.Unlock([agentId]);
            Assert.True(await Wait.UntilAsync(() => returned.Lock is null && console.Session.FindLinked(agentId)?.OverlayLocked == false, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await returned.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_hard_limit_ends_a_lock_on_the_pcs_own_clock_while_the_console_stays_silent_and_a_far_limit_is_clamped()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-03"), 3, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-03"));
        machine.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(machine.AgentId), TimeSpan.FromSeconds(20)));
        var connection = console.Session.FindLinked(machine.AgentId)!;

        // A proposal thirty days out comes back at one hour from now: the agent's ceiling, not the console's wish.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(connection.TrySend(new ConsoleMessage
        {
            Overlay = new Overlay { Kind = Overlay.Types.Kind.Lock, Message = "far", SessionId = "far-lock", HardLimitSeconds = 30 * 24 * 3600 },
        }));
        Assert.True(await Wait.UntilAsync(() => connection.OverlayState is { Kind: Overlay.Types.Kind.Lock, SessionId: "far-lock" }, TimeSpan.FromSeconds(10)));
        var ceiling = (long)Defaults.LockHardLimit.TotalSeconds;
        Assert.InRange(connection.OverlayState!.HardLimitUnix, now + ceiling - 5, now + ceiling + 10);

        // A short one is honoured as it is — and ends by itself, with nothing sent from here.
        var lifted = new List<PolicySession>();
        machine.Policy.Lifted += sessions => lifted.AddRange(sessions);
        var eventsBefore = console.Session.Events.Recent.Count;
        Assert.True(connection.TrySend(new ConsoleMessage
        {
            Overlay = new Overlay { Kind = Overlay.Types.Kind.Lock, Message = "short", SessionId = "short-lock", HardLimitSeconds = 2 },
        }));
        Assert.True(await Wait.UntilAsync(() => connection.OverlayState is { Kind: Overlay.Types.Kind.Lock, SessionId: "short-lock" }, TimeSpan.FromSeconds(10)));
        Assert.True(await Wait.UntilAsync(() => connection.OverlayState is { Kind: Overlay.Types.Kind.Unspecified }, TimeSpan.FromSeconds(15)));
        Assert.Null(machine.Lock);
        Assert.Contains(lifted, s => s.SessionId == "short-lock");
        Assert.Contains(console.Session.Events.Recent.Skip(eventsBefore), e => e.Code == PolicyEvents.OverlayExpired);
        Assert.DoesNotContain(console.Session.Events.Recent.Skip(eventsBefore), e => e.Code is "overlay.unlock_sent");
        Assert.False(Tile(console, machine.AgentId).IsLocked);

        // A request whose end is already past (a negative duration) is refused with a problem, and nothing shows.
        Assert.True(connection.TrySend(new ConsoleMessage
        {
            Overlay = new Overlay { Kind = Overlay.Types.Kind.Lock, Message = "late", SessionId = "late-lock", UntilSeconds = -60 },
        }));
        Assert.True(await Wait.UntilAsync(() => connection.OverlayProblem is not null, TimeSpan.FromSeconds(10)));
        Assert.Contains("already past", connection.OverlayProblem);
        Assert.False(connection.OverlayLocked);
        var tile = Tile(console, machine.AgentId);
        Assert.False(tile.IsLocked);
        Assert.NotEmpty(tile.LockProblem);
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "overlay.refused");
    }

    [Fact]
    public async Task A_console_the_pc_holds_withdrawn_is_refused_at_the_gate_and_the_tile_shows_no_lock()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-05"), 5, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-05"));
        machine.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(machine.AgentId), TimeSpan.FromSeconds(20)));
        var connection = console.Session.FindLinked(machine.AgentId)!;

        // The PC learns that this console's instance was withdrawn. On the wire that entry
        // makes the PC leave at once (LeaveIfConsoleRevoked, D-56 item 6), which is the right
        // behaviour and leaves no link to send on; the gate exists for the window in between —
        // a message already on the stream when the entry lands — so there is no production
        // path that keeps the link with the entry in place, and the entry is placed in the
        // PC's revocation set directly, as if it had just been merged. Field contract: the
        // set is AgentLink's private `_revocations`, the very RevocationSet DeliverOverlayAsync
        // hands to OwnershipGate.Refusal; a rename fails this test loudly at GetField, not
        // silently. That the wire merges into the same set is M5 code, proven by LinkTests.
        var lab = console.Session.Vault!.Peek()!;
        var entry = RevocationSet.Create(lab, LabCertificates.InstanceSerial(console.Session.Instance.InstanceId), "withdrawn in a test", DateTimeOffset.UtcNow);
        var revocations = (RevocationSet)typeof(AgentLink).GetField("_revocations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(machine.Link)!;
        Assert.Single(revocations.Merge(console.Authority, [entry]));

        Assert.Equal(1, console.Session.Lock([machine.AgentId], "refused", null));
        Assert.True(await Wait.UntilAsync(() => connection.OverlayProblem is not null, TimeSpan.FromSeconds(10)));
        Assert.Equal(PolicyEvents.RefusedWithdrawn, connection.OverlayProblem);
        Assert.False(connection.OverlayLocked);
        Assert.Null(machine.Lock);
        Assert.False(File.Exists(machine.Policy.DocumentPath) && File.ReadAllText(machine.Policy.DocumentPath).Contains("refused", StringComparison.Ordinal));

        var tile = Tile(console, machine.AgentId);
        Assert.False(tile.IsLocked);
        Assert.Equal(PolicyEvents.RefusedWithdrawn, tile.LockProblem);
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "overlay.refused" && e.Message.Contains("withdrawn", StringComparison.Ordinal));
        Assert.True(console.Session.IsLinked(machine.AgentId));
    }

    [Fact]
    public async Task A_lock_survives_the_consoles_departure_and_the_pc_ends_it_at_its_own_limit()
    {
        // D-70 item 1: a lock does not end on lab departure — the teacher who set it may be
        // back in a minute — and the PC's own clock ends it, with no console in sight.
        var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-02"), 2, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-02"));
        var departed = false;
        try
        {
            machine.Start();
            Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(machine.AgentId), TimeSpan.FromSeconds(20)));

            var lifted = new List<PolicySession>();
            machine.Policy.Lifted += sessions => lifted.AddRange(sessions);
            Assert.Equal(1, console.Session.Lock([machine.AgentId], "Stay put", TimeSpan.FromSeconds(6)));
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(machine.AgentId)?.OverlayLocked == true, TimeSpan.FromSeconds(10)));
            var sessionId = machine.Lock!.SessionId;

            // The console leaves the lab (the window closes, Disconnect, a switch): the PC is unlinked...
            departed = true;
            await console.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => machine.Link.State != LinkState.Linked, TimeSpan.FromSeconds(15)));

            // ...and still locked, by its own word and its own journal.
            Assert.NotNull(machine.Lock);
            Assert.Equal(sessionId, machine.Lock!.SessionId);
            Assert.Equal(Overlay.Types.Kind.Lock, machine.Link.LatestOverlayState!.Kind);
            Assert.True(File.ReadAllText(machine.Policy.DocumentPath).Contains(sessionId, StringComparison.Ordinal));

            // Then its own clock ends it — nobody asked.
            Assert.True(await Wait.UntilAsync(() => machine.Lock is null, TimeSpan.FromSeconds(15)));
            Assert.Contains(lifted, s => s.SessionId == sessionId);
            Assert.Equal(Overlay.Types.Kind.Unspecified, machine.Link.LatestOverlayState!.Kind);
        }
        finally
        {
            if (!departed)
            {
                await console.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_second_console_of_the_same_lab_sees_whose_lock_it_is_ends_it_and_the_first_sees_the_second_lock_named()
    {
        // Owner decision 4: any console of the same lab may end a lock it can see, and the
        // console shows whose it is. Two consoles take turns over the same PC (D-58).
        await using var macBook = await TestConsole.CreateLabAsync("MacBook");
        var payload = SetupPayload.Open(macBook.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-09"), 9, payload, consoleHost: null, macBook.Port, burnedCode: false);
        using var listener = new BeaconListener(TestConsole.BeaconPort);
        await using var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-09"));
        listener.Received += (datagram, _, receivedAt) => machine.OfferBeacon(datagram, receivedAt);
        listener.Start();
        machine.Start();
        var id = machine.AgentId;
        Assert.True(await Wait.UntilAsync(() => macBook.Session.IsLinked(id), TimeSpan.FromSeconds(20)));

        Assert.Equal(1, macBook.Session.Lock([id], "From the MacBook", null));
        Assert.True(await Wait.UntilAsync(() => macBook.Session.FindLinked(id)?.OverlayLocked == true, TimeSpan.FromSeconds(10)));
        var first = machine.Lock!.SessionId;

        // The desk PC joins the lab and takes the room over; the PC re-sends its state after Welcome.
        await using var deskPc = await TestConsole.JoinLabAsync(macBook, "Lab PC");
        Assert.True(await Wait.UntilAsync(() => deskPc.Session.OtherConsoles.Any(o => o.Name == "MacBook") && macBook.Session.OtherConsoles.Any(o => o.Name == "Lab PC"), TimeSpan.FromSeconds(15)));
        deskPc.Session.TakeOver();
        Assert.True(await Wait.UntilAsync(() => deskPc.Session.FindLinked(id)?.OverlayLocked == true, TimeSpan.FromSeconds(20)));
        var seenOnDesk = deskPc.Session.FindLinked(id)!.OverlayState!;
        Assert.Equal(first, seenOnDesk.SessionId);
        Assert.Equal(macBook.Session.Instance.InstanceId, seenOnDesk.SetByInstance);
        Assert.Equal("locked by MacBook", Tile(deskPc, id).LockText);
        Assert.Contains(deskPc.Session.Events.Recent, e => e.Code == "overlay.locked" && e.Message.Contains("MacBook", StringComparison.Ordinal));

        // The desk PC ends the MacBook's lock, then sets one of its own.
        Assert.Equal(1, deskPc.Session.Unlock([id]));
        Assert.True(await Wait.UntilAsync(() => machine.Lock is null && deskPc.Session.FindLinked(id)?.OverlayLocked == false, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, deskPc.Session.Lock([id], "From the desk PC", null));
        Assert.True(await Wait.UntilAsync(() => machine.Lock is { } held && held.SessionId != first, TimeSpan.FromSeconds(10)));
        var second = machine.Lock!.SessionId;

        // The MacBook takes the PC back and sees the new lock, named after the desk PC.
        macBook.Session.TakeOver();
        Assert.True(await Wait.UntilAsync(() => macBook.Session.FindLinked(id)?.OverlayState is { Kind: Overlay.Types.Kind.Lock } s && s.SessionId == second, TimeSpan.FromSeconds(20)));
        Assert.Equal(deskPc.Session.Instance.InstanceId, macBook.Session.FindLinked(id)!.OverlayState!.SetByInstance);
        Assert.Equal("locked by Lab PC", Tile(macBook, id).LockText);

        // And may end it too.
        Assert.Equal(1, macBook.Session.Unlock([id]));
        Assert.True(await Wait.UntilAsync(() => machine.Lock is null && macBook.Session.FindLinked(id)?.OverlayLocked == false, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_corrupt_policy_file_and_a_machine_that_throws_still_link_and_the_console_hears_why()
    {
        await using var console = await TestConsole.CreateLabAsync();

        // A simulated PC whose policy.json is torn: it links, reports policy.unreadable, and
        // the next lock overwrites the file.
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var directory = Path.Combine(TestConsole.TempDirectory(), "PC-06");
        var store = FakeMachine.Install(directory, 6, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        File.WriteAllText(Path.Combine(directory, Defaults.PolicyFileName), "{ \"schema\": 1, \"lab_id\": null, \"policy\": { \"overlay\": { \"session_id\": null ");
        await using var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-06"));
        machine.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(machine.AgentId), TimeSpan.FromSeconds(20)));
        Assert.True(await Wait.UntilAsync(() => console.Session.Events.Recent.Any(e => e.Code == PolicyEvents.Unreadable), TimeSpan.FromSeconds(10)));
        Assert.Null(machine.Lock);
        Assert.Equal(1, console.Session.Lock([machine.AgentId], "after the tear", null));
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(machine.AgentId)?.OverlayLocked == true, TimeSpan.FromSeconds(10)));
        Assert.Contains("after the tear", File.ReadAllText(machine.Policy.DocumentPath));

        // The real enforcer over a machine whose every call throws, on a link that dials as
        // the service does — Start first, then the link: the PC still links, the console hears
        // policy.restore_failed and policy.unreadable, and a lock is answered with the problem.
        await using var pc = TestAgent.Install(console, 16, console.IssueCodes(1)[0]);
        File.WriteAllText(Path.Combine(pc.Store.Directory, Defaults.PolicyFileName), "not even json");
        await using var enforcer = new PolicyEnforcer(pc.Link, new ThrowingMachine(), pc.Store.Directory, console.Session.LabId,
            TestLogging.Factory.CreateLogger("enforcer"), mutexName: @"Global\LabControl.Policy.test." + Guid.NewGuid().ToString("n"));
        var outcome = enforcer.Start();
        Assert.NotNull(outcome);
        Assert.False(outcome!.MachineOpen);
        pc.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(pc.AgentId), TimeSpan.FromSeconds(20)));
        Assert.True(await Wait.UntilAsync(() => console.Session.Events.Recent.Count(e => e.Code == PolicyEvents.Unreadable) >= 2
            && console.Session.Events.Recent.Any(e => e.Code == PolicyEvents.RestoreFailed && e.Message.Contains("fell over", StringComparison.Ordinal)), TimeSpan.FromSeconds(10)));
        var connection = console.Session.FindLinked(pc.AgentId)!;
        Assert.True(await Wait.UntilAsync(() => connection.OverlayState is { Kind: Overlay.Types.Kind.Unspecified }, TimeSpan.FromSeconds(10)));

        Assert.Equal(1, console.Session.Lock([pc.AgentId], "doomed", null));
        Assert.True(await Wait.UntilAsync(() => connection.OverlayProblem is not null, TimeSpan.FromSeconds(10)));
        Assert.Contains("fell over", connection.OverlayProblem, StringComparison.Ordinal);
        Assert.False(connection.OverlayLocked);
        Assert.True(console.Session.IsLinked(pc.AgentId));
        Assert.Equal(LinkState.Linked, pc.Link.State);
    }

    [Fact]
    public async Task A_busy_policy_mutex_answers_the_console_with_a_problem_and_touches_nothing()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-08"), 8, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-08"), policyMutexWait: TimeSpan.FromMilliseconds(300));
        machine.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(machine.AgentId), TimeSpan.FromSeconds(20)));
        var connection = console.Session.FindLinked(machine.AgentId)!;
        Assert.True(await Wait.UntilAsync(() => connection.OverlayState is { Kind: Overlay.Types.Kind.Unspecified }, TimeSpan.FromSeconds(10)));

        // Another restore or apply holds the PC's mutex (the watchdog task, on a real PC).
        var eventsBefore = console.Session.Events.Recent.Count;
        using (var held = PolicyMutex.TryAcquire(TimeSpan.Zero, FakeMachine.PolicyMutexName(machine.AgentId)))
        {
            Assert.True(held.Acquired);
            Assert.Equal(1, console.Session.Lock([machine.AgentId], "while busy", null));
            Assert.True(await Wait.UntilAsync(() => connection.OverlayProblem is not null, TimeSpan.FromSeconds(10)));
            Assert.Equal(PolicyEvents.Text("Busy", 0), connection.OverlayProblem);
            Assert.False(connection.OverlayLocked);
            Assert.Null(machine.Lock);
            Assert.True(await Wait.UntilAsync(() => console.Session.Events.Recent.Skip(eventsBefore).Any(e => e.Code == PolicyEvents.Busy), TimeSpan.FromSeconds(10)));
            Assert.False(Tile(console, machine.AgentId).IsLocked);
            Assert.NotEmpty(Tile(console, machine.AgentId).LockProblem);
        }

        // Released: the same request goes through, and the problem clears.
        Assert.Equal(1, console.Session.Lock([machine.AgentId], "no longer busy", null));
        Assert.True(await Wait.UntilAsync(() => connection.OverlayLocked, TimeSpan.FromSeconds(10)));
        Assert.Null(connection.OverlayProblem);
        Assert.Equal("no longer busy", machine.Lock!.Message);
    }

    [Fact]
    public async Task An_unlinked_or_outdated_pc_is_skipped_and_the_lock_is_never_queued_for_it()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 2));

        // One PC too old to know the message, one that is switched off, one that is fine.
        var oldStore = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-11"), 11, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var old = new FakeMachine(oldStore, new FailureSpec(11, FailureKind.Outdated, 0), TestLogging.Factory.CreateLogger("PC-11"));
        var fineStore = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-12"), 12, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var fine = new FakeMachine(fineStore, null, TestLogging.Factory.CreateLogger("PC-12"));
        old.Start();
        fine.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(old.AgentId) && console.Session.IsLinked(fine.AgentId), TimeSpan.FromSeconds(20)));
        Assert.True(console.Session.FindLinked(old.AgentId)!.IsOutdated);
        var off = Guid.NewGuid().ToString("d");

        Assert.Equal(1, console.Session.Lock([old.AgentId, off, fine.AgentId, fine.AgentId], "some of you", null));
        var sent = Assert.Single(console.Session.Events.Recent, e => e.Code == "overlay.lock_sent");
        Assert.Contains("1 PC(s), 2 skipped", sent.Message);
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(fine.AgentId)?.OverlayLocked == true, TimeSpan.FromSeconds(10)));

        // Nothing waits for the skipped ones: no lock on the outdated PC, no problem on its tile, nothing in its journal.
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Null(old.Lock);
        Assert.False(console.Session.FindLinked(old.AgentId)!.OverlayLocked);
        Assert.Null(console.Session.FindLinked(old.AgentId)!.OverlayProblem);
        Assert.False(File.Exists(old.Policy.DocumentPath) && File.ReadAllText(old.Policy.DocumentPath).Contains("some of you", StringComparison.Ordinal));

        Assert.Equal(1, console.Session.Unlock([old.AgentId, off, fine.AgentId]));
        var unlockSent = Assert.Single(console.Session.Events.Recent, e => e.Code == "overlay.unlock_sent");
        Assert.Contains("1 PC(s), 2 skipped", unlockSent.Message);
        Assert.True(await Wait.UntilAsync(() => fine.Lock is null, TimeSpan.FromSeconds(10)));
    }

    /// <summary>A machine whose every call throws: what the enforcer must survive (D-69, nothing throws).</summary>
    private sealed class ThrowingMachine : IMachineState
    {
        private static MachineResult Boom(string what) => throw new InvalidOperationException($"{what} fell over");

        public MachineResult RestoreDefaults() => Boom(nameof(RestoreDefaults));

        public MachineResult RefineRestore(DisplacedSettings displaced) => Boom(nameof(RefineRestore));

        public MachineResult Confirm() => Boom(nameof(Confirm));

        public MachineResult ApplyOverlay(OverlaySession session) => Boom(nameof(ApplyOverlay));

        public MachineResult LiftOverlay() => Boom(nameof(LiftOverlay));

        public MachineResult ApplyInternet(InternetSession session, DisplacedSettings displaced) => Boom(nameof(ApplyInternet));

        public MachineResult LiftInternet() => Boom(nameof(LiftInternet));

        public MachineResult ApplyExam(ExamSession session) => Boom(nameof(ApplyExam));

        public MachineResult LiftExam() => Boom(nameof(LiftExam));
    }

    /// <summary>A tile refreshed the way the main view model refreshes it, for the badge.</summary>
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
