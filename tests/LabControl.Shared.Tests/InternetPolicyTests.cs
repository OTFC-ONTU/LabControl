using LabControl.Shared.Persistence;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// The standalone internet policy in the journal (M6 portion 3, D-73): durations stamped on the
/// PC's clock, the 8 h clamp, originals saved before the machine changes, the undo after a
/// failed apply, amend, lift, the clock, the restart, the exam's precedence and the watchdog's
/// restore-only pass — all against a recording machine on the Mac.
/// </summary>
public sealed class InternetPolicyTests
{
    private const string LabId = "1c383cd3-0b7c-4d69-9a37-4bfd08bb6c2e";
    private const string Instance = "console-a";
    private const long Now = 1_800_000_000;
    private static readonly long EightHours = (long)Defaults.InternetPolicyHardLimit.TotalSeconds;

    /// <summary>Records calls; an apply writes one original into the block and has the journal save it before "mutating".</summary>
    private sealed class Machine : IMachineState
    {
        public List<string> Calls { get; } = [];

        public Dictionary<string, Func<MachineResult>> Answers { get; } = [];

        public InternetSession? Enforced { get; private set; }

        public DisplacedSettings? LiftedWith { get; private set; }

        public Action? BeforeMutate { get; set; }

        public InternetRuntime Runtime { get; set; } = Policy.InternetRuntime.None;

        public MachineResult RestoreDefaults() => Answer(nameof(RestoreDefaults));

        public MachineResult RefineRestore(DisplacedSettings displaced) => Answer(nameof(RefineRestore));

        public MachineResult Confirm() => Answer(nameof(Confirm));

        public MachineResult ApplyOverlay(OverlaySession session) => Answer(nameof(ApplyOverlay));

        public MachineResult LiftOverlay() => Answer(nameof(LiftOverlay));

        public MachineResult ApplyInternet(InternetSession session, DisplacedSettings displaced, Action recordDisplaced)
        {
            if (displaced.ProfileDefaultOutbound.Count == 0)
            {
                displaced.ProfileDefaultOutbound["public"] = "allow";
            }

            recordDisplaced();
            BeforeMutate?.Invoke();
            var result = Answer(nameof(ApplyInternet));
            if (result.Succeeded)
            {
                displaced.Applied = true;
                Enforced = session;
            }

            return result;
        }

        public MachineResult LiftInternet(DisplacedSettings displaced)
        {
            LiftedWith = displaced;
            var result = Answer(nameof(LiftInternet));
            if (result.Succeeded)
            {
                Enforced = null;
            }

            return result;
        }

        public InternetRuntime InternetRuntime() => Runtime;

        public MachineResult ApplyExam(ExamSession session) => Answer(nameof(ApplyExam));

        public MachineResult LiftExam() => Answer(nameof(LiftExam));

        private MachineResult Answer(string call)
        {
            Calls.Add(call);
            return Answers.TryGetValue(call, out var answer) ? answer() : MachineResult.Done;
        }
    }

    private sealed class Rig
    {
        public Machine Machine { get; } = new();

        public List<PolicyDocument> Saves { get; } = [];

        public List<PolicyEvent> Events { get; } = [];

        public PolicyJournal Journal { get; }

        public Rig(string? document = null)
        {
            Journal = new PolicyJournal(Machine, LabId, () => document, Saves.Add);
            Journal.Reported += Events.Add;
        }

        public PolicyDocument LastSave => Saves[^1];
    }

    private static InternetPolicy Whitelist(string session = "w1", long untilSeconds = 0, params string[] hosts)
    {
        var policy = new InternetPolicy { Mode = InternetPolicy.Types.Mode.Whitelist, SessionId = session, UntilSeconds = untilSeconds, PresetName = "Java docs" };
        policy.AllowedHosts.AddRange(hosts.Length > 0 ? hosts : ["*.jetbrains.com", "docs.oracle.com"]);
        return policy;
    }

    private static string Document(ClassroomPolicy policy) =>
        JsonStore.Serialize(new PolicyDocument { LabId = LabId, SavedAtUnix = Now, Policy = policy }, PolicyDocument.Migrations);

