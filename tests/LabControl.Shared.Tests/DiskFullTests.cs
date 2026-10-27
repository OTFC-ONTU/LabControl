using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LabControl.Shared.Files;
using LabControl.Shared.Identity;
using LabControl.Shared.Jobs;
using LabControl.Shared.Link;
using LabControl.Shared.Packages;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using LabControl.Tests;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// M7 portion 5, robustness: a PC's data or staging directory, or any document's, runs out of
/// space. Nothing may crash or leave a torn file where a good one was; the teacher gets a plain
/// failure; and everything works again once there is space. The <see cref="FullVolume"/> tests
/// fill a real volume (macOS RAM disk, or <c>LABCONTROL_FULL_DISK_VOLUME</c>); the rest provoke the
/// same exception with a directory squatting on the name a write needs.
/// </summary>
public sealed class DiskFullTests
{
    private static PackageStatusDocument Big(int entries)
    {
        var document = new PackageStatusDocument { LabId = "lab" };
        for (var i = 0; i < entries; i++)
        {
            document.Packages.Add(new PackageStatusEntry { Package = $"package-{i}-" + new string('x', 200) });
        }

        return document;
    }

    private static string[] Temporaries(string path) => JsonStore.TemporaryPathsFor(path).Where(File.Exists).ToArray();

    // ------------------------------------------------------------------ documents (JsonStore)

    /// <summary>
    /// A disk that fills up in the middle of a save used to keep the torn half as
    /// <c>&lt;document&gt;.tmp</c>: it held the last free space, and every later save went to a
    /// name of its own while the leftover stayed for good (robustness pass 2026-10-27).
    /// </summary>
    [Fact]
    public void A_document_saved_onto_a_full_disk_keeps_the_last_good_one_and_leaves_no_temporary()
    {
        using var volume = FullVolume.Create();
        var path = Path.Combine(volume.Root, "package-status.json");
        JsonStore.Save(path, Big(10), PackageStatusDocument.Migrations);
        var good = File.ReadAllBytes(path);

        volume.Fill(leaveBytes: 16 * 1024);
        Assert.Throws<IOException>(() => JsonStore.Save(path, Big(2000), PackageStatusDocument.Migrations));

        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Empty(Temporaries(path));
        Assert.Equal(10, JsonStore.Load<PackageStatusDocument>(path, PackageStatusDocument.Migrations).Packages.Count);

        volume.Free();
        JsonStore.Save(path, Big(2000), PackageStatusDocument.Migrations);
        Assert.Equal(2000, JsonStore.Load<PackageStatusDocument>(path, PackageStatusDocument.Migrations).Packages.Count);
        Assert.Empty(Temporaries(path));
    }

    [Fact]
    public void A_document_that_cannot_be_written_at_all_keeps_the_last_good_one()
    {
        var directory = Path.Combine(Path.GetTempPath(), "labcontrol-tests", Guid.NewGuid().ToString("n"));
        var path = Path.Combine(directory, "policy.json");
        JsonStore.Save(path, Big(3), PackageStatusDocument.Migrations);
        var good = File.ReadAllBytes(path);

        // Stands in for a disk that refuses every write.
        Directory.CreateDirectory(JsonStore.TemporaryPathFor(path));
        Assert.Throws<IOException>(() => JsonStore.Save(path, Big(5), PackageStatusDocument.Migrations));
        Assert.Equal(good, File.ReadAllBytes(path));

        Directory.Delete(JsonStore.TemporaryPathFor(path));
        JsonStore.Save(path, Big(5), PackageStatusDocument.Migrations);
        Assert.Equal(5, JsonStore.Load<PackageStatusDocument>(path, PackageStatusDocument.Migrations).Packages.Count);
        Directory.Delete(directory, recursive: true);
    }

    // ------------------------------------------------------------------ the agent's own store

    private static (DirectoryAgentStore Store, LabKey Lab) InstallAgent(string directory)
    {
        var lab = TestLab.Create();
        var config = new AgentConfigDocument
        {
            LabId = lab.LabId,
            AgentId = Guid.NewGuid().ToString("d"),
            Number = 7,
            Hostname = "PC-07",
            Mac = "02:00:5E:00:00:07",
        };
        var store = DirectoryAgentStore.Install(directory, config, lab.Authority);
        using var first = Issue(lab, store, store.Key);
        store.InstallCertificate(store.Key, first);
        return (store, lab);
    }

