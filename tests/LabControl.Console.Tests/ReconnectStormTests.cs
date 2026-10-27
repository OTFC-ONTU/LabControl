using System.Diagnostics;
using System.Net.NetworkInformation;
using LabControl.Console.ViewModels;
using LabControl.Shared;
using LabControl.Shared.Lab;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// M7 robustness pass, the reconnect storm (ROADMAP M7): a full room of
/// <see cref="Defaults.MaxStudentPcs"/> PCs loses the link at the same moment — the console
/// closes every connection, or every PC drops its own — and all of them dial back at once.
/// The jobs that were running or queued at that moment must neither be lost nor run twice
/// (the agent's ledger, D-32 item 7), the console's picture of the room must end consistent,
/// and nothing — threads, TCP connections, linked entries — may grow from storm to storm.
/// </summary>
public sealed class ReconnectStormTests
{
    /// <summary>
    /// How long a full room may take to relink. On the Mac the thirty simulated PCs share one
    /// process, and every dial re-imports the PC's TLS identity through the macOS keychain,
    /// which serialises them (about 170 ms each, 5 s for thirty); real PCs dial in parallel.
    /// </summary>
    private static readonly TimeSpan RelinkBound = TimeSpan.FromSeconds(20);

    private const int Storms = 3;

    [Fact]
    public async Task A_full_room_dropped_by_the_console_relinks_and_every_job_runs_exactly_once()
    {
        await using var room = await Room.OpenAsync();

        // Batch A is running on every PC when the storm hits; it finishes during the outage.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        room.HoldJobsTagged("long", gate.Task);
        var running = room.Console.Session.CreateJobs(room.AgentIds, Job.Types.Kind.RunScript, Args("long"));
        Assert.True(await Wait.UntilAsync(() => room.Agents.All(a => a.Ran(running) == running.Count(j => j.AgentId == a.AgentId)), TimeSpan.FromSeconds(15)),
            "batch A did not reach every PC before the storm");

        // The storm: every connection closed by the console in the same instant, while the
        // teacher clicks another batch (B) — some rows go out on dying links, some wait.
        var noticed = new System.Collections.Concurrent.ConcurrentDictionary<string, TimeSpan>();
        var clock = Stopwatch.StartNew();
        foreach (var agent in room.Agents)
        {
            agent.Link.Unlinked += _ => noticed.TryAdd(agent.AgentId, clock.Elapsed);
        }

        foreach (var connection in room.Console.Session.Linked)
        {
            connection.Close("storm");
        }

        var queued = room.Console.Session.CreateJobs(room.AgentIds, Job.Types.Kind.RunScript, Args("quick"));
        gate.SetResult();

        // Every PC sees the console's close at once and starts dialling back. Before the M7
        // fix a PC waited for its heartbeat loop's next tick (up to Defaults.HeartbeatInterval)
        // before it even counted the link as gone.
        Assert.True(await Wait.UntilAsync(() => noticed.Count == Defaults.MaxStudentPcs, TimeSpan.FromSeconds(10)), $"{noticed.Count} PCs noticed the close");
        var slowest = noticed.Values.Max();
        Assert.True(slowest < TimeSpan.FromSeconds(2.5), $"the slowest PC noticed the console's close after {slowest.TotalSeconds:0.0} s");

        await room.AssertSettledAsync([.. running, .. queued]);
    }

    [Fact]
    public async Task A_full_room_whose_links_are_cut_on_the_pcs_relinks_and_every_job_runs_exactly_once()
    {
        await using var room = await Room.OpenAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        room.HoldJobsTagged("long", gate.Task);
        var running = room.Console.Session.CreateJobs(room.AgentIds, Job.Types.Kind.RunScript, Args("long"));
        Assert.True(await Wait.UntilAsync(() => room.Agents.All(a => a.Ran(running) == 1), TimeSpan.FromSeconds(15)));

        // The switch reboots: every PC sees its link die at once. Batch B is clicked while
        // the console still believes the links are up, so it is queued on streams nobody reads.
        await Parallel.ForEachAsync(room.Agents, (agent, _) =>
        {
            agent.Link.Disconnect("simulated switch reboot");
            return ValueTask.CompletedTask;
        });

        var queued = room.Console.Session.CreateJobs(room.AgentIds, Job.Types.Kind.RunScript, Args("quick"));

        // The long jobs finish while the room is still dialling back: their results wait in
        // each PC's pending list and go out after the next Welcome.
        gate.SetResult();

        await room.AssertSettledAsync([.. running, .. queued]);
    }

