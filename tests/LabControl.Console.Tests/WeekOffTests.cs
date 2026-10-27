using LabControl.Console.Services;
using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Jobs;
using LabControl.Shared.Lab;
using LabControl.Shared.Link;
using LabControl.Shared.Packages;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// M7 portion 5, robustness scenario 1: a PC that was powered off for a week comes back. What
/// the console queued for it, what its certificate says, what was revoked meanwhile and which
/// build it runs must all be answered on its first link — and nothing clicked last week for
/// the PC as it was then may act on it now.
/// </summary>
public sealed class WeekOffTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_power_action_clicked_while_the_pc_was_off_never_runs_when_it_comes_back_but_a_script_does()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var pc = TestAgent.Install(console, 3, console.IssueCodes(1)[0]);

        // Last week: Shut down, Reboot and Log off all clicked for a room in which PC-03 was off,
        // and a script for it as well.
        var shutdown = console.Session.CreateJobs([pc.AgentId], Job.Types.Kind.Shutdown).Single();
        var reboot = console.Session.CreateJobs([pc.AgentId], Job.Types.Kind.Reboot).Single();
        var logoff = console.Session.CreateJobs([pc.AgentId], Job.Types.Kind.Logoff).Single();
        var script = console.Session.CreateJobs([pc.AgentId], Job.Types.Kind.RunScript,
            new Dictionary<string, string> { ["script"] = "hello.ps1" }).Single();

        // A power action is about the PC as it is now: closed on the spot, never kept (D-25 item 4).
        Assert.All([shutdown, reboot, logoff], job => Assert.Equal(JobState.NotDelivered, job.State));
        Assert.Equal(JobState.Pending, script.State);

        await using (pc)
        {
            pc.Start();
            Assert.True(await Wait.UntilAsync(() => script.State == JobState.Succeeded, TimeSpan.FromSeconds(15)));
            await Task.Delay(300, Ct);
            var run = Assert.Single(pc.Behaviour.JobsRun);
            Assert.Equal(Job.Types.Kind.RunScript, run.Kind);
        }
    }

    [Fact]
    public void A_reboot_still_waiting_when_the_pc_dropped_off_is_closed_not_kept_for_its_return()
    {
        var queue = new JobQueue();
        var now = DateTimeOffset.UtcNow;

        // Created while the PC was linked, never collected before its link dropped.
        var reboot = queue.Create("pc-01", Job.Types.Kind.Reboot, now, agentOnline: true);
        var logoff = queue.Create("pc-01", Job.Types.Kind.Logoff, now, agentOnline: true);
        var install = queue.Create("pc-01", Job.Types.Kind.InstallPackage, now, agentOnline: true);
        Assert.All([reboot, logoff, install], job => Assert.Equal(JobState.Pending, job.State));

        var dropped = queue.AgentWentOffline("pc-01", now.AddSeconds(1));
        Assert.Equal(new[] { reboot.Id, logoff.Id }.Order(), dropped.Select(j => j.Id).Order());

        // A week later the PC returns: only the queued kind is still waiting.
        var delivered = queue.TakePending("pc-01", now.AddDays(7));
        Assert.Equal(install.Id, Assert.Single(delivered).Id);
    }

    [Fact]
    public async Task A_certificate_that_expired_while_the_pc_was_off_is_refused_by_name_and_the_pc_stays_flagged()
    {
        // The console's clock is 40 days on; the PC's certificate was issued today for 30 days,
        // so it expired while the PC was off. The PC is in the list from before.
        var later = TimeSpan.FromDays(40);
        await using var console = await TestConsole.CreateLabAsync(clock: () => DateTimeOffset.UtcNow + later);
        await using var pc = TestAgent.InstallEnrolled(console, 6, TimeSpan.FromDays(30));
        var notAfter = new DateTimeOffset(pc.Store.Certificate!.NotAfter.ToUniversalTime()).ToUnixTimeSeconds();
        console.Session.Registry.Persist(document => document.Machines.Add(new MachineRecord
        {
            AgentId = pc.AgentId,
            Number = 6,
            CertificateSerial = LabCertificates.SerialOf(pc.Store.Certificate!),
            CertificateNotAfterUnix = notAfter,
        }));

        pc.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.Events.Recent.Any(e =>
            e.Code == "link.refused" && e.Message.Contains("expired", StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(15)));
        Assert.False(console.Session.IsLinked(pc.AgentId));

        // The PC keeps its place in the list and is named in the renewal banner; nothing re-added it twice.
        Assert.Single(console.Session.Registry.Document.Machines, m => m.Number == 6);
        Assert.Contains(console.Session.MachinesNeedingRenewal(), m => m.Number == 6);
    }

    [Fact]
    public async Task A_certificate_that_entered_its_renewal_lead_time_while_the_pc_was_off_is_renewed_on_its_first_link()
    {
        // Both clocks a week on: the console and the PC agree it is next week.
        var week = TimeSpan.FromDays(7);
        await using var console = await TestConsole.CreateLabAsync(clock: () => DateTimeOffset.UtcNow + week);
        var pc = TestAgent.Install(console, 8, code: null, options: new AgentLinkOptions { Clock = () => DateTimeOffset.UtcNow + week });

        // Issued with 65 days left last week: 58 days left now, inside the 60-day lead time.
        var lab = console.Session.Vault!.Peek()!;
        var csr = LabCertificates.CreateSigningRequest(pc.Store.Key, "test");
        using (var certificate = LabCertificates.IssueAgentFromCsr(lab.Authority, lab.LabId, pc.AgentId, 8, csr, DateTimeOffset.UtcNow, TimeSpan.FromDays(65)))
        {
            pc.Store.InstallCertificate(pc.Store.Key, certificate);
        }

        var firstSerial = LabCertificates.SerialOf(pc.Store.Certificate!);
        Assert.False(LabCertificates.NeedsRenewal(pc.Store.Certificate!, DateTimeOffset.UtcNow));
        Assert.True(LabCertificates.NeedsRenewal(pc.Store.Certificate!, DateTimeOffset.UtcNow + week));

        await using (pc)
        {
            pc.Start();
            Assert.True(await Wait.UntilAsync(() => LabCertificates.SerialOf(pc.Store.Certificate!) != firstSerial, TimeSpan.FromSeconds(20)));
            var newSerial = LabCertificates.SerialOf(pc.Store.Certificate!);
            Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId)?.CertificateSerial == newSerial, TimeSpan.FromSeconds(20)));
            Assert.Empty(console.Session.MachinesNeedingRenewal());
        }
    }

    [Fact]
    public async Task A_revocation_made_while_the_pc_was_off_reaches_it_on_its_first_link()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var codes = console.IssueCodes(2);
        await using var stolen = TestAgent.Install(console, 1, codes[0]).Start();
        var away = TestAgent.Install(console, 2, codes[1]).Start();
        Assert.True(await Wait.UntilAsync(() => stolen.Link.State == LinkState.Linked && away.Link.State == LinkState.Linked));
        var directory = away.Store.Directory;
        await away.DisposeAsync();

        // While PC-02 was off, PC-01 was stolen and revoked.
        var serial = LabCertificates.SerialOf(stolen.Store.Certificate!);
        Assert.True(console.Session.TryRevoke(serial, "stolen", out _));

        await using var back = TestAgent.Open(DirectoryAgentStore.Open(directory)).Start();
        Assert.True(await Wait.UntilAsync(() => back.Store.Config.Revocations.Any(r =>
            string.Equals(LabCertificates.NormalizeSerial(r.Serial), LabCertificates.NormalizeSerial(serial), StringComparison.Ordinal)), TimeSpan.FromSeconds(15)));
        Assert.True(await Wait.UntilAsync(() =>
            console.Session.Registry.FindByAgentId(back.AgentId)!.RevocationSerialsSeen.Contains(LabCertificates.NormalizeSerial(serial)), TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task An_older_build_that_returns_is_linked_never_given_a_lock_and_can_still_be_updated()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var codes = console.IssueCodes(2);
        await using var current = TestAgent.Install(console, 1, codes[0]).Start();
        await using var old = TestAgent.Install(console, 2, codes[1],
            options: new AgentLinkOptions { ProtocolVersion = Defaults.ClassroomControlProtocolVersion - 1 });
        old.Behaviour.Version = "1.0.0";

        // An update pushed while it was off waits for it (queued); a lock is a link message only.
        var update = console.Session.CreateJobs([old.AgentId], Job.Types.Kind.SelfUpdate).Single();
        Assert.Equal(JobState.Pending, update.State);

        old.Start();
        Assert.True(await Wait.UntilAsync(() => current.Link.State == LinkState.Linked && old.Link.State == LinkState.Linked));
        Assert.True(await Wait.UntilAsync(() => update.State == JobState.Succeeded, TimeSpan.FromSeconds(15)));
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "link.no_classroom_control" && e.AgentId == old.AgentId);

        // Lock all: only the PC that can enforce it is asked.
        var received = new System.Collections.Concurrent.ConcurrentBag<string>();
        current.Link.OverlayReceived += (_, _) => received.Add("current");
        old.Link.OverlayReceived += (_, _) => received.Add("old");
        Assert.Equal(1, console.Session.Lock([current.AgentId, old.AgentId], "Eyes here", null));
        Assert.True(await Wait.UntilAsync(() => received.Contains("current"), TimeSpan.FromSeconds(10)));
        await Task.Delay(300, Ct);
        Assert.DoesNotContain("old", received);
    }

    [Fact]
    public async Task Two_builds_pushed_while_the_pc_was_off_deliver_only_the_latest()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var pc = TestAgent.Install(console, 4, console.IssueCodes(1)[0]);
        var monday = Build(1);
        var wednesday = Build(2);
        try
        {
            Assert.True(AgentBuild.TryLoad(monday, "0.1.1", out var first, out var error), error);
            Assert.True(AgentBuild.TryLoad(wednesday, "0.1.2", out var second, out error), error);
            var older = Assert.Single(console.Session.PushAgentBuild([pc.AgentId], first));
            var latest = Assert.Single(console.Session.PushAgentBuild([pc.AgentId], second));

            Assert.Equal(JobState.NotDelivered, older.State);
            Assert.Contains("0.1.2", older.Message, StringComparison.Ordinal);
            Assert.Equal(JobState.Pending, latest.State);

            await using (pc)
            {
                pc.Behaviour.OnJob = job => Task.FromResult(new JobResult { JobId = job.Id, Ok = true, Message = "updated" });
                pc.Start();
                Assert.True(await Wait.UntilAsync(() => latest.State == JobState.Succeeded, TimeSpan.FromSeconds(15)));
                await Task.Delay(300, Ct);
                var run = Assert.Single(pc.Behaviour.JobsRun);
                Assert.True(SelfUpdateRequest.TryParse(run, out var request, out var problem), problem);
                Assert.StartsWith("0.1.2", request.Version, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(monday, recursive: true);
            Directory.Delete(wednesday, recursive: true);
        }

        static string Build(int seed)
        {
            var folder = TestConsole.TempDirectory();
            var bytes = new byte[10_000];
            new Random(seed).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(folder, Defaults.AgentExecutableName), bytes);
            File.WriteAllText(Path.Combine(folder, Defaults.SessionExecutableName), $"session helper {seed}");
            return folder;
        }
    }

    [Fact]
    public async Task An_install_queued_for_an_offline_pc_is_dropped_when_its_installer_is_replaced_and_the_new_one_installs()
    {
        await using var console = await TestConsole.CreateLabAsync("MacBook");
        var session = console.Session;
        var payload = SetupPayload.Open(session.WritePayload(TestConsole.TempDirectory(), pcCount: 2));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-01"), 1, payload,
            System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);

        // Enrolled once, then switched off for the week.
        var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-01"));
        machine.Start();
        Assert.True(await Wait.UntilAsync(() => session.Linked.Count == 1, TimeSpan.FromSeconds(20)));
        var agentId = machine.AgentId;
        var directory = store.Directory;
        await machine.DisposeAsync();
        Assert.True(await Wait.UntilAsync(() => session.Linked.Count == 0, TimeSpan.FromSeconds(10)));

        // Monday: version 1 added and *Install on all missing* queued for the PC that is off.
        var v1 = await PackageCatalogTests.AddAsync(session.Packages, PackageCatalogTests.NsisInstaller("tool.exe", "v1"), "Tool");
        var queued = Assert.Single(session.InstallPackageOnAllMissing(v1));
        Assert.Equal(JobState.Pending, queued.State);

        // Wednesday: version 2 replaces version 1 and its installer is gone from the catalog.
        var model = new LabControl.Console.ViewModels.AddPackageModel(session.Packages);
        Assert.Null(await model.InspectAsync(PackageCatalogTests.NsisInstaller("tool.exe", "v2"), null, Ct));
        model.Name = "Tool";
        var (v2, problem) = await model.AddAsync(null, Ct);
        Assert.Null(problem);
        Assert.Equal(v1.Id, v2!.Id);
        Assert.NotEqual(v1.Sha256, v2.Sha256);

        // The week-old job for an installer that no longer exists is closed with a reason,
        // and the PC is free for the new version's *Install on all missing*.
        Assert.Equal(JobState.NotDelivered, queued.State);
        Assert.False(string.IsNullOrWhiteSpace(queued.Message));
        var fresh = Assert.Single(session.InstallPackageOnAllMissing(v2));

        // Next week: the PC comes back and installs only version 2.
        await using var back = new FakeMachine(LabControl.Shared.Link.DirectoryAgentStore.Open(directory), null, TestLogging.Factory.CreateLogger("PC-01"));
        back.Start();
        Assert.True(await Wait.UntilAsync(() => fresh.IsFinished, TimeSpan.FromSeconds(30)));
        Assert.True(fresh.Ok, fresh.Message);
        Assert.Equal(PackageOutcomes.Installed, fresh.Outcome);
        Assert.Equal(PcPackageState.Installed, session.PackageStatus.StateOf(v2, agentId));
        Assert.Equal(JobState.NotDelivered, queued.State);
    }
}