    [Fact]
    public void A_whitelist_is_stamped_on_this_clock_clamped_to_eight_hours_and_normalized()
    {
        var rig = new Rig();
        var policy = Whitelist("w1", 5400, "https://docs.oracle.com/javase/17/", "*.JetBrains.com", "not a host", "docs.oracle.com");
        policy.HardLimitSeconds = 30L * 24 * 3600;

        var state = rig.Journal.SetInternet(policy, Instance, Now);

        Assert.Equal(string.Empty, state.Problem);
        Assert.Equal(InternetPolicy.Types.Mode.Whitelist, state.Mode);
        Assert.Equal(Now + 5400, state.UntilUnix);
        Assert.Equal(Now + EightHours, state.HardLimitUnix);
        Assert.Equal(5400, state.RemainingSeconds);
        Assert.Equal(["docs.oracle.com", "*.jetbrains.com"], state.AllowedHosts);
        Assert.Equal(Instance, state.SetByInstance);
        Assert.Equal("Java docs", state.PresetName);
        Assert.Equal("w1", rig.LastSave.Policy!.Internet!.SessionId);
        Assert.Equal(Now + 5400, rig.Journal.NextDeadline);
        Assert.True(rig.Journal.HoldsNetworkPolicy);
    }

    [Fact]
    public void The_originals_are_saved_before_the_machine_changes_anything()
    {
        var rig = new Rig();
        PolicyDocument? atMutation = null;
        rig.Machine.BeforeMutate = () => atMutation = rig.LastSave;

        rig.Journal.SetInternet(new InternetPolicy { Mode = InternetPolicy.Types.Mode.Blocked, SessionId = "b1" }, Instance, Now);

        Assert.NotNull(atMutation);
        Assert.Equal("allow", atMutation!.Policy!.Displaced!.ProfileDefaultOutbound["public"]);
        Assert.Null(atMutation.Policy.Internet);
        Assert.True(rig.LastSave.Policy!.Displaced!.Applied);
        Assert.Equal(InternetPolicy.Types.Mode.Blocked, rig.LastSave.Policy.Internet!.Mode);
    }

    [Fact]
    public void Open_lifts_with_the_displaced_block_and_forgets_it()
    {
        var rig = new Rig();
        rig.Journal.SetInternet(Whitelist(), Instance, Now);

        var state = rig.Journal.SetInternet(new InternetPolicy { Mode = InternetPolicy.Types.Mode.Open }, "console-b", Now + 60);

        Assert.Equal(InternetPolicy.Types.Mode.Open, state.Mode);
        Assert.Equal("allow", rig.Machine.LiftedWith!.ProfileDefaultOutbound["public"]);
        Assert.True(rig.LastSave.Policy!.Displaced!.IsEmpty);
        Assert.Null(rig.LastSave.Policy.Internet);
        Assert.False(rig.Journal.HoldsNetworkPolicy);
        Assert.Null(rig.Journal.NextDeadline);
    }

    [Fact]
    public void Amend_changes_only_the_hosts_and_keeps_the_timing()
    {
        var rig = new Rig();
        rig.Journal.SetInternet(Whitelist("w1", 3600), Instance, Now);

        var amend = Whitelist("w1", 0, "*.jetbrains.com", "docs.oracle.com", "fonts.gstatic.com");
        amend.AmendHosts = true;
        var state = rig.Journal.SetInternet(amend, "console-b", Now + 600);

        Assert.Equal(string.Empty, state.Problem);
        Assert.Contains("fonts.gstatic.com", state.AllowedHosts);
        Assert.Equal(Now + 3600, state.UntilUnix);
        Assert.Equal(Instance, state.SetByInstance);
        Assert.Equal(2, rig.Machine.Calls.Count(c => c == nameof(IMachineState.ApplyInternet)));
    }

    [Fact]
    public void Amending_a_session_the_pc_does_not_hold_is_refused_and_changes_nothing()
    {
        var rig = new Rig();
        rig.Journal.SetInternet(Whitelist("w1"), Instance, Now);
        var amend = Whitelist("other", 0, "example.com");
        amend.AmendHosts = true;

        var state = rig.Journal.SetInternet(amend, Instance, Now + 1);

        Assert.Equal(PolicyEvents.RefusedNoInternetSession, state.Problem);
        Assert.DoesNotContain("example.com", state.AllowedHosts);
        Assert.Equal(1, rig.Machine.Calls.Count(c => c == nameof(IMachineState.ApplyInternet)));
    }

