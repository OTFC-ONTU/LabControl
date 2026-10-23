using LabControl.Shared.Exam;
using LabControl.Shared.Persistence;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// Exam mode in the journal (M6 portion 4, D-75, D-76): durations stamped on the PC's clock and
/// clamped to four hours, each switch alone, re-stating and ending, the "time is up" lock when
/// the timer — not the hard limit — runs out, the exam's internet switch winning over a
/// standalone policy and handing it back, and an exam whose internet cannot be applied not
/// applied at all. Against the recording <see cref="FakeMachine"/>.
/// </summary>
public sealed class ExamPolicyTests
{
    private const string LabId = "1c383cd3-0b7c-4d69-9a37-4bfd08bb6c2e";
    private const string Instance = "console-a";
    private const long Now = 1_800_000_000;
    private static readonly long FourHours = (long)Defaults.ExamHardLimit.TotalSeconds;

    private sealed class Rig
    {
        public FakeMachine Machine { get; } = new();

        public List<PolicyEvent> Events { get; } = [];

        public PolicyDocument? Saved { get; private set; }

        public PolicyJournal Journal { get; }

        public Rig(string? document = null)
        {
            Journal = new PolicyJournal(Machine, LabId, () => document, saved => Saved = saved);
            Journal.Reported += Events.Add;
        }
    }

    private static ExamMode Exam(string session = "e1", long untilSeconds = 0, bool restrict = false, InternetPolicy? internet = null, params string[] programs)
    {
        var exam = new ExamMode { SessionId = session, Active = true, UntilSeconds = untilSeconds, RestrictPrograms = restrict, Message = "Variant 2", PresetName = "Practical", Internet = internet };
        exam.AllowedPrograms.AddRange(programs);
        return exam;
    }

    private static InternetPolicy Blocked() => new() { Mode = InternetPolicy.Types.Mode.Blocked, PresetName = "blocked" };

    [Fact]
    public void A_timer_only_exam_is_stamped_on_this_clock_and_touches_neither_the_internet_nor_the_programs()
    {
        var rig = new Rig();
        var request = Exam(untilSeconds: 4800);
        request.HardLimitSeconds = 30L * 24 * 3600;

        var state = rig.Journal.SetExam(request, Instance, Now);

        Assert.Equal(string.Empty, state.Problem);
        Assert.True(state.Active);
        Assert.Equal(Now + 4800, state.UntilUnix);
        Assert.Equal(Now + FourHours, state.HardLimitUnix);
        Assert.Equal(4800, state.RemainingSeconds);
        Assert.Equal(Instance, state.SetByInstance);
        Assert.Equal("Practical", state.PresetName);
        Assert.False(state.RestrictPrograms);
        Assert.False(state.WhitelistEnforced);
        Assert.DoesNotContain(nameof(IMachineState.ApplyInternet), rig.Machine.Calls);
        Assert.Null(rig.Journal.Snapshot().ExamInternet);
        Assert.False(rig.Journal.HoldsNetworkPolicy);
        Assert.Equal("e1", rig.Saved!.Policy!.Exam!.SessionId);
        Assert.Equal(Now + 4800, rig.Journal.NextDeadline);
    }

