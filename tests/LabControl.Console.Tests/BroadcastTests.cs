using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using LabControl.Shared.Video;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// The broadcast end to end (M6 portion 2, PROTOCOL "Broadcast", D-71, D-72): the console's
/// screen — a drawn one here — is encoded once and reaches simulated PCs over their own
/// <c>PullBroadcast</c> streams; each PC says in <c>OverlayState</c> that it shows it and the
/// tile follows that word; a lock stays on top and the broadcast comes back when it lifts; a
/// PC that relinks joins again; the PC ends it at its own limit with the console still
/// streaming, the console ends it at its limit and when it leaves the lab; and the console
/// serves the picture only to the PCs it was meant for, for the session it is running.
/// </summary>
public class BroadcastTests
{
    private const int Simulated = Defaults.MaxStudentPcs - 1;

    [Fact]
    public async Task Thirty_pcs_receive_one_encoded_picture_and_the_tile_follows_what_each_pc_reports()
    {
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.BroadcastSource = TeacherScreen;
        var (machines, root) = await StartMachinesAsync(console, Simulated);
        try
        {
            // The thirtieth PC has no enforcer: it answers BROADCAST_START with a problem and never dials.
            await using var mute = TestAgent.Install(console, Defaults.MaxStudentPcs, console.IssueCodes(1)[0]).Start();
            Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(mute.AgentId), TimeSpan.FromSeconds(15)));

            var all = machines.Select(m => m.AgentId).Append(mute.AgentId).ToArray();
            Assert.Equal(Defaults.MaxStudentPcs, console.Session.StartBroadcast(all));
            var hub = console.Session.Broadcast;
            Assert.NotNull(hub);

            Assert.True(await Wait.UntilAsync(() => machines.All(m => console.Session.FindLinked(m.AgentId)?.OverlayBroadcasting == true), TimeSpan.FromSeconds(30)),
                $"{machines.Count(m => console.Session.FindLinked(m.AgentId)?.OverlayBroadcasting == true)} of {Simulated} show the broadcast");
            Assert.Equal(Simulated, hub!.Watching);
            Assert.Equal(BroadcastHub.StreamCap(Simulated), hub.StreamBitsPerSecond);
            Assert.Equal("simulated", hub.SourceKind);

            var first = machines[0];
            var state = console.Session.FindLinked(first.AgentId)!.OverlayState!;
            Assert.Equal(hub.SessionId, state.SessionId);
            Assert.Equal(console.Session.Instance.InstanceId, state.SetByInstance);
            Assert.True(state.InputBlocked);
            Assert.True(state.HardLimitUnix <= DateTimeOffset.UtcNow.Add(Defaults.BroadcastHardLimit).ToUnixTimeSeconds() + 5);
            Assert.All(machines, m => Assert.Equal(hub.SessionId, m.Broadcast?.SessionId));

            // The picture the PCs decoded is the teacher's, at the broadcast size; every PC got it.
            Assert.True(await Wait.UntilAsync(() => machines.All(m => m.BroadcastPicture.Image.HasFrame), TimeSpan.FromSeconds(20)));
            Assert.Equal(1280, first.BroadcastPicture.Image.Width);
            Assert.Equal(720, first.BroadcastPicture.Image.Height);

            // Nothing is in policy.json for a broadcast (D-71): only a lock is ever a session there.
            Assert.Null(first.Policy.Journal.Snapshot().Overlay);

