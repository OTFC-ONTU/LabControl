using LabControl.Console.Services;
using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Jobs;
using LabControl.Shared.Lab;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabControl.Console.Tests;

public sealed class SendFileTests
{
    [Fact]
    public async Task Handouts_fan_out_replace_verified_files_and_preserve_old_files_on_bad_hash()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var stick = TestConsole.TempDirectory();
        console.Session.WritePayload(stick, 2);
        var payload = SetupPayload.Open(stick);
        var directory = TestConsole.TempDirectory();
        await using var first = Machine(1);
        await using var second = Machine(2);
        FakeMachine Machine(int n) => new(FakeMachine.Install(Path.Combine(directory, $"PC-{n}"), n, payload,
            "127.0.0.1", console.Port, false), null, TestLogging.Factory.CreateLogger("handout"));
        first.Start(); second.Start();
        Assert.True(await Wait.UntilAsync(() => first.Link.IsLinked && second.Link.IsLinked));
        var text = Path.Combine(directory, "Завдання.docx");
        var executable = Path.Combine(directory, "program.exe");
        var content = new byte[Defaults.FileChunkBytes * 3];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(text, content);
        File.WriteAllBytes(executable, []);
        var jobs = console.Session.SendFiles([first.AgentId, second.AgentId, first.AgentId], [text, executable], true);
        Assert.Equal(4, jobs.Count);
        Assert.Single(jobs.Select(j => j.BatchId).Distinct());
        Assert.True(await Wait.UntilAsync(() => jobs.All(j => j.IsFinished)));
        Assert.All(jobs, j => Assert.Equal(JobState.Succeeded, j.State));
        foreach (var pc in new[] { first, second })
        {
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(pc.MaterialsDirectory, Path.GetFileName(text))));
            Assert.Empty(File.ReadAllBytes(Path.Combine(pc.MaterialsDirectory, "program.exe")));
        }
        Assert.All(jobs.Where(j => j.Args["name"] == "program.exe"), j => Assert.Contains("not opened", j.Message));
        Assert.True(await Wait.UntilAsync(() => Load().IsComplete));
        var log = Load();
        Assert.Equal(2, log.Computers.Count);
        Assert.All(log.Computers, pc => Assert.Equal(2, pc.Jobs.Count));
        JobBatchLogDocument Load() => JsonStore.Load<JobBatchLogDocument>(console.Session.BatchLogs.PathFor(jobs[0].BatchId), JobBatchLogDocument.Migrations);

        File.WriteAllText(text, "replacement");
        var replaced = Assert.Single(console.Session.SendFiles([first.AgentId], [text], false));
        Assert.True(await Wait.UntilAsync(() => replaced.IsFinished));
        Assert.Equal(JobState.Succeeded, replaced.State);
        var destination = Path.Combine(first.MaterialsDirectory, Path.GetFileName(text));
        Assert.Equal("replacement", File.ReadAllText(destination));
        var offer = console.Session.Files.OfferBytes([1, 2, 3], "bad");
        var bad = Assert.Single(console.Session.CreateJobs([first.AgentId], Job.Types.Kind.SendFile,
            new SendFileRequest(offer.Reference, new string('0', 64), Path.GetFileName(text), false).ToArgs()));
        Assert.True(await Wait.UntilAsync(() => bad.IsFinished));
        Assert.Equal(JobState.Failed, bad.State);
        Assert.Equal("replacement", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(first.MaterialsDirectory, "*.partial"));
    }

    [Fact]
    public async Task Invalid_selection_dispatches_nothing_and_offline_delivery_keeps_the_roster()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var directory = TestConsole.TempDirectory();
        var path = Path.Combine(directory, "a.pdf");
        File.WriteAllText(path, "test");
        var target = Guid.NewGuid().ToString("d");
        Assert.Throws<ArgumentException>(() => console.Session.SendFiles([target], [path, path], false));
        Assert.Throws<FileNotFoundException>(() => console.Session.SendFiles([target], [path, Path.Combine(directory, "missing.pdf")], false));
        var job = Assert.Single(console.Session.SendFiles([target, target], [path], false));
        Assert.Equal(JobState.Pending, job.State);
        var log = JsonStore.Load<JobBatchLogDocument>(console.Session.BatchLogs.PathFor(job.BatchId), JobBatchLogDocument.Migrations);
        Assert.False(log.IsComplete);
        Assert.Equal(target, Assert.Single(log.Computers).AgentId);
    }
}