    [Fact]
    public async Task Repeated_storms_leave_no_growth_in_threads_connections_or_linked_entries()
    {
        await using var room = await Room.OpenAsync();
        var baseline = Resources.Measure(room.Console.Port);
        var all = new List<JobRecord>();

        for (var storm = 1; storm <= Storms; storm++)
        {
            // Alternate who cuts the link, and click a batch right into every storm.
            if (storm % 2 == 1)
            {
                foreach (var connection in room.Console.Session.Linked)
                {
                    connection.Close($"storm {storm}");
                }
            }
            else
            {
                foreach (var agent in room.Agents)
                {
                    agent.Link.Disconnect($"storm {storm}");
                }
            }

            all.AddRange(room.Console.Session.CreateJobs(room.AgentIds, Job.Types.Kind.RunScript, Args("quick")));
            await room.AssertSettledAsync(all);
        }

        // Give the runtime a moment to retire what the last storm's links left behind.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Resources after = default;
        var settled = await Wait.UntilAsync(() =>
        {
            after = Resources.Measure(room.Console.Port);
            return after.Threads <= baseline.Threads + 40 && (after.Established < 0 || after.Established <= baseline.Established);
        }, TimeSpan.FromSeconds(15));

        TestContext.Current.SendDiagnosticMessage($"reconnect storms: before {baseline}, after {Storms} storms {after}");
        Assert.True(settled, $"after {Storms} storms: {after} (before: {baseline})");
        Assert.Equal(Defaults.MaxStudentPcs, room.Console.Session.Registry.Document.Machines.Count);
    }

    /// <summary>
    /// A PC pinned to the console's address keeps dialling with a growing backoff while the
    /// console is away. When the console comes back, the dial that links is followed by that
    /// backoff step (4 s and more); a link lost in that window — the console closed again, a
    /// storm — must be redialled within about a second, not after the rest of the step.
    /// </summary>
    [Fact]
    public async Task A_pc_that_linked_after_a_long_backoff_redials_at_once_when_that_link_drops()
    {
        var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.InstallEnrolled(console, 3, Defaults.AgentCertificateLifetime).Start();
        LabControl.Console.Services.LabSession? again = null;
        try
        {
            Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(pc.AgentId)));
            var port = console.Port;
            var directory = console.Directory;

