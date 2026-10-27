using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Link;
using LabControl.Shared.Packages;
using LabControl.Shared.Persistence;
using LabControl.Tests;
using Microsoft.Extensions.Logging;
using Xunit;
using Strings = LabControl.Console.Localization.Strings;

namespace LabControl.Console.Tests;

/// <summary>
/// M7 portion 5, robustness: the console's data directory runs out of space. A save the teacher
/// asks for must answer in words — a command that throws takes the whole console down, there is
/// no global handler — must leave what is on disk as it was, and must work again once there is
/// space. A directory squatting on the temporary a save needs stands in for the full disk here;
/// <see cref="FullVolume"/> fills a real one where the copy itself is what does not fit.
/// </summary>
public sealed class DiskFullTests
{
    private static readonly string SaveFailedPrefix = Strings.Format("Store.SaveFailed", string.Empty);

    /// <summary>Makes every <see cref="JsonStore.Save{T}"/> of <paramref name="path"/> fail until disposed.</summary>
    private static Blocked Block(string path) => new(JsonStore.TemporaryPathFor(path));

    private sealed class Blocked : IDisposable
    {
        private readonly string _path;

        public Blocked(string path)
        {
            _path = path;
            Directory.CreateDirectory(path);
        }

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }

    [Fact]
    public async Task A_script_that_cannot_be_saved_is_refused_in_words_and_the_library_stays_as_on_disk()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var library = console.Session.Scripts;
        var path = console.Session.Store.ScriptsPath;
        var kept = library.NewScript();
        kept.Text = "Write-Output kept";
        Assert.True(library.TrySave(kept, out _));
        var onDisk = File.ReadAllBytes(path);
        var before = library.Scripts.Count;
        var first = library.Scripts[0];

        using (Block(path))
        {
            var script = library.NewScript();
            script.Text = "Write-Output 1";
            Assert.False(library.TrySave(script, out var error));
            Assert.StartsWith(SaveFailedPrefix, error, StringComparison.Ordinal);
            Assert.Equal(before, library.Scripts.Count);

            var edited = first.Clone();
            edited.Text = "changed";
            Assert.False(library.TrySave(edited, out _));
            Assert.Equal(first.Text, library.Find(first.Id)!.Text);

            Assert.Throws<IOException>(() => library.Remove(first.Id));
            Assert.NotNull(library.Find(first.Id));
            Assert.Equal(onDisk, File.ReadAllBytes(path));
        }

        // Space is back: the same save goes through.
        var again = library.NewScript();
        Assert.True(library.TrySave(again, out var none), none);
        Assert.Equal(before + 1, library.Scripts.Count);
        Assert.Contains(again.Id, File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preset_that_cannot_be_saved_or_deleted_says_so_in_the_dialog()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var internet = new InternetDialogModel(console.Session, 4);
        var exam = new ExamDialogModel(console.Session, 4, wholeLab: false);
        var seeded = internet.Presets.Single();
        var examSeeded = exam.Presets.Count;
        var examRequest = new ExamRequest("Test", TimeSpan.FromMinutes(40), false, [], LabControl.Shared.Protocol.InternetPolicy.Types.Mode.Open, [], string.Empty, "Practical");

        using (Block(console.Session.Store.InternetPresetsPath))
        using (Block(console.Session.Store.ExamPresetsPath))
        {
            var (saved, problem) = internet.SavePreset(null, "Python docs", ["docs.python.org"]);
            Assert.Null(saved);
            Assert.StartsWith(SaveFailedPrefix, problem, StringComparison.Ordinal);
            Assert.Single(internet.Presets);

            (saved, problem) = internet.SavePreset(seeded.Id, "Renamed", ["docs.oracle.com"]);
            Assert.Null(saved);
            Assert.Equal(seeded.Name, internet.Presets.Single().Name);

            Assert.False(internet.DeletePreset(seeded.Id, out problem));
            Assert.StartsWith(SaveFailedPrefix, problem, StringComparison.Ordinal);
            Assert.Single(internet.Presets);

            var (examSaved, examProblem) = exam.SavePreset(null, ExamDialogModel.ToPreset("Test 3", examRequest));
            Assert.Null(examSaved);
            Assert.StartsWith(SaveFailedPrefix, examProblem, StringComparison.Ordinal);
            Assert.Equal(examSeeded, exam.Presets.Count);
        }

        Assert.NotNull(internet.SavePreset(null, "Python docs", ["docs.python.org"]).Saved);
        Assert.Equal(2, internet.Presets.Count);
        var stored = exam.SavePreset(null, ExamDialogModel.ToPreset("Test 3", examRequest)).Saved;
        Assert.NotNull(stored);
        using (Block(console.Session.Store.ExamPresetsPath))
        {
            Assert.False(exam.DeletePreset(stored!.Id, out var problem));
            Assert.StartsWith(SaveFailedPrefix, problem, StringComparison.Ordinal);
            Assert.Equal(examSeeded + 1, exam.Presets.Count);
        }

        Assert.True(exam.DeletePreset(stored.Id, out _));
        Assert.Equal(2, JsonStore.Load<InternetPresetsDocument>(console.Session.Store.InternetPresetsPath, InternetPresetsDocument.Migrations).Presets.Count);
    }