            var tile = Tile(console, first.AgentId);
            Assert.True(tile.IsBroadcasting);
            Assert.False(tile.IsLocked);
            var muteTile = Tile(console, mute.AgentId);
            Assert.False(muteTile.IsBroadcasting);
            Assert.NotEqual(string.Empty, muteTile.LockProblem);
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "broadcast.started");

            await console.Session.StopBroadcastAsync();
            Assert.Null(console.Session.Broadcast);
            Assert.True(await Wait.UntilAsync(() => machines.All(m => console.Session.FindLinked(m.AgentId)?.OverlayBroadcasting == false), TimeSpan.FromSeconds(20)));
            Assert.True(await Wait.UntilAsync(() => machines.All(m => m.Broadcast is null), TimeSpan.FromSeconds(10)));
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "broadcast.stopped");
            Assert.False(Tile(console, first.AgentId).IsBroadcasting);
        }
        finally
        {
            await DisposeAllAsync(machines);
        }
    }

    [Fact]
    public async Task A_lock_stays_on_top_of_a_broadcast_and_the_broadcast_comes_back_when_it_lifts()
    {
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.BroadcastSource = TeacherScreen;
        var (machines, _) = await StartMachinesAsync(console, 1);
        var machine = machines[0];
        try
        {
            console.Session.StartBroadcast([machine.AgentId]);
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(machine.AgentId)?.OverlayBroadcasting == true, TimeSpan.FromSeconds(15)));

            console.Session.Lock([machine.AgentId], "Hands off", TimeSpan.FromMinutes(2));
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(machine.AgentId)?.OverlayLocked == true, TimeSpan.FromSeconds(10)));
            Assert.NotNull(machine.Broadcast); // still received underneath
            Assert.True(Tile(console, machine.AgentId).IsLocked);
            Assert.False(Tile(console, machine.AgentId).IsBroadcasting);

            console.Session.Unlock([machine.AgentId]);
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(machine.AgentId)?.OverlayBroadcasting == true, TimeSpan.FromSeconds(10)));
            Assert.Null(machine.Lock);
        }
        finally
        {
            await DisposeAllAsync(machines);
        }
    }

    [Fact]
    public async Task A_pc_that_relinks_mid_broadcast_joins_it_again()
    {
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.BroadcastSource = TeacherScreen;
        var (machines, _) = await StartMachinesAsync(console, 1);
        var machine = machines[0];
        try
        {
            console.Session.StartBroadcast([machine.AgentId]);
            var sessionId = console.Session.Broadcast!.SessionId;
            Assert.True(await Wait.UntilAsync(() => machine.Broadcast is not null, TimeSpan.FromSeconds(15)));

            // The link drops: the stream goes with it, and so does the picture on the PC.
            machine.Link.Disconnect("test: the Wi-Fi blinked");
            Assert.True(await Wait.UntilAsync(() => machine.Broadcast is null, TimeSpan.FromSeconds(10)));

            // It relinks on the next beacon and is told to join the same broadcast.
            Assert.True(await Wait.UntilAsync(() => machine.Broadcast?.SessionId == sessionId, TimeSpan.FromSeconds(20)));
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(machine.AgentId)?.OverlayBroadcasting == true, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await DisposeAllAsync(machines);
        }
    }

    [Fact]
    public async Task The_pc_ends_a_broadcast_at_its_own_limit_while_the_console_still_streams()
    {
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.BroadcastSource = TeacherScreen;
        var (machines, _) = await StartMachinesAsync(console, 1, broadcastCeiling: TimeSpan.FromSeconds(6));
        var machine = machines[0];
        try
        {
            console.Session.StartBroadcast([machine.AgentId]);
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(machine.AgentId)?.OverlayBroadcasting == true, TimeSpan.FromSeconds(15)));
            var limit = console.Session.FindLinked(machine.AgentId)!.OverlayState!.HardLimitUnix;
            Assert.True(limit <= DateTimeOffset.UtcNow.AddSeconds(7).ToUnixTimeSeconds(), "the PC clamps the console's 30 minutes to its own ceiling");

            Assert.True(await Wait.UntilAsync(() => machine.Broadcast is null, TimeSpan.FromSeconds(15)));
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(machine.AgentId)?.OverlayBroadcasting == false, TimeSpan.FromSeconds(10)));
            Assert.True(await Wait.UntilAsync(() => console.Session.Events.Recent.Any(e => e.Code == "broadcast.expired" && e.AgentId == machine.AgentId), TimeSpan.FromSeconds(10)));
            Assert.NotNull(console.Session.Broadcast);
        }
        finally
        {
            await DisposeAllAsync(machines);
        }
    }

    [Fact]
    public async Task The_console_ends_the_broadcast_at_its_limit_and_every_pc_gives_the_screen_back()
    {
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.BroadcastSource = TeacherScreen;
        // Long enough that both PCs show it first even on a loaded runner; short enough for a test.
        console.Session.BroadcastLimit = TimeSpan.FromSeconds(10);
        var (machines, _) = await StartMachinesAsync(console, 2);
        try
        {
            console.Session.StartBroadcast(machines.Select(m => m.AgentId));
            Assert.True(await Wait.UntilAsync(() => machines.All(m => m.Broadcast is not null), TimeSpan.FromSeconds(9)));

            Assert.True(await Wait.UntilAsync(() => console.Session.Broadcast is null, TimeSpan.FromSeconds(20)));
            Assert.True(await Wait.UntilAsync(() => machines.All(m => m.Broadcast is null), TimeSpan.FromSeconds(10)));
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "broadcast.expired" && e.AgentId is null);
        }
        finally
        {
            await DisposeAllAsync(machines);
        }
    }

    [Fact]
    public async Task The_console_serves_the_picture_only_to_the_pcs_it_is_meant_for_and_only_for_its_own_session()
    {
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.BroadcastSource = TeacherScreen;
        var (machines, _) = await StartMachinesAsync(console, 2);
        var shown = machines[0];
        var other = machines[1];
        try
        {
            console.Session.StartBroadcast([shown.AgentId]);
            var sessionId = console.Session.Broadcast!.SessionId;
            Assert.True(await Wait.UntilAsync(() => shown.Broadcast is not null, TimeSpan.FromSeconds(15)));
            Assert.Null(other.Broadcast);

            // A PC that was not chosen cannot pull it, even knowing the session.
            var refused = await other.Link.PullBroadcastAsync(sessionId, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
            Assert.Equal(BroadcastPullEnd.Failed, refused.End);
            Assert.Contains("not one the broadcast is shown to", refused.Detail);

            // A chosen PC asking for another session gets nothing either.
            var wrong = await shown.Link.PullBroadcastAsync("not-the-session", (_, _) => ValueTask.CompletedTask, CancellationToken.None);
            Assert.Equal(BroadcastPullEnd.Failed, wrong.End);
            Assert.Contains("not broadcasting that session", wrong.Detail);
            Assert.Equal(1, console.Session.Broadcast!.Watching);
        }
        finally
        {
            await DisposeAllAsync(machines);
        }
    }

    [Fact]
    public async Task Leaving_the_lab_ends_the_broadcast_before_the_links_close()
    {
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.BroadcastSource = TeacherScreen;
        var (machines, _) = await StartMachinesAsync(console, 1);
        var machine = machines[0];
        try
        {
            console.Session.StartBroadcast([machine.AgentId]);
            Assert.True(await Wait.UntilAsync(() => machine.Broadcast is not null, TimeSpan.FromSeconds(15)));
            var steps = new List<LabCloseStep>();
            var watchingWhenBroadcastStepRan = -1;
            console.Session.BeforeCloseStep = step =>
            {
                steps.Add(step);
                if (step == LabCloseStep.Broadcast)
                {
                    watchingWhenBroadcastStepRan = console.Session.Linked.Count;
                }
            };

            await console.Session.CloseAsync("test: the teacher switched rooms");
            Assert.Equal(LabCloseStep.Broadcast, steps[1]);
            Assert.Equal(1, watchingWhenBroadcastStepRan);
            Assert.Null(console.Session.Broadcast);
            Assert.True(await Wait.UntilAsync(() => machine.Broadcast is null, TimeSpan.FromSeconds(10)));
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "broadcast.stopped");
        }
        finally
        {
            await DisposeAllAsync(machines);
        }
    }

    [Fact]
    public void The_stream_cap_shares_the_total_between_the_pcs_watching()
    {
        Assert.Equal(Defaults.BroadcastBitsPerSecond, BroadcastHub.StreamCap(0));
        Assert.Equal(Defaults.BroadcastBitsPerSecond, BroadcastHub.StreamCap(1));
        Assert.Equal(Defaults.BroadcastBitsPerSecond, BroadcastHub.StreamCap(3));
        Assert.Equal(Defaults.BroadcastTotalBitsPerSecond / 14, BroadcastHub.StreamCap(14));
        Assert.Equal(Defaults.BroadcastTotalBitsPerSecond / 30, BroadcastHub.StreamCap(30));
    }

    [Fact]
    public async Task A_slow_pc_skips_to_the_next_keyframe_and_never_holds_the_others_back()
    {
        await using var hub = new BroadcastHub("session", TeacherScreen, TestLogging.Factory.CreateLogger("hub"), () => DateTimeOffset.UtcNow, (_, _, _) => { });
        hub.Start();
        var fast = new CollectingWriter();
        var slow = new CollectingWriter { Gate = new SemaphoreSlim(0) };
        using var stop = new CancellationTokenSource();
        var fastTask = hub.ServeAsync("fast", fast, stop.Token);
        var slowTask = hub.ServeAsync("slow", slow, stop.Token);

        // The slow PC holds its first frame on the wire while the fast one keeps receiving.
        Assert.True(await Wait.UntilAsync(() => fast.Frames.Count >= 20, TimeSpan.FromSeconds(20)), $"fast got {fast.Frames.Count}");
        Assert.True(slow.Frames.Count <= 1);
        Assert.True(hub.Statistics("slow")!.Value.Skipped > 0);
        Assert.Equal(0, hub.Statistics("fast")!.Value.Skipped);

        // Released, the slow PC resumes on a keyframe — never on a delta it cannot patch.
        var before = slow.Frames.Count;
        slow.Gate.Release(1000);
        Assert.True(await Wait.UntilAsync(() => slow.Frames.Count >= before + 3, TimeSpan.FromSeconds(20)));
        Assert.True(slow.Frames[0].Keyframe);
        Assert.True(slow.Frames[before].Keyframe || slow.Frames[before].Seq == slow.Frames[before - 1].Seq + 1);

        // The fast PC's frames run without a gap: every delta follows the frame before it.
        var fastFrames = fast.Frames.ToArray();
        for (var i = 1; i < fastFrames.Length; i++)
        {
            Assert.True(fastFrames[i].Keyframe || fastFrames[i].Seq == fastFrames[i - 1].Seq + 1, $"frame {fastFrames[i].Seq} after {fastFrames[i - 1].Seq}");
        }

        stop.Cancel();
        await Task.WhenAll(fastTask, slowTask);
    }

    [Fact]
    public async Task A_broadcast_that_brings_no_picture_any_more_gives_the_screen_back()
    {
        // D-71 item 10: the console's capture stops (its screen locked, the permission gone);
        // the stream stays up but carries nothing, and the PC must not hold a frozen picture
        // with the input blocked until the hard limit.
        await using var console = await TestConsole.CreateLabAsync();
        var screen = new FreezableScreen();
        console.Session.BroadcastSource = () => screen;
        var (machines, _) = await StartMachinesAsync(console, 1, broadcastStallTimeout: TimeSpan.FromSeconds(2));
        var machine = machines[0];
        try
        {
            console.Session.StartBroadcast([machine.AgentId]);
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(machine.AgentId)?.OverlayBroadcasting == true, TimeSpan.FromSeconds(15)));

            screen.Frozen = true;
            Assert.True(await Wait.UntilAsync(() => machine.Broadcast is null, TimeSpan.FromSeconds(15)));
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(machine.AgentId)?.OverlayBroadcasting == false, TimeSpan.FromSeconds(10)));
            Assert.True(await Wait.UntilAsync(() => console.Session.Events.Recent.Any(e => e.Code == "broadcast.stalled" && e.AgentId == machine.AgentId), TimeSpan.FromSeconds(10)));
            Assert.DoesNotContain(console.Session.Events.Recent, e => e.Code == "broadcast.failed");
            Assert.True(console.Session.FindLinked(machine.AgentId) is not null, "the link itself stays up");
        }
        finally
        {
            await DisposeAllAsync(machines);
        }
    }

    [Fact]
    public async Task A_console_that_vanishes_ends_the_broadcast_as_a_lost_connection_not_a_failure()
    {
        // D-71 item 11: the connection is cut without a goodbye (a reset, as when the
        // console's process dies or its lid closes); the PC gives the screen back and does not
        // tell the next console that the broadcast "could not be shown".
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.BroadcastSource = TeacherScreen;
        await using var relay = new CuttableRelay(console.Port);
        var (machines, _) = await StartMachinesAsync(console, 1, port: relay.Port);
        var machine = machines[0];
        try
        {
            console.Session.StartBroadcast([machine.AgentId]);
            Assert.True(await Wait.UntilAsync(() => machine.Broadcast is not null, TimeSpan.FromSeconds(15)));

            relay.Cut();
            Assert.True(await Wait.UntilAsync(() => machine.Broadcast is null, TimeSpan.FromSeconds(15)), "the PC gives the screen back with the connection");

            // It links again through the relay, joins the broadcast again, and has nothing to
            // report about the one that was cut off.
            Assert.True(await Wait.UntilAsync(() => machine.Broadcast is not null, TimeSpan.FromSeconds(30)), "the PC joins the broadcast again when it relinks");
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(console.Session.Events.Recent, e => e.Code == "broadcast.failed" && e.AgentId == machine.AgentId);
        }
        finally
        {
            await DisposeAllAsync(machines);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The teacher's screen in the tests: a drawn 1280×720 desktop that changes every tick.</summary>
    private static IScreenSource TeacherScreen() => new FakeScreen(90, 1280, 720, idle: false);

    private static async Task<(List<FakeMachine> Machines, string Root)> StartMachinesAsync(TestConsole console, int count, TimeSpan? broadcastCeiling = null,
        TimeSpan? broadcastStallTimeout = null, int? port = null)
    {
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: count));
        var root = TestConsole.TempDirectory();
        var machines = new List<FakeMachine>();
        for (var n = 1; n <= count; n++)
        {
            var store = FakeMachine.Install(Path.Combine(root, $"PC-{n:00}"), n, payload, System.Net.IPAddress.Loopback.ToString(), port ?? console.Port, burnedCode: false);
            var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger($"PC-{n:00}"), broadcastCeiling: broadcastCeiling,
                broadcastStallTimeout: broadcastStallTimeout);
            machines.Add(machine);
            machine.Start();
        }

        Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count >= count, TimeSpan.FromSeconds(30)),
            $"{console.Session.Linked.Count} of {count} linked");
        return (machines, root);
    }

    private static async Task DisposeAllAsync(IEnumerable<FakeMachine> machines)
    {
        foreach (var machine in machines)
        {
            await machine.DisposeAsync();
        }
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

    /// <summary>The teacher's screen until <see cref="Frozen"/>: then every capture fails, as a locked or asleep display does.</summary>
    private sealed class FreezableScreen : IScreenSource
    {
        private readonly FakeScreen _screen = new(90, 1280, 720, idle: false);

        public volatile bool Frozen;

        public string Kind => "freezable";

        public IScreenFrame? Acquire(TimeSpan timeout)
        {
            if (Frozen)
            {
                Thread.Sleep(timeout);
                throw new ScreenCaptureException("access_lost", "the test froze the screen");
            }

            return _screen.Acquire(timeout);
        }

        public void Dispose()
        {
            // The hub reopens the source after a failure; this one lives for the whole test.
        }
    }

    /// <summary>
    /// A TCP relay between the PC and the console; <see cref="Cut"/> resets every connection
    /// through it — what the PC sees when the console's process dies — and keeps accepting.
    /// </summary>
    private sealed class CuttableRelay : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly int _target;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<System.Net.Sockets.TcpClient> _open = [];
        private readonly Task _accepting;

        public CuttableRelay(int target)
        {
            _target = target;
            _listener.Start();
            _accepting = AcceptAsync();
        }

        public int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

        public void Cut()
        {
            lock (_open)
            {
                foreach (var client in _open)
                {
                    try
                    {
                        client.LingerState = new System.Net.Sockets.LingerOption(true, 0);
                        client.Close();
                    }
                    catch (Exception)
                    {
                    }
                }

                _open.Clear();
            }
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var outbound = new System.Net.Sockets.TcpClient();
                    await outbound.ConnectAsync(System.Net.IPAddress.Loopback, _target, _stop.Token);
                    lock (_open)
                    {
                        _open.Add(inbound);
                        _open.Add(outbound);
                    }

                    _ = PumpAsync(inbound, outbound);
                    _ = PumpAsync(outbound, inbound);
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task PumpAsync(System.Net.Sockets.TcpClient from, System.Net.Sockets.TcpClient to)
        {
            try
            {
                await from.GetStream().CopyToAsync(to.GetStream(), _stop.Token);
            }
            catch (Exception)
            {
            }

            try
            {
                to.LingerState = new System.Net.Sockets.LingerOption(true, 0);
                to.Close();
                from.Close();
            }
            catch (Exception)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            Cut();
            try
            {
                await _accepting;
            }
            catch (Exception)
            {
            }

            _stop.Dispose();
        }
    }

    /// <summary>A PC's stream: keeps every frame; with a gate, each write waits for it, like a PC that reads slowly.</summary>
    private sealed class CollectingWriter : Grpc.Core.IServerStreamWriter<VideoFrame>
    {
        private readonly List<VideoFrame> _frames = [];

        public SemaphoreSlim? Gate { get; init; }

        public List<VideoFrame> Frames
        {
            get
            {
                lock (_frames)
                {
                    return [.. _frames];
                }
            }
        }

        public Grpc.Core.WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(VideoFrame message) => WriteAsync(message, CancellationToken.None);

        public async Task WriteAsync(VideoFrame message, CancellationToken cancellationToken)
        {
            lock (_frames)
            {
                _frames.Add(message);
            }

            if (Gate is not null)
            {
                await Gate.WaitAsync(cancellationToken);
            }
        }
    }
}
