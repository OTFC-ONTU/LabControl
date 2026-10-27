using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.FakeAgent;
using LabControl.Shared.Lab;
using LabControl.Shared.Packages;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// <c>install_package</c> end to end (M7 portion 4, D-89): simulated PCs pull the catalog's
/// installer over the real link, check its hash, run the pretended installer and evaluate the
/// rule; the console records each PC's answer, installs on all missing — skipping who has it —
/// and a broken package answers with its reason on every PC.
/// </summary>
public sealed class PackageInstallTests
{
    private static async Task<(TestConsole Console, List<FakeMachine> Machines)> LabAsync(int count)
    {
        var console = await TestConsole.CreateLabAsync("MacBook");
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

    private static async Task DisposeAsync(TestConsole console, List<FakeMachine> machines)
    {
        foreach (var machine in machines) await machine.DisposeAsync();
        await console.DisposeAsync();
    }

    private static Task<bool> Finished(IReadOnlyList<JobRecord> jobs) =>
        Wait.UntilAsync(() => jobs.All(j => j.IsFinished), TimeSpan.FromSeconds(30));

    [Fact]
    public async Task A_test_install_on_one_pc_then_all_missing_installs_only_where_the_program_is_not()
    {
        var (console, machines) = await LabAsync(3);
        try
        {
            var session = console.Session;
            var entry = await PackageCatalogTests.AddAsync(session.Packages, PackageCatalogTests.NsisInstaller("tool.exe"), "Tool");
            Assert.False(session.PackageStatus.EverInstalled(entry));

            // The test install on one PC.
            var test = session.InstallPackage([machines[0].AgentId], entry, InstallMode.Install);
            Assert.True(await Finished(test));
            var first = Assert.Single(test);
            Assert.True(first.Ok, first.Message);
            Assert.Equal(PackageOutcomes.Installed, first.Outcome);
            Assert.Contains(first.Output, line => line.Contains("tool.exe", StringComparison.Ordinal));
            Assert.Equal(PcPackageState.Installed, session.PackageStatus.StateOf(entry, machines[0].AgentId));
            Assert.Equal(PcPackageState.Unknown, session.PackageStatus.StateOf(entry, machines[1].AgentId));
            Assert.True(session.PackageStatus.EverInstalled(entry));

            // The rest: PC-01 is known to have it and gets nothing.
            var rest = session.InstallPackageOnAllMissing(entry);
            Assert.Equal(new[] { machines[1].AgentId, machines[2].AgentId }.Order(), rest.Select(j => j.AgentId).Order());
            Assert.True(await Finished(rest));
            Assert.All(rest, j => Assert.Equal(PackageOutcomes.Installed, j.Outcome));
            Assert.All(machines, m => Assert.Equal(PcPackageState.Installed, session.PackageStatus.StateOf(entry, m.AgentId)));
            Assert.Empty(session.InstallPackageOnAllMissing(entry));

            // A check finds it everywhere; nothing is pulled for it.
            var check = session.CheckPackage(entry);
            Assert.True(await Finished(check));
            Assert.All(check, j => Assert.Equal(PackageOutcomes.Present, j.Outcome));

            // The status survives the console: a new book over the same file reads it back.
            var again = new PackageStatusBook(session.Store, session.LabId, new JobQueue(), TestLogging.Factory.CreateLogger<PackageStatusBook>());
            Assert.All(machines, m => Assert.Equal(PcPackageState.Installed, again.StateOf(entry, m.AgentId)));
        }
        finally
        {
            await DisposeAsync(console, machines);
        }
    }

    [Fact]
    public async Task A_pc_that_already_has_the_program_skips_the_installer_when_asked_for_missing_only()
    {
        var (console, machines) = await LabAsync(1);
        try
        {
            var session = console.Session;
            var entry = await PackageCatalogTests.AddAsync(session.Packages, PackageCatalogTests.NsisInstaller("tool.exe"), "Tool");
            Assert.True(await Finished(session.InstallPackage([machines[0].AgentId], entry, InstallMode.Install)));

            var missing = session.InstallPackage([machines[0].AgentId], entry, InstallMode.Missing);
            Assert.True(await Finished(missing));
            Assert.Equal(PackageOutcomes.Present, missing[0].Outcome);
            Assert.DoesNotContain(missing[0].Output, line => line.Contains("Receiving", StringComparison.Ordinal));
        }
        finally
        {
            await DisposeAsync(console, machines);
        }
    }

    [Theory]
    [InlineData("/S /fail", PackageOutcomes.Failed, 1603)]
    [InlineData("/S /nodetect", PackageOutcomes.NotDetected, -1)]
    [InlineData("/S /reboot", PackageOutcomes.RebootRequired, 3010)]
    public async Task A_broken_package_answers_with_its_reason_on_every_pc(string silent, string outcome, int exitCode)
    {
        var (console, machines) = await LabAsync(2);
        try
        {
            var session = console.Session;
            var entry = await PackageCatalogTests.AddAsync(session.Packages, PackageCatalogTests.NsisInstaller("tool.exe"), "Tool", m => m.Silent = silent);
            var jobs = session.InstallPackage(machines.Select(m => m.AgentId), entry, InstallMode.Install);
            Assert.True(await Finished(jobs));
            Assert.All(jobs, j =>
            {
                Assert.Equal(outcome, j.Outcome);
                Assert.Equal(exitCode, j.ExitCode);
                Assert.Equal(outcome == PackageOutcomes.RebootRequired, j.Ok);
                Assert.False(string.IsNullOrWhiteSpace(j.Message));
            });
            var expected = outcome == PackageOutcomes.RebootRequired ? PcPackageState.RebootRequired : PcPackageState.Failed;
            Assert.All(machines, m => Assert.Equal(expected, session.PackageStatus.StateOf(entry, m.AgentId)));
        }
        finally
        {
            await DisposeAsync(console, machines);
        }
    }

    [Fact]
    public async Task A_pc_in_an_exam_refuses_and_a_missing_installer_is_never_sent()
    {
        var (console, machines) = await LabAsync(1);
        try
        {
            var session = console.Session;
            var id = machines[0].AgentId;
            var entry = await PackageCatalogTests.AddAsync(session.Packages, PackageCatalogTests.NsisInstaller("tool.exe"), "Tool");
            session.StartExam([id], new ExamRequest("Test", null, false, [], InternetPolicy.Types.Mode.Open, [], string.Empty, string.Empty), wholeLab: false);
            Assert.True(await Wait.UntilAsync(() => session.FindLinked(id)?.InExam == true, TimeSpan.FromSeconds(10)));

            var jobs = session.InstallPackage([id], entry, InstallMode.Install);
            Assert.True(await Finished(jobs));
            Assert.Equal(PackageOutcomes.ExamHeld, jobs[0].Outcome);
            Assert.False(jobs[0].Ok);

            File.Delete(session.Packages.InstallerPath(entry));
            Assert.Throws<InvalidOperationException>(() => session.InstallPackage([id], entry, InstallMode.Install));
        }
        finally
        {
            await DisposeAsync(console, machines);
        }
    }

    [Fact]
    public async Task The_tab_asks_before_sending_an_untried_package_to_the_whole_lab()
    {
        var (console, machines) = await LabAsync(2);
        try
        {
            var session = console.Session;
            var dialogs = new PackageCatalogTests.WizardDialogs(PackageCatalogTests.NsisInstaller("tool.exe"), "Tool");
            var selection = new List<string>();
            var tab = new PackagesViewModel(session, dialogs, action => action(), () => selection);
            await tab.AddCommand.ExecuteAsync(null);
            Assert.Equal(2, tab.Pcs.Count);
            Assert.All(tab.Pcs, row => Assert.Equal(PcPackageState.Unknown, row.State));
            Assert.False(tab.InstallSelectedCommand.CanExecute(null));

            dialogs.Confirm = false;
            await tab.InstallAllMissingCommand.ExecuteAsync(null);
            Assert.DoesNotContain(session.Jobs.All(), j => j.Kind == Job.Types.Kind.InstallPackage);

            selection.Add(machines[0].AgentId);
            tab.SetSelectedPcCount(1);
            tab.InstallSelectedCommand.Execute(null);
            Assert.True(await Wait.UntilAsync(() => tab.Pcs.Any(r => r.State == PcPackageState.Installed), TimeSpan.FromSeconds(30)));

            // Tried once: the rest goes without a question.
            await tab.InstallAllMissingCommand.ExecuteAsync(null);
            Assert.True(await Wait.UntilAsync(() => tab.Pcs.All(r => r.State == PcPackageState.Installed), TimeSpan.FromSeconds(30)));
            tab.Detach();
        }
        finally
        {
            await DisposeAsync(console, machines);
        }
    }
}
