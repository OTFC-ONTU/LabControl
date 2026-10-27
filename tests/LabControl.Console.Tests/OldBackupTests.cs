using System.Net;
using LabControl.Console.Services;
using LabControl.Shared.Identity;
using LabControl.Shared.Lab;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// M7 portion 5, robustness scenario 2: the teacher machine dies and a new one is set up from
/// a <c>.lcbak</c> made weeks earlier — before a PC was added, before another was removed and
/// revoked, before codes from the stick were used. The machine list is a cache (ARCHITECTURE
/// §3.7): the PCs heal it, the revocations they carry are learned as a set and acted on, and
/// nothing the backup forgot can open a door the lab had closed.
/// </summary>
public sealed class OldBackupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_console_restored_from_an_old_backup_heals_its_list_learns_the_revocation_and_cuts_the_revoked_pc()
    {
        var files = TestConsole.TempDirectory();
        var backup = Path.Combine(files, "room.lcbak");
        var agents = new List<TestAgent>();
        var device = TestConsole.TempDirectory();
        LabSession? restored = null;

        try
        {
            await using var source = await TestConsole.CreateLabAsync("MacBook");
            var codes = source.IssueCodes(4);

            // September: PC-01 and PC-02 enrolled; the library, a preset and a package added; the backup made.
            agents.Add(TestAgent.Install(source, 1, codes[0]).Start());
            agents.Add(TestAgent.Install(source, 2, codes[1]).Start());
            Assert.True(await Wait.UntilAsync(() => source.Session.Linked.Count == 2, TimeSpan.FromSeconds(15)));
            var script = source.Session.Scripts.NewScript();
            script.Name = "install-jdk";
            script.Text = "Write-Output 'jdk'\n";
            Assert.True(source.Session.Scripts.TrySave(script, out var scriptError), scriptError);
            Assert.True(source.Session.InternetPresets.Save(null, "Python docs", ["docs.python.org"]).Ok);
            var package = await PackageCatalogTests.AddAsync(source.Session.Packages, PackageCatalogTests.NsisInstaller("tool.exe"), "Tool");
            Assert.True(source.Bootstrap.TryExportBackup(source.Session, backup, out var error), error);

            // October, after the backup: PC-03 enrolled with a code from the same stick; PC-02
            // removed from the lab (revoked, then forgotten), which PC-01 hears about.
            agents.Add(TestAgent.Install(source, 3, codes[2]).Start());
            Assert.True(await Wait.UntilAsync(() => source.Session.Linked.Count == 3, TimeSpan.FromSeconds(15)));
            var removedSerial = LabCertificates.SerialOf(agents[1].Store.Certificate!);
            Assert.True(source.Session.TryRevoke(removedSerial, "PC-02 removed from the lab by the teacher", out _));
            source.Session.ForgetMachine(agents[1].AgentId);
            Assert.True(await Wait.UntilAsync(() => agents[0].Store.Config.Revocations.Any(r =>
                LabCertificates.NormalizeSerial(r.Serial) == LabCertificates.NormalizeSerial(removedSerial)), TimeSpan.FromSeconds(15)));

            // The MacBook dies.
            await source.Session.DisposeAsync();
            Assert.True(await Wait.UntilAsync(() => agents.All(a => a.Link.State != LinkState.Linked), TimeSpan.FromSeconds(15)));

            // A new teacher machine imports the September backup.
            var bootstrap = new ConsoleBootstrap(new ConsoleOptions
            {
                DataDirectory = device,
                Port = 0,
                BeaconPort = TestConsole.BeaconPort,
                BindAddress = IPAddress.Loopback,
            }, TestLogging.Factory, () => new FileSecretProtector());
            var imports = new LabImports(bootstrap, _ => Task.FromResult<BackupSecret?>(new BackupSecret(TestConsole.Passphrase, null)),
                "Windows desk PC", TestLogging.Factory.CreateLogger("imports"));
            var added = Assert.Single(await imports.ImportAsync([backup]));
            Assert.True(added.Ok, added.Message);
            var opened = bootstrap.OpenExisting(source.Session.LabId);
            restored = bootstrap.Build(opened, opened.Instance);
            await restored.StartAsync();
            if (restored.Vault!.Peek() is null)
            {
                Assert.True(restored.Vault.TryUnlock(TestConsole.Passphrase));
            }

            // What it knows is September's: PC-01 and PC-02, no revocation, the library and catalog.
            Assert.Equal([1, 2], restored.Registry.Document.Machines.Select(m => m.Number).Order());
            Assert.False(restored.Registry.Revocations.IsRevoked(removedSerial));
            Assert.Contains(restored.Scripts.Document.Scripts, s => s.Name == "install-jdk");
            Assert.Contains(restored.InternetPresets.Presets, p => p.Name == "Python docs");
            var restoredPackage = restored.Packages.Find(package.Id);
            Assert.NotNull(restoredPackage);
            Assert.Equal(package.Sha256, restoredPackage.Sha256);

            // The installer itself does not travel (D-88): the entry says so, and nothing is sent.
            Assert.NotEqual(InstallerState.Present, restored.Packages.StateOf(restoredPackage));
            Assert.Throws<InvalidOperationException>(() => restored.InstallPackage([agents[0].AgentId], restoredPackage, LabControl.Shared.Packages.InstallMode.Install));

            // The PCs find the new machine. The removed PC happens to be first: the restored
            // console has not heard of its revocation and accepts it.
            var reopened = new List<TestAgent>();
            foreach (var agent in agents)
            {
                var directory = agent.Store.Directory;
                await agent.DisposeAsync();
                var store = DirectoryAgentStore.Open(directory);
                store.Config.ConsolePort = restored.Port;
                store.SaveConfig();
                reopened.Add(TestAgent.Open(store));
            }

            agents = reopened;
            var (pc1, pc2, pc3) = (agents[0], agents[1], agents[2]);
            pc2.Start();
            Assert.True(await Wait.UntilAsync(() => restored.IsLinked(pc2.AgentId), TimeSpan.FromSeconds(15)));

            // PC-03, added after the backup, heals into the list and the teacher is told.
            pc3.Start();
            Assert.True(await Wait.UntilAsync(() => restored.IsLinked(pc3.AgentId), TimeSpan.FromSeconds(15)));
            Assert.Contains(restored.Registry.Document.Machines, m => m.Number == 3 && m.AgentId == pc3.AgentId);
            Assert.Contains(restored.Events.Recent, e => e.Code == "link.new_machine" && e.AgentId == pc3.AgentId);

            // PC-01 brings the revocation: learned as a set, and the PC it names is cut at once
            // and refused from then on.
            pc1.Start();
            Assert.True(await Wait.UntilAsync(() => restored.Registry.Revocations.IsRevoked(removedSerial), TimeSpan.FromSeconds(15)));
            Assert.Contains(restored.Events.Recent, e => e.Code == "revocation.learned");
            Assert.True(await Wait.UntilAsync(() => !restored.IsLinked(pc2.AgentId), TimeSpan.FromSeconds(10)));
            Assert.Contains(restored.Events.Recent, e => e.Code == "link.revoked" && e.AgentId == pc2.AgentId);
            Assert.True(await Wait.UntilAsync(() => restored.Events.Recent.Any(e =>
                e.Code == "link.refused" && e.Message.Contains("revoked", StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(20)));
            Assert.False(restored.IsLinked(pc2.AgentId));

            // The other linked PC learned it from the console too, without relinking.
            Assert.True(await Wait.UntilAsync(() => pc3.Store.Config.Revocations.Any(r =>
                LabCertificates.NormalizeSerial(r.Serial) == LabCertificates.NormalizeSerial(removedSerial)), TimeSpan.FromSeconds(10)));
            Assert.True(restored.IsLinked(pc1.AgentId));
            Assert.True(restored.IsLinked(pc3.AgentId));

            // Codes from the backup sleep (D-60): the one burned by PC-03 after the backup and
            // the one never used are both refused until the administrator activates them.
            Assert.Equal(2, restored.Enrollment.DormantCodeCount);
            var config = new AgentConfigDocument
            {
                LabId = restored.LabId,
                AgentId = Guid.NewGuid().ToString("d"),
                Number = 4,
                Hostname = "PC-04",
                Mac = "02:00:5E:00:00:04",
                EnrollmentCode = codes[2],
                ConsoleHost = IPAddress.Loopback.ToString(),
                ConsolePort = restored.Port,
            };
            var reuse = TestAgent.Open(DirectoryAgentStore.Install(Path.Combine(TestConsole.TempDirectory(), "PC-04"), config, restored.Authority));
            agents.Add(reuse);
            reuse.Start();
            Assert.True(await Wait.UntilAsync(() => restored.Events.Recent.Any(e => e.Code == "enroll.dormant_code" && e.AgentId == config.AgentId), TimeSpan.FromSeconds(15)));
            Assert.Null(reuse.Store.Certificate);
            Assert.DoesNotContain(restored.Registry.Document.Machines, m => m.Number == 4);
        }
        finally
        {
            foreach (var agent in agents)
            {
                await agent.DisposeAsync();
            }

            if (restored is not null)
            {
                await restored.DisposeAsync();
            }

            TestConsole.DeleteTempDirectory(device);
            TestConsole.DeleteTempDirectory(files);
        }
    }
}
