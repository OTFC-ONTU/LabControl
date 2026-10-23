using System.IO.Compression;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>Collected work and the light reset (M6 portion 5, D-77, D-78), all on the Mac.</summary>
public sealed class CollectWorkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lc-collect-" + Guid.NewGuid().ToString("n"));

    public CollectWorkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(@"Desktop\Exam", @"Desktop\Exam")]
    [InlineData("Documents/Java/Lab3", @"Documents\Java\Lab3")]
    [InlineData(@" Desktop \ Exam ", @"Desktop\Exam")]
    [InlineData("Work", "Work")]
    public void A_folder_inside_the_profile_is_normalized(string text, string expected)
    {
        Assert.True(CollectRequest.TryNormalize(text, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\Users\student")]
    [InlineData(@"Desktop\..\..\Administrator")]
    [InlineData(@"Desktop\.")]
    [InlineData("")]
    [InlineData(@"Desktop\CON")]
    [InlineData(@"Desktop\a|b")]
    [InlineData(@"\\server\share")]
    public void A_folder_outside_the_profile_is_refused(string text) => Assert.False(CollectRequest.TryNormalize(text, out _));

    [Fact]
    public void The_first_part_names_a_known_folder_or_the_profile()
    {
        Assert.Equal("Desktop:Exam", Split(@"desktop\Exam"));
        Assert.Equal("Documents:", Split("Documents"));
        Assert.Equal("Profile:Projects/Lab3", Split(@"Projects\Lab3"));

        static string Split(string folder)
        {
            var (root, below) = new CollectRequest(folder).Split();
            return root + ":" + string.Join('/', below);
        }
    }

    [Fact]
    public void Requests_round_trip_through_job_args_and_refuse_nonsense()
    {
        var job = new Job { Kind = Job.Types.Kind.CollectFiles };
        job.Args.Add(new CollectRequest(CollectRequest.DefaultFolder).ToArgs());
        Assert.True(CollectRequest.TryParse(job, out var collect, out _));
        Assert.Equal(CollectRequest.DefaultFolder, collect.Folder);
        job.Args["folder"] = @"..\x";
        Assert.False(CollectRequest.TryParse(job, out _, out _));

        var reset = new Job { Kind = Job.Types.Kind.ResetProfile };
        reset.Args.Add(new ResetProfileRequest(ResetMode.Full).ToArgs());
        Assert.True(ResetProfileRequest.TryParse(reset, out var parsed, out _));
        Assert.Equal(ResetMode.Full, parsed.Mode);
        reset.Args["mode"] = "everything";
        Assert.False(ResetProfileRequest.TryParse(reset, out _, out _));
    }

    [Fact]
    public void A_folder_is_zipped_without_its_links_and_unpacked_to_the_same_tree()
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "Exam")).FullName;
        File.WriteAllText(Path.Combine(work, "Main.java"), "class Main {}");
        Directory.CreateDirectory(Path.Combine(work, "src", "util"));
        File.WriteAllText(Path.Combine(work, "src", "util", "Робота.txt"), "текст");
        Directory.CreateDirectory(Path.Combine(work, "empty"));
        var secret = Directory.CreateDirectory(Path.Combine(_root, "secret")).FullName;
        File.WriteAllText(Path.Combine(secret, "sam"), "not the student's");
        Directory.CreateSymbolicLink(Path.Combine(work, "link-to-secret"), secret);
        File.CreateSymbolicLink(Path.Combine(work, "file-link"), Path.Combine(secret, "sam"));

        using var zip = new MemoryStream();
        var summary = WorkArchive.Create(work, zip, Defaults.CollectMaxBytes, Defaults.CollectMaxFiles, CancellationToken.None);
        Assert.Equal(2, summary.Files);
        Assert.Equal(2, summary.Links);
        Assert.Empty(summary.InUse);

        zip.Position = 0;
        var target = Path.Combine(_root, "Collected", "PC-07");
        Assert.Equal(2, WorkArchive.Extract(zip, target, Defaults.CollectMaxBytes, Defaults.CollectMaxFiles, CancellationToken.None));
        Assert.Equal("class Main {}", File.ReadAllText(Path.Combine(target, "Main.java")));
        Assert.Equal("текст", File.ReadAllText(Path.Combine(target, "src", "util", "Робота.txt")));
        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        Assert.False(Directory.Exists(Path.Combine(target, "link-to-secret")));
        Assert.False(File.Exists(Path.Combine(target, "file-link")));
        Assert.False(Directory.Exists(target + ".partial"));
    }

    [Fact]
    public void A_folder_past_the_caps_is_refused_whole()
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "big")).FullName;
        File.WriteAllBytes(Path.Combine(work, "a.bin"), new byte[600]);
        File.WriteAllBytes(Path.Combine(work, "b.bin"), new byte[600]);
        Assert.Throws<CollectTooLargeException>(() => WorkArchive.Create(work, new MemoryStream(), 1000, 100, CancellationToken.None));
        Assert.Throws<CollectTooLargeException>(() => WorkArchive.Create(work, new MemoryStream(), 10_000, 1, CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../b.txt")]
    [InlineData("C:/Windows/x.txt")]
    [InlineData("a\\..\\..\\b.txt")]
    public void An_archive_naming_a_place_outside_its_folder_leaves_nothing_behind(string name)
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("ok.txt").Open());
            writer.Write("fine");
        }

        using (var archive = new ZipArchive(zip, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write("evil");
        }

        zip.Position = 0;
        var target = Path.Combine(_root, "Collected", "PC-08");
        Assert.Throws<InvalidDataException>(() => WorkArchive.Extract(zip, target, Defaults.CollectMaxBytes, Defaults.CollectMaxFiles, CancellationToken.None));
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(target + ".partial"));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "Collected", "b.txt")));
    }

    [Fact]
    public void An_archive_that_unpacks_past_the_cap_is_refused_whatever_its_headers_say()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry("bomb.bin", CompressionLevel.Optimal).Open();
            stream.Write(new byte[2 * 1024 * 1024]);
        }

        zip.Position = 0;
        var target = Path.Combine(_root, "Collected", "PC-09");
        Assert.Throws<CollectTooLargeException>(() => WorkArchive.Extract(zip, target, 1024 * 1024, 10, CancellationToken.None));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void A_light_reset_empties_the_folder_keeps_desktop_ini_and_never_enters_a_link()
    {
        var desktop = Directory.CreateDirectory(Path.Combine(_root, "Desktop")).FullName;
        File.WriteAllText(Path.Combine(desktop, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(desktop, "notes.txt"), "x");
        var materials = Directory.CreateDirectory(Path.Combine(desktop, "Materials", "deep")).FullName;
        File.WriteAllText(Path.Combine(materials, "handout.pdf"), "x");
        var readOnly = Path.Combine(desktop, "readonly.txt");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "must survive");
        Directory.CreateSymbolicLink(Path.Combine(desktop, "link"), outside);

        var summary = ProfileWipe.Clear(desktop, Defaults.ProfileWipeMaxEntries, Defaults.ProfileWipeMaxDuration, CancellationToken.None);

        Assert.Equal(0, summary.Left);
        Assert.Equal(["desktop.ini"], Directory.EnumerateFileSystemEntries(desktop).Select(Path.GetFileName).ToArray());
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.True(Directory.Exists(desktop));
    }

    [Fact]
    public void A_light_reset_stops_at_its_budget_and_says_what_is_left()
    {
        var documents = Directory.CreateDirectory(Path.Combine(_root, "Documents")).FullName;
        for (var i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(documents, $"f{i}.txt"), "x");
        }

        var summary = ProfileWipe.Clear(documents, 4, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(4, summary.Removed);
        Assert.Equal(6, summary.Left);
        Assert.Equal(new WipeSummary(0, 0), ProfileWipe.Clear(Path.Combine(_root, "missing"), 10, TimeSpan.FromMinutes(1), CancellationToken.None));
    }

    [Fact]
    public void A_name_windows_11_allows_travels_and_one_the_console_cannot_store_is_left_out_and_named()
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "names")).FullName;
        File.WriteAllText(Path.Combine(work, "Aux.java"), "class Aux {}");
        File.WriteAllText(Path.Combine(work, "trailing."), "odd");
        using var zip = new MemoryStream();
        var summary = WorkArchive.Create(work, zip, Defaults.CollectMaxBytes, Defaults.CollectMaxFiles, CancellationToken.None);
        Assert.Equal(1, summary.Files);
        Assert.Equal(["trailing."], summary.Skipped);

        zip.Position = 0;
        var target = Path.Combine(_root, "Collected", "PC-10");
        WorkArchive.Extract(zip, target, Defaults.CollectMaxBytes, Defaults.CollectMaxFiles, CancellationToken.None);
        Assert.Equal("class Aux {}", File.ReadAllText(Path.Combine(target, "Aux.java")));
    }

    [Fact]
    public void A_light_reset_of_a_very_deep_tree_does_not_recurse()
    {
        // A student can nest folders thousands deep; a recursive walk would overflow the service's stack.
        var desktop = Directory.CreateDirectory(Path.Combine(_root, "DeepDesktop")).FullName;
        var path = desktop;
        for (var i = 0; i < 300; i++)
        {
            path = Path.Combine(path, "d");
        }

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "leaf.txt"), "x");

        var summary = ProfileWipe.Clear(desktop, Defaults.ProfileWipeMaxEntries, Defaults.ProfileWipeMaxDuration, CancellationToken.None);
        Assert.Equal(0, summary.Left);
        Assert.Equal(301, summary.Removed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(desktop));
    }
}