    [Fact]
    public void An_internet_only_exam_has_no_timer_and_its_internet_lives_and_dies_with_it()
    {
        var rig = new Rig();

        var state = rig.Journal.SetExam(Exam(internet: Blocked()), Instance, Now);

        Assert.Equal(0, state.UntilUnix);
        Assert.Equal(FourHours, state.RemainingSeconds);
        var held = rig.Journal.Snapshot();
        Assert.Equal(InternetPolicy.Types.Mode.Blocked, held.ExamInternet!.Mode);
        Assert.Equal("e1" + PolicyJournal.ExamInternetSuffix, held.ExamInternet.SessionId);
        Assert.Equal(Now + FourHours, held.ExamInternet.HardLimitUnix);
        Assert.Equal(InternetPolicy.Types.Mode.Blocked, rig.Journal.InternetStateOf(Now).Mode);

        var ended = rig.Journal.SetExam(new ExamMode { SessionId = "e1", Active = false }, "console-b", Now + 60);

        Assert.False(ended.Active);
        Assert.Equal(string.Empty, ended.Problem);
        Assert.True(rig.Journal.Snapshot().IsEmpty);
        Assert.Equal(InternetPolicy.Types.Mode.Open, rig.Journal.InternetStateOf(Now + 60).Mode);
        Assert.Contains(nameof(IMachineState.LiftInternet), rig.Machine.Calls);
        Assert.Contains(nameof(IMachineState.LiftExam), rig.Machine.Calls);
    }

    [Fact]
    public void Program_names_are_normalized_and_the_switch_is_carried_as_asked()
    {
        var rig = new Rig();

        var state = rig.Journal.SetExam(Exam(restrict: true, programs: ["IDEA64", @"C:\Program Files\Java\bin\java.exe", "idea64.exe", "  ", "bad|name"]), Instance, Now);

        Assert.True(state.RestrictPrograms);
        Assert.Equal(["idea64.exe", "java.exe"], state.AllowedPrograms);
    }

    [Fact]
    public void Re_stating_the_same_exam_keeps_its_start_and_extends_its_timer()
    {
        var rig = new Rig();
        rig.Journal.SetExam(Exam(untilSeconds: 600), Instance, Now);

        var extended = rig.Journal.SetExam(Exam(untilSeconds: 900), "console-b", Now + 300);

        Assert.Equal(Now, extended.SinceUnix);
        Assert.Equal(Now + 1200, extended.UntilUnix);
        Assert.Equal("console-b", extended.SetByInstance);
        Assert.Equal(2, rig.Machine.Calls.Count(c => c == nameof(IMachineState.ApplyExam)));
    }

    [Fact]
    public void A_negative_timer_or_an_end_for_another_exam_is_refused_and_changes_nothing()
    {
        var rig = new Rig();
        Assert.Equal(PolicyEvents.RefusedAlreadyEnded, rig.Journal.SetExam(Exam(untilSeconds: -1), Instance, Now).Problem);
        Assert.Null(rig.Journal.Snapshot().Exam);

        rig.Journal.SetExam(Exam("e1"), Instance, Now);
        var refused = rig.Journal.SetExam(new ExamMode { SessionId = "other", Active = false }, Instance, Now);

        Assert.Equal(PolicyEvents.RefusedNoExamSession, refused.Problem);
        Assert.True(refused.Active);
        Assert.Equal("e1", rig.Journal.Snapshot().Exam!.SessionId);
    }

    [Fact]
    public void When_the_timer_runs_out_the_screen_locks_with_time_is_up_until_the_teacher_unlocks_it()
    {
        var rig = new Rig();
        rig.Journal.SetExam(Exam(untilSeconds: 600), Instance, Now);

        var lifted = rig.Journal.Tick(Now + 600);

        Assert.Single(lifted);
        var held = rig.Journal.Snapshot();
        Assert.Null(held.Exam);
        Assert.NotNull(held.Overlay);
        Assert.Equal(Overlay.Types.Kind.Lock, held.Overlay!.Kind);
        Assert.Equal("e1" + PolicyJournal.TimeUpSuffix, held.Overlay.SessionId);
        Assert.Equal(Instance, held.Overlay.SetByInstance);
        Assert.Equal(0, held.Overlay.UntilUnix);
        Assert.Equal(Now + 600 + (long)Defaults.LockHardLimit.TotalSeconds, held.Overlay.HardLimitUnix);
        Assert.Equal(OverlayText.TimeUpMessage, held.Overlay.Message);
        Assert.Contains(rig.Events, e => e.Code == PolicyEvents.ExamTimeUp);
        Assert.Equal(Overlay.Types.Kind.Lock, rig.Journal.OverlayStateOf(Now + 601).Kind);
        Assert.False(rig.Journal.ExamStateOf(Now + 601).Active);
    }

