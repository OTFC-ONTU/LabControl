using System.IO.Compression;
using System.Text;
using LabControl.Shared;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Shared.Tests;

public sealed class ProfileTemplateTests : IDisposable
{
    private const string Sid = "S-1-5-21-11-22-33-1001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "labcontrol-template-" + Guid.NewGuid().ToString("N"));
    private readonly InstallationState _state;
    private readonly SetupSettingsJournal _journal;
    private readonly Store _store = new();
    public ProfileTemplateTests()
    {
        Directory.CreateDirectory(_directory);
        _state = new(_directory); _state.Configure(false); _state.BeginStudentCreation(null); _state.CompleteStudentCreation(Sid);
        _journal = new(_directory, _state.Read()!.InstallationId, bytes => bytes.ToArray(), bytes => bytes.ToArray());
        _journal.InitializeNew();
    }
    private DefaultProfileTemplate Component() => new(_state, () => Sid, () => _journal, _store, _directory);
    private static MemoryStream Zip(params (string Path, byte[] Content)[] files)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var file in files)
            { using var stream = zip.CreateEntry(file.Path).Open(); stream.Write(file.Content); }
        buffer.Position = 0;
        return buffer;
    }
    [Theory]
    [InlineData("../Desktop/a.txt")]
    [InlineData("/Desktop/a.txt")]
    [InlineData("Desktop/../a.txt")]
    [InlineData("Desktop\\a.txt")]
    [InlineData("Desktop/a.txt:stream")]
    [InlineData("Desktop/CON.txt")]
    [InlineData("NTUSER.DAT")]
    [InlineData("AppData/Roaming/startup.txt")]
    [InlineData("Desktop/run.exe")]
    [InlineData("Desktop/start.lnk")]
    public void Unsafe_or_out_of_contract_archives_are_refused_before_profile_access(string path)
    {
        using var archive = Zip((path, "content"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => Component().Apply(archive));
        Assert.Equal(0, _store.Calls);
    }
    [Fact]
    public void Links_duplicates_and_file_directory_collisions_are_refused()
    {
        using var symlink = new MemoryStream();
        using (var zip = new ZipArchive(symlink, ZipArchiveMode.Create, true))
        { var entry = zip.CreateEntry("Desktop/link.txt"); entry.ExternalAttributes = unchecked((int)0xa1ff0000); }
        symlink.Position = 0;
        Assert.Throws<InvalidDataException>(() => ProfileTemplateArchive.Read(symlink));
        using var duplicate = Zip(("Desktop/a.txt", []), ("desktop/A.txt", []));
        Assert.Throws<InvalidDataException>(() => ProfileTemplateArchive.Read(duplicate));
        using var collision = Zip(("Desktop/a.txt", []), ("Desktop/a.txt/b.txt", []));
        Assert.Throws<InvalidDataException>(() => ProfileTemplateArchive.Read(collision));
    }
    [Fact]
    public void Archive_count_and_file_size_are_bounded()
    {
        using var many = Zip(Enumerable.Range(0, Defaults.ProfileTemplateMaxEntries + 1).Select(i => ($"Desktop/{i}.txt", Array.Empty<byte>())).ToArray());
        Assert.Throws<InvalidDataException>(() => ProfileTemplateArchive.Read(many));
        using var large = Zip(("Desktop/a.txt", new byte[Defaults.ProfileTemplateMaxFileBytes + 1]));
        Assert.Throws<InvalidDataException>(() => ProfileTemplateArchive.Read(large));
    }
    [Fact]
    public void Applies_new_files_and_removes_only_owned_unchanged_files_and_empty_directories()
    {
        _store.Directories.Add("Desktop");
        _store.Files.Add("Desktop/existing.txt", "preexisting"u8.ToArray());
        using var zip = Zip(("Desktop/lesson.txt", "lesson"u8.ToArray()), ("Documents/topic/notes.txt", "private-template-content"u8.ToArray()));
        Assert.All(Component().Apply(zip), result => Assert.Contains(result, new[] { SettingChangeResult.Applied, SettingChangeResult.Unchanged }));
        zip.Position = 0;
        Assert.All(Component().Apply(zip), result => Assert.Contains(result, new[] { SettingChangeResult.AlreadyApplied, SettingChangeResult.Unchanged }));
        Assert.DoesNotContain("private-template-content", File.ReadAllText(Path.Combine(_directory, Defaults.ProfileTemplatePlanFileName)));
        Assert.All(Component().Restore(), result => Assert.Contains(result, new[] { SettingChangeResult.Restored, SettingChangeResult.Untracked }));
        Assert.Single(_store.Files); Assert.Contains("Desktop/existing.txt", _store.Files.Keys);
        Assert.Equal(new[] { "Desktop" }, _store.Directories);
    }
    [Fact]
    public void An_existing_identical_file_stays_unowned_and_a_different_file_blocks_every_change()
    {
        _store.Directories.Add("Desktop");
        _store.Files.Add("Desktop/existing.txt", "old"u8.ToArray());
        using var conflict = Zip(("Desktop/new.txt", "new"u8.ToArray()), ("Desktop/existing.txt", "changed"u8.ToArray()));
        Assert.Throws<InvalidOperationException>(() => Component().Apply(conflict));
        Assert.Single(_store.Files); Assert.Equal(0, _store.Writes);
        using var same = Zip(("Desktop/existing.txt", "old"u8.ToArray()));
        Component().Apply(same); Component().Restore();
        Assert.Single(_store.Files); Assert.Equal(0, _store.Writes);
    }
    [Fact]
    public void Later_edits_are_preserved_and_report_conflicts()
    {
        using var zip = Zip(("Desktop/a.txt", "original"u8.ToArray()));
        Component().Apply(zip);
        _store.Files["Desktop/a.txt"] = "teacher edit"u8.ToArray();
        Assert.Contains(SettingChangeResult.Conflict, Component().Restore());
        Assert.Equal("teacher edit", Encoding.UTF8.GetString(_store.Files["Desktop/a.txt"]));
    }
    [Fact]
    public void Opt_out_touches_neither_archive_nor_profile_nor_settings()
    {
        _state.Configure(true, false);
        var component = new DefaultProfileTemplate(_state, () => throw new Exception(), () => throw new Exception(), _store, _directory);
        Assert.Empty(component.Apply(Stream.Null)); Assert.Empty(component.Restore()); Assert.Equal(0, _store.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_template_work_skips_SAM_and_profile_before_creation_or_after_account_deletion(bool recordedSid)
    {
        if (!recordedSid)
        {
            File.Delete(_state.FilePath);
            _state.Configure(false);
            File.Delete(_journal.FilePath);
            new SetupSettingsJournal(_directory, _state.Read()!.InstallationId, bytes => bytes.ToArray(), bytes => bytes.ToArray()).InitializeNew();
        }
        var journal = new SetupSettingsJournal(_directory, _state.Read()!.InstallationId, bytes => bytes.ToArray(), bytes => bytes.ToArray());
        var sidCalls = 0;
        var component = new DefaultProfileTemplate(_state, () => { sidCalls++; return null; }, () => journal, _store, _directory);
        Assert.Empty(component.Restore());
        Assert.Equal(0, sidCalls);
        Assert.Equal(0, _store.Calls);
        Assert.Equal(0, _store.RootReads);
    }
    [Fact]
    public void Completed_template_restore_skips_SAM_and_profile_on_pending_account_removal_retry()
    {
        using var zip = Zip(("Desktop/a.txt", "content"u8.ToArray()));
        Component().Apply(zip);
        Component().Restore();
        Assert.True(_journal.HasEntries("student.template-"));
        _state.BeginStudentRemoval(Sid);
        var calls = _store.Calls;
        var roots = _store.RootReads;
        var component = new DefaultProfileTemplate(_state, () => throw new Exception("SAM must not be queried"), () => _journal, _store, _directory);
        Assert.Empty(component.Restore());
        Assert.Equal(calls, _store.Calls);
        Assert.Equal(roots, _store.RootReads);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("S-1-5-21-11-22-33-1002")]
    public void Pending_template_restoration_still_requires_the_recorded_student(string? currentSid)
    {
        using var zip = Zip(("Desktop/a.txt", "content"u8.ToArray()));
        Component().Apply(zip);
        var writes = _store.Writes;
        var component = new DefaultProfileTemplate(_state, () => currentSid, () => _journal, _store, _directory);
        Assert.Throws<InvalidOperationException>(() => component.Restore());
        Assert.Equal(writes, _store.Writes);
        Assert.Single(_store.Files);
        Assert.NotEmpty(_journal.PendingRestorationIds());
    }
    [Fact]
    public void Repair_refuses_a_changed_archive_and_preserves_the_first_plan()
    {
        using var first = Zip(("Desktop/a.txt", "first"u8.ToArray())); Component().Apply(first);
        var before = File.ReadAllBytes(Path.Combine(_directory, Defaults.ProfileTemplatePlanFileName));
        using var second = Zip(("Desktop/b.txt", "second"u8.ToArray()));
        Assert.Throws<InvalidOperationException>(() => Component().Apply(second));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_directory, Defaults.ProfileTemplatePlanFileName)));
    }
    [Fact]
    public void Missing_plan_never_silently_loses_existing_ownership()
    {
        using var zip = Zip(("Desktop/a.txt", "content"u8.ToArray()));
        Component().Apply(zip);
        File.Delete(Path.Combine(_directory, Defaults.ProfileTemplatePlanFileName));
        Assert.Throws<InvalidDataException>(() => Component().Restore());
        zip.Position = 0;
        Assert.Throws<InvalidDataException>(() => Component().Apply(zip));
        Assert.Single(_store.Files);
    }

    [Fact]
    public void Total_uncompressed_size_is_bounded_even_with_small_entries()
    {
        using var zip = Zip(Enumerable.Range(0, Defaults.ProfileTemplateMaxTotalBytes / Defaults.ProfileTemplateMaxFileBytes + 1)
            .Select(index => ($"Desktop/{index}.txt", new byte[Defaults.ProfileTemplateMaxFileBytes])).ToArray());
        Assert.Throws<InvalidDataException>(() => ProfileTemplateArchive.Read(zip));
    }

    [Fact]
    public void A_changed_default_profile_location_never_reuses_ownership()
    {
        using var zip = Zip(("Desktop/a.txt", "content"u8.ToArray()));
        Component().Apply(zip);
        var writes = _store.Writes;
        _store.RootIdentity = "default-profile-2";
        Assert.Throws<InvalidOperationException>(() => Component().Restore());
        zip.Position = 0;
        Assert.Throws<InvalidOperationException>(() => Component().Apply(zip));
        Assert.Equal(writes, _store.Writes);
    }

    private sealed class Store : IProfileTemplateStore
    {
        private string _rootIdentity = "default-profile-1";
        public int RootReads;
        public string RootIdentity { get { RootReads++; return _rootIdentity; } set => _rootIdentity = value; }
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Directories = new(StringComparer.OrdinalIgnoreCase);
        public int Calls; public int Writes;
        public byte[]? ReadFile(string path) { Calls++; return Files.TryGetValue(path, out var data) ? data.ToArray() : null; }
        public bool DirectoryExists(string path) { Calls++; return Directories.Contains(path); }
        public void CreateFile(string path, byte[] content) { Writes++; Files.Add(path, content.ToArray()); }
        public void RemoveFile(string path, byte[] expected) { Writes++; Assert.Equal(expected, Files[path]); Files.Remove(path); }
        public void CreateDirectory(string path) { Writes++; Assert.True(Directories.Add(path)); }
        public void RemoveEmptyDirectory(string path)
        {
            if (Files.Keys.Any(file => file.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
                || Directories.Any(dir => dir.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))) throw new IOException("Not empty.");
            Writes++; Directories.Remove(path);
        }
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
