using System.IO.Compression;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Lab;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Console.Tests;

public sealed class JobBatchLogTests
{
    [Fact]
    public async Task Fan_out_preserves_success_failure_and_offline_PC_in_durable_logs_and_export()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var codes = console.IssueCodes(2);
        await using var first = TestAgent.Install(console, 1, codes[0]).Start();
        await using var second = TestAgent.Install(console, 2, codes[1]).Start();
        Assert.True(await Wait.UntilAsync(() => first.Link.State == LinkState.Linked && second.Link.State == LinkState.Linked));
        second.Behaviour.OnJob = job => Task.FromResult(new JobResult
        {
            JobId = job.Id, Ok = false, ExitCode = 3, Message = "Cannot open file — перевірка",
        });

        var offline = Guid.NewGuid().ToString("d");
        var created = console.Session.CreateJobs([first.AgentId, second.AgentId, offline, first.AgentId],
            Job.Types.Kind.Logoff, new Dictionary<string, string> { ["private_argument"] = "not-for-export" },
            delivery: JobDelivery.OnlineOnly);
        Assert.Equal(3, created.Count);
        Assert.True(await Wait.UntilAsync(() => created.All(job => job.IsFinished)));
        var batch = created[0].BatchId;
        var path = console.Session.BatchLogs.PathFor(batch);
        Assert.True(await Wait.UntilAsync(() => Load(path).IsComplete));
        var document = Load(path);
        Assert.Equal(3, document.Computers.Count);
        Assert.Contains(document.Computers.SelectMany(pc => pc.Jobs), j => j.State == JobState.Succeeded);
        Assert.Contains(document.Computers.SelectMany(pc => pc.Jobs), j => j.State == JobState.Failed && j.ExitCode == 3);
        Assert.Contains(document.Computers.SelectMany(pc => pc.Jobs), j => j.State == JobState.NotDelivered);
        Assert.Equal(1, document.Computers.Single(pc => pc.AgentId == first.AgentId).Number);
        Assert.DoesNotContain("not-for-export", File.ReadAllText(path));

        var archivePath = Path.Combine(console.Directory, "result.zip");
        console.Session.BatchLogs.Export(batch, archivePath);
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Equal(4, archive.Entries.Count);
        var perPc = archive.Entries.Where(entry => entry.Name != Defaults.JobBatchManifestFileName).ToArray();
        Assert.All(perPc, entry =>
        {
            using var reader = new StreamReader(entry.Open());
            var pcDocument = JsonStore.Parse<JobBatchLogDocument>(reader.ReadToEnd(), entry.Name, JobBatchLogDocument.Migrations);
            Assert.Single(pcDocument.Computers);
            Assert.True(pcDocument.IsComplete);
        });
    }

    [Fact]
    public async Task Thirty_concurrent_results_keep_all_output_and_replace_timeout_with_late_result()
    {
        var directory = TestConsole.TempDirectory();
        try
        {
            var queue = new JobQueue();
            var batch = Guid.NewGuid().ToString("d");
            var now = DateTimeOffset.UtcNow;
            var roster = Enumerable.Range(1, Defaults.MaxStudentPcs).Select(number =>
                queue.Create(number.ToString(), Job.Types.Kind.RunScript, now, true, batchId: batch)).ToArray();
            var logs = new JobBatchLogs(directory, queue, int.Parse, () => now);
            logs.Register(batch);
            queue.Updated += job => { if (job.IsFinished) logs.Record(job.BatchId); };
            foreach (var job in roster)
            {
                queue.TakePending(job.AgentId, now);
            }

            var before = queue.SnapshotBatch(batch);
            await Task.WhenAll(roster.Select(job => Task.Run(() =>
            {
                queue.Progress(new JobProgress { JobId = job.Id, Line = "Вивід " + job.AgentId }, now);
                queue.Complete(new JobResult { JobId = job.Id, Ok = true }, now);
            })));
            Assert.All(before, job => Assert.Empty(job.Output));
            var saved = Load(logs.PathFor(batch));
            Assert.True(saved.IsComplete);
            Assert.Equal(Defaults.MaxStudentPcs, saved.Computers.Count);
            Assert.All(saved.Computers, pc => Assert.Equal("Вивід " + pc.AgentId, Assert.Single(Assert.Single(pc.Jobs).Output)));

            var lateBatch = Guid.NewGuid().ToString("d");
            var late = queue.Create("1", Job.Types.Kind.Logoff, now, true, timeout: TimeSpan.FromSeconds(1), batchId: lateBatch);
            logs.Register(lateBatch);
            queue.TakePending("1", now);
            queue.TimeOutStale(now.AddSeconds(2));
            Assert.Equal(JobState.TimedOut, Load(logs.PathFor(lateBatch)).Computers[0].Jobs[0].State);
            queue.Complete(new JobResult { JobId = late.Id, Ok = true }, now.AddSeconds(3));
            Assert.Equal(JobState.Succeeded, Load(logs.PathFor(lateBatch)).Computers[0].Jobs[0].State);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Log_storage_failure_does_not_prevent_delivery_and_is_reported()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 1, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));
        File.WriteAllText(Path.Combine(console.Session.Store.LogsDirectory, Defaults.JobBatchesDirectoryName), "blocks directory creation");
        var job = console.Session.CreateJobs([pc.AgentId], Job.Types.Kind.Logoff).Single();
        Assert.True(await Wait.UntilAsync(() => job.State == JobState.Succeeded));
        Assert.Contains(console.Session.Events.Recent, item => item.Code == "jobs.log_failed");
    }

    [Fact]
    public void Pending_export_is_marked_incomplete_and_export_failure_cleans_temporary_file()
    {
        var directory = TestConsole.TempDirectory();
        try
        {
            var queue = new JobQueue();
            var batch = Guid.NewGuid().ToString("d");
            queue.Create("pc", Job.Types.Kind.RunScript, DateTimeOffset.UtcNow, false, batchId: batch);
            var logs = new JobBatchLogs(directory, queue, _ => 1, () => DateTimeOffset.UtcNow);
            logs.Register(batch);
            Assert.False(Load(logs.PathFor(batch)).IsComplete);
            var archivePath = Path.Combine(directory, "pending.zip");
            logs.Export(batch, archivePath);
            using (var archive = ZipFile.OpenRead(archivePath))
            using (var reader = new StreamReader(archive.GetEntry(Defaults.JobBatchManifestFileName)!.Open()))
            {
                var doc = JsonStore.Parse<JobBatchLogDocument>(reader.ReadToEnd(), "manifest", JobBatchLogDocument.Migrations);
                Assert.False(doc.IsComplete);
                Assert.Equal(JobState.Pending, doc.Computers[0].Jobs[0].State);
            }

            var blocked = Path.Combine(directory, "destination");
            Directory.CreateDirectory(blocked);
            Assert.ThrowsAny<IOException>(() => logs.Export(batch, blocked));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.Throws<FormatException>(() => logs.PathFor("../escape"));
            var newer = File.ReadAllText(logs.PathFor(batch)).Replace("\"schema_version\": 1", "\"schema_version\": 2");
            Assert.Throws<SchemaVersionException>(() => JsonStore.Parse<JobBatchLogDocument>(newer, "batch", JobBatchLogDocument.Migrations));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static JobBatchLogDocument Load(string path) => JsonStore.Load<JobBatchLogDocument>(path, JobBatchLogDocument.Migrations);
}
