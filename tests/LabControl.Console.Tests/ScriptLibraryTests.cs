using Xunit;

using LabControl.Console.Services;
using LabControl.Shared.Jobs;
using LabControl.Shared.Persistence;

namespace LabControl.Console.Tests;

/// <summary>The console's script library (M4 portion 1, D-31 item 4, D-38): seeded once, saved on every change, restored from a backup.</summary>
public sealed class ScriptLibraryTests
{
    private static readonly IReadOnlyList<SeedScript> Seed =
    [
        new("pc-info.ps1", "# Prints the PC's name.\nhostname\n"),
        new("clear-temp.cmd", "@echo off\nrem Empties temp.\nrem timeout: 300\necho done\n"),
    ];

    [Fact]
    public void The_seed_is_imported_on_the_first_run_and_never_again()
    {
        var directory = TestConsole.TempDirectory();
        var store = new LabStore(directory);
        store.EnsureDirectories();

        var first = new ScriptLibrary(store, "lab", Seed, () => DateTimeOffset.UtcNow, TestLogging.Factory.CreateLogger("t"));
        Assert.Equal(["clear-temp", "pc-info"], first.Scripts.Select(s => s.Name));
        Assert.True(File.Exists(store.ScriptsPath));

        // The teacher deletes one and edits the other; a later run must not bring the seed back.
        Assert.True(first.Remove(first.Scripts.Single(s => s.Name == "clear-temp").Id));
        var edited = first.Scripts.Single();
        edited.Text = "hostname\nipconfig\n";
        Assert.True(first.TrySave(edited, out var error), error);

        var second = new ScriptLibrary(store, "lab", Seed, () => DateTimeOffset.UtcNow, TestLogging.Factory.CreateLogger("t"));
        var only = Assert.Single(second.Scripts);
        Assert.Equal("pc-info", only.Name);
        Assert.Equal("hostname\nipconfig\n", only.Text);
        Assert.Equal("Prints the PC's name.", only.Description);
    }

    [Fact]
    public void Saving_validates_the_name_the_timeout_and_uniqueness()
    {
        var store = new LabStore(TestConsole.TempDirectory());
        store.EnsureDirectories();
        var library = new ScriptLibrary(store, "lab", Seed, () => DateTimeOffset.UtcNow, TestLogging.Factory.CreateLogger("t"));
        var changes = 0;
        library.Changed += () => changes++;

        var fresh = library.NewScript();
        Assert.Equal("new-script", fresh.Name);
        Assert.Null(library.Find(fresh.Id));

        fresh.Name = " ";
        Assert.False(library.TrySave(fresh, out var error));
        Assert.Contains("name", error, StringComparison.OrdinalIgnoreCase);

        fresh.Name = "PC-INFO";
        Assert.False(library.TrySave(fresh, out error));
        Assert.Contains("already", error, StringComparison.OrdinalIgnoreCase);

        fresh.Name = "reboot-later";
        fresh.TimeoutSeconds = 0;
        Assert.False(library.TrySave(fresh, out error));
        Assert.Contains("timeout", error, StringComparison.OrdinalIgnoreCase);

        fresh.TimeoutSeconds = 30;
        Assert.True(library.TrySave(fresh, out error), error);
        Assert.Equal(1, changes);
        Assert.Equal(3, library.Scripts.Count);
        Assert.NotNull(library.Find(fresh.Id));

        // A second blank script gets the next free name.
        Assert.True(library.TrySave(library.NewScript(), out error), error);
        Assert.Equal("new-script-2", library.NewScript().Name);
    }

    [Fact]
    public async Task The_library_travels_in_the_backup_and_the_seed_is_not_re_imported_over_it()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var script = console.Session.Scripts.NewScript();
        script.Name = "from-the-macbook";
        script.Text = "hostname\n";
        Assert.True(console.Session.Scripts.TrySave(script, out var error), error);

        var bootstrap = new ConsoleBootstrap(console.Session.Options, TestLogging.Factory);
        var backupPath = Path.Combine(TestConsole.TempDirectory(), "lab.lcbak");
        Assert.True(bootstrap.TryExportBackup(console.Session, backupPath, out error), error);

        var other = new ConsoleBootstrap(new ConsoleOptions { DataDirectory = TestConsole.TempDirectory(), Port = 0, BindAddress = System.Net.IPAddress.Loopback }, TestLogging.Factory);
        var imported = other.ImportBackup(ConsoleBootstrap.ReadBackup(backupPath), TestConsole.Passphrase, null, "Windows desk PC");
        await using (imported)
        {
            var restored = Assert.Single(imported.Scripts.Scripts);
            Assert.Equal("from-the-macbook", restored.Name);
            Assert.Equal("hostname\n", restored.Text);
        }
    }
}