    [Fact]
    public void The_hard_limit_only_restores_it_never_locks()
    {
        var rig = new Rig();
        rig.Journal.SetExam(Exam(), Instance, Now);

        rig.Journal.Tick(Now + FourHours);

        Assert.True(rig.Journal.Snapshot().IsEmpty);
        Assert.DoesNotContain(rig.Events, e => e.Code == PolicyEvents.ExamTimeUp);
    }

    [Fact]
    public void A_teachers_lock_held_when_the_timer_runs_out_stays_as_it_is()
    {
        var rig = new Rig();
        rig.Journal.SetExam(Exam(untilSeconds: 600), Instance, Now);
        rig.Journal.SetOverlay(new Overlay { Kind = Overlay.Types.Kind.Lock, SessionId = "teacher", Message = "Eyes front" }, "console-b", Now + 500);

        rig.Journal.Tick(Now + 600);

        Assert.Equal("teacher", rig.Journal.Snapshot().Overlay!.SessionId);
    }

    [Fact]
    public void Ending_the_exam_early_does_not_lock()
    {
        var rig = new Rig();
        rig.Journal.SetExam(Exam(untilSeconds: 600), Instance, Now);

        rig.Journal.SetExam(new ExamMode { SessionId = "e1", Active = false }, Instance, Now + 60);

        Assert.True(rig.Journal.Snapshot().IsEmpty);
    }

    [Fact]
    public void A_standalone_policy_set_for_the_lesson_survives_an_exam_that_starts_and_ends_inside_it()
    {
        var rig = new Rig();
        rig.Journal.SetInternet(new InternetPolicy { Mode = InternetPolicy.Types.Mode.Whitelist, SessionId = "lesson", UntilSeconds = 5400, AllowedHosts = { "docs.oracle.com" } }, Instance, Now);

        rig.Journal.SetExam(Exam(untilSeconds: 1800, internet: Blocked()), Instance, Now + 600);

        var during = rig.Journal.InternetStateOf(Now + 700);
        Assert.Equal(InternetPolicy.Types.Mode.Blocked, during.Mode);
        Assert.Contains(InternetReasons.ExamWins, during.Reason);
        Assert.Equal(InternetPolicy.Types.Mode.Blocked, rig.Machine.EnforcedInternet!.Mode);

        rig.Journal.Tick(Now + 2400);

        var after = rig.Journal.InternetStateOf(Now + 2401);
        Assert.Equal(InternetPolicy.Types.Mode.Whitelist, after.Mode);
        Assert.Equal("lesson", after.SessionId);
        Assert.Equal("lesson", rig.Machine.EnforcedInternet!.SessionId);
        Assert.DoesNotContain(nameof(IMachineState.LiftInternet), rig.Machine.Calls);
    }

    [Fact]
    public void An_exam_whose_internet_switch_cannot_be_applied_is_not_applied_at_all()
    {
        var rig = new Rig();
        rig.Machine.Answers[nameof(IMachineState.ApplyInternet)] = () => MachineResult.Failed("firewall off");

        var state = rig.Journal.SetExam(Exam(untilSeconds: 600, internet: Blocked()), Instance, Now);

        Assert.Contains("firewall off", state.Problem);
        Assert.False(state.Active);
        Assert.True(rig.Journal.Snapshot().IsEmpty);
        Assert.Contains(nameof(IMachineState.LiftInternet), rig.Machine.Calls);

        // The internet switch is applied first: a refused exam never asked a program to close.
        Assert.DoesNotContain(nameof(IMachineState.ApplyExam), rig.Machine.Calls);
    }

