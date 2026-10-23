using System.Net;
using LabControl.Console.Services;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>The console's named exam presets (M6 portion 4, D-76): one example seeded once, names validated, carried by the backup and never by the lab file.</summary>
public sealed class ExamPresetLibraryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private static ExamPresetLibrary Open(LabStore store) =>
        new(store, "lab", () => Now, new InternetPresetLibrary(store, "lab", () => Now, TestLogging.Factory.CreateLogger<InternetPresetLibrary>()),
            TestLogging.Factory.CreateLogger<ExamPresetLibrary>());

    [Fact]
    public void The_example_is_created_on_the_first_open_only_with_the_java_docs_whitelist()
    {
        var directory = TestConsole.TempDirectory();
        var store = new LabStore(directory);
        store.EnsureDirectories();
        try
        {
            var first = Open(store);
            var seed = Assert.Single(first.Presets);
            Assert.Equal("Practical (IntelliJ IDEA)", seed.Name);
            Assert.Equal(80, seed.TimerMinutes);
            Assert.True(seed.RestrictPrograms);
            Assert.Equal(["idea64.exe", "fsnotifier.exe", "jcef_helper.exe", "java.exe", "javaw.exe", "cmd.exe", "msedge.exe"], seed.AllowedPrograms);
            Assert.Equal(ExamInternetModes.Whitelist, seed.InternetMode);
            Assert.Equal("Java docs", seed.InternetPresetName);
            Assert.Equal(["*.jetbrains.com", "docs.oracle.com"], seed.InternetHosts);
            Assert.True(File.Exists(store.ExamPresetsPath));

            Assert.True(first.Delete(seed.Id));
            Assert.Empty(Open(store).Presets);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void Save_normalizes_programs_refuses_a_taken_or_empty_name_and_updates_in_place()
    {
        var directory = TestConsole.TempDirectory();
        var store = new LabStore(directory);
        store.EnsureDirectories();
        try
        {
            var library = Open(store);
            var changes = 0;
            library.Changed += () => changes++;

            var written = library.Save(null, new ExamPreset { Name = "  Written test ", TimerMinutes = 45, RestrictPrograms = true, AllowedPrograms = ["WINWORD", "winword.exe", "bad|name"], InternetMode = ExamInternetModes.Blocked, InternetHosts = ["x.org"] });
            Assert.True(written.Ok);
            Assert.Equal("Written test", written.Saved!.Name);
            Assert.Equal(["winword.exe"], written.Saved.AllowedPrograms);
            Assert.Empty(written.Saved.InternetHosts);
            Assert.Equal(1, changes);

            Assert.Equal(InternetPresetLibrary.NameProblems.Taken, library.Save(null, new ExamPreset { Name = "written TEST" }).NameProblem);
            Assert.Equal(InternetPresetLibrary.NameProblems.Empty, library.Save(null, new ExamPreset { Name = "  " }).NameProblem);
            Assert.Equal(1, changes);

            var updated = library.Save(written.Saved.Id, new ExamPreset { Name = "Written test", TimerMinutes = 50 });
            Assert.True(updated.Ok);
            Assert.Equal(written.Saved.Id, updated.Saved!.Id);
            Assert.Equal(2, library.Presets.Count);
            Assert.Equal(50, Open(store).Presets.Single(p => p.Name == "Written test").TimerMinutes);
        }
        finally
        {
            TestConsole.DeleteTempDirectory(directory);
        }
    }

    /// <summary>A restored console gets the exam presets from the backup; a teacher device importing the lab file gets none of them (D-76).</summary>
    [Fact]
    public async Task The_backup_carries_the_presets_and_the_lab_file_does_not()
    {
        await using var room = await TestConsole.CreateLabAsync("Room 214");
        Assert.True(room.Session.ExamPresets.Save(null, new ExamPreset { Name = "Written test", TimerMinutes = 45 }).Ok);

        var files = TestConsole.TempDirectory();
        var labFile = Path.Combine(files, "room.lclab");
        var backup = Path.Combine(files, "room.lcbak");
        Assert.True(room.Bootstrap.TryExportLabFile(room.Session, labFile, out var error), error);
        Assert.True(room.Bootstrap.TryExportBackup(room.Session, backup, out error), error);
        Assert.DoesNotContain("Written test", File.ReadAllText(labFile), StringComparison.Ordinal);

        var teacherDevice = NewDevice(out var teacherDirectory);
        var adminDevice = NewDevice(out var adminDirectory);
        try
        {
            var teacherImports = new LabImports(teacherDevice, _ => Task.FromResult<BackupSecret?>(null), "Teacher laptop", TestLogging.Factory.CreateLogger("imports"));
            Assert.True((await teacherImports.ImportAsync([labFile])).Single().Ok);
            Assert.Null(teacherDevice.StoreFor(room.Session.LabId).LoadExamPresets());

            var adminImports = new LabImports(adminDevice, _ => Task.FromResult<BackupSecret?>(new BackupSecret(TestConsole.Passphrase, null)), "Windows desk PC", TestLogging.Factory.CreateLogger("imports"));
            Assert.True((await adminImports.ImportAsync([backup])).Single().Ok);
            var carried = adminDevice.StoreFor(room.Session.LabId).LoadExamPresets();
            Assert.NotNull(carried);
            Assert.Equal(room.Session.LabId, carried.LabId);
            Assert.Equal(["Practical (IntelliJ IDEA)", "Written test"], carried.Presets.Select(p => p.Name).Order(StringComparer.Ordinal));
        }
        finally
        {
            TestConsole.DeleteTempDirectory(teacherDirectory);
            TestConsole.DeleteTempDirectory(adminDirectory);
            TestConsole.DeleteTempDirectory(files);
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

    [Fact]
    public void Sites_with_no_browser_on_the_list_are_flagged()
    {
        Assert.True(LabControl.Console.ViewModels.ExamDialogModel.SitesWithoutBrowser(true, true, "idea64.exe\njava.exe"));
        Assert.False(LabControl.Console.ViewModels.ExamDialogModel.SitesWithoutBrowser(true, true, "idea64\nMSEDGE"));
        Assert.False(LabControl.Console.ViewModels.ExamDialogModel.SitesWithoutBrowser(false, true, "idea64.exe"));
        Assert.False(LabControl.Console.ViewModels.ExamDialogModel.SitesWithoutBrowser(true, false, "idea64.exe"));
    }
}