            // The console goes away long enough for the PC's backoff to reach its 4 s step.
            await console.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => !pc.Link.IsLinked));
            await Task.Delay(TimeSpan.FromSeconds(3.5), TestContext.Current.CancellationToken);

            var store = new LabControl.Console.Services.LabStore(directory);
            var vault = new LabControl.Console.Services.LabKeyVault(store, store.LoadLabKey());
            var instance = LabControl.Shared.Identity.ConsoleInstance.Open(store.LoadInstance()!);
            var options = new LabControl.Console.Services.ConsoleOptions
            {
                DataDirectory = directory,
                Port = port,
                BeaconPort = TestConsole.BeaconPort,
                BindAddress = System.Net.IPAddress.Loopback,
            };
            again = new LabControl.Console.Services.LabSession(options, store, vault, instance, instance.Document, TestLogging.Factory);
            await again.StartAsync();
            Assert.True(await Wait.UntilAsync(() => again.IsLinked(pc.AgentId) && pc.Link.IsLinked, TimeSpan.FromSeconds(15)), "the PC did not come back to the restarted console");

            // Lost again straight away: the PC is back within about a second, not after its step.
            var clock = Stopwatch.StartNew();
            again.FindLinked(pc.AgentId)!.Close("closed again");
            Assert.True(await Wait.UntilAsync(() => !pc.Link.IsLinked));
            Assert.True(await Wait.UntilAsync(() => again.IsLinked(pc.AgentId) && pc.Link.IsLinked, TimeSpan.FromSeconds(15)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"the PC relinked {clock.Elapsed.TotalSeconds:0.0} s after the link dropped");
        }
        finally
        {
            if (again is not null)
            {
                await again.DisposeAsync();
            }

            TestConsole.DeleteTempDirectory(console.DataDirectory);
        }
    }

    private static Dictionary<string, string> Args(string tag) => new() { ["script"] = $"{tag}.ps1", ["tag"] = tag };

    /// <summary>The process's thread count and the established TCP connections on the console's port (both ends), when the OS lets us see them.</summary>
    private readonly record struct Resources(int Threads, int Established)
    {
        public static Resources Measure(int port)
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var established = -1;
            try
            {
                established = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                    .Count(c => c.State == TcpState.Established && (c.LocalEndPoint.Port == port || c.RemoteEndPoint.Port == port));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
            }

            return new Resources(process.Threads.Count, established);
        }
    }

    /// <summary>One console and a full room of enrolled PCs, every one linked.</summary>
    private sealed class Room : IAsyncDisposable
    {
        // There is no dispatcher here, so a lock stands in for the UI thread (as in OwnershipTests).
        private readonly Lock _ui = new();
        private MainViewModel? _view;

        private Room(TestConsole console, IReadOnlyList<StormAgent> agents)
        {
            Console = console;
            Agents = agents;
        }

        /// <summary>The main window's view model over the session: the mosaic the teacher would be looking at during the storm.</summary>
        private MainViewModel View
        {
            get
            {
                lock (_ui)
                {
                    return _view ??= new MainViewModel(Console.Session, Console.Bootstrap, new OwnershipTests.SilentDialogs(), action =>
                    {
                        lock (_ui)
                        {
                            action();
                        }
                    });
                }
            }
        }

        public TestConsole Console { get; }

        public IReadOnlyList<StormAgent> Agents { get; }

        public IReadOnlyList<string> AgentIds => Agents.Select(a => a.AgentId).ToArray();

        public static async Task<Room> OpenAsync()
        {
            var console = await TestConsole.CreateLabAsync();
            var agents = new List<StormAgent>();
            for (var n = 1; n <= Defaults.MaxStudentPcs; n++)
            {
                agents.Add(new StormAgent(TestAgent.InstallEnrolled(console, n, Defaults.AgentCertificateLifetime)));
            }

            foreach (var agent in agents)
            {
                agent.Agent.Start();
            }

            var room = new Room(console, agents);
            _ = room.View;
            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == Defaults.MaxStudentPcs && agents.All(a => a.Link.IsLinked), RelinkBound),
                $"only {console.Session.Linked.Count} of {Defaults.MaxStudentPcs} linked");
            return room;
        }

        public void HoldJobsTagged(string tag, Task gate)
        {
            foreach (var agent in Agents)
            {
                agent.Hold(tag, gate);
            }
        }

        /// <summary>
        /// The room is whole again: every PC relinked to this console on exactly one
        /// connection, every job closed as succeeded, and each one run exactly once on its PC.
        /// </summary>
        public async Task AssertSettledAsync(IReadOnlyList<JobRecord> jobs)
        {
            var session = Console.Session;
            var relinked = await Wait.UntilAsync(() => session.Linked.Count == Defaults.MaxStudentPcs && Agents.All(a => a.Link.IsLinked), RelinkBound);
            Assert.True(relinked, $"{session.Linked.Count} of {Defaults.MaxStudentPcs} relinked within {RelinkBound.TotalSeconds:0} s; " +
                                  $"not linked: {string.Join(", ", Agents.Where(a => !a.Link.IsLinked).Select(a => a.Link.Name))}");

            var finished = await Wait.UntilAsync(() => jobs.All(j => j.IsFinished), RelinkBound);
            Assert.True(finished, "unfinished after the storm: " + string.Join("; ", jobs.Where(j => !j.IsFinished).Select(j => $"PC-{Number(j.AgentId):00} {j.Args["tag"]} {j.State}")));

            var failed = jobs.Where(j => j.State != JobState.Succeeded).ToArray();
            Assert.True(failed.Length == 0, "not succeeded: " + string.Join("; ", failed.Select(j => $"PC-{Number(j.AgentId):00} {j.Args["tag"]} {j.State} {j.Message}")));

            // Never lost, never twice: each job id was admitted by its own PC's ledger once.
            foreach (var agent in Agents)
            {
                var mine = jobs.Where(j => j.AgentId == agent.AgentId).Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
                var runs = agent.Runs.Where(mine.Contains).GroupBy(id => id).ToArray();
                Assert.True(runs.Length == mine.Count, $"{agent.Link.Name} ran {runs.Length} of its {mine.Count} jobs");
                Assert.True(runs.All(g => g.Count() == 1), $"{agent.Link.Name} ran a job twice: {string.Join(", ", runs.Where(g => g.Count() > 1).Select(g => g.Key))}");
                Assert.Equal(0, agent.Link.PendingResults);
            }

            // One live connection per PC, every one current, and the registry has the room once.
            var linked = session.Linked;
            Assert.Equal(Defaults.MaxStudentPcs, linked.Select(c => c.AgentId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(linked, c => Assert.Null(c.ClosedBecause));
            Assert.All(Agents, a => Assert.True(session.IsLinked(a.AgentId)));
            Assert.Equal(Defaults.MaxStudentPcs, session.Registry.Document.Machines.Count);
            Assert.DoesNotContain(session.Jobs.All(), j => j.State is JobState.Pending or JobState.Delivered or JobState.Running);

            // The mosaic agrees: thirty tiles, every one online, and the status line counts them.
            var view = View;
            var tilesOnline = await Wait.UntilAsync(() =>
            {
                lock (_ui)
                {
                    view.Tick();
                    return view.Machines.Count == Defaults.MaxStudentPcs && view.Machines.All(t => t.IsOnline);
                }
            }, TimeSpan.FromSeconds(10));
            lock (_ui)
            {
                Assert.True(tilesOnline, $"{view.Machines.Count(t => t.IsOnline)} of {view.Machines.Count} tiles online: " +
                                         string.Join(", ", view.Machines.Where(t => !t.IsOnline).Select(t => $"PC-{t.Number:00} {t.Status}")));
            }
        }

        private int Number(string agentId) => Agents.Single(a => a.AgentId == agentId).Agent.Number;

        public async ValueTask DisposeAsync()
        {
            lock (_ui)
            {
                _view?.Detach();
            }

            foreach (var agent in Agents)
            {
                await agent.Agent.DisposeAsync();
            }

            await Console.DisposeAsync();
        }
    }

    /// <summary>A <see cref="TestAgent"/> whose behaviour records every job it runs and can hold some of them.</summary>
    private sealed class StormAgent
    {
        private readonly List<string> _runs = [];
        private readonly Dictionary<string, Task> _holds = new(StringComparer.Ordinal);

        public StormAgent(TestAgent agent)
        {
            Agent = agent;
            agent.Behaviour.OnJob = async job =>
            {
                Task? hold;
                lock (_runs)
                {
                    _runs.Add(job.Id);
                    hold = job.Args.TryGetValue("tag", out var tag) ? _holds.GetValueOrDefault(tag) : null;
                }

                if (hold is not null)
                {
                    await hold;
                }

                return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = "done" };
            };
        }

        public TestAgent Agent { get; }

        public AgentLink Link => Agent.Link;

        public string AgentId => Agent.AgentId;

        public IReadOnlyList<string> Runs
        {
            get
            {
                lock (_runs)
                {
                    return _runs.ToArray();
                }
            }
        }

        public int Ran(IEnumerable<JobRecord> jobs)
        {
            var ids = jobs.Where(j => j.AgentId == AgentId).Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
            return Runs.Count(ids.Contains);
        }

        public void Hold(string tag, Task gate)
        {
            lock (_runs)
            {
                _holds[tag] = gate;
            }
        }
    }
}