    [Fact]
    public void A_failed_apply_is_undone_and_nothing_is_held()
    {
        var rig = new Rig();
        rig.Machine.Answers[nameof(IMachineState.ApplyInternet)] = () => MachineResult.Failed("netsh broke");

        var state = rig.Journal.SetInternet(Whitelist(), Instance, Now);

        Assert.Contains("netsh broke", state.Problem);
        Assert.Equal(InternetPolicy.Types.Mode.Open, state.Mode);
        Assert.Contains(nameof(IMachineState.LiftInternet), rig.Machine.Calls);
        Assert.Null(rig.LastSave.Policy!.Internet);
        Assert.True(rig.LastSave.Policy.Displaced!.IsEmpty);
        Assert.Contains(rig.Events, e => e.Code == PolicyEvents.ApplyFailed);
    }

    [Fact]
    public void A_failed_apply_whose_undo_also_fails_keeps_the_session_so_the_clock_retries()
    {
        var rig = new Rig();
        rig.Machine.Answers[nameof(IMachineState.ApplyInternet)] = () => MachineResult.Failed("half done");
        rig.Machine.Answers[nameof(IMachineState.LiftInternet)] = () => MachineResult.Failed("stuck");

        var state = rig.Journal.SetInternet(Whitelist(), Instance, Now);

        Assert.NotEmpty(state.Problem);
        Assert.False(rig.Journal.MachineOpen);
        Assert.NotNull(rig.LastSave.Policy!.Internet);
        Assert.False(rig.LastSave.Policy.Displaced!.IsEmpty);
        Assert.Equal(Now + EightHours, rig.Journal.NextDeadline);
    }

    [Theory]
    [InlineData(InternetPolicy.Types.Mode.Unspecified, -1L)]
    [InlineData(InternetPolicy.Types.Mode.Whitelist, -5L)]
    public void Unknown_modes_and_ended_requests_are_refused_untouched(InternetPolicy.Types.Mode mode, long until)
    {
        var rig = new Rig();

        var state = rig.Journal.SetInternet(new InternetPolicy { Mode = mode, SessionId = "x", UntilSeconds = until }, Instance, Now);

        Assert.NotEmpty(state.Problem);
        Assert.DoesNotContain(nameof(IMachineState.ApplyInternet), rig.Machine.Calls);
    }

    [Fact]
    public void The_clock_lifts_it_at_its_end_and_forgets_the_originals()
    {
        var rig = new Rig();
        rig.Journal.SetInternet(Whitelist("w1", 600), Instance, Now);

        Assert.Empty(rig.Journal.Tick(Now + 599));
        var lifted = rig.Journal.Tick(Now + 600);

        Assert.Single(lifted);
        Assert.Contains(nameof(IMachineState.LiftInternet), rig.Machine.Calls);
        Assert.True(rig.LastSave.Policy!.Displaced!.IsEmpty);
        Assert.Contains(rig.Events, e => e.Code == PolicyEvents.Expired);
    }

    [Fact]
    public void A_restart_restores_first_then_re_applies_a_valid_policy_with_its_limit_re_clamped()
    {
        var rig = new Rig(Document(new ClassroomPolicy
        {
            Internet = new InternetSession
            {
                SessionId = "w1", SetByInstance = Instance, Mode = InternetPolicy.Types.Mode.Whitelist, AllowedHosts = ["docs.oracle.com"],
                SinceUnix = Now - 60, HardLimitUnix = Now + 30L * 24 * 3600,
            },
            Displaced = new DisplacedSettings { ProfileDefaultOutbound = { ["public"] = "block" } },
        }));

        var outcome = rig.Journal.Start(Now);

        Assert.True(outcome.MachineOpen);
        Assert.Single(outcome.Reapplied);
        Assert.True(rig.Machine.Calls.IndexOf(nameof(IMachineState.RestoreDefaults)) < rig.Machine.Calls.IndexOf(nameof(IMachineState.ApplyInternet)));
        Assert.Equal(Now + EightHours, rig.Journal.Snapshot().Internet!.HardLimitUnix);
    }