    [Fact]
    public void A_re_state_whose_internet_fails_keeps_the_running_exam()
    {
        var rig = new Rig();
        rig.Journal.SetExam(Exam(untilSeconds: 600, internet: Blocked()), Instance, Now);
        rig.Machine.Answers[nameof(IMachineState.ApplyInternet)] = () => MachineResult.Failed("firewall off");
        var whitelist = new InternetPolicy { Mode = InternetPolicy.Types.Mode.Whitelist, AllowedHosts = { "docs.oracle.com" } };

        var state = rig.Journal.SetExam(Exam(untilSeconds: 900, internet: whitelist), Instance, Now + 60);

        Assert.Contains("firewall off", state.Problem);
        Assert.True(state.Active);
        Assert.Equal(Now + 600, state.UntilUnix);
        Assert.Null(rig.Journal.Snapshot().ExamInternet);
    }

    [Fact]
    public void A_clock_that_notices_the_end_hours_late_does_not_lock()
    {
        var rig = new Rig();
        rig.Journal.SetExam(Exam(untilSeconds: 600), Instance, Now);

        rig.Journal.Tick(Now + 600 + 3 * 3600);

        Assert.True(rig.Journal.Snapshot().IsEmpty);
    }

    [Fact]
    public void A_restart_inside_the_exam_re_applies_it_with_the_time_it_had_left()
    {
        var first = new Rig();
        first.Journal.SetExam(Exam(untilSeconds: 3600, restrict: true, internet: Blocked(), programs: ["idea64.exe"]), Instance, Now);
        var document = JsonStore.Serialize(first.Saved!, PolicyDocument.Migrations);

        var rebooted = new Rig(document);
        var outcome = rebooted.Journal.Start(Now + 900);

        Assert.Equal(2, outcome.Reapplied.Count);
        var state = rebooted.Journal.ExamStateOf(Now + 900);
        Assert.True(state.Active);
        Assert.Equal(2700, state.RemainingSeconds);
        Assert.Equal(["idea64.exe"], state.AllowedPrograms);
        Assert.Equal(InternetPolicy.Types.Mode.Blocked, rebooted.Journal.InternetStateOf(Now + 900).Mode);
        Assert.True(rebooted.Machine.Calls.IndexOf(nameof(IMachineState.RestoreDefaults)) < rebooted.Machine.Calls.IndexOf(nameof(IMachineState.ApplyExam)));
    }

    [Fact]
    public void The_live_side_is_folded_into_the_state()
    {
        var rig = new Rig();
        rig.Machine.Exam = new ExamRuntime(true, ["telegram.exe", "chrome.exe"], 3, ExamReasons.EndRefused);
        rig.Journal.SetExam(Exam(restrict: true, programs: ["idea64.exe"]), Instance, Now);

        var state = rig.Journal.ExamStateOf(Now + 5);

        Assert.True(state.WhitelistEnforced);
        Assert.Equal(["telegram.exe", "chrome.exe"], state.TerminatedRecently);
        Assert.Equal(ExamReasons.EndRefused, state.Reason);
    }

    [Fact]
    public void A_helper_that_restarted_is_told_the_exam_again()
    {
        var rig = new Rig();
        rig.Journal.SetExam(Exam(untilSeconds: 600), Instance, Now);
        rig.Machine.Calls.Clear();

        Assert.True(rig.Journal.ReassertExam(Now + 10));
        Assert.Equal([nameof(IMachineState.ApplyExam)], rig.Machine.Calls);
        Assert.False(rig.Journal.ReassertExam(Now + 700));
    }
}