    /// <summary>The <i>Packages</i> tab's <i>Save</i> threw out of its command on a full disk: the console closed.</summary>
    [Fact]
    public async Task A_package_edit_that_cannot_be_saved_is_an_error_line_not_a_crash()
    {
        await using var console = await TestConsole.CreateLabAsync("MacBook");
        var dialogs = new PackageCatalogTests.WizardDialogs(PackageCatalogTests.NsisInstaller("tool.exe", "full"), "Tool");
        var tab = new PackagesViewModel(console.Session, dialogs, action => action(), () => []);
        await tab.AddCommand.ExecuteAsync(null);
        var entry = console.Session.Packages.List().Single().Entry!;
        var yaml = console.Session.Packages.YamlPath(entry.Id);
        var onDisk = File.ReadAllText(yaml);

        tab.Version = "9.9";
        using (new Blocked(yaml + ".tmp"))
        {
            tab.SaveCommand.Execute(null);
            Assert.StartsWith(SaveFailedPrefix, tab.Error, StringComparison.Ordinal);
            Assert.Equal(onDisk, File.ReadAllText(yaml));
        }

        tab.SaveCommand.Execute(null);
        Assert.Equal(string.Empty, tab.Error);
        Assert.Equal("9.9", console.Session.Packages.Find(entry.Id)!.Version);
        tab.Detach();
    }

