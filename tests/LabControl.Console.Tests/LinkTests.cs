using Xunit;

using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Lab;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;

namespace LabControl.Console.Tests;

/// <summary>
/// The M1 acceptance criteria that can be proved in-process (ROADMAP M1): enrolment over
/// real TLS, the link, jobs end to end, refusals with reasons, renewal, reinstall,
/// revocation carried by agents. Beacons are tested separately, over real UDP.
/// </summary>
public sealed class LinkTests
{
    [Fact]
    public async Task Thirty_fake_agents_enrol_from_a_fresh_lab_and_appear_online()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var codes = console.IssueCodes(Defaults.MaxStudentPcs);

        var agents = new List<TestAgent>();
        try
        {
            for (var n = 1; n <= Defaults.MaxStudentPcs; n++)
            {
                agents.Add(TestAgent.Install(console, n, codes[n - 1]).Start());
            }

            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == Defaults.MaxStudentPcs, TimeSpan.FromSeconds(15)),
                $"only {console.Session.Linked.Count} of {Defaults.MaxStudentPcs} linked");

            Assert.Equal(Defaults.MaxStudentPcs, console.Session.Registry.Document.Machines.Count);
            Assert.Equal(0, console.Session.Enrollment.UnusedCodeCount);
            Assert.All(agents, a => Assert.True(a.Link.IsEnrolled));
            Assert.All(agents, a => Assert.Equal(console.Session.Instance.InstanceId, a.Link.LinkedInstanceId));

            // The machine list is the lab's cache and has what Hello and Inventory said.
            var seven = console.Session.Registry.Document.Machines.Single(m => m.Number == 7);
            Assert.True(await Wait.UntilAsync(() => seven.Hostname == "test-host"));
            Assert.Equal("student", seven.LoggedOnUser);
            Assert.True(seven.CertificateNotAfterUnix > 0);
        }
        finally
        {
            foreach (var agent in agents)
            {
                await agent.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task An_agent_that_enrols_while_the_key_is_locked_waits_and_enrols_once_it_is_unlocked()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var code = console.IssueCodes(1)[0];
        console.Session.Vault.Lock();

        await using var agent = TestAgent.Install(console, 3, code).Start();

        Assert.True(await Wait.UntilAsync(() => agent.Refusals.Count > 0));
        Assert.Contains("unlock", agent.Refusals[0], StringComparison.OrdinalIgnoreCase);
        Assert.False(agent.Link.IsEnrolled);
        Assert.Equal(1, console.Session.Enrollment.UnusedCodeCount);

        Assert.True(console.Session.Vault.TryUnlock(TestConsole.Passphrase));

        Assert.True(await Wait.UntilAsync(() => agent.Link.State == LinkState.Linked, TimeSpan.FromSeconds(20)));
        Assert.True(agent.Link.IsEnrolled);
    }

    [Fact]
    public async Task A_burned_code_is_refused_and_surfaces_as_an_event()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var code = console.IssueCodes(1)[0];

        await using var first = TestAgent.Install(console, 1, code).Start();
        Assert.True(await Wait.UntilAsync(() => first.Link.State == LinkState.Linked));

        await using var second = TestAgent.Install(console, 2, code).Start();
        Assert.True(await Wait.UntilAsync(() => second.Refusals.Count > 0));

        Assert.Contains("already used", second.Refusals[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "enroll.burned_code" && e.Number == 2);
        Assert.False(second.Link.IsEnrolled);
    }

    [Fact]
    public async Task A_job_runs_on_a_linked_pc_and_waits_for_an_offline_one()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var codes = console.IssueCodes(2);

        await using var online = TestAgent.Install(console, 1, codes[0]).Start();
        Assert.True(await Wait.UntilAsync(() => online.Link.State == LinkState.Linked));

        var offline = TestAgent.Install(console, 2, codes[1]);   // installed, never started yet

        var jobs = console.Session.CreateJobs([online.AgentId, offline.AgentId], Job.Types.Kind.RunScript,
            new Dictionary<string, string> { ["script"] = "hello.ps1" });

        var forOnline = jobs.Single(j => j.AgentId == online.AgentId);
        var forOffline = jobs.Single(j => j.AgentId == offline.AgentId);

        Assert.True(await Wait.UntilAsync(() => forOnline.State == JobState.Succeeded));
        Assert.Equal("RunScript done", forOnline.Message);
        Assert.Contains("half way", forOnline.Output);
        Assert.Equal(JobState.Pending, forOffline.State);

        // The offline PC comes back and collects its job.
        await using (offline)
        {
            offline.Start();
            Assert.True(await Wait.UntilAsync(() => forOffline.State == JobState.Succeeded));
            Assert.Single(offline.Behaviour.JobsRun);
        }
    }

    [Fact]
    public async Task A_shutdown_for_an_offline_pc_is_not_delivered_even_when_it_comes_back()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var code = console.IssueCodes(1)[0];
        var pc = TestAgent.Install(console, 5, code);

        var job = console.Session.CreateJobs([pc.AgentId], Job.Types.Kind.Shutdown).Single();
        Assert.Equal(JobState.NotDelivered, job.State);

        await using (pc)
        {
            pc.Start();
            Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.Empty(pc.Behaviour.JobsRun);
        }
    }

    [Fact]
    public async Task A_re_sent_job_answers_from_the_cache_after_a_reconnect()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var code = console.IssueCodes(1)[0];
        await using var pc = TestAgent.Install(console, 4, code).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        // The PC drops the link the moment it has the job, before its result gets out.
        var gate = new TaskCompletionSource();
        pc.Behaviour.OnJob = async job =>
        {
            pc.Link.Disconnect("simulated cable pull");
            await gate.Task;
            return new JobResult { JobId = job.Id, Ok = true, Message = "rebooted once" };
        };

        var job = console.Session.CreateJobs([pc.AgentId], Job.Types.Kind.Reboot).Single();
        Assert.True(await Wait.UntilAsync(() => pc.Behaviour.JobsRun.Count == 1));
        Assert.True(await Wait.UntilAsync(() => !console.Session.IsLinked(pc.AgentId)));
        gate.SetResult();

        // On reconnect the console re-sends the in-flight job; the ledger answers, nothing runs twice.
        Assert.True(await Wait.UntilAsync(() => job.State == JobState.Succeeded, TimeSpan.FromSeconds(20)));
        Assert.Equal("rebooted once", job.Message);
        Assert.Single(pc.Behaviour.JobsRun);
    }

    [Fact]
    public async Task A_reinstalled_pc_replaces_its_record_and_the_event_names_both()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var codes = console.IssueCodes(2);

        await using var before = TestAgent.Install(console, 7, codes[0]).Start();
        Assert.True(await Wait.UntilAsync(() => before.Link.State == LinkState.Linked));

        await using var after = TestAgent.Install(console, 7, codes[1]).Start();
        Assert.True(await Wait.UntilAsync(() => after.Link.State == LinkState.Linked));

        var machines = console.Session.Registry.Document.Machines.Where(m => m.Number == 7).ToArray();
        Assert.Single(machines);
        Assert.Equal(after.AgentId, machines[0].AgentId);

        var replaced = Assert.Single(console.Session.Events.Recent, e => e.Code == "machine.replaced");
        Assert.Contains(before.AgentId, replaced.Message, StringComparison.Ordinal);
        Assert.Contains(after.AgentId, replaced.Message, StringComparison.Ordinal);

        // The old agent was cut off and cannot come back as PC-07 with its old id: its
        // record is gone, so the next Hello would re-add it — and replace the new one. That
        // is the reinstall rule working both ways; what matters is one PC-07 at any time.
        Assert.True(await Wait.UntilAsync(() => before.Link.State != LinkState.Linked));
    }

    [Fact]
    public async Task A_certificate_is_renewed_over_the_link_once_the_key_is_unlocked()
    {
        await using var console = await TestConsole.CreateLabAsync();

        // A PC enrolled long ago: 30 days left on its certificate, inside the 60-day lead time.
        await using var pc = TestAgent.InstallEnrolled(console, 9, TimeSpan.FromDays(30));
        var firstSerial = LabCertificates.SerialOf(pc.Store.Certificate!);
        Assert.True(LabCertificates.NeedsRenewal(pc.Store.Certificate!, DateTimeOffset.UtcNow));

        // Locked: the PC links, is refused politely and keeps working with its current certificate.
        console.Session.Vault.Lock();
        pc.Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));
        Assert.True(await Wait.UntilAsync(() => console.Session.Registry.FindByAgentId(pc.AgentId) is not null));
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));
        Assert.Equal(firstSerial, LabCertificates.SerialOf(pc.Store.Certificate!));
        Assert.Single(console.Session.MachinesNeedingRenewal());
        Assert.DoesNotContain(console.Session.Events.Recent, e => e.Code == "renew.issued");

        console.Session.Vault.TryUnlock(TestConsole.Passphrase);

        Assert.True(await Wait.UntilAsync(() => LabCertificates.SerialOf(pc.Store.Certificate!) != firstSerial, TimeSpan.FromSeconds(40)));
        var newSerial = LabCertificates.SerialOf(pc.Store.Certificate!);

        // Reconnected with the new certificate; the console shows the new serial for the same PC.
        Assert.True(await Wait.UntilAsync(() =>
            console.Session.FindLinked(pc.AgentId)?.CertificateSerial == newSerial, TimeSpan.FromSeconds(20)));
        Assert.Equal(newSerial, console.Session.Registry.FindByAgentId(pc.AgentId)!.CertificateSerial);
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "renew.issued");
        Assert.Single(console.Session.Registry.Document.Machines, m => m.Number == 9);

        // The renewed certificate has the full lifetime, so the banner clears.
        Assert.Empty(console.Session.MachinesNeedingRenewal());
        Assert.False(LabCertificates.NeedsRenewal(pc.Store.Certificate!, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_revoked_pc_is_refused_with_the_reason_and_a_forged_entry_is_ignored()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var codes = console.IssueCodes(2);

        await using var victim = TestAgent.Install(console, 1, codes[0]).Start();
        Assert.True(await Wait.UntilAsync(() => victim.Link.State == LinkState.Linked));
        var serial = LabCertificates.SerialOf(victim.Store.Certificate!);

        Assert.True(console.Session.TryRevoke(serial, "stolen", out _));
        Assert.True(await Wait.UntilAsync(() => victim.Link.State != LinkState.Linked));
        Assert.True(await Wait.UntilAsync(() => victim.Refusals.Any(r => r.Contains("revoked", StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(20)));

        // A forgery offered by another PC changes nothing and is logged.
        var forged = new RevocationEntry { Serial = "ABCD", RevokedAtUnix = 1, Reason = "made up", Signature = Google.Protobuf.ByteString.CopyFrom(new byte[64]) };
        await using var liar = TestAgent.Install(console, 2, codes[1], options: new AgentLinkOptions { ExtraRevocations = () => [forged] }).Start();
        Assert.True(await Wait.UntilAsync(() => liar.Link.State == LinkState.Linked));
        Assert.True(await Wait.UntilAsync(() => console.Session.Events.Recent.Any(e => e.Code == "revocation.forged")));
        Assert.False(console.Session.Registry.Revocations.IsRevoked("ABCD"));

        // The honest PC learned the genuine revocation from the console.
        Assert.True(await Wait.UntilAsync(() => liar.Store.Config.Revocations.Any(r => r.Serial == serial)));
    }

    [Fact]
    public async Task A_revocation_made_on_the_other_console_arrives_with_the_first_agent()
    {
        await using var a = await TestConsole.CreateLabAsync("A");
        var code = console_code(a);
        await using var pc = TestAgent.Install(a, 1, code).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        // A revokes something (not this PC) and the PC carries the entry away.
        Assert.True(a.Session.TryRevoke("0F0F", "a laptop that was stolen", out _));
        Assert.True(await Wait.UntilAsync(() => pc.Store.Config.Revocations.Any(r => r.Serial == "F0F")));

        // B never heard of it — until the PC connects to B.
        await using var b = await TestConsole.JoinLabAsync(a, "B");
        Assert.False(b.Session.Registry.Revocations.IsRevoked("0F0F"));

        pc.Store.Config.ConsolePort = b.Port;
        pc.Store.SaveConfig();
        await using var moved = TestAgent.Open(DirectoryAgentStore.Open(pc.Store.Directory)).Start();

        Assert.True(await Wait.UntilAsync(() => b.Session.Registry.Revocations.IsRevoked("0F0F"), TimeSpan.FromSeconds(20)));
        Assert.Contains(b.Session.Events.Recent, e => e.Code == "revocation.learned");

        // And B added the PC to its list from Hello, without being told (§3.7).
        Assert.Single(b.Session.Registry.Document.Machines);

        static string console_code(TestConsole c) => c.IssueCodes(1)[0];
    }

    [Fact]
    public async Task An_outdated_agent_is_connected_and_marked_not_refused()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var code = console.IssueCodes(1)[0];

        await using var old = TestAgent.Install(console, 1, code, options: new AgentLinkOptions { ProtocolVersion = Defaults.MinimumProtocolVersion - 1 }).Start();
        Assert.True(await Wait.UntilAsync(() => old.Link.State == LinkState.Linked));

        var connection = console.Session.FindLinked(old.AgentId)!;
        Assert.True(connection.IsOutdated);
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "link.outdated");

        // It can still be sent the one job that matters.
        var job = console.Session.CreateJobs([old.AgentId], Job.Types.Kind.SelfUpdate).Single();
        Assert.True(await Wait.UntilAsync(() => job.State == JobState.Succeeded));
    }

    [Fact]
    public async Task Killing_the_console_and_restarting_it_brings_every_agent_back()
    {
        var console = await TestConsole.CreateLabAsync(port: 0);
        var codes = console.IssueCodes(3);
        var agents = new List<TestAgent>();

        try
        {
            for (var n = 1; n <= 3; n++)
            {
                agents.Add(TestAgent.Install(console, n, codes[n - 1]).Start());
            }

            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == 3));
            var port = console.Port;

            // Same lab, same instance directory: what a restart of the app looks like.
            var directory = console.Directory;
            await console.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => agents.All(a => a.Link.State != LinkState.Linked)));

            var store = new LabControl.Console.Services.LabStore(directory);
            var keyDocument = store.LoadLabKey();
            var vault = new LabControl.Console.Services.LabKeyVault(store, keyDocument);
            var instance = ConsoleInstance.Open(store.LoadInstance()!);
            var options = new LabControl.Console.Services.ConsoleOptions { DataDirectory = directory, Port = port, BindAddress = System.Net.IPAddress.Loopback };
            await using var again = new LabControl.Console.Services.LabSession(options, store, vault, instance, instance.Document, TestLogging.Factory);
            await again.StartAsync();

            Assert.True(await Wait.UntilAsync(() => again.Linked.Count == 3, TimeSpan.FromSeconds(15)), $"{again.Linked.Count} linked after restart");
            Assert.Equal(3, again.Registry.Document.Machines.Count);   // restored from lab.json, not re-learned
        }
        finally
        {
            foreach (var agent in agents)
            {
                await agent.DisposeAsync();
            }

            try
            {
                Directory.Delete(console.Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
