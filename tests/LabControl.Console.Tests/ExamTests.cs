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
/// Exam mode end to end (M6 portion 4, PROTOCOL "Exam mode", D-75, D-76): the console's
/// <c>ExamMode</c> reaches simulated PCs over the real link, each PC's own journal holds it and
/// says so in <c>ExamState</c>, the tile follows that word, the whitelist closes the simulated
/// student's programs and the console hears which, a PC that links during a lab-wide exam
/// joins it with what is left, extend keeps the session, a timer that runs out leaves a "time
/// is up" lock, the exam's internet switch hands back to a standalone policy, a second console
/// of the lab may end an exam it sees, and a withdrawn console is refused at the gate.
/// </summary>
public class ExamTests
{
    private const int Simulated = Defaults.MaxStudentPcs - 1;

    private static ExamRequest Request(TimeSpan? timer, bool restrict = true, InternetPolicy.Types.Mode internet = InternetPolicy.Types.Mode.Open, IReadOnlyList<string>? hosts = null) =>
        new("Test 3", timer, restrict, restrict ? ["idea64.exe", "JAVA"] : [], internet, hosts ?? [], hosts is null ? string.Empty : "Java docs", "Practical");

    [Fact]
    public async Task A_lab_wide_exam_reaches_thirty_pcs_closes_what_is_not_allowed_and_a_late_pc_joins_with_the_time_left()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: Defaults.MaxStudentPcs));
        var root = TestConsole.TempDirectory();
        var machines = new List<FakeMachine>();
        FakeMachine? late = null;
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
            Assert.True(await Wait.UntilAsync(() => machines.All(m => console.Session.FindLinked(m.AgentId)?.ExamState is { Active: false })));

            var all = console.Session.Registry.Document.Machines.Select(m => m.AgentId).ToArray();
            var (sessionId, sent) = console.Session.StartExam(all, Request(TimeSpan.FromMinutes(40)), wholeLab: true);
            Assert.Equal(Simulated, sent);
            Assert.Equal(sessionId, console.Session.LabExamSessionId);

            Assert.True(await Wait.UntilAsync(() => machines.All(m => console.Session.FindLinked(m.AgentId)?.InExam == true), TimeSpan.FromSeconds(20)),
                $"{machines.Count(m => console.Session.FindLinked(m.AgentId)?.InExam == true)} in the exam");
            var first = machines[0];
            var state = console.Session.FindLinked(first.AgentId)!.ExamState!;
            Assert.Equal(sessionId, state.SessionId);
            Assert.Equal(console.Session.Instance.InstanceId, state.SetByInstance);
            Assert.Equal(["idea64.exe", "java.exe"], state.AllowedPrograms);
            Assert.True(state.RestrictPrograms);
            Assert.InRange(state.RemainingSeconds, 39 * 60, 40 * 60);
            Assert.True(state.HardLimitUnix <= DateTimeOffset.UtcNow.Add(Defaults.ExamHardLimit).ToUnixTimeSeconds() + 5);
            Assert.All(machines, m => Assert.Equal(sessionId, m.Exam!.SessionId));

            // The opening sweep closed what the simulated student had open, and the console hears which.
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(first.AgentId)!.ExamState!.TerminatedRecently.Count >= 2, TimeSpan.FromSeconds(10)));
            Assert.Contains("chrome.exe", console.Session.FindLinked(first.AgentId)!.ExamState!.TerminatedRecently);
            Assert.Contains("notepad.exe", console.Session.FindLinked(first.AgentId)!.ExamState!.TerminatedRecently);
            Assert.Contains("explorer.exe", first.Running);
            Assert.DoesNotContain("chrome.exe", first.Running);

            // A program started during the exam is closed at once; one on the list keeps running.
            first.Launch("telegram.exe");
            first.Launch("idea64.exe");
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(first.AgentId)!.ExamState!.TerminatedRecently.Contains("telegram.exe"), TimeSpan.FromSeconds(10)));
            Assert.Contains("idea64.exe", first.Running);

            var tile = Tile(console, first.AgentId);
            Assert.True(tile.InExam);
            Assert.StartsWith("EXAM ", tile.ExamBadge, StringComparison.Ordinal);
            Assert.EndsWith("· Practical", tile.ExamBadge, StringComparison.Ordinal);
            Assert.Contains("this console", tile.ExamText);
            Assert.Contains("telegram.exe", tile.ExamText);
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "exam.started");

            // The thirtieth PC comes online during the exam and joins it with the time that is left.
            var lateStore = FakeMachine.Install(Path.Combine(root, "PC-30"), Defaults.MaxStudentPcs, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
            late = new FakeMachine(lateStore, null, TestLogging.Factory.CreateLogger("PC-30"));
            late.Start();
            Assert.True(await Wait.UntilAsync(() => late.Exam?.SessionId == sessionId, TimeSpan.FromSeconds(30)));
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(late.AgentId)?.ExamState is { Active: true } s && s.SessionId == sessionId, TimeSpan.FromSeconds(10)));
            Assert.InRange(console.Session.FindLinked(late.AgentId)!.ExamState!.RemainingSeconds, 38 * 60, 40 * 60);
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "exam.late_join");

            // Extend: the same session, more time — on every PC of the lab, the late one included.
            all = console.Session.Registry.Document.Machines.Select(m => m.AgentId).ToArray();
            var before = console.Session.FindLinked(first.AgentId)!.ExamState!.UntilUnix;
            Assert.Equal(Defaults.MaxStudentPcs, console.Session.ExtendExam(all, TimeSpan.FromMinutes(10)));
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(first.AgentId)!.ExamState!.UntilUnix >= before + 9 * 60, TimeSpan.FromSeconds(10)));
            Assert.Equal(sessionId, console.Session.FindLinked(first.AgentId)!.ExamState!.SessionId);

            // End: every PC lifts, the lab-wide exam is forgotten, no lock is left behind.
            Assert.Equal(Defaults.MaxStudentPcs, console.Session.EndExam(all));
            Assert.Null(console.Session.LabExamSessionId);
            Assert.True(await Wait.UntilAsync(() => machines.Append(late).All(m => m.Exam is null && console.Session.FindLinked(m.AgentId)?.InExam == false), TimeSpan.FromSeconds(20)));
            Assert.All(machines, m => Assert.Null(m.Lock));
            Assert.False(Tile(console, first.AgentId).InExam);
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "exam.ended");

            // The next exam starts with nothing closed: what this one closed was its own (VM drill 2026-10-23).
            var (next, _) = console.Session.StartExam([first.AgentId], Request(null, restrict: false), wholeLab: false);
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(first.AgentId)?.ExamState is { Active: true } s && s.SessionId == next, TimeSpan.FromSeconds(10)));
            Assert.Empty(console.Session.FindLinked(first.AgentId)!.ExamState!.TerminatedRecently);
        }
        finally
        {
            foreach (var machine in machines)
            {
                await machine.DisposeAsync();
            }

            if (late is not null)
            {
                await late.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_timer_that_runs_out_ends_the_exam_and_leaves_a_time_up_lock_the_console_can_lift()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-04"), 4, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-04"));
        machine.Start();
        var id = machine.AgentId;
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(id), TimeSpan.FromSeconds(20)));

        var (sessionId, _) = console.Session.StartExam([id], Request(TimeSpan.FromSeconds(3), restrict: false), wholeLab: false);
        Assert.Null(console.Session.LabExamSessionId);
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id)?.InExam == true, TimeSpan.FromSeconds(10)));

        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id) is { InExam: false, OverlayLocked: true }, TimeSpan.FromSeconds(15)));
        var overlay = console.Session.FindLinked(id)!.OverlayState!;
        Assert.Equal(sessionId + PolicyJournal.TimeUpSuffix, overlay.SessionId);
        Assert.Equal(console.Session.Instance.InstanceId, overlay.SetByInstance);
        Assert.Equal(0, overlay.UntilUnix);
        Assert.Equal(OverlayText.TimeUpMessage("en"), machine.Lock!.Message);
        Assert.Equal("locked by this console", Tile(console, id).LockText);

        Assert.Equal(1, console.Session.Unlock([id]));
        Assert.True(await Wait.UntilAsync(() => machine.Lock is null && console.Session.FindLinked(id)?.OverlayLocked == false, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Extend_counts_from_what_is_left_now_not_from_the_last_state_the_pc_sent()
    {
        // VM drill 2026-10-23: the PC re-sends ExamState only on change, so its remaining_seconds
        // ages; ten console minutes after the last state, +5 min must leave 35 of 40, not 45.
        var ahead = TimeSpan.Zero;
        await using var console = await TestConsole.CreateLabAsync(clock: () => DateTimeOffset.UtcNow + ahead);
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-05"), 5, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-05"));
        machine.Start();
        var id = machine.AgentId;
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(id), TimeSpan.FromSeconds(20)));

        console.Session.StartExam([id], Request(TimeSpan.FromMinutes(40), restrict: false), wholeLab: false);
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id)?.InExam == true, TimeSpan.FromSeconds(10)));
        var before = console.Session.FindLinked(id)!.ExamState!.UntilUnix;

        ahead = TimeSpan.FromMinutes(10);
        Assert.Equal(1, console.Session.ExtendExam([id], TimeSpan.FromMinutes(5)));
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id)!.ExamState!.UntilUnix < before - 4 * 60, TimeSpan.FromSeconds(10)));
        Assert.InRange(console.Session.FindLinked(id)!.ExamState!.UntilUnix, before - 5 * 60 - 5, before - 5 * 60 + 5);
    }

    [Fact]
    public async Task The_exams_internet_switch_wins_over_a_standalone_policy_and_hands_it_back_at_the_end()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-06"), 6, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-06"));
        machine.Start();
        var id = machine.AgentId;
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(id), TimeSpan.FromSeconds(20)));

        console.Session.SetInternet([id], InternetPolicy.Types.Mode.Blocked, [], string.Empty, TimeSpan.FromMinutes(90));
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id)?.InternetMode == InternetPolicy.Types.Mode.Blocked, TimeSpan.FromSeconds(10)));
        var standalone = console.Session.FindLinked(id)!.InternetState!.SessionId;

        var (sessionId, _) = console.Session.StartExam([id], Request(TimeSpan.FromMinutes(30), restrict: false, InternetPolicy.Types.Mode.Whitelist, ["docs.oracle.com"]), wholeLab: false);
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id) is { InExam: true, InternetMode: InternetPolicy.Types.Mode.Whitelist }, TimeSpan.FromSeconds(10)));
        var during = console.Session.FindLinked(id)!.InternetState!;
        Assert.Equal(sessionId + PolicyJournal.ExamInternetSuffix, during.SessionId);
        Assert.Equal(["docs.oracle.com"], during.AllowedHosts);
        Assert.Contains(InternetReasons.ExamWins, during.Reason);
        Assert.Equal("Java docs", during.PresetName);

        Assert.Equal(1, console.Session.EndExam([id]));
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id) is { InExam: false, InternetMode: InternetPolicy.Types.Mode.Blocked } c
            && c.InternetState!.SessionId == standalone, TimeSpan.FromSeconds(10)));
        Assert.Equal(InternetPolicy.Types.Mode.Blocked, machine.Internet!.Mode);
        Assert.Null(machine.Lock);
    }

    [Fact]
    public async Task A_second_console_of_the_lab_sees_whose_exam_it_is_and_may_end_it()
    {
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

        var (sessionId, _) = macBook.Session.StartExam([id], Request(null), wholeLab: false);
        Assert.True(await Wait.UntilAsync(() => macBook.Session.FindLinked(id)?.InExam == true, TimeSpan.FromSeconds(10)));
        Assert.Equal("EXAM · Practical", Tile(macBook, id).ExamBadge);

        await using var deskPc = await TestConsole.JoinLabAsync(macBook, "Lab PC");
        Assert.True(await Wait.UntilAsync(() => deskPc.Session.OtherConsoles.Any(o => o.Name == "MacBook") && macBook.Session.OtherConsoles.Any(o => o.Name == "Lab PC"), TimeSpan.FromSeconds(15)));
        deskPc.Session.TakeOver();
        Assert.True(await Wait.UntilAsync(() => deskPc.Session.FindLinked(id)?.InExam == true, TimeSpan.FromSeconds(20)));
        Assert.Equal(sessionId, deskPc.Session.FindLinked(id)!.ExamState!.SessionId);
        Assert.Contains("MacBook", Tile(deskPc, id).ExamText);

        Assert.Equal(1, deskPc.Session.EndExam([id]));
        Assert.True(await Wait.UntilAsync(() => machine.Exam is null && deskPc.Session.FindLinked(id)?.InExam == false, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_console_the_pc_holds_withdrawn_is_refused_at_the_gate_and_the_tile_shows_no_exam()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-05"), 5, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-05"));
        machine.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(machine.AgentId), TimeSpan.FromSeconds(20)));
        var connection = console.Session.FindLinked(machine.AgentId)!;
        Assert.True(await Wait.UntilAsync(() => connection.ExamState is not null, TimeSpan.FromSeconds(10)));

        // As in LockTests: the entry placed in the PC's revocation set, as if just merged.
        var lab = console.Session.Vault!.Peek()!;
        var entry = RevocationSet.Create(lab, LabCertificates.InstanceSerial(console.Session.Instance.InstanceId), "withdrawn in a test", DateTimeOffset.UtcNow);
        var revocations = (RevocationSet)typeof(AgentLink).GetField("_revocations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(machine.Link)!;
        Assert.Single(revocations.Merge(console.Authority, [entry]));

        console.Session.StartExam([machine.AgentId], Request(TimeSpan.FromMinutes(10)), wholeLab: false);
        Assert.True(await Wait.UntilAsync(() => connection.ExamProblem is not null, TimeSpan.FromSeconds(10)));
        Assert.Equal(PolicyEvents.RefusedWithdrawn, connection.ExamProblem);
        Assert.False(connection.InExam);
        Assert.Null(machine.Exam);
        var tile = Tile(console, machine.AgentId);
        Assert.False(tile.InExam);
        Assert.Equal(PolicyEvents.RefusedWithdrawn, tile.ExamProblem);
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "exam.refused");
    }

    [Fact]
    public async Task A_pc_without_an_enforcer_answers_with_a_problem_not_silence()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var mute = TestAgent.Install(console, 9, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(mute.AgentId), TimeSpan.FromSeconds(15)));

        console.Session.StartExam([mute.AgentId], Request(TimeSpan.FromMinutes(5)), wholeLab: false);
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(mute.AgentId)?.ExamProblem is not null, TimeSpan.FromSeconds(15)));
        Assert.False(console.Session.FindLinked(mute.AgentId)!.InExam);
        Assert.NotEmpty(Tile(console, mute.AgentId).ExamProblem);
    }

    [Fact]
    public void The_dialog_explains_every_bad_program_name()
    {
        var (programs, problem) = ExamDialogModel.ParsePrograms("IDEA64\nC:\\Program Files\\Java\\bin\\java.exe\n\nbad|name\nidea64.exe");
        Assert.Equal(["idea64.exe", "java.exe"], programs);
        Assert.NotNull(problem);
        Assert.Contains("bad|name", problem);

        var (none, ok) = ExamDialogModel.ParsePrograms(" \n ");
        Assert.Empty(none);
        Assert.Null(ok);
    }

    [Fact]
    public void The_badge_reasons_are_words_and_an_unknown_code_is_shown_as_itself()
    {
        var text = MachineTileViewModel.ExamReasonText("exam.no_user exam.something_new");
        Assert.Contains("nobody is signed in", text);
        Assert.Contains("exam.something_new", text);
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
