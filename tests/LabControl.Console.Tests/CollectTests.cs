using LabControl.Console.Services;
using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Jobs;
using LabControl.Shared.Lab;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// Collected work and profile reset end to end (M6 portion 5, D-77, D-78): simulated PCs zip a
/// folder of their simulated profile, ask this console for the grant over the real link, upload
/// it, and the console unpacks it into <c>&lt;name date&gt;/PC-07/</c> with a report; a PC that is
/// not online is missing, never skipped; an exam collects at its end on its own.
/// </summary>
public class CollectTests
{
    private static async Task<(TestConsole Console, List<FakeMachine> Machines)> LabAsync(int count)
    {
        var console = await TestConsole.CreateLabAsync();
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: count + 1));
        var root = TestConsole.TempDirectory();
        var machines = new List<FakeMachine>();
        for (var n = 1; n <= count; n++)
        {
            var store = FakeMachine.Install(Path.Combine(root, $"PC-{n:00}"), n, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
            var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger($"PC-{n:00}"));
            machines.Add(machine);
            machine.Start();
        }

        Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == count, TimeSpan.FromSeconds(30)));
        return (console, machines);
    }

    private static void Write(FakeMachine pc, string relative, string text)
    {
        var path = Path.Combine(pc.ProfileDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task Collect_files_brings_each_pcs_folder_into_its_own_folder_and_reports_the_one_that_was_off()
    {
        var (console, machines) = await LabAsync(3);
        await using var _ = console;
        try
        {
            Write(machines[0], @"Documents/Lab3/Main.java", "class Main {}");
            Write(machines[0], @"Documents/Lab3/src/Util.java", "class Util {}");
            Write(machines[1], @"Documents/Lab3/notes.txt", "PC-02");
            Write(machines[2], @"Documents/Lab3/late.txt", "never sent");
            var offline = machines[2].AgentId;
            await machines[2].DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => !console.Session.IsLinked(offline), TimeSpan.FromSeconds(20)));

            var destination = TestConsole.TempDirectory();
            var all = machines.Select(m => m.AgentId).ToArray();
            var collection = console.Session.CollectFiles(all, "Lab 3: Java", destination, @"Documents\Lab3");

            Assert.True(await Wait.UntilAsync(() => collection.Rows.All(r => r.IsFinished), TimeSpan.FromSeconds(30)),
                string.Join("; ", collection.Rows.Select(r => $"{r.Number} {r.State} {r.Message}")));
            Assert.StartsWith(Path.Combine(destination, "Lab 3_ Java "), collection.Directory, StringComparison.Ordinal);
            Assert.Equal("class Main {}", File.ReadAllText(Path.Combine(collection.Directory, "PC-01", "Main.java")));
            Assert.Equal("class Util {}", File.ReadAllText(Path.Combine(collection.Directory, "PC-01", "src", "Util.java")));
            Assert.Equal("PC-02", File.ReadAllText(Path.Combine(collection.Directory, "PC-02", "notes.txt")));
            Assert.False(Directory.Exists(Path.Combine(collection.Directory, "PC-03")));
            Assert.Equal([CollectedState.Collected, CollectedState.Collected, CollectedState.Missing], collection.Rows.Select(r => r.State).ToArray());
            Assert.Equal(2, collection.Rows[0].Files);

            var report = File.ReadAllText(Path.Combine(collection.Directory, Defaults.CollectReportFileName));
            Assert.Contains("PC-03  MISSING", report);
            Assert.Contains("PC-01  collected  2 file(s)", report);
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "collect.done" && e.Message.Contains("2 of 3") && e.Message.Contains("PC-03"));
            Assert.Empty(Directory.GetFiles(collection.Directory, "*.partial", SearchOption.AllDirectories));
            Assert.Equal(0, console.Session.Uploads.InProgressCount);
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
    public async Task A_pc_without_the_folder_has_nothing_to_collect_and_a_grant_is_never_given_for_a_job_that_is_not_its_own()
    {
        var (console, machines) = await LabAsync(2);
        await using var _ = console;
        try
        {
            var collection = console.Session.CollectFiles([machines[0].AgentId], "Nothing", TestConsole.TempDirectory(), @"Desktop\Exam");
            Assert.True(await Wait.UntilAsync(() => collection.Rows.All(r => r.IsFinished), TimeSpan.FromSeconds(20)));
            Assert.Equal(CollectedState.Empty, collection.Rows[0].State);
            Assert.Contains("does not exist", collection.Rows[0].Message);

            // Once finished, the job grants nothing to anyone; an unknown job and an oversized archive are refused too (WorkCollectionsTests holds one in progress).
            var job = console.Session.Jobs.All().Single(j => j.Kind == Job.Types.Kind.CollectFiles);
            var request = new UploadRequest { JobId = job.Id, Size = 10, Sha256 = new string('a', 64) };
            Assert.Empty(console.Session.Collections.RequestUpload(machines[1].AgentId, request).Reference);
            Assert.Empty(console.Session.Collections.RequestUpload(machines[0].AgentId, new UploadRequest { JobId = "nope", Size = 10, Sha256 = new string('a', 64) }).Reference);
            Assert.Empty(console.Session.Collections.RequestUpload(machines[0].AgentId, new UploadRequest { JobId = job.Id, Size = Defaults.CollectMaxBytes * 2, Sha256 = new string('a', 64) }).Reference);
            Assert.Throws<ArgumentException>(() => console.Session.CollectFiles([machines[0].AgentId], "Bad", TestConsole.TempDirectory(), @"..\Windows"));
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
    public async Task An_exam_creates_the_folder_and_collects_it_on_its_own_when_the_timer_runs_out()
    {
        var (console, machines) = await LabAsync(2);
        await using var _ = console;
        try
        {
            var destination = TestConsole.TempDirectory();
            var ids = machines.Select(m => m.AgentId).ToArray();
            var request = new ExamRequest("Test", TimeSpan.FromSeconds(4), false, [], InternetPolicy.Types.Mode.Open, [], string.Empty, "Practical",
                CollectFolder: CollectRequest.DefaultFolder, CollectDestination: destination);
            console.Session.StartExam(ids, request, wholeLab: false);

            var folder = Path.Combine(machines[0].DesktopDirectory, "Exam");
            Assert.True(await Wait.UntilAsync(() => Directory.Exists(folder), TimeSpan.FromSeconds(10)));
            File.WriteAllText(Path.Combine(folder, "answer.txt"), "42");

            // A reset during the exam is refused: it would delete what is about to be collected.
            var reset = console.Session.ResetProfiles([machines[1].AgentId], ResetMode.Light).Single();
            Assert.True(await Wait.UntilAsync(() => reset.IsFinished, TimeSpan.FromSeconds(10)));
            Assert.False(reset.Ok);
            Assert.Contains("exam", reset.Message);

            Assert.True(await Wait.UntilAsync(() => console.Session.Collections.All() is [{ } c] && c.Rows.All(r => r.IsFinished), TimeSpan.FromSeconds(30)),
                string.Join("; ", console.Session.Collections.All().SelectMany(c => c.Rows).Select(r => $"{r.Number} {r.State} {r.Message}")));
            var collection = console.Session.Collections.All().Single();
            Assert.StartsWith(Path.Combine(destination, "Practical "), collection.Directory, StringComparison.Ordinal);
            Assert.Equal("42", File.ReadAllText(Path.Combine(collection.Directory, "PC-01", "answer.txt")));
            Assert.True(Directory.Exists(Path.Combine(collection.Directory, "PC-02")));
            Assert.All(collection.Rows, r => Assert.Equal(CollectedState.Collected, r.State));
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
    public async Task An_exam_ended_by_the_teacher_is_collected_and_a_pc_that_left_is_reported_missing_after_the_grace()
    {
        var clockAhead = TimeSpan.Zero;
        var console = await TestConsole.CreateLabAsync(clock: () => DateTimeOffset.UtcNow + clockAhead);
        await using var _ = console;
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 2));
        var root = TestConsole.TempDirectory();
        var machines = new List<FakeMachine>();
        try
        {
            for (var n = 1; n <= 2; n++)
            {
                var store = FakeMachine.Install(Path.Combine(root, $"PC-{n:00}"), n, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
                machines.Add(new FakeMachine(store, null, TestLogging.Factory.CreateLogger($"PC-{n:00}")));
                machines[^1].Start();
            }

            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == 2, TimeSpan.FromSeconds(30)));
            var ids = machines.Select(m => m.AgentId).ToArray();
            var request = new ExamRequest("Test", null, false, [], InternetPolicy.Types.Mode.Open, [], string.Empty, string.Empty,
                CollectFolder: @"Desktop\Exam", CollectDestination: TestConsole.TempDirectory());
            console.Session.StartExam(ids, request, wholeLab: false);
            Assert.True(await Wait.UntilAsync(() => ids.All(id => console.Session.FindLinked(id)?.InExam == true), TimeSpan.FromSeconds(10)));

            var gone = machines[1].AgentId;
            await machines[1].DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => !console.Session.IsLinked(gone), TimeSpan.FromSeconds(20)));

            console.Session.EndExam(ids);
            Assert.True(await Wait.UntilAsync(() => console.Session.Collections.All() is [{ } c] && c.Rows.Any(r => r.State == CollectedState.Collected), TimeSpan.FromSeconds(20)));

            // The PC that left never reports the end; a minute later it is asked for — and is missing.
            clockAhead = Defaults.CollectAfterDeadlineGrace + TimeSpan.FromSeconds(5);
            var collection = console.Session.Collections.All().Single();
            Assert.True(await Wait.UntilAsync(() => collection.Rows.All(r => r.IsFinished), TimeSpan.FromSeconds(10)));
            Assert.Equal([CollectedState.Collected, CollectedState.Missing], collection.Rows.Select(r => r.State).ToArray());
            Assert.Contains(Localization.Strings.Get("Collect.ExamName") + " ", Path.GetFileName(collection.Directory));
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
    public async Task An_exam_ended_while_its_only_pc_is_off_still_reports_that_pc_missing()
    {
        // VM drill 2026-10-23: the end was judged by what the PCs last reported, so a lab whose
        // PCs were all off when the teacher pressed End exam never reported them missing.
        var clockAhead = TimeSpan.Zero;
        var console = await TestConsole.CreateLabAsync(clock: () => DateTimeOffset.UtcNow + clockAhead);
        await using var _ = console;
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-01"), 1, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-01"));
        machine.Start();
        var id = machine.AgentId;
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(id), TimeSpan.FromSeconds(20)));
        console.Session.StartExam([id], new ExamRequest("Test", null, false, [], InternetPolicy.Types.Mode.Open, [], string.Empty, string.Empty,
            CollectFolder: @"Desktop\Exam", CollectDestination: TestConsole.TempDirectory()), wholeLab: false);
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id)?.InExam == true, TimeSpan.FromSeconds(10)));

        await machine.DisposeAsync();
        Assert.True(await Wait.UntilAsync(() => !console.Session.IsLinked(id), TimeSpan.FromSeconds(20)));
        console.Session.EndExam([id]);

        clockAhead = Defaults.CollectAfterDeadlineGrace + TimeSpan.FromSeconds(5);
        Assert.True(await Wait.UntilAsync(() => console.Session.Collections.All() is [{ } c] && c.Rows.All(r => r.IsFinished), TimeSpan.FromSeconds(10)));
        Assert.Equal(CollectedState.Missing, console.Session.Collections.All().Single().Rows.Single().State);
    }

    [Fact]
    public async Task A_light_reset_empties_the_three_folders_and_a_full_reset_deletes_the_profile_and_reboots()
    {
        var (console, machines) = await LabAsync(1);
        await using var _ = console;
        try
        {
            var pc = machines[0];
            Write(pc, "Desktop/Materials/handout.pdf", "x");
            Write(pc, "Documents/old.docx", "x");
            Write(pc, "Downloads/game.exe", "x");
            Write(pc, "AppData/settings.ini", "kept by a light reset");

            var light = console.Session.ResetProfiles([pc.AgentId], ResetMode.Light).Single();
            Assert.True(await Wait.UntilAsync(() => light.IsFinished, TimeSpan.FromSeconds(10)));
            Assert.True(light.Ok, light.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(pc.DesktopDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(pc.ProfileDirectory, "Documents")));
            Assert.True(File.Exists(Path.Combine(pc.ProfileDirectory, "AppData", "settings.ini")));

            var full = console.Session.ResetProfiles([pc.AgentId], ResetMode.Full).Single();
            Assert.True(await Wait.UntilAsync(() => full.IsFinished, TimeSpan.FromSeconds(10)));
            Assert.True(full.Ok, full.Message);
            Assert.False(Directory.Exists(pc.ProfileDirectory));
            Assert.True(await Wait.UntilAsync(() => !pc.PoweredOn, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            foreach (var machine in machines)
            {
                await machine.DisposeAsync();
            }
        }
    }
}
