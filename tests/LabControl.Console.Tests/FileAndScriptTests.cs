using Xunit;

using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using LabControl.Shared.Lab;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;

namespace LabControl.Console.Tests;

/// <summary>
/// M2 portion 3 on the console side: the minimal <c>PullFile</c> (D-31), running a script
/// from the library (M4 portion 1), a job that outlives its link (D-32) and Wake-on-LAN's
/// bookkeeping. The Windows half — powershell.exe, cmd.exe, the student's token — is proved
/// on the VM.
/// </summary>
public sealed class FileAndScriptTests
{
    [Fact]
    public async Task An_agent_pulls_an_offered_file_and_verifies_it()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 1, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        var content = new byte[Defaults.FileChunkBytes * 3 + 123];
        Random.Shared.NextBytes(content);
        var offer = console.Session.Files.OfferBytes(content, "blob.bin");

        using var received = new MemoryStream();
        var bytes = await pc.Link.PullFileAsync(offer.Reference, offer.Sha256, received, TestContext.Current.CancellationToken);

        Assert.Equal(content.Length, bytes);
        Assert.Equal(content, received.ToArray());
    }

    [Fact]
    public async Task A_pull_that_does_not_match_its_hash_or_names_nothing_is_refused_in_plain_language()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 2, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        var offer = console.Session.Files.OfferText("Write-Output 'hi'", "hi");

        var mismatch = await Assert.ThrowsAsync<FilePullException>(() =>
            pc.Link.PullFileAsync(offer.Reference, new string('0', 64), new MemoryStream(), TestContext.Current.CancellationToken));
        Assert.Contains("hash", mismatch.Message, StringComparison.OrdinalIgnoreCase);

        var unknown = await Assert.ThrowsAsync<FilePullException>(() =>
            pc.Link.PullFileAsync(new string('f', 64), new string('f', 64), new MemoryStream(), TestContext.Current.CancellationToken));
        Assert.Contains("not offering", unknown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "file.unknown");
    }

    [Fact]
    public async Task A_pull_needs_a_link()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var pc = TestAgent.Install(console, 3, console.IssueCodes(1)[0]);
        await using (pc)
        {
            var refused = await Assert.ThrowsAsync<FilePullException>(() =>
                pc.Link.PullFileAsync(new string('a', 64), new string('a', 64), new MemoryStream(), TestContext.Current.CancellationToken));
            Assert.Contains("not linked", refused.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Running_a_script_offers_its_text_and_the_agent_runs_what_it_pulled()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 4, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        // A behaviour that does what the Windows agent does up to the shell: pull, verify, then "run".
        string? pulled = null;
        pc.Behaviour.OnJob = async job =>
        {
            Assert.True(RunScriptRequest.TryParse(job, out var request, out var error), error);
            using var buffer = new MemoryStream();
            await pc.Link.PullFileAsync(request.Reference, request.Sha256, buffer, CancellationToken.None);
            pulled = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = TestScripts.HundredLinesExitCode, Message = $"exit {TestScripts.HundredLinesExitCode}" };
        };

        var script = new ScriptRecord
        {
            Id = "t",
            Name = "test 100 lines",
            Shell = RunScriptRequest.CmdValue,
            RunAs = RunScriptRequest.UserValue,
            TimeoutSeconds = 20,
            Text = TestScripts.Text(TestScriptKind.HundredLines, ScriptShell.Cmd),
        };
        var job = console.Session.RunScript([pc.AgentId], script).Single();

        Assert.True(await Wait.UntilAsync(() => job.State == JobState.Failed));
        Assert.Equal(TestScripts.HundredLinesExitCode, job.ExitCode);
        Assert.NotNull(pulled);
        Assert.Contains("for /l", pulled);
        Assert.Equal(RunScriptRequest.CmdValue, job.Args[RunScriptRequest.ShellKey]);
        Assert.Equal(RunScriptRequest.UserValue, job.Args[RunScriptRequest.RunAsKey]);
        Assert.Equal("20", job.Args[RunScriptRequest.TimeoutKey]);
        Assert.Equal(20 + (int)Defaults.JobTimeoutGrace.TotalSeconds, job.TimeoutSeconds);
        Assert.True(FileHash.LooksLikeSha256(job.Args[RunScriptRequest.ReferenceKey]));
        Assert.Equal("test100lines", job.Args[RunScriptRequest.NameKey]);
    }

    [Fact]
    public async Task A_job_outlives_its_link_and_its_result_arrives_on_the_next_one()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 5, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        // The job is running when the cable comes out; it keeps running and finishes while
        // the PC is unlinked. Nothing may cancel it, and the result must not be lost.
        var finish = new TaskCompletionSource();
        var cancelled = false;
        pc.Behaviour.OnJob = async job =>
        {
            pc.Link.Disconnect("simulated cable pull mid-job");
            try
            {
                await finish.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = "finished while unlinked" };
        };

        var job = console.Session.CreateJobs([pc.AgentId], Job.Types.Kind.RunScript).Single();
        Assert.True(await Wait.UntilAsync(() => !console.Session.IsLinked(pc.AgentId)));
        Assert.Equal(1, pc.Link.RunningJobs);

        finish.SetResult();
        Assert.True(await Wait.UntilAsync(() => pc.Link.RunningJobs == 0));
        Assert.False(cancelled);
        Assert.NotEqual(JobState.Succeeded, job.State);

        // The PC reconnects; the queued result goes out and the re-sent job answers from the ledger.
        Assert.True(await Wait.UntilAsync(() => job.State == JobState.Succeeded, TimeSpan.FromSeconds(20)));
        Assert.Equal("finished while unlinked", job.Message);
        Assert.Single(pc.Behaviour.JobsRun);
    }

    [Fact]
    public async Task Waking_an_offline_pc_is_tracked_until_it_links()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var pc = TestAgent.Install(console, 6, console.IssueCodes(1)[0]);
        await using (pc)
        {
            // Enrol once so the console knows the MAC, then go offline.
            pc.Start();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));
            pc.Link.Disconnect("switched off");
            Assert.True(await Wait.UntilAsync(() => !console.Session.IsLinked(pc.AgentId)));

            var started = await console.Session.WakeAsync([pc.AgentId]);
            var events = console.Session.Events.Recent;
            if (started.Count == 0)
            {
                // A machine with no usable network cannot send a broadcast at all; the failure is an event, not silence.
                Assert.Contains(events, e => e.Code == "wake.send_failed");
                return;
            }

            Assert.Contains(console.Session.Events.Recent, e => e.Code == "wake.sent" && e.Message.Contains(pc.Store.Config.Mac, StringComparison.Ordinal));

            // It reconnects on its own (the pinned host is retried every second — often before
            // the three packets have even gone out): the wake is resolved and reported.
            Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(pc.AgentId), TimeSpan.FromSeconds(30)));
            Assert.True(await Wait.UntilAsync(() => console.Session.Waking(pc.AgentId) is null));
            Assert.Contains(console.Session.Events.Recent, e => e.Code == "wake.woke");
        }
    }

    [Fact]
    public async Task Waking_a_pc_that_is_online_or_has_no_mac_does_nothing_harmful()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 7, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        Assert.Empty(await console.Session.WakeAsync([pc.AgentId]));

        var machine = console.Session.Registry.FindByAgentId(pc.AgentId)!;
        pc.Link.Disconnect("off");
        Assert.True(await Wait.UntilAsync(() => !console.Session.IsLinked(pc.AgentId)));
        machine.Mac = string.Empty;

        Assert.Empty(await console.Session.WakeAsync([pc.AgentId]));
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "wake.no_mac");
    }
}