    /// <summary>
    /// The installer was copied, then the entry could not be written: the copy stayed in the
    /// catalog's folder with nothing naming it — hundreds of megabytes on a disk that is full.
    /// </summary>
    [Fact]
    public async Task A_package_whose_entry_cannot_be_written_leaves_no_installer_behind_and_a_replaced_one_intact()
    {
        await using var console = await TestConsole.CreateLabAsync("MacBook");
        var catalog = console.Session.Packages;
        var root = console.Session.Store.PackagesDirectory;
        var id = PackageEntry.IdFor("Tool");

        using (new Blocked(Path.Combine(root, id + ".yaml.tmp")))
        {
            var model = new AddPackageModel(catalog);
            Assert.Null(await model.InspectAsync(PackageCatalogTests.NsisInstaller("tool.exe", "one"), null, TestContext.Current.CancellationToken));
            model.Name = "Tool";
            var (entry, problem) = await model.AddAsync(null, TestContext.Current.CancellationToken);
            Assert.Null(entry);
            Assert.NotNull(problem);
        }

        Assert.Empty(catalog.List());
        Assert.False(Directory.Exists(Path.Combine(root, id)));

        var old = await PackageCatalogTests.AddAsync(catalog, PackageCatalogTests.NsisInstaller("tool.exe", "one"), "Tool");
        using (new Blocked(Path.Combine(root, id + ".yaml.tmp")))
        {
            var model = new AddPackageModel(catalog);
            Assert.Null(await model.InspectAsync(PackageCatalogTests.NsisInstaller("tool.exe", "two"), null, TestContext.Current.CancellationToken));
            model.Name = "Tool";
            Assert.NotNull((await model.AddAsync(null, TestContext.Current.CancellationToken)).Problem);
        }

        var only = Assert.Single(catalog.List());
        Assert.Equal(old.Sha256, only.Entry!.Sha256);
        Assert.Equal(InstallerState.Present, only.State);
        Assert.Equal([Path.GetDirectoryName(catalog.InstallerPath(old))], Directory.EnumerateDirectories(Path.Combine(root, id)));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    /// <summary>The copy into the catalog is what does not fit: a plain refusal, the partial removed, the old entry kept.</summary>
    [Fact]
    public async Task An_installer_too_big_for_the_disk_is_refused_and_the_catalog_is_unchanged()
    {
        using var volume = FullVolume.Create(megabytes: 8);
        var store = new LabStore(Path.Combine(volume.Root, "lab"));
        store.EnsureDirectories();
        var catalog = new PackageCatalog(store, "MacBook", () => DateTimeOffset.UtcNow, TestLogging.Factory.CreateLogger<PackageCatalog>());
        var old = await PackageCatalogTests.AddAsync(catalog, PackageCatalogTests.NsisInstaller("tool.exe", "one"), "Tool");
        var events = new EventLog(store.LogsDirectory);

        volume.Fill(leaveBytes: 64 * 1024);
        var model = new AddPackageModel(catalog);
        Assert.Null(await model.InspectAsync(PackageCatalogTests.NsisInstaller("tool.exe", "two"), null, TestContext.Current.CancellationToken));
        model.Name = "Tool";
        var (entry, problem) = await model.AddAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(entry);
        Assert.StartsWith(Strings.Format("Packages.AddFailed", string.Empty), problem, StringComparison.Ordinal);

        var only = Assert.Single(catalog.List());
        Assert.Equal(old.Sha256, only.Entry!.Sha256);
        Assert.Equal(InstallerState.Present, only.State);
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(store.PackagesDirectory, old.Id)));
        Assert.DoesNotContain(Directory.EnumerateFiles(store.PackagesDirectory, "*", SearchOption.AllDirectories),
            f => f.EndsWith(".partial", StringComparison.Ordinal) || f.EndsWith(".tmp", StringComparison.Ordinal));

        // The event panel and the job journal keep going on a full disk; only their files miss the lines.
        Assert.Equal("disk.full", events.Warning("disk.full", "nothing fits").Code);
        Assert.Single(events.Recent);

        volume.Free();
        Assert.Null((await model.AddAsync(null, TestContext.Current.CancellationToken)).Problem);
        Assert.NotEqual(old.Sha256, Assert.Single(catalog.List()).Entry!.Sha256);
    }

    /// <summary>
    /// A PC that cannot keep its renewed certificate (its disk is full) used to end renewal
    /// silently until the next link. It now says so, keeps linking with the pair it has, and
    /// asks again — and renews once there is space.
    /// </summary>
    [Fact]
    public async Task A_renewal_the_pc_cannot_save_is_reported_and_retried()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.InstallEnrolled(console, 9, TimeSpan.FromDays(30));
        var first = LabCertificates.SerialOf(pc.Store.Certificate!);

        var blocked = new Blocked(Path.Combine(pc.Store.Directory, Defaults.AgentCertificateFileName + JsonStore.TemporarySuffix));
        pc.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.Events.Recent.Any(e => e.Code == "renew.not_saved" && e.AgentId == pc.AgentId), TimeSpan.FromSeconds(20)));
        Assert.Equal(first, LabCertificates.SerialOf(pc.Store.Certificate!));
        Assert.Equal(LinkState.Linked, pc.Link.State);

        blocked.Dispose();
        Assert.True(await Wait.UntilAsync(() => LabCertificates.SerialOf(pc.Store.Certificate!) != first, TimeSpan.FromSeconds(30)));
        var renewed = LabCertificates.SerialOf(pc.Store.Certificate!);
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId)?.CertificateSerial == renewed, TimeSpan.FromSeconds(20)));
    }
}
