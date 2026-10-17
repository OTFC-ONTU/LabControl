using System.Net;
using LabControl.Console.Services;
using LabControl.Shared.Internet;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>The console's named whitelist presets (M6 portion 3, D-73): seeded once, validated, saved on every change, carried by the backup and the lab file.</summary>
public sealed class InternetPresetLibraryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static InternetPresetLibrary Open(LabStore store, Func<DateTimeOffset>? clock = null) =>
        new(store, "lab", clock ?? (() => Now), TestLogging.Factory.CreateLogger<InternetPresetLibrary>());

    private static LabStore NewStore(out string directory)
    {
        directory = TestConsole.TempDirectory();
        var store = new LabStore(directory);
        store.EnsureDirectories();
        return store;
    }

    [Fact]
    public void The_seed_preset_is_created_on_the_first_open_only_and_not_brought_back_after_a_delete()
    {
        var store = NewStore(out var directory);
        try
        {
            var first = Open(store);
            var seed = Assert.Single(first.Presets);
            Assert.Equal("Java docs", seed.Name);
            Assert.Equal(["*.jetbrains.com", "docs.oracle.com"], seed.Hosts);
            Assert.Equal(Now.ToUnixTimeSeconds(), seed.CreatedAtUnix);
            Assert.True(File.Exists(store.InternetPresetsPath));
            var document = store.LoadInternetPresets()!;
            Assert.Equal("lab", document.LabId);
            Assert.Equal(Now.ToUnixTimeSeconds(), document.SeedImportedAtUnix);

            Assert.True(first.Delete(seed.Id));
            Assert.Empty(first.Presets);

            var second = Open(store);
            Assert.Empty(second.Presets);
            Assert.Empty(store.LoadInternetPresets()!.Presets);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void Save_adds_updates_and_renames_and_everything_survives_a_reopen()
    {
        var store = NewStore(out var directory);
        try
        {
            var time = Now;
            var library = Open(store, () => time);
            var changes = 0;
            library.Changed += () => changes++;

            time = Now.AddMinutes(1);
            var added = library.Save(null, "  Python  ", ["https://docs.python.org/3/library/", "", "   ", "DOCS.python.org", "*.pypi.org"]);
            Assert.True(added.Ok);
            Assert.Null(added.NameProblem);
            Assert.Empty(added.HostProblems);
            var python = added.Saved!;
            Assert.Equal("Python", python.Name);
            Assert.Equal(["docs.python.org", "*.pypi.org"], python.Hosts);
            Assert.Equal(32, python.Id.Length);
            Assert.Equal(time.ToUnixTimeSeconds(), python.CreatedAtUnix);
            Assert.Equal(1, changes);

            // Ordered by name ignoring case; the caller's copy is not the stored one.
            Assert.Equal(["Java docs", "Python"], library.Presets.Select(p => p.Name));
            library.Presets[1].Hosts.Clear();
            library.Find(python.Id)!.Hosts.Clear();
            Assert.Equal(2, library.Find(python.Id)!.Hosts.Count);
            Assert.Null(library.Find("nope"));

            // Update: same id, new name and hosts, creation time kept.
            time = Now.AddMinutes(2);
            var renamed = library.Save(python.Id, "python 3", ["docs.python.org"]);
            Assert.True(renamed.Ok);
            Assert.Equal(python.Id, renamed.Saved!.Id);
            Assert.Equal(python.CreatedAtUnix, renamed.Saved.CreatedAtUnix);
            Assert.Equal(time.ToUnixTimeSeconds(), renamed.Saved.ModifiedAtUnix);
            Assert.Equal(2, changes);

            // Keeping its own name with a different case is not a conflict.
            Assert.True(library.Save(python.Id, "Python 3", ["docs.python.org"]).Ok);
            Assert.Equal(3, changes);

            var reopened = Open(store);
            Assert.Equal(["Java docs", "Python 3"], reopened.Presets.Select(p => p.Name));
            var stored = reopened.Find(python.Id)!;
            Assert.Equal(["docs.python.org"], stored.Hosts);
            Assert.Equal(python.CreatedAtUnix, stored.CreatedAtUnix);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void A_bad_name_or_a_bad_host_saves_nothing_and_says_why()
    {
        var store = NewStore(out var directory);
        try
        {
            var library = Open(store);
            var changes = 0;
            library.Changed += () => changes++;
            var before = File.ReadAllText(store.InternetPresetsPath);

            var empty = library.Save(null, "   ", ["docs.python.org"]);
            Assert.False(empty.Ok);
            Assert.Equal(InternetPresetLibrary.NameProblems.Empty, empty.NameProblem);

            var taken = library.Save(null, "JAVA DOCS", ["docs.python.org"]);
            Assert.False(taken.Ok);
            Assert.Equal(InternetPresetLibrary.NameProblems.Taken, taken.NameProblem);

            var python = library.Save(null, "Python", ["docs.python.org"]).Saved!;
            changes = 0;
            before = File.ReadAllText(store.InternetPresetsPath);
            var renameConflict = library.Save(python.Id, "Java docs", ["docs.python.org"]);
            Assert.Equal(InternetPresetLibrary.NameProblems.Taken, renameConflict.NameProblem);

            var hosts = library.Save(null, "Mixed", ["docs.python.org", "10.0.0.1", "", "*.com", "docs.*.org"]);
            Assert.False(hosts.Ok);
            Assert.Null(hosts.NameProblem);
            Assert.Equal(
                [("10.0.0.1", HostPattern.Problems.IpLiteral), ("*.com", HostPattern.Problems.WildcardTooBroad), ("docs.*.org", HostPattern.Problems.WildcardMisplaced)],
                hosts.HostProblems);

            Assert.Equal(0, changes);
            Assert.Equal(before, File.ReadAllText(store.InternetPresetsPath));
            Assert.Equal(["Java docs", "Python"], library.Presets.Select(p => p.Name));
            Assert.False(library.Delete("nope"));
            Assert.Equal(0, changes);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    /// <summary>A second console gets the presets from the lab file, a restored one from the backup (D-73, like D-31 item 4).</summary>
    [Fact]
    public async Task The_backup_and_the_lab_file_carry_the_presets_to_another_device()
    {
        await using var room = await TestConsole.CreateLabAsync("Room 214");
        var library = new InternetPresetLibrary(room.Session.Store, room.Session.LabId, () => Now, TestLogging.Factory.CreateLogger<InternetPresetLibrary>());
        Assert.True(library.Save(null, "Python", ["docs.python.org"]).Ok);

        var files = TestConsole.TempDirectory();
        var labFile = Path.Combine(files, "room.lclab");
        var backup = Path.Combine(files, "room.lcbak");
        Assert.True(room.Bootstrap.TryExportLabFile(room.Session, labFile, out var error), error);
        Assert.True(room.Bootstrap.TryExportBackup(room.Session, backup, out error), error);

        var teacherDevice = NewDevice(out var teacherDirectory);
        var adminDevice = NewDevice(out var adminDirectory);
        try
        {
            var teacherImports = new LabImports(teacherDevice, _ => Task.FromResult<BackupSecret?>(null), "Teacher laptop", TestLogging.Factory.CreateLogger("imports"));
            var fromLabFile = (await teacherImports.ImportAsync([labFile])).Single();
            Assert.True(fromLabFile.Ok, fromLabFile.Message);
            AssertCarried(teacherDevice.StoreFor(room.Session.LabId), room.Session.LabId);

            var adminImports = new LabImports(adminDevice, _ => Task.FromResult<BackupSecret?>(new BackupSecret(TestConsole.Passphrase, null)), "Windows desk PC", TestLogging.Factory.CreateLogger("imports"));
            var fromBackup = (await adminImports.ImportAsync([backup])).Single();
            Assert.True(fromBackup.Ok, fromBackup.Message);
            AssertCarried(adminDevice.StoreFor(room.Session.LabId), room.Session.LabId);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(teacherDirectory);
            TestConsole.DeleteTempDirectory(adminDirectory);
            TestConsole.DeleteTempDirectory(files);
        }

        static void AssertCarried(LabStore store, string labId)
        {
            var document = store.LoadInternetPresets();
            Assert.NotNull(document);
            Assert.Equal(labId, document.LabId);
            Assert.Equal(["Java docs", "Python"], document.Presets.Select(p => p.Name).Order(StringComparer.Ordinal));

            // The library opens the imported document as it is: no second seed preset.
            var reopened = new InternetPresetLibrary(store, labId, () => Now, TestLogging.Factory.CreateLogger<InternetPresetLibrary>());
            Assert.Equal(["Java docs", "Python"], reopened.Presets.Select(p => p.Name));
        }
    }

    private static ConsoleBootstrap NewDevice(out string dataDirectory)
    {
        dataDirectory = TestConsole.TempDirectory();
        return new ConsoleBootstrap(new ConsoleOptions
        {
            DataDirectory = dataDirectory,
            Port = 0,
            BeaconPort = TestConsole.BeaconPort,
            BindAddress = IPAddress.Loopback,
        }, TestLogging.Factory, () => new FileSecretProtector());
    }
}
