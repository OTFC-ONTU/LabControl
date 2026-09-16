using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// The platform-neutral enforcer (M6 portion 1, D-69) over a never-dialling link and the
/// recording <see cref="FakeMachine"/>: it restores before it reads, publishes what it holds,
/// ends a session on its own timer, re-asserts to a machine that lost it, merges the helper's
/// enforcement word, and restores again on stop without clearing the document.
/// </summary>
public sealed class PolicyEnforcerTests
{
    private const string MutexPrefix = @"Global\LabControl.Policy.test.";

    private sealed class Rig : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "labcontrol-tests", Guid.NewGuid().ToString("n"));

        public DirectoryAgentStore Store { get; }

        public AgentLink Link { get; }

        public FakeMachine Machine { get; } = new();

        public PolicyEnforcer Enforcer { get; }

        public long Now { get; set; } = 1_800_000_000;

        public Rig(string mutexName)
        {
            using var lab = TestLab.Create();
            var config = new AgentConfigDocument
            {
                LabId = lab.LabId,
                AgentId = Guid.NewGuid().ToString("d"),
                Number = 7,
                Hostname = "PC-07",
                Mac = "02:00:5E:00:00:07",
            };
            Store = DirectoryAgentStore.Install(Directory, config, lab.Authority, new KeyProtection(b => b, b => b));
            Link = new AgentLink(Store, new NoBehaviour(), NullLogger.Instance);
            Enforcer = new PolicyEnforcer(Link, Machine, Directory, lab.LabId, NullLogger.Instance, () => DateTimeOffset.FromUnixTimeSeconds(Now), mutexName, TimeSpan.FromMilliseconds(200));
        }

        public async ValueTask DisposeAsync()
        {
            await Enforcer.DisposeAsync();
            await Link.DisposeAsync();
            Store.Dispose();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
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

    [Fact]
    public async Task Start_restores_before_it_reads_and_publishes_what_it_holds()
    {
        await using var rig = new Rig(MutexPrefix + Guid.NewGuid().ToString("n"));
        var outcome = rig.Enforcer.Start();
        Assert.NotNull(outcome);
        Assert.True(outcome!.MachineOpen);
        Assert.Equal([nameof(IMachineState.RestoreDefaults), nameof(IMachineState.Confirm)], rig.Machine.Calls);
        Assert.True(File.Exists(rig.Enforcer.DocumentPath));

        var published = rig.Link.LatestOverlayState;
        Assert.NotNull(published);
        Assert.Equal(Overlay.Types.Kind.Unspecified, published!.Kind);
        Assert.Null(rig.Enforcer.Start());
    }

    [Fact]
    public async Task A_lock_is_applied_to_the_machine_ends_on_the_timer_and_is_reasserted_to_a_machine_that_lost_it()
    {
        await using var rig = new Rig(MutexPrefix + Guid.NewGuid().ToString("n"));
        rig.Enforcer.Start();

        // What the link hands over after the gate: the enforcer answers by publishing.
        var lifted = new List<PolicySession>();
        rig.Enforcer.Lifted += sessions => lifted.AddRange(sessions);
        rig.Enforcer.Apply(new Overlay { Kind = Overlay.Types.Kind.Lock, Message = "look up", SessionId = "s1", HardLimitSeconds = 1 }, "console-a");
        var state = rig.Link.LatestOverlayState!;
        Assert.Equal(Overlay.Types.Kind.Lock, state.Kind);
        Assert.Equal("console-a", state.SetByInstance);
        Assert.NotNull(rig.Machine.Shown);
        Assert.Equal(rig.Now + 1, state.HardLimitUnix);

        // The helper says its hooks hold: merged into the published state.
        rig.Enforcer.NoteEnforcement(true, string.Empty);
        Assert.True(rig.Link.LatestOverlayState!.InputBlocked);
        rig.Enforcer.NoteEnforcement(false, OverlayReasons.HelperDown);
        Assert.False(rig.Link.LatestOverlayState!.InputBlocked);
        Assert.Equal(OverlayReasons.HelperDown, rig.Link.LatestOverlayState!.Reason);

        // A machine that lost the lock (a restarted helper) is told again.
        rig.Machine.Calls.Clear();
        rig.Enforcer.ReassertOverlay();
        Assert.Equal([nameof(IMachineState.ApplyOverlay)], rig.Machine.Calls);

        // The timer is the enforcer's own clock: the deadline passes and the lock is lifted with nobody asking.
        rig.Now += 2;
        Assert.True(await WaitAsync(() => lifted.Count == 1, TimeSpan.FromSeconds(10)));
        Assert.Equal("s1", lifted[0].SessionId);
        Assert.Null(rig.Machine.Shown);
        Assert.Equal(Overlay.Types.Kind.Unspecified, rig.Link.LatestOverlayState!.Kind);
        Assert.Null(rig.Enforcer.Journal.Snapshot().Overlay);
    }

    [Fact]
    public async Task Stop_restores_again_and_keeps_the_document_so_the_next_start_reapplies_a_valid_lock()
    {
        var directory = string.Empty;
        var mutex = MutexPrefix + Guid.NewGuid().ToString("n");
        var rig = new Rig(mutex);
        try
        {
            directory = rig.Directory;
            rig.Enforcer.Start();
            rig.Enforcer.Apply(new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "survivor", Message = "still here" }, "console-a");
            rig.Machine.Calls.Clear();
            await rig.Enforcer.DisposeAsync();
            Assert.Equal([nameof(IMachineState.RestoreDefaults), nameof(IMachineState.Confirm)], rig.Machine.Calls);

            var document = JsonStore.Load<PolicyDocument>(rig.Enforcer.DocumentPath, PolicyDocument.Migrations);
            Assert.Equal("survivor", document.Policy!.Overlay!.SessionId);
        }
        finally
        {
            await rig.Link.DisposeAsync();
            rig.Store.Dispose();
        }

        // A second enforcer over the same directory: restore first, then the lock is back.
        await using var again = new Rig(mutex);
        var enforcer = new PolicyEnforcer(again.Link, again.Machine, directory, again.Store.Config.LabId, NullLogger.Instance, () => DateTimeOffset.FromUnixTimeSeconds(again.Now), mutex);
        try
        {
            // The document names the first rig's lab; give it this rig's lab id so it is not foreign.
            var text = File.ReadAllText(Path.Combine(directory, Defaults.PolicyFileName));
            var first = JsonStore.Parse<PolicyDocument>(text, Defaults.PolicyFileName, PolicyDocument.Migrations);
            first.LabId = again.Store.Config.LabId;
            JsonStore.Save(Path.Combine(directory, Defaults.PolicyFileName), first, PolicyDocument.Migrations);

            var outcome = enforcer.Start();
            Assert.NotNull(outcome);
            Assert.Single(outcome!.Reapplied);
            Assert.Equal("survivor", outcome.Reapplied[0].SessionId);
            Assert.Equal([nameof(IMachineState.RestoreDefaults), nameof(IMachineState.Confirm), nameof(IMachineState.ApplyOverlay)], again.Machine.Calls);
            Assert.Equal(Overlay.Types.Kind.Lock, again.Link.LatestOverlayState!.Kind);
        }
        finally
        {
            await enforcer.DisposeAsync();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task A_mutex_held_elsewhere_makes_the_pass_report_busy_and_touch_nothing()
    {
        var mutex = MutexPrefix + Guid.NewGuid().ToString("n");
        await using var rig = new Rig(mutex);

        using var held = PolicyMutex.TryAcquire(TimeSpan.Zero, mutex);
        Assert.True(held.Acquired);

        Assert.Null(rig.Enforcer.Start());
        Assert.Empty(rig.Machine.Calls);
    }

    [Fact]
    public async Task A_lock_whose_session_id_is_empty_survives_a_restart_and_the_state_for_the_welcome_resend_is_built_without_throwing()
    {
        await using var rig = new Rig(MutexPrefix + Guid.NewGuid().ToString("n"));
        var document = new PolicyDocument
        {
            LabId = rig.Store.Config.LabId,
            SavedAtUnix = rig.Now,
            Policy = new ClassroomPolicy
            {
                Overlay = new OverlaySession { SessionId = string.Empty, SetByInstance = string.Empty, Message = string.Empty, SinceUnix = rig.Now - 5, UntilUnix = 0, HardLimitUnix = rig.Now + 600 },
                Displaced = new DisplacedSettings(),
            },
        };
        JsonStore.Save(rig.Enforcer.DocumentPath, document, PolicyDocument.Migrations);

        var outcome = rig.Enforcer.Start();
        Assert.NotNull(outcome);
        Assert.Single(outcome!.Reapplied);

        // What the link re-sends after Welcome, and what the helper re-assert and the helper's word produce: no throw, empty strings.
        var state = rig.Link.LatestOverlayState!;
        Assert.Equal(Overlay.Types.Kind.Lock, state.Kind);
        Assert.Equal(string.Empty, state.SessionId);
        Assert.Equal(string.Empty, state.SetByInstance);
        rig.Enforcer.ReassertOverlay();
        rig.Enforcer.NoteEnforcement(true, null!);
        Assert.Equal(Overlay.Types.Kind.Lock, rig.Link.LatestOverlayState!.Kind);
        Assert.True(rig.Link.LatestOverlayState!.InputBlocked);
        Assert.Equal(string.Empty, rig.Link.LatestOverlayState!.Reason);
    }

    [Fact]
    public async Task A_machine_that_throws_on_every_call_costs_events_never_the_enforcer()
    {
        // The rule (D-69, M3 of the second-half review): nothing in the enforcer throws, ever —
        // the service dials the link right after Start, and a machine that falls over must
        // cost a warning on the console, not an offline PC.
        await using var rig = new Rig(MutexPrefix + Guid.NewGuid().ToString("n"));
        foreach (var call in new[] { nameof(IMachineState.RestoreDefaults), nameof(IMachineState.RefineRestore), nameof(IMachineState.Confirm),
                     nameof(IMachineState.ApplyOverlay), nameof(IMachineState.LiftOverlay), nameof(IMachineState.ApplyInternet),
                     nameof(IMachineState.LiftInternet), nameof(IMachineState.ApplyExam), nameof(IMachineState.LiftExam) })
        {
            rig.Machine.Answers[call] = () => throw new InvalidOperationException($"{call} fell over");
        }

        var reported = new List<PolicyEvent>();
        rig.Enforcer.Journal.Reported += reported.Add;

        // A corrupt document on top: torn JSON, read only after the (failed) restore.
        File.WriteAllText(rig.Enforcer.DocumentPath, "{ \"schema\": 1, \"lab_id\": ");

        var outcome = rig.Enforcer.Start();
        Assert.NotNull(outcome);
        Assert.False(outcome!.MachineOpen);
        Assert.Contains(reported, e => e.Code == PolicyEvents.RestoreFailed && e.Message.Contains("fell over", StringComparison.Ordinal));
        Assert.Contains(reported, e => e.Code == PolicyEvents.Unreadable);
        Assert.NotNull(rig.Link.LatestOverlayState);
        Assert.Equal(Overlay.Types.Kind.Unspecified, rig.Link.LatestOverlayState!.Kind);

        // Every other entry point, with the machine still throwing: answered, never thrown.
        rig.Enforcer.Apply(new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "doomed", Message = "x" }, "console-a");
        Assert.Equal(Overlay.Types.Kind.Unspecified, rig.Link.LatestOverlayState!.Kind);
        Assert.Contains("fell over", rig.Link.LatestOverlayState!.Problem, StringComparison.Ordinal);
        Assert.Contains(reported, e => e.Code == PolicyEvents.ApplyFailed);
        rig.Enforcer.Apply(null!, "console-a");
        rig.Enforcer.ReassertOverlay();
        rig.Enforcer.NoteEnforcement(false, OverlayReasons.HelperDown);
        await rig.Enforcer.DisposeAsync();
        Assert.False(rig.Enforcer.Journal.MachineOpen);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return condition();
    }
}