    private static X509Certificate2 Issue(LabKey lab, DirectoryAgentStore store, ECDsa key) =>
        LabCertificates.IssueAgentFromCsr(lab.Authority, lab.LabId, store.Config.AgentId, store.Config.Number,
            LabCertificates.CreateSigningRequest(key, "PC-07"), DateTimeOffset.UtcNow);

    private static void AssertOpensWithAMatchingPair(string directory, string serial)
    {
        using var reopened = DirectoryAgentStore.Open(directory);
        Assert.Equal(serial, LabCertificates.SerialOf(reopened.Certificate!));
        using var paired = reopened.Certificate!.CopyWithPrivateKey(reopened.Key);
        Assert.True(paired.HasPrivateKey);
        Assert.Empty(Directory.EnumerateFiles(directory, "*" + JsonStore.TemporarySuffix));
    }

    /// <summary>
    /// Renewal (D-25) writes a new key and a new certificate. Written in place one after the other,
    /// a full disk could leave a truncated <c>agent.key</c>, or the new key beside the old
    /// certificate — and the agent could not open its store at the next start: a USB visit.
    /// </summary>
    [Fact]
    public void A_renewal_that_cannot_be_written_keeps_the_pair_the_pc_had()
    {
        var directory = Path.Combine(Path.GetTempPath(), "labcontrol-tests", Guid.NewGuid().ToString("n"));
        var (store, lab) = InstallAgent(directory);
        using (lab)
        using (store)
        {
            var serial = LabCertificates.SerialOf(store.Certificate!);
            var keyBefore = File.ReadAllBytes(Path.Combine(directory, Defaults.AgentKeyFileName));

            // The key's temporary is written, the certificate's cannot be: nothing may be replaced.
            var blocked = Path.Combine(directory, Defaults.AgentCertificateFileName + JsonStore.TemporarySuffix);
            Directory.CreateDirectory(blocked);
            var newKey = LabCertificates.CreateKey();
            using (var renewed = Issue(lab, store, newKey))
            {
                Assert.ThrowsAny<Exception>(() => store.InstallCertificate(newKey, renewed));
            }

            Assert.Equal(keyBefore, File.ReadAllBytes(Path.Combine(directory, Defaults.AgentKeyFileName)));
            Assert.Equal(serial, LabCertificates.SerialOf(store.Certificate!));
            Assert.NotSame(newKey, store.Key);
            Directory.Delete(blocked);
            AssertOpensWithAMatchingPair(directory, serial);

            // Space back: the next renewal goes through.
            using var again = Issue(lab, store, newKey);
            store.InstallCertificate(newKey, again);
            AssertOpensWithAMatchingPair(directory, LabCertificates.SerialOf(again));
        }

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void A_renewal_on_a_full_disk_keeps_the_pair_the_pc_had()
    {
        using var volume = FullVolume.Create();
        var (store, lab) = InstallAgent(volume.Root);
        using (lab)
        using (store)
        {
            var serial = LabCertificates.SerialOf(store.Certificate!);
            volume.Fill();
            var newKey = LabCertificates.CreateKey();
            using (var renewed = Issue(lab, store, newKey))
            {
                Assert.Throws<IOException>(() => store.InstallCertificate(newKey, renewed));
            }

            AssertOpensWithAMatchingPair(volume.Root, serial);

            volume.Free();
            using var again = Issue(lab, store, newKey);
            store.InstallCertificate(newKey, again);
            AssertOpensWithAMatchingPair(volume.Root, LabCertificates.SerialOf(again));
        }
    }

    // ------------------------------------------------------------------ install_package staging

    private sealed class StagingHost(string staging) : IPackageHost
    {
        public bool ExamHeld => false;

        public string StagingDirectory => staging;

        public int Runs { get; private set; }

        public DetectFinding Detect(DetectRule rule) => new(Runs > 0, Runs > 0 ? "1.0" : null);

        public Task<InstallerExit> RunInstallerAsync(InstallPackageRequest request, string stagedPath, CancellationToken token)
        {
            Runs++;
            return Task.FromResult(new InstallerExit(0, false));
        }
    }

    [Fact]
    public async Task An_installer_that_does_not_fit_in_the_staging_directory_is_a_plain_not_run_and_leaves_nothing()
    {
        using var volume = FullVolume.Create();
        var staging = Path.Combine(volume.Root, "staging");
        Directory.CreateDirectory(staging);
        var host = new StagingHost(staging);
        var bytes = new byte[2 * 1024 * 1024];
        var entry = new PackageEntry
        {
            Id = "tool", Name = "Tool", Version = "1.0", Family = InstallerFamily.Msi, File = "tool.msi", Size = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Silent = "/qn", Detect = new DetectRule(DetectKind.UninstallName, "Tool"), TimeoutMinutes = 15,
        };
        var job = new Job { Id = "job-1", Kind = Job.Types.Kind.InstallPackage };
        job.Args.Add(InstallPackageRequest.For(entry, InstallMode.Install).ToArgs());
        async Task<long> Pull(string reference, string sha, Stream destination, CancellationToken token, Func<long, Task>? progress)
        {
            await destination.WriteAsync(bytes, token);
            return bytes.Length;
        }

        var runner = new PackageInstallRunner(host, Pull);
        volume.Fill(leaveBytes: 64 * 1024);
        var result = await runner.RunAsync(job, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(PackageOutcomes.NotRun, result.Outcome);
        Assert.Contains("tool.msi", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.Runs);
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));

        volume.Free();
        result = await runner.RunAsync(job, _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Equal(PackageOutcomes.Installed, result.Outcome);
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    // ------------------------------------------------------------------ handouts and collected work

    [Fact]
    public void A_handout_that_does_not_fit_keeps_the_students_existing_file_and_leaves_no_partial()
    {
        using var volume = FullVolume.Create();
        var materials = Path.Combine(volume.Root, "Materials");
        Directory.CreateDirectory(materials);
        var existing = Path.Combine(materials, "notes.pdf");
        File.WriteAllText(existing, "version one");

        var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        var request = new SendFileRequest("ref", Convert.ToHexStringLower(SHA256.HashData(bytes)), "notes.pdf", false);
        volume.Fill(leaveBytes: 64 * 1024);
        Assert.Throws<IOException>(() => HandoutDelivery.Deliver(new MemoryStream(bytes), materials, request, TestContext.Current.CancellationToken));

        Assert.Equal("version one", File.ReadAllText(existing));
        Assert.Equal(["notes.pdf"], Directory.EnumerateFiles(materials).Select(Path.GetFileName));

        volume.Free();
        Assert.Equal(bytes.Length, HandoutDelivery.Deliver(new MemoryStream(bytes), materials, request, TestContext.Current.CancellationToken));
        Assert.Equal(bytes, File.ReadAllBytes(existing));
    }

    [Fact]
    public void Collected_work_that_does_not_fit_in_the_staging_archive_fails_as_an_io_error()
    {
        using var volume = FullVolume.Create();
        var source = Path.Combine(Path.GetTempPath(), "labcontrol-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "Main.java"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
        var staged = Path.Combine(volume.Root, "work.collect.zip");

        volume.Fill(leaveBytes: 64 * 1024);
        // What CollectRunner and the simulator catch and turn into "collecting failed: …".
        Assert.ThrowsAny<IOException>(() =>
        {
            using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            WorkArchive.Create(source, output, Defaults.CollectMaxBytes, Defaults.CollectMaxFiles, TestContext.Current.CancellationToken);
            output.Flush();
        });

        volume.Free();
        File.Delete(staged);
        using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(1, WorkArchive.Create(source, output, Defaults.CollectMaxBytes, Defaults.CollectMaxFiles, TestContext.Current.CancellationToken).Files);
        }

        Directory.Delete(source, recursive: true);
    }

    // ------------------------------------------------------------------ policy.json

    [Fact]
    public void A_policy_journal_on_a_full_disk_keeps_the_last_good_document()
    {
        using var volume = FullVolume.Create();
        var path = Path.Combine(volume.Root, Defaults.PolicyFileName);
        var writer = Policy.PolicyJournal.FileWriter(path);
        writer(new PolicyDocument { LabId = "lab", SavedAtUnix = 1 });
        var good = File.ReadAllBytes(path);

        volume.Fill();
        Assert.Throws<IOException>(() => writer(new PolicyDocument { LabId = "lab", SavedAtUnix = 2 }));
        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Empty(Temporaries(path));

        volume.Free();
        writer(new PolicyDocument { LabId = "lab", SavedAtUnix = 3 });
        Assert.Equal(3, JsonStore.Load<PolicyDocument>(path, PolicyDocument.Migrations).SavedAtUnix);
    }
}
