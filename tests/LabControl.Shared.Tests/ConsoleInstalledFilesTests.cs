using System.IO.Compression;
using System.Text;
using LabControl.Shared;
using LabControl.Shared.Packaging;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// What the per-user console installer does to files (M5 portion 7, D-59 item 1), against a
/// real temporary directory. The install root is a parameter, so the whole of it — the
/// manifest, the upgrade, the removal and the temporary copy's job — runs on the Mac exactly
/// as it runs on a teacher's Windows PC.
///
/// The manifest lives in a directory its own user can edit, so most of what is asserted here
/// is what the installer refuses to do with it.
/// </summary>
public class ConsoleInstalledFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "labcontrol-install-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private string Install => Path.Combine(_root, "Console");

    private ConsoleInstalledFiles Files() => new(Install);

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(Install, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void Manifest(params string[] lines)
    {
        Directory.CreateDirectory(Install);
        File.WriteAllLines(Path.Combine(Install, Defaults.ConsoleInstallManifestFileName), lines);
    }

    // ------------------------------------------------------------------ hostile manifest lines

    public static TheoryData<string> HostileLines() =>
    [
        @"C:\Users\teacher\Documents\thesis.docx",
        @"C:/Users/teacher/Documents/thesis.docx",
        @"\\fileserver\share\marks.xlsx",
        @"..\..\..\Documents\thesis.docx",
        "../../outside.txt",
        @"sub\..\..\outside.txt",
        "/etc/hosts",
    ];

    [Theory]
    [MemberData(nameof(HostileLines))]
    public void A_manifest_line_that_points_outside_the_install_directory_is_refused(string line)
    {
        var outside = Path.Combine(_root, "outside.txt");
        Directory.CreateDirectory(_root);
        File.WriteAllText(outside, "a teacher's file");
        File.SetAttributes(outside, FileAttributes.ReadOnly);
        Write(Defaults.ConsoleExecutableName);
        Manifest(Defaults.ConsoleExecutableName, line);

        var removal = Files().RemoveAll();

        Assert.True(File.Exists(outside), "a file outside the install directory was deleted");
        Assert.Equal(FileAttributes.ReadOnly, File.GetAttributes(outside) & FileAttributes.ReadOnly);
        Assert.Equal([line], removal.Refused.Select(entry => entry.Line));
        Assert.NotEmpty(removal.Refused[0].Reason);
        // The rest of the manifest is still carried out: one bad line does not save the app.
        Assert.Equal([Defaults.ConsoleExecutableName], removal.Removed);
    }

    [Fact]
    public void A_refused_line_names_itself_and_why()
    {
        Assert.Null(ConsoleInstalledFiles.RefuseReason(Path.Combine("runtimes", "win-x64", "native.dll")));
        Assert.Contains("absolute", ConsoleInstalledFiles.RefuseReason("/etc/hosts")!, StringComparison.Ordinal);
        Assert.Contains("drive", ConsoleInstalledFiles.RefuseReason(@"D:\payload.exe")!, StringComparison.Ordinal);
        Assert.Contains("climbs out", ConsoleInstalledFiles.RefuseReason(@"..\thesis.docx")!, StringComparison.Ordinal);
        Assert.Contains("empty", ConsoleInstalledFiles.RefuseReason("   ")!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ a missing manifest

    [Fact]
    public void Uninstall_without_a_manifest_still_removes_both_program_files()
    {
        // The Installed-apps entry is deleted before the files are, so an uninstall that says
        // "nothing was owned here" would leave a machine with both executables and no way back.
        Write(Defaults.ConsoleExecutableName);
        Write(Defaults.ConsoleSetupExecutableName);

        var removal = Files().RemoveAll();

        Assert.True(removal.FromFallback);
        Assert.Equal(
            [Defaults.ConsoleExecutableName, Defaults.ConsoleSetupExecutableName],
            removal.Removed.Order(StringComparer.Ordinal));
        Assert.True(removal.DirectoryRemoved);
        Assert.False(Directory.Exists(Install));
    }

    [Fact]
    public void An_empty_manifest_is_treated_as_no_manifest()
    {
        Write(Defaults.ConsoleExecutableName);
        Manifest("", "   ", "");

        var removal = Files().RemoveAll();

        Assert.True(removal.FromFallback);
        Assert.Equal([Defaults.ConsoleExecutableName], removal.Removed);
    }

    [Fact]
    public void A_manifest_of_nothing_but_refused_lines_falls_back_rather_than_doing_nothing()
    {
        Write(Defaults.ConsoleExecutableName);
        Manifest(@"..\..\thesis.docx", @"C:\Windows\System32\kernel32.dll");

        var removal = Files().RemoveAll();

        Assert.True(removal.FromFallback);
        Assert.Equal(2, removal.Refused.Count);
        Assert.Equal([Defaults.ConsoleExecutableName], removal.Removed);
    }

    // ------------------------------------------------------------------ removal

    [Fact]
    public void Nothing_that_is_not_owned_is_ever_removed()
    {
        Write(Defaults.ConsoleExecutableName);
        var mine = Write(Path.Combine("logs", "teacher-notes.txt"), "not the installer's");
        Manifest(Defaults.ConsoleExecutableName);

        var removal = Files().RemoveAll();

        Assert.True(File.Exists(mine));
        Assert.False(removal.DirectoryRemoved);
        Assert.True(Directory.Exists(Install));
        Assert.Equal([Defaults.ConsoleExecutableName], removal.Removed);
    }

    [Fact]
    public void A_read_only_owned_file_is_still_removed()
    {
        var path = Write(Defaults.ConsoleExecutableName);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Manifest(Defaults.ConsoleExecutableName);

        Files().RemoveAll();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void The_running_executable_is_kept_and_so_is_the_manifest_the_temporary_copy_needs()
    {
        Write(Defaults.ConsoleExecutableName);
        var self = Write(Defaults.ConsoleSetupExecutableName);
        Manifest(Defaults.ConsoleExecutableName, Defaults.ConsoleSetupExecutableName);

        var first = Files().RemoveAll(self);

        Assert.Equal([Defaults.ConsoleSetupExecutableName], first.Kept);
        Assert.True(File.Exists(self));
        Assert.True(File.Exists(Path.Combine(Install, Defaults.ConsoleInstallManifestFileName)),
            "the temporary copy still needs the manifest");
        Assert.False(first.DirectoryRemoved);

        // The temporary copy, once the uninstaller has exited.
        var second = Files().RemoveAll();

        Assert.Equal([Defaults.ConsoleSetupExecutableName], second.Removed);
        Assert.True(second.DirectoryRemoved);
        Assert.False(Directory.Exists(Install));
    }

    [Fact]
    public void Empty_directories_the_removal_left_behind_go_with_it()
    {
        Write(Path.Combine("runtimes", "win-x64", "native", "libSkiaSharp.dll"));
        Manifest(Path.Combine("runtimes", "win-x64", "native", "libSkiaSharp.dll"));

        Assert.True(Files().RemoveAll().DirectoryRemoved);
        Assert.False(Directory.Exists(Path.Combine(Install, "runtimes")));
    }

    [Fact]
    public void Removing_a_directory_that_was_never_there_is_success()
    {
        var removal = Files().RemoveAll();

        Assert.True(removal.DirectoryRemoved);
        Assert.Empty(removal.Removed);
        Assert.Empty(removal.Refused);
    }

    // ------------------------------------------------------------------ the upgrade

    [Fact]
    public void An_upgrade_removes_what_the_new_payload_no_longer_carries_and_nothing_else()
    {
        var gone = Write("OldDependency.dll");
        var stays = Write(Defaults.ConsoleExecutableName);
        var foreign = Write("teacher-put-this-here.txt");
        var files = Files();
        files.WriteManifest(["OldDependency.dll", Defaults.ConsoleExecutableName]);

        var previous = files.ReadManifest();
        Assert.False(previous.FromFallback);
        var stale = previous.Owned.Except([Defaults.ConsoleExecutableName], StringComparer.OrdinalIgnoreCase);
        var pruned = files.Prune(stale);

        Assert.Equal(["OldDependency.dll"], pruned.Removed);
        Assert.False(File.Exists(gone));
        Assert.True(File.Exists(stays));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void The_manifest_is_written_sorted_deduplicated_and_free_of_lines_it_would_refuse()
    {
        var files = Files();

        files.WriteManifest([Defaults.ConsoleExecutableName, "b.dll", "b.dll", @"..\escape.exe", "a.dll"]);

        Assert.Equal(
            ["a.dll", "b.dll", Defaults.ConsoleExecutableName],
            File.ReadAllLines(files.ManifestPath));
    }

    // ------------------------------------------------------------------ the payload

    [Fact]
    public void A_payload_entry_that_escapes_the_install_directory_is_refused_and_writes_nothing()
    {
        var escape = Path.Combine(_root, "escaped.txt");
        using var archive = Archive(
            ("LabControl.Console.exe", "the console"),
            ("../escaped.txt", "not on this machine"));

        var error = Assert.Throws<IOException>(() => Files().ExtractArchive(archive));

        Assert.Contains("outside the install directory", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(escape), "a zip entry wrote outside the install directory");
    }

    [Fact]
    public void A_payload_is_written_once_and_a_second_run_writes_nothing()
    {
        var files = Files();
        using (var archive = Archive(
            ("LabControl.Console.exe", "the console"),
            ("runtimes/win-x64/native/libSkiaSharp.dll", "native")))
        {
            Assert.Equal(2, files.ExtractArchive(archive));
        }

        Assert.Equal("the console", File.ReadAllText(Path.Combine(Install, "LabControl.Console.exe")));

        using (var again = Archive(
            ("LabControl.Console.exe", "the console"),
            ("runtimes/win-x64/native/libSkiaSharp.dll", "native")))
        {
            Assert.Equal(0, files.ExtractArchive(again));
        }

        using (var changed = Archive(
            ("LabControl.Console.exe", "a new console"),
            ("runtimes/win-x64/native/libSkiaSharp.dll", "native")))
        {
            Assert.Equal(1, files.ExtractArchive(changed));
        }

        Assert.Equal("a new console", File.ReadAllText(Path.Combine(Install, "LabControl.Console.exe")));
    }

    [Fact]
    public void A_read_only_file_left_by_an_earlier_install_is_replaced()
    {
        var path = Write("LabControl.Console.exe", "old");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        using var archive = Archive(("LabControl.Console.exe", "new"));
        Assert.Equal(1, Files().ExtractArchive(archive));
        Assert.Equal("new", File.ReadAllText(path));
    }

    private static ZipArchive Archive(params (string Name, string Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var writing = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = writing.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    // ------------------------------------------------------------------ the temporary copy's guard

    [Fact]
    public void The_temporary_copy_only_recognises_its_own_install_directory()
    {
        Assert.True(ConsoleInstalledFiles.IsTheSameDirectory(Install, Install));
        Assert.True(ConsoleInstalledFiles.IsTheSameDirectory(Install + Path.DirectorySeparatorChar, Install));
        Assert.True(ConsoleInstalledFiles.IsTheSameDirectory(
            Path.Combine(Install, "..", "Console"), Install));
        Assert.True(ConsoleInstalledFiles.IsTheSameDirectory(Install.ToUpperInvariant(), Install));

        Assert.False(ConsoleInstalledFiles.IsTheSameDirectory(_root, Install));
        Assert.False(ConsoleInstalledFiles.IsTheSameDirectory(Path.Combine(Install, "sub"), Install));
        Assert.False(ConsoleInstalledFiles.IsTheSameDirectory(Path.Combine(_root, "Console2"), Install));
        Assert.False(ConsoleInstalledFiles.IsTheSameDirectory(Path.GetTempPath(), Install));
        Assert.False(ConsoleInstalledFiles.IsTheSameDirectory("", Install));
        Assert.False(ConsoleInstalledFiles.IsTheSameDirectory(null, Install));
    }

    // ------------------------------------------------------------------ the leftover copies

    [Fact]
    public void Old_copies_of_the_installer_are_swept_and_running_ones_are_not()
    {
        var temp = Path.Combine(_root, "temp");
        Directory.CreateDirectory(temp);
        var now = DateTime.UtcNow;

        var old = Path.Combine(temp, ConsoleTempCopies.NameFor(Guid.NewGuid()));
        var running = Path.Combine(temp, ConsoleTempCopies.NameFor(Guid.NewGuid()));
        var fresh = Path.Combine(temp, ConsoleTempCopies.NameFor(Guid.NewGuid()));
        var foreign = Path.Combine(temp, "LabControl.ConsoleSetup.exe");
        var stranger = Path.Combine(temp, "SomethingElse.exe");
        foreach (var path in new[] { old, running, fresh, foreign, stranger })
        {
            File.WriteAllText(path, "installer");
        }

        File.SetLastWriteTimeUtc(old, now - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(running, now - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(foreign, now - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(stranger, now - TimeSpan.FromDays(2));

        var swept = ConsoleTempCopies.Sweep(temp, running, ConsoleTempCopies.Stale, now);

        Assert.Equal([Path.GetFileName(old)], swept);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(running), "the copy doing the sweeping deleted itself");
        Assert.True(File.Exists(fresh), "a copy from a minute ago may still be working");
        Assert.True(File.Exists(foreign), "the installed uninstaller is not a temporary copy");
        Assert.True(File.Exists(stranger));
    }

    [Fact]
    public void Only_this_installers_own_temporary_names_count()
    {
        Assert.True(ConsoleTempCopies.IsTemporaryCopy(ConsoleTempCopies.NameFor(Guid.NewGuid())));
        Assert.False(ConsoleTempCopies.IsTemporaryCopy(Defaults.ConsoleSetupExecutableName));
        Assert.False(ConsoleTempCopies.IsTemporaryCopy("LabControl.ConsoleSetup.not-a-guid.exe"));
        Assert.False(ConsoleTempCopies.IsTemporaryCopy("LabControl.ConsoleSetup." + new string('z', 32) + ".exe"));
        Assert.False(ConsoleTempCopies.IsTemporaryCopy(null));
    }

    [Fact]
    public void Sweeping_a_directory_that_is_not_there_is_not_an_error() =>
        Assert.Empty(ConsoleTempCopies.Sweep(Path.Combine(_root, "nowhere"), null, ConsoleTempCopies.Stale, DateTime.UtcNow));
}