    [Fact]
    public void Under_an_exam_internet_policy_the_standalone_one_waits_and_comes_back_after_it()
    {
        var rig = new Rig(Document(new ClassroomPolicy
        {
            Exam = new ExamSession { SessionId = "e", SetByInstance = Instance, SinceUnix = Now - 60, UntilUnix = Now + 600, HardLimitUnix = Now + 3600 },
            ExamInternet = new InternetSession { SessionId = "ei", SetByInstance = Instance, Mode = InternetPolicy.Types.Mode.Blocked, HardLimitUnix = Now + 3600 },
            Internet = new InternetSession { SessionId = "w1", SetByInstance = Instance, Mode = InternetPolicy.Types.Mode.Whitelist, AllowedHosts = ["docs.oracle.com"], HardLimitUnix = Now + 7200 },
            Displaced = new DisplacedSettings(),
        }));

        rig.Journal.Start(Now);

        Assert.Equal("ei", rig.Machine.Enforced!.SessionId);
        var state = rig.Journal.InternetStateOf(Now);
        Assert.Equal(InternetPolicy.Types.Mode.Blocked, state.Mode);
        Assert.Contains(InternetReasons.ExamWins, state.Reason);

        rig.Journal.Tick(Now + 600);

        Assert.Equal("w1", rig.Machine.Enforced!.SessionId);
        Assert.Equal(InternetPolicy.Types.Mode.Whitelist, rig.Journal.InternetStateOf(Now + 600).Mode);
        Assert.DoesNotContain(nameof(IMachineState.LiftInternet), rig.Machine.Calls);
    }

    [Fact]
    public void The_watchdog_pass_restores_and_refines_but_re_applies_nothing_and_keeps_the_sessions()
    {
        var rig = new Rig(Document(new ClassroomPolicy
        {
            Internet = new InternetSession { SessionId = "w1", SetByInstance = Instance, Mode = InternetPolicy.Types.Mode.Whitelist, AllowedHosts = ["docs.oracle.com"], HardLimitUnix = Now + 3600 },
            Displaced = new DisplacedSettings { Dns = { new DisplacedDns { Interface = "{1}", Servers = ["10.0.0.2"] } } },
        }));

        var outcome = rig.Journal.RestoreOnly(Now);

        Assert.True(outcome.MachineOpen);
        Assert.Equal([nameof(IMachineState.RestoreDefaults), nameof(IMachineState.RefineRestore), nameof(IMachineState.Confirm)], rig.Machine.Calls);
        Assert.Equal("w1", rig.LastSave.Policy!.Internet!.SessionId);
        Assert.True(rig.LastSave.Policy.Displaced!.IsEmpty);
    }

    [Fact]
    public void Refused_names_travel_only_under_a_whitelist_with_the_machine_reasons()
    {
        var rig = new Rig();
        rig.Machine.Runtime = new InternetRuntime(true, ["fonts.gstatic.com"], 3, InternetReasons.NoUpstream);

        var blocked = rig.Journal.SetInternet(new InternetPolicy { Mode = InternetPolicy.Types.Mode.Blocked, SessionId = "b" }, Instance, Now, rig.Machine.Runtime);
        Assert.Empty(blocked.RefusedRecently);

        var whitelist = rig.Journal.SetInternet(Whitelist(), Instance, Now, rig.Machine.Runtime);
        Assert.Equal(["fonts.gstatic.com"], whitelist.RefusedRecently);
        Assert.True(whitelist.ResolverActive);
        Assert.Contains(InternetReasons.NoUpstream, whitelist.Reason);
    }

    [Fact]
    public void An_old_document_without_disabled_rules_still_reads()
    {
        var text = Document(new ClassroomPolicy { Displaced = new DisplacedSettings() }).Replace("\"disabled_rules\": [],", string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled_rules", text);
        var rig = new Rig(text);

        rig.Journal.Start(Now);

        Assert.DoesNotContain(rig.Events, e => e.Code == PolicyEvents.Unreadable);
    }
}