/// <summary>The allowed-programs judgement and its state machine (D-75), pure.</summary>
public sealed class ProgramWatchTests
{
    private const string Student = "S-1-5-21-1-2-3-1001";
    private static readonly SessionContext Context = new(1, Student, [@"C:\Program Files\LabControl"]);
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static ProcessInfo P(int pid, string name, string? sid = Student, int session = 1, string? path = null) =>
        new(pid, name, session, sid, path ?? @"C:\Apps\" + name, pid * 10L);

    [Theory]
    [InlineData("IDEA64", "idea64.exe")]
    [InlineData(@"C:\Program Files\JetBrains\bin\idea64.exe", "idea64.exe")]
    [InlineData("\"java.EXE\"", "java.exe")]
    [InlineData("python3.12", "python3.12.exe")]
    public void Names_normalize_to_the_lower_case_file_name_with_exe(string text, string expected)
    {
        Assert.True(ProgramNames.TryNormalize(text, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\Windows\")]
    [InlineData("a|b.exe")]
    [InlineData("...")]
    public void Nonsense_is_not_a_program(string text) => Assert.False(ProgramNames.TryNormalize(text, out _));

    [Fact]
    public void Session_and_token_are_judged_before_the_name()
    {
        var allowed = new HashSet<string> { "idea64.exe" };

        Assert.True(ProgramWhitelist.ShouldClose(P(1, "chrome.exe"), Context, allowed));
        Assert.False(ProgramWhitelist.ShouldClose(P(2, "chrome.exe", sid: "S-1-5-18"), Context, allowed));
        Assert.False(ProgramWhitelist.ShouldClose(P(3, "chrome.exe", sid: null), Context, allowed));
        Assert.False(ProgramWhitelist.ShouldClose(P(4, "chrome.exe", session: 0), Context, allowed));
        Assert.False(ProgramWhitelist.ShouldClose(P(5, "chrome.exe"), SessionContext.Nobody, allowed));
        Assert.False(ProgramWhitelist.ShouldClose(P(6, "IDEA64.EXE"), Context, allowed));
        Assert.False(ProgramWhitelist.ShouldClose(P(7, "explorer.exe"), Context, allowed));
        Assert.False(ProgramWhitelist.ShouldClose(P(8, "conhost.exe"), Context, allowed));
        Assert.False(ProgramWhitelist.ShouldClose(P(9, "session.exe", path: @"C:\Program Files\LabControl\app\1.0.0\session.exe"), Context, allowed));
    }

    [Fact]
    public void A_never_touch_name_outside_windows_is_judged_like_any_program()
    {
        var context = Context with { SystemDirectories = [@"C:\Windows"] };
        var allowed = new HashSet<string>();

        Assert.False(ProgramWhitelist.ShouldClose(P(1, "conhost.exe", path: @"C:\Windows\System32\conhost.exe"), context, allowed));
        Assert.True(ProgramWhitelist.ShouldClose(P(2, "conhost.exe", path: @"C:\Users\student\Desktop\conhost.exe"), context, allowed));
        Assert.False(ProgramWhitelist.ShouldClose(P(3, "conhost.exe") with { ImagePath = null }, context, allowed));
    }

    [Fact]
    public void A_look_that_sees_nobody_keeps_the_sweep_for_the_next()
    {
        var watch = new ProgramWatch(grace: TimeSpan.FromSeconds(10));
        watch.Begin([]);

        Assert.True(watch.Observe([], SessionContext.Nobody, T0).IsEmpty);

        var next = watch.Observe([P(4, "word.exe")], Context, T0.AddSeconds(1));
        Assert.Single(next.AskToClose);
        Assert.Empty(next.End);
    }

    [Fact]
    public void An_empty_list_closes_everything_but_the_shell_and_windows()
    {
        var allowed = new HashSet<string>();
        foreach (var name in ProgramWhitelist.NeverTouch)
        {
            Assert.False(ProgramWhitelist.ShouldClose(P(1, name), Context, allowed));
        }

        Assert.True(ProgramWhitelist.ShouldClose(P(2, "notepad.exe"), Context, allowed));
    }

    [Fact]
    public void The_opening_sweep_asks_first_and_ends_after_the_grace()
    {
        var watch = new ProgramWatch(grace: TimeSpan.FromSeconds(10));
        watch.Begin(["idea64.exe"]);
        var open = new[] { P(1, "explorer.exe"), P(2, "chrome.exe"), P(3, "idea64.exe") };

        var first = watch.Observe(open, Context, T0);
        Assert.Equal([2], first.AskToClose.Select(p => p.ProcessId));
        Assert.Empty(first.End);

        Assert.True(watch.Observe(open, Context, T0.AddSeconds(5)).IsEmpty);

        var late = watch.Observe(open, Context, T0.AddSeconds(10));
        Assert.Equal([2], late.End.Select(p => p.ProcessId));
    }

    [Fact]
    public void A_program_started_during_the_exam_is_ended_at_once()
    {
        var watch = new ProgramWatch();
        watch.Begin(["idea64.exe"]);
        watch.Observe([P(1, "explorer.exe")], Context, T0);

        var decision = watch.Observe([P(1, "explorer.exe"), P(4, "telegram.exe"), P(5, "idea64.exe")], Context, T0.AddSeconds(1));

        Assert.Empty(decision.AskToClose);
        Assert.Equal([4], decision.End.Select(p => p.ProcessId));
    }

    [Fact]
    public void A_program_that_will_not_end_is_decided_again()
    {
        var watch = new ProgramWatch();
        watch.Begin([]);
        watch.Observe([], Context, T0);
        var stubborn = P(4, "game.exe");

        Assert.Single(watch.Observe([stubborn], Context, T0.AddSeconds(1)).End);
        Assert.Single(watch.Observe([stubborn], Context, T0.AddSeconds(2)).End);
    }

    [Fact]
    public void The_agents_own_processes_are_never_judged()
    {
        var watch = new ProgramWatch(exempt: pid => pid == 7);
        watch.Begin([]);
        watch.Observe([], Context, T0);

        Assert.True(watch.Observe([P(7, "powershell.exe")], Context, T0.AddSeconds(1)).IsEmpty);
    }

    [Fact]
    public void A_power_job_pauses_the_watch_and_the_look_after_it_is_a_sweep()
    {
        var watch = new ProgramWatch(grace: TimeSpan.FromSeconds(10));
        watch.Begin([]);
        watch.Observe([], Context, T0);
        watch.Suspend(T0.AddSeconds(60));

        Assert.True(watch.Observe([P(4, "shutdown-helper.exe")], Context, T0.AddSeconds(30)).IsEmpty);

        var after = watch.Observe([P(4, "shutdown-helper.exe")], Context, T0.AddSeconds(61));
        Assert.Single(after.AskToClose);
        Assert.Empty(after.End);
    }

    [Fact]
    public void Re_stating_the_same_list_does_not_sweep_again_but_a_new_list_does()
    {
        var watch = new ProgramWatch();
        watch.Begin(["idea64.exe"]);
        watch.Observe([P(1, "idea64.exe")], Context, T0);

        watch.Begin(["IDEA64"]);
        Assert.True(watch.Observe([P(1, "idea64.exe")], Context, T0.AddSeconds(1)).IsEmpty);

        watch.Begin(["java.exe"]);
        Assert.Single(watch.Observe([P(1, "idea64.exe")], Context, T0.AddSeconds(2)).AskToClose);
    }

    [Fact]
    public void A_reused_process_id_is_another_process()
    {
        var watch = new ProgramWatch();
        watch.Begin([]);
        watch.Observe([P(4, "game.exe") with { StartedAt = 1 }], Context, T0);

        // The same id, a new creation time: a program started during the exam.
        var decision = watch.Observe([P(4, "game.exe") with { StartedAt = 2 }], Context, T0.AddSeconds(30));
        Assert.Empty(decision.AskToClose);
        Assert.Single(decision.End);
    }

    [Fact]
    public void An_ended_watch_judges_nothing()
    {
        var watch = new ProgramWatch();
        watch.Begin([]);
        watch.End();

        Assert.True(watch.Observe([P(4, "game.exe")], Context, T0).IsEmpty);
        Assert.False(watch.Active);
    }
}
