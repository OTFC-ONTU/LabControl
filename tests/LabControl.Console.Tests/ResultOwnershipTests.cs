using System.Net;
using System.Text;
using System.Text.Json;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Jobs;
using LabControl.Shared.Lab;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// A result belongs to the console instance that delivered the job (M5 portion 4, D-57
/// item 4): two consoles of the same lab take turns on one port, as two teacher machines
/// do, and the PC answers each job only to the instance that sent it. The same tests cover
/// what a returning console may send again — a script whose text it can still serve, and
/// nothing whose second run would act on the machine.
/// </summary>
public sealed class ResultOwnershipTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A script in the library, so a returning session can offer its text again.</summary>
    private static ScriptRecord SaveScript(TestConsole console, string name, string text)
    {
        var script = new ScriptRecord { Id = Guid.NewGuid().ToString("d"), Name = name, Text = text, TimeoutSeconds = 300 };
        Assert.True(console.Session.Scripts.TrySave(script, out var error), error);
        return script;
    }

    [Fact]
    public async Task A_result_never_reaches_another_instance_of_the_same_lab_and_reaches_the_deliverer_on_relink()
    {
        var first = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var port = first.Port;
        var firstInstance = first.Session.Instance.InstanceId;
        var firstDirectory = first.Directory;
        TestAgent? pc = null;
        TestConsole? second = null;

        try
        {
            pc = TestAgent.Install(first, 3, first.IssueCodes(1)[0]).Start();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

            // The script is running when the MacBook leaves; it finishes while the other console holds the room.
            var finish = new TaskCompletionSource();
            pc.Behaviour.OnJob = async job =>
            {
                await finish.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = "the MacBook's output" };
            };

            var script = SaveScript(first, "long", "Start-Sleep 60\n");
            var job = first.Session.RunScript([pc.AgentId], script).Single();
            Assert.True(await Wait.UntilAsync(() => pc.Behaviour.JobsRun.Count == 1));
            Assert.Equal(firstInstance, pc.Link.DeliveringInstanceOf(job.Id));

            // The MacBook stops serving (its session is closed, the in-flight row is saved) and
            // the lab PC — another instance of the same lab — takes the same port.
            await first.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State != LinkState.Linked));
            Assert.True(File.Exists(Path.Combine(first.Session.Store.LogsDirectory, Defaults.InFlightJobsFileName)));

            second = await TestConsole.JoinLabAsync(first, "Lab PC", port: port);
            var secondInstance = second.Session.Instance.InstanceId;
            Assert.NotEqual(firstInstance, secondInstance);

            // The other console happens to send the very same id (as a stale copy of the queue
            // would): it must be refused, not answered, not run again, before or after the finish.
            second.Session.Jobs.Create(pc.AgentId, Job.Types.Kind.RunScript, DateTimeOffset.UtcNow, agentOnline: false, id: job.Id);
            var received = new List<JobResult>();
            second.Session.Jobs.Updated += j =>
            {
                if (j.IsFinished)
                {
                    lock (received) { received.Add(new JobResult { JobId = j.Id, Ok = j.Ok, Message = j.Message }); }
                }
            };

            Assert.True(await Wait.UntilAsync(() => second.Session.IsLinked(pc.AgentId), TimeSpan.FromSeconds(20)));
            Assert.Equal(secondInstance, pc.Link.LinkedInstanceId);

            finish.SetResult();
            Assert.True(await Wait.UntilAsync(() => pc.Link.RunningJobs == 0));

            // The result waits on the PC for the MacBook; the lab PC's row closed with the refusal only.
            Assert.True(await Wait.UntilAsync(() => pc.Link.PendingResults == 1));
            Assert.True(await Wait.UntilAsync(() => received.Count == 1, TimeSpan.FromSeconds(15)));
            var refused = Assert.Single(received);
            Assert.False(refused.Ok);
            Assert.Contains("delivered by another console", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("the MacBook's output", refused.Message, StringComparison.Ordinal);
            Assert.True(await Wait.UntilAsync(() => second.Session.Events.Recent.Any(e => e.Code == "job.other_instance")));
            await Task.Delay(500, Ct);
            Assert.Equal(1, pc.Link.PendingResults);
            Assert.Single(pc.Behaviour.JobsRun);

            // The lab PC leaves; the MacBook returns on the same port and gets its result.
            await second.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State != LinkState.Linked));

            await using var again = Reopen(firstDirectory, port);
            await again.StartAsync();
            var restored = Assert.Single(again.Jobs.All());
            Assert.Equal(job.Id, restored.Id);
            Assert.Equal(firstInstance, restored.InstanceId);
            Assert.Equal(first.Session.LabId, restored.LabId);
            Assert.True(restored.RestoredFromDisk);

            Assert.True(await Wait.UntilAsync(() => restored.State == JobState.Succeeded, TimeSpan.FromSeconds(20)), restored.State.ToString());
            Assert.Equal("the MacBook's output", restored.Message);
            Assert.Equal(0, pc.Link.PendingResults);
            Assert.Single(pc.Behaviour.JobsRun);
            Assert.False(File.Exists(again.Store.InFlightJobsPath));

            // Journaled under the MacBook's instance, in the lab's own logs.
            var row = await JournalRowAsync(again.Store.LogsDirectory, job.Id);
            Assert.Equal(firstInstance, row.GetProperty("instance_id").GetString());
            Assert.Equal(first.Session.LabId, row.GetProperty("lab_id").GetString());
            Assert.Equal("Succeeded", row.GetProperty("state").GetString());
        }
        finally
        {
            if (pc is not null)
            {
                await pc.DisposeAsync();
            }

            if (second is not null)
            {
                await second.DisposeAsync();
            }

            await first.DisposeAsync();
        }
    }

    /// <summary>
    /// M5 portion 8, finding F: events were the one thing a PC still handed to whoever linked
    /// next. An event about a job now waits for the console that delivered that job, exactly
    /// as its result does, so the next teacher in the room is never shown the previous
    /// teacher's output — while a machine event, which is about the PC and not about anyone's
    /// lesson, still reaches whoever is there to act on it.
    /// </summary>
    [Fact]
    public async Task An_event_about_a_job_waits_for_the_console_that_delivered_it_while_a_machine_event_does_not()
    {
        const string lessonText = "the MacBook's job said this";
        const string machineText = "the helper died on this PC";

        var first = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var port = first.Port;
        var firstInstance = first.Session.Instance.InstanceId;
        var firstDirectory = first.Directory;
        TestAgent? pc = null;
        TestConsole? second = null;

        try
        {
            pc = TestAgent.Install(first, 7, first.IssueCodes(1)[0]).Start();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

            var report = new TaskCompletionSource();
            pc.Behaviour.OnJob = async job =>
            {
                await report.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                pc!.Link.ReportForJob(job.Id, Event.Types.Severity.Warning, "power.failed", lessonText);
                pc.Link.Report(Event.Types.Severity.Error, "session.helper_exited", machineText);
                return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = "done" };
            };

            var script = SaveScript(first, "long", "Start-Sleep 60\n");
            var job = first.Session.RunScript([pc.AgentId], script).Single();
            Assert.True(await Wait.UntilAsync(() => pc.Behaviour.JobsRun.Count == 1));
            Assert.Equal(firstInstance, pc.Link.DeliveringInstanceOf(job.Id));

            // The MacBook leaves and the other teacher machine of the same lab takes the room.
            await first.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State != LinkState.Linked));
            second = await TestConsole.JoinLabAsync(first, "Lab PC", port: port);
            Assert.True(await Wait.UntilAsync(() => second!.Session.IsLinked(pc.AgentId), TimeSpan.FromSeconds(20)));
            Assert.NotEqual(firstInstance, second.Session.Instance.InstanceId);

            // Both events are produced now, under the second teacher's link.
            report.SetResult();
            Assert.True(await Wait.UntilAsync(() => second!.Session.Events.Recent.Any(e => e.Message.Contains(machineText, StringComparison.Ordinal)), TimeSpan.FromSeconds(15)));
            await Task.Delay(500, Ct);
            Assert.DoesNotContain(second.Session.Events.Recent, e => e.Message.Contains(lessonText, StringComparison.Ordinal));
            Assert.DoesNotContain(second.Session.Events.Recent, e => e.Code == "power.failed");

            // The MacBook comes back on the same port and is handed what was kept for it.
            await second.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State != LinkState.Linked));

            await using var again = Reopen(firstDirectory, port);
            await again.StartAsync();
            Assert.True(await Wait.UntilAsync(() => again.Events.Recent.Any(e => e.Message.Contains(lessonText, StringComparison.Ordinal)), TimeSpan.FromSeconds(20)));
            Assert.DoesNotContain(again.Events.Recent, e => e.Message.Contains(machineText, StringComparison.Ordinal));
        }
        finally
        {
            if (pc is not null)
            {
                await pc.DisposeAsync();
            }

            if (second is not null)
            {
                await second.DisposeAsync();
            }

            await first.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_restored_script_pulls_its_payload_from_the_new_session_and_completes()
    {
        // The offer the job's `ref` names died with the closed session (FileOffers is per
        // session), so the returning console offers the library text again before it re-sends
        // the row; the PC really pulls it (D-57 item 4, S2).
        var console = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var port = console.Port;
        TestAgent? pc = null;

        try
        {
            pc = TestAgent.Install(console, 6, console.IssueCodes(1)[0]).Start();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

            var linkedAgain = new TaskCompletionSource();
            var pulled = new List<string>();
            pc.Behaviour.OnJob = async job =>
            {
                Assert.True(RunScriptRequest.TryParse(job, out var request, out var error), error);

                // The script "runs" across the console's absence and pulls its file only once
                // the console is back — from the session that restored the row.
                await linkedAgain.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                using var text = new MemoryStream();
                await pc!.Link.PullFileAsync(request.Reference, request.Sha256, text, Ct);
                lock (pulled) { pulled.Add(Encoding.UTF8.GetString(text.ToArray()).TrimStart('\uFEFF')); }
                return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = "pulled it" };
            };

            var script = SaveScript(console, "greeting", "Write-Host 'привіт'\n");
            var job = console.Session.RunScript([pc.AgentId], script).Single();
            Assert.True(await Wait.UntilAsync(() => pc.Behaviour.JobsRun.Count == 1));

            await console.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State != LinkState.Linked));
            Assert.True(File.Exists(console.Session.Store.InFlightJobsPath));

            await using var again = Reopen(console.Directory, port);
            await again.StartAsync();
            var restored = Assert.Single(again.Jobs.All());
            Assert.Equal(job.Id, restored.Id);
            Assert.True(await Wait.UntilAsync(() => again.IsLinked(pc.AgentId), TimeSpan.FromSeconds(20)));

            linkedAgain.SetResult();
            Assert.True(await Wait.UntilAsync(() => restored.State == JobState.Succeeded, TimeSpan.FromSeconds(20)), restored.State.ToString());
            Assert.Equal("pulled it", restored.Message);
            Assert.Equal("Write-Host 'привіт'\n", Assert.Single(pulled));
            Assert.Single(pc.Behaviour.JobsRun);
        }
        finally
        {
            if (pc is not null)
            {
                await pc.DisposeAsync();
            }

            await console.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(Job.Types.Kind.Reboot)]
    [InlineData(Job.Types.Kind.ResetProfile)]
    public async Task A_saved_power_or_profile_job_is_never_sent_again_and_the_row_says_the_outcome_is_unknown(Job.Types.Kind kind)
    {
        // *Shut down all* and then *Disconnect* must not power the class off the next morning.
        var console = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var port = console.Port;
        TestAgent? pc = null;

        try
        {
            pc = TestAgent.Install(console, 7, console.IssueCodes(1)[0]).Start();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

            var block = new TaskCompletionSource();
            pc.Behaviour.OnJob = async job =>
            {
                await block.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = "done" };
            };

            var job = console.Session.CreateJobs([pc.AgentId], kind).Single();
            Assert.True(await Wait.UntilAsync(() => pc.Behaviour.JobsRun.Count == 1));

            await console.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State != LinkState.Linked));
            Assert.True(File.Exists(console.Session.Store.InFlightJobsPath));

            await using var again = Reopen(console.Directory, port);
            await again.StartAsync();
            var restored = Assert.Single(again.Jobs.All());
            Assert.Equal(job.Id, restored.Id);
            Assert.Equal(JobState.TimedOut, restored.State);
            Assert.Contains("Outcome unknown", restored.Message, StringComparison.Ordinal);
            Assert.Contains("never sent to a PC a second time", restored.Message, StringComparison.Ordinal);
            Assert.Contains(again.Events.Recent, e => e.Code == "job.outcome_unknown");
            Assert.DoesNotContain(again.Events.Recent, e => e.Code == "jobs.restored");

            // The PC links again and is sent nothing: it ran the job once and only once.
            Assert.True(await Wait.UntilAsync(() => again.IsLinked(pc.AgentId), TimeSpan.FromSeconds(20)));
            block.SetResult();
            await Task.Delay(500, Ct);
            Assert.Single(pc.Behaviour.JobsRun);
            Assert.False(File.Exists(again.Store.InFlightJobsPath));
        }
        finally
        {
            if (pc is not null)
            {
                await pc.DisposeAsync();
            }

            await console.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_job_delivered_before_the_PC_booted_is_not_sent_again_and_is_reported_as_unknown()
    {
        // The agent's ledger died with the reboot, so the re-sent copy would run rather than be
        // answered from the cache (D-32 item 7). Hello.boot_time_unix is the evidence.
        var console = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var port = console.Port;
        TestAgent? pc = null;
        TestAgent? restarted = null;

        try
        {
            pc = TestAgent.Install(console, 8, console.IssueCodes(1)[0]).Start();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

            var block = new TaskCompletionSource();
            pc.Behaviour.OnJob = async job =>
            {
                await block.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = "before the reboot" };
            };
            var script = SaveScript(console, "long", "Start-Sleep 60\n");
            var job = console.Session.RunScript([pc.AgentId], script).Single();
            Assert.True(await Wait.UntilAsync(() => pc.Behaviour.JobsRun.Count == 1));

            await console.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State != LinkState.Linked));
            block.SetResult();
            await pc.Link.DisposeAsync();

            // The PC reboots: a new agent process over the same store, with a boot time later
            // than the delivery, and an empty ledger.
            restarted = TestAgent.Open(pc.Store);
            // Its own clock says it came up after the job was handed over — which is exactly
            // what a PC that rebooted while the console was in another lab reports.
            restarted.Behaviour.BootTime = DateTimeOffset.UtcNow.AddSeconds(5);
            restarted.Behaviour.OnJob = j => Task.FromResult(new JobResult { JobId = j.Id, Ok = true, Message = "ran a second time" });
            restarted.Start();

            await using var again = Reopen(console.Directory, port);
            await again.StartAsync();
            var restored = Assert.Single(again.Jobs.All());          // restorable in principle…
            Assert.True(await Wait.UntilAsync(() => again.IsLinked(restarted.AgentId), TimeSpan.FromSeconds(20)));
            Assert.True(await Wait.UntilAsync(() => restored.IsFinished, TimeSpan.FromSeconds(10)), restored.State.ToString());
            Assert.Equal(JobState.TimedOut, restored.State);         // …but not to a PC that has restarted
            Assert.Contains("restarted since", restored.Message, StringComparison.Ordinal);
            Assert.Contains(again.Events.Recent, e => e.Code == "job.outcome_unknown");
            await Task.Delay(500, Ct);
            Assert.Empty(restarted.Behaviour.JobsRun);
        }
        finally
        {
            if (restarted is not null)
            {
                await restarted.Link.DisposeAsync();
            }

            if (pc is not null)
            {
                await pc.DisposeAsync();
            }

            await console.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("newer")]
    public async Task An_unreadable_in_flight_file_leaves_the_lab_openable(string kind)
    {
        var console = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var port = console.Port;

        try
        {
            var script = SaveScript(console, "long", "Start-Sleep 60\n");
            console.Session.RunScript(["never-linked-pc"], script);
            var path = console.Session.Store.InFlightJobsPath;
            await console.Session.DisposeAsync();

            // A queued job that was never delivered is not saved at all (D-57 item 2), so the
            // file is written by hand in the two shapes that must not stop a lab from opening.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, kind == "newer"
                ? "{\n  \"schema_version\": 999,\n  \"lab_id\": \"" + console.Session.LabId + "\",\n  \"jobs\": []\n}"
                : kind, Ct);

            await using var again = Reopen(console.Directory, port);
            await again.StartAsync();
            Assert.Empty(again.Jobs.All());
            Assert.Contains(again.Events.Recent, e => e.Code == "jobs.inflight_unreadable");
        }
        finally
        {
            await console.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_file_written_for_another_lab_or_instance_is_ignored()
    {
        var console = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var port = console.Port;

        try
        {
            var path = console.Session.Store.InFlightJobsPath;
            await console.Session.DisposeAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            JsonStore.Save(path, new InFlightJobsDocument
            {
                LabId = console.Session.LabId,
                InstanceId = Guid.NewGuid().ToString("d"),
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Jobs = [new InFlightJob { Id = "j", AgentId = "pc", Kind = Job.Types.Kind.RunScript, State = JobState.Delivered }],
            }, InFlightJobsDocument.Migrations);

            await using var again = Reopen(console.Directory, port);
            await again.StartAsync();
            Assert.Empty(again.Jobs.All());
            Assert.Contains(again.Events.Recent, e => e.Code == "jobs.inflight_unreadable");
        }
        finally
        {
            await console.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_restored_batch_keeps_the_results_of_the_PCs_that_finished_before_the_console_left()
    {
        var console = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var port = console.Port;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var script = SaveScript(console, "long", "Start-Sleep 60\n");
            var jobs = console.Session.RunScript(["pc-a", "pc-b", "pc-c"], script);
            Assert.Equal(3, jobs.Count);
            var batch = jobs[0].BatchId;
            foreach (var job in jobs)
            {
                console.Session.Jobs.TakePending(job.AgentId, now);
            }

            console.Session.Jobs.Complete(new JobResult { JobId = jobs[0].Id, Ok = true, Message = "finished first" }, now);
            console.Session.Jobs.Complete(new JobResult { JobId = jobs[1].Id, Ok = false, ExitCode = 3, Message = "failed first" }, now);
            var logs = console.Session.Store.LogsDirectory;
            var path = console.Session.BatchLogs.PathFor(batch);
            await console.Session.DisposeAsync();
            Assert.Equal(3, Load(path).Computers.Count);

            await using var again = Reopen(console.Directory, port);
            await again.StartAsync();
            Assert.Single(again.Jobs.All());                     // only the unfinished row comes back

            var saved = Load(path);
            Assert.Equal(3, saved.Computers.Count);
            var rows = saved.Computers.SelectMany(pc => pc.Jobs).ToArray();
            Assert.Contains(rows, j => j.State == JobState.Succeeded && j.Message == "finished first");
            Assert.Contains(rows, j => j.State == JobState.Failed && j.ExitCode == 3);
            Assert.Contains(rows, j => j.State == JobState.Delivered);
            Assert.False(saved.IsComplete);
            Assert.Equal(logs, again.Store.LogsDirectory);
        }
        finally
        {
            await console.DisposeAsync();
        }
    }

    [Fact]
    public async Task Thirty_PCs_finishing_one_batch_save_once_and_report_nothing()
    {
        // Every JobResult lands on its own gRPC handler thread; the durable in-flight set used
        // to be written straight from there, so thirty PCs raced on one file. It is coalesced
        // now, the way lab.json's save is.
        await using var console = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var now = DateTimeOffset.UtcNow;
        var script = SaveScript(console, "long", "Start-Sleep 60\n");
        var jobs = console.Session.RunScript(Enumerable.Range(1, Defaults.MaxStudentPcs).Select(n => $"pc-{n:00}"), script);
        Assert.Equal(Defaults.MaxStudentPcs, jobs.Count);
        foreach (var job in jobs)
        {
            console.Session.Jobs.TakePending(job.AgentId, now);
        }

        await Task.WhenAll(jobs.Select(job => Task.Run(() =>
            console.Session.Jobs.Complete(new JobResult { JobId = job.Id, Ok = true, Message = "done" }, now), Ct)));

        Assert.All(jobs, job => Assert.Equal(JobState.Succeeded, job.State));
        var path = console.Session.Store.InFlightJobsPath;
        Assert.True(await Wait.UntilAsync(() => !File.Exists(path)), "the in-flight file is removed once nothing is owed");
        Assert.DoesNotContain(console.Session.Events.Recent, e => e.Code is "jobs.inflight_save_failed" or "jobs.log_failed");
        Assert.True(await Wait.UntilAsync(() => Directory.GetFiles(console.Session.Store.LogsDirectory, "*.tmp").Length == 0),
            "no temporary is left behind");

        // Every PC's result is in the batch log, written from those same threads.
        var batch = Load(console.Session.BatchLogs.PathFor(jobs[0].BatchId));
        Assert.True(batch.IsComplete);
        Assert.Equal(Defaults.MaxStudentPcs, batch.Computers.Count);
    }

    [Fact]
    public async Task Thirty_concurrent_saves_of_the_in_flight_file_never_tear_it()
    {
        // What the coalescing above protects, proved directly: many savers, one document. A
        // shared temporary name meant two of them interleaved their bytes into it, and a
        // reader then parsed half of one save and half of another.
        var directory = TestConsole.TempDirectory();
        try
        {
            var store = new LabStore(directory);
            store.EnsureDirectories();
            var now = DateTimeOffset.UtcNow;
            var queue = new JobQueue("lab-a", "macbook");
            for (var n = 1; n <= Defaults.MaxStudentPcs; n++)
            {
                var agentId = $"pc-{n:00}";
                queue.Create(agentId, Job.Types.Kind.RunScript, now, agentOnline: true);
                queue.TakePending(agentId, now);
            }

            Assert.Equal(Defaults.MaxStudentPcs, queue.SnapshotInFlight().Count);

            var reading = true;
            var reads = 0;
            var reader = Task.Run(() =>
            {
                while (Volatile.Read(ref reading))
                {
                    // A torn document throws here — invalid JSON, or a document that is not
                    // the complete set of rows every saver wrote.
                    if (JsonStore.LoadIfExists<InFlightJobsDocument>(store.InFlightJobsPath, InFlightJobsDocument.Migrations) is { } saved)
                    {
                        Assert.Equal(Defaults.MaxStudentPcs, saved.Jobs.Count);
                        reads++;
                    }

                    Thread.Sleep(1);
                }
            }, Ct);

            await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => Task.Run(() => InFlightJobs.Save(store, queue, now), Ct)));
            Volatile.Write(ref reading, false);
            await reader;

            Assert.True(reads > 0, "the reader saw the document at least once");
            Assert.Empty(Directory.GetFiles(store.LogsDirectory, "*.tmp"));
            var file = InFlightJobs.Load(store, "lab-a", "macbook", out var problem);
            Assert.Null(problem);
            Assert.Equal(Defaults.MaxStudentPcs, file.Jobs.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_certificate_names_the_instance_a_PC_binds_to_not_the_Welcome()
    {
        // Welcome.instance_id is a claim the peer writes; the SAN URI of the leaf the handshake
        // validated is not (D-57 item 4, S4).
        await using var console = await TestConsole.CreateLabAsync("MacBook", port: 0);
        console.Session.BeforeWelcome = welcome => welcome.InstanceId = "11111111-1111-1111-1111-111111111111";

        await using var pc = TestAgent.Install(console, 9, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        var certified = console.Session.Instance.InstanceId;
        Assert.Equal(certified, pc.Link.LinkedInstanceId);
        Assert.Equal(certified, pc.Store.Config.LastInstanceId);
        Assert.True(await Wait.UntilAsync(() => console.Session.Events.Recent.Any(e => e.Code == "console.instance_mismatch")));

        // And the job the claimed instance never sent still belongs to the certified one.
        var script = SaveScript(console, "quick", "Write-Host hi\n");
        var job = console.Session.RunScript([pc.AgentId], script).Single();
        Assert.True(await Wait.UntilAsync(() => job.State == JobState.Succeeded, TimeSpan.FromSeconds(15)));
        Assert.Equal(certified, pc.Link.DeliveringInstanceOf(job.Id));
    }

    [Fact]
    public async Task Progress_lines_do_not_reach_a_console_that_did_not_send_the_job()
    {
        var first = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var port = first.Port;
        TestAgent? pc = null;
        TestConsole? second = null;

        try
        {
            pc = TestAgent.Install(first, 10, first.IssueCodes(1)[0]).Start();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

            var handedOver = new TaskCompletionSource();
            var finish = new TaskCompletionSource();
            pc.Behaviour.OnJobProgress = async (job, report) =>
            {
                await report(new JobProgress { JobId = job.Id, Percent = 10, Line = "before the handover" });
                await handedOver.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                await report(new JobProgress { JobId = job.Id, Percent = 50, Line = "the MacBook's secret line" });
                await finish.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                return new JobResult { JobId = job.Id, Ok = true, Message = "done" };
            };

            var script = SaveScript(first, "long", "Start-Sleep 60\n");
            var job = first.Session.RunScript([pc.AgentId], script).Single();
            Assert.True(await Wait.UntilAsync(() => job.Output.Count == 1));

            await first.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State != LinkState.Linked));

            second = await TestConsole.JoinLabAsync(first, "Lab PC", port: port);
            Assert.True(await Wait.UntilAsync(() => second.Session.IsLinked(pc.AgentId), TimeSpan.FromSeconds(20)));

            // A row with the same id on the other console, delivered but never sent: if a
            // progress line for it reached this console it would land in this row's output.
            var shadow = second.Session.Jobs.Create(pc.AgentId, Job.Types.Kind.RunScript, DateTimeOffset.UtcNow, agentOnline: true, id: job.Id);
            Assert.Same(shadow, Assert.Single(second.Session.Jobs.TakePending(pc.AgentId, DateTimeOffset.UtcNow)));

            handedOver.SetResult();
            await Task.Delay(750, Ct);
            Assert.Empty(shadow.Output);
            Assert.Equal(JobState.Delivered, shadow.State);
            Assert.DoesNotContain(second.Session.Events.Recent, e => e.Message.Contains("secret line", StringComparison.Ordinal));

            // The result itself still belongs to the MacBook and waits on the PC for it.
            finish.SetResult();
            Assert.True(await Wait.UntilAsync(() => pc.Link.PendingResults == 1));
            await Task.Delay(500, Ct);
            Assert.Equal(JobState.Delivered, shadow.State);
        }
        finally
        {
            if (pc is not null)
            {
                await pc.DisposeAsync();
            }

            if (second is not null)
            {
                await second.DisposeAsync();
            }

            await first.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_oldest_kept_results_are_dropped_with_an_event_when_the_PC_is_full()
    {
        // The wait for an absent console is bounded and lives only in the agent's memory
        // (D-57 item 4). Nobody may lose a result without being told.
        var console = await TestConsole.CreateLabAsync("MacBook", port: 0);
        var port = console.Port;
        TestAgent? pc = null;

        try
        {
            pc = TestAgent.Install(console, 11, console.IssueCodes(1)[0], options: new AgentLinkOptions { MaxPendingResults = 2 }).Start();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

            var block = new TaskCompletionSource();
            pc.Behaviour.OnJob = async job =>
            {
                await block.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                return new JobResult { JobId = job.Id, Ok = true, Message = "done " + job.Id };
            };

            var script = SaveScript(console, "long", "Start-Sleep 60\n");
            var jobs = new List<JobRecord>();
            for (var i = 0; i < 3; i++)
            {
                jobs.Add(console.Session.RunScript([pc.AgentId], script).Single());
            }

            Assert.True(await Wait.UntilAsync(() => pc.Behaviour.JobsRun.Count == 3));
            await console.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State != LinkState.Linked));

            block.SetResult();
            Assert.True(await Wait.UntilAsync(() => pc.Link.RunningJobs == 0));
            Assert.True(await Wait.UntilAsync(() => pc.Link.PendingResults == 2));

            await using var again = Reopen(console.Directory, port);
            await again.StartAsync();
            Assert.True(await Wait.UntilAsync(() => again.Events.Recent.Any(e => e.Code == "job.result_dropped"), TimeSpan.FromSeconds(20)));
            Assert.Contains(again.Events.Recent, e => e.Code == "job.result_dropped" && e.Message.Contains("at most 2", StringComparison.Ordinal));

            // The dropped one is still recovered here, because the returning console re-sends
            // the row and the ledger — a separate 500-entry cache — answers it. The loss bites
            // when nobody asks again: a row that was not restored, or a PC that restarted.
            Assert.True(await Wait.UntilAsync(() => again.Jobs.All().Count(j => j.State == JobState.Succeeded) == 3, TimeSpan.FromSeconds(20)),
                string.Join(", ", again.Jobs.All().Select(j => j.State)));
            Assert.Equal(3, jobs.Count);
            Assert.Equal(3, pc.Behaviour.JobsRun.Count);
        }
        finally
        {
            if (pc is not null)
            {
                await pc.DisposeAsync();
            }

            await console.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_finished_job_is_journaled_with_its_lab_and_instance_and_the_batch_export_carries_them()
    {
        await using var console = await TestConsole.CreateLabAsync("MacBook");
        await using var pc = TestAgent.Install(console, 5, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        var job = console.Session.CreateJobs([pc.AgentId], Job.Types.Kind.RunScript).Single();
        Assert.True(await Wait.UntilAsync(() => job.State == JobState.Succeeded, TimeSpan.FromSeconds(15)));
        Assert.Equal(console.Session.Instance.InstanceId, job.InstanceId);
        Assert.Equal(console.Session.LabId, job.LabId);

        var row = await JournalRowAsync(console.Session.Store.LogsDirectory, job.Id);
        Assert.Equal(console.Session.Instance.InstanceId, row.GetProperty("instance_id").GetString());
        Assert.Equal(console.Session.LabId, row.GetProperty("lab_id").GetString());

        var zip = Path.Combine(TestConsole.TempDirectory(), "batch.zip");
        console.Session.BatchLogs.Export(job.BatchId, zip);
        using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: Ct);
            Assert.Equal(console.Session.LabId, document.RootElement.GetProperty("lab_id").GetString());
            Assert.Equal(console.Session.Instance.InstanceId, document.RootElement.GetProperty("instance_id").GetString());
            var jobs = document.RootElement.GetProperty("computers")[0].GetProperty("jobs");
            Assert.Equal(console.Session.Instance.InstanceId, jobs[0].GetProperty("instance_id").GetString());
            Assert.Equal(console.Session.LabId, jobs[0].GetProperty("lab_id").GetString());
        }

        Assert.Equal(2, archive.Entries.Count);
    }

    private static JobBatchLogDocument Load(string path) =>
        JsonStore.Load<JobBatchLogDocument>(path, JobBatchLogDocument.Migrations);

    /// <summary>The same lab directory and instance, opened again on the same port: what a restart or a return to the lab looks like.</summary>
    internal static LabSession Reopen(string labDirectory, int port)
    {
        var store = new LabStore(labDirectory);
        var vault = new LabKeyVault(store, store.LoadLabKey());
        var instance = ConsoleInstance.Open(store.LoadInstance()!);
        var options = new ConsoleOptions { DataDirectory = labDirectory, Port = port, BindAddress = IPAddress.Loopback, BeaconPort = TestConsole.BeaconPort };
        return new LabSession(options, store, vault, instance, instance.Document, TestLogging.Factory);
    }

    internal static async Task<JsonElement> JournalRowAsync(string logsDirectory, string jobId)
    {
        foreach (var file in Directory.EnumerateFiles(logsDirectory, "jobs-*.jsonl"))
        {
            foreach (var line in await File.ReadAllLinesAsync(file, Ct))
            {
                var element = JsonDocument.Parse(line).RootElement;
                if (element.GetProperty("id").GetString() == jobId)
                {
                    return element;
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"no journal row for job {jobId} under {logsDirectory}");
    }
}
