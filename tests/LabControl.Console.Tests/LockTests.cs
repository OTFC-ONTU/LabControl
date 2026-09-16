using System.Reflection;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.FakeAgent;
using LabControl.Shared;
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
/// limit ends a lock on the PC's own clock with the console silent, and a console the PC
/// holds revoked is refused at the gate.
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

            // The thirtieth PC never answers Overlay (an agent with no enforcer, as a pre-M6 build would be).
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
            Overlay = new Overlay { Kind = Overlay.Types.Kind.Lock, Message = "far", SessionId = "far-lock", HardLimitUnix = now + 30 * 24 * 3600 },
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
            Overlay = new Overlay { Kind = Overlay.Types.Kind.Lock, Message = "short", SessionId = "short-lock", HardLimitUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2 },
        }));
        Assert.True(await Wait.UntilAsync(() => connection.OverlayState is { Kind: Overlay.Types.Kind.Lock, SessionId: "short-lock" }, TimeSpan.FromSeconds(10)));
        Assert.True(await Wait.UntilAsync(() => connection.OverlayState is { Kind: Overlay.Types.Kind.Unspecified }, TimeSpan.FromSeconds(15)));
        Assert.Null(machine.Lock);
        Assert.Contains(lifted, s => s.SessionId == "short-lock");
        Assert.Contains(console.Session.Events.Recent.Skip(eventsBefore), e => e.Code == PolicyEvents.OverlayExpired);
        Assert.DoesNotContain(console.Session.Events.Recent.Skip(eventsBefore), e => e.Code is "overlay.unlock_sent");
        Assert.False(Tile(console, machine.AgentId).IsLocked);

        // A request whose end is already past is refused with a problem, and nothing shows.
        Assert.True(connection.TrySend(new ConsoleMessage
        {
            Overlay = new Overlay { Kind = Overlay.Types.Kind.Lock, Message = "late", SessionId = "late-lock", UntilUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60 },
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
        // a message already on the stream when the entry lands — so the entry is placed in the
        // PC's revocation set directly, as if it had just been merged, and the link kept.
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
