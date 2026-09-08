using System.Text;
using LabControl.Shared.Files;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Setup;

public interface IProfileTemplateStore
{
    string RootIdentity { get; }
    byte[]? ReadFile(string relativePath);
    bool DirectoryExists(string relativePath);
    void CreateFile(string relativePath, byte[] content);
    void RemoveFile(string relativePath, byte[] expected);
    void CreateDirectory(string relativePath);
    void RemoveEmptyDirectory(string relativePath);
}

public sealed class ProfileTemplatePlan : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(1);
    public int SchemaVersion { get; set; } = 1;
    public string InstallationId { get; set; } = "";
    public string RootIdentity { get; set; } = "";
    public List<ProfileTemplatePlanFile> Files { get; set; } = [];
}
public sealed record ProfileTemplatePlanFile(string RelativePath, string Sha256);

/// <summary>Runs under Setup's private storage lock. The plan contains only paths and
/// hashes; file contents and ownership live in the existing protected settings journal.</summary>
public sealed class DefaultProfileTemplate(InstallationState state, Func<string?> currentSid,
    Func<SetupSettingsJournal> journal, IProfileTemplateStore store, string dataDirectory)
{
    private string PlanPath => Path.Combine(dataDirectory, Defaults.ProfileTemplatePlanFileName);

    public IReadOnlyList<SettingChangeResult> Apply(Stream archive)
    {
        if (!Enabled()) return [];
        var files = ProfileTemplateArchive.Read(archive);
        var desired = new ProfileTemplatePlan { InstallationId = state.Read()!.InstallationId, RootIdentity = store.RootIdentity,
            Files = files.Select(file => new ProfileTemplatePlanFile(file.RelativePath, FileHash.Sha256Hex(file.Content))).ToList() };
        var old = ReadPlan();
        if (old is not null && !old.Files.SequenceEqual(desired.Files))
            throw new InvalidOperationException("A different profile template is already recorded; repair preserves its original files.");
        var settings = journal();
        settings.Validate();
        if (old is null && settings.HasEntries("student.template-"))
            throw new InvalidDataException("Profile template ownership entries exist but their path plan is missing; preserve the Default profile.");
        // Every collision is checked before making any Default-profile change.
        foreach (var file in files)
        {
            RequireAccount();
            var current = store.ReadFile(file.RelativePath);
            if (current is not null && !current.AsSpan().SequenceEqual(file.Content))
                throw new InvalidOperationException("A pre-existing or later-edited Default-profile file conflicts with the template; it was preserved.");
        }
        var directories = Directories(desired);
        foreach (var directory in directories) { RequireAccount(); _ = store.DirectoryExists(directory); }
        if (old is null) SavePlan(desired);
        var results = new List<SettingChangeResult>();
        foreach (var directory in directories)
            results.Add(settings.Apply(new DirectorySetting(store, RequireAccount, directory), [1]));
        foreach (var file in files)
            results.Add(settings.Apply(new FileSetting(store, RequireAccount, file.RelativePath), file.Content));
        return results;
    }

    public IReadOnlyList<SettingChangeResult> Restore()
    {
        var configuration = state.Read() ?? throw new InvalidOperationException("Installation history is required for profile templates.");
        if (configuration.CreateStudentAccount != true) return [];
        var settings = journal();
        // Restored history remains in the journal. A removal retry after account deletion,
        // or an install that never reached template setup, has no profile work to authorize.
        if (!settings.PendingRestorationIds().Any(id => id.StartsWith("student.template-", StringComparison.Ordinal)))
            return [];
        RequireAccount();
        var plan = ReadPlan();
        if (plan is null)
            throw new InvalidDataException("Profile template ownership entries exist but their path plan is missing; preserve the Default profile.");
        var results = new List<SettingChangeResult>();
        foreach (var file in plan.Files)
            results.Add(settings.Restore(new FileSetting(store, RequireAccount, file.RelativePath)));
        foreach (var directory in Directories(plan).Reverse())
        {
            try { results.Add(settings.Restore(new DirectorySetting(store, RequireAccount, directory))); }
            catch (IOException) { results.Add(SettingChangeResult.Conflict); }
        }
        return results;
    }

    private bool Enabled()
    {
        var configuration = state.Read() ?? throw new InvalidOperationException("Installation history is required for profile templates.");
        if (configuration.CreateStudentAccount != true) return false;
        RequireAccount();
        return true;
    }
    private void RequireAccount() => state.RequireManagedStudent(currentSid());
    private ProfileTemplatePlan? ReadPlan()
    {
        ProfileTemplatePlan plan;
        try { plan = JsonStore.Load<ProfileTemplatePlan>(PlanPath, ProfileTemplatePlan.Migrations); }
        catch (FileNotFoundException) { return null; }
        if (plan.InstallationId != state.Read()!.InstallationId || plan.Files is null || plan.Files.Count is 0 or > Defaults.ProfileTemplateMaxEntries)
            throw new InvalidDataException("The profile template plan does not match this installation.");
        if (string.IsNullOrWhiteSpace(plan.RootIdentity) || plan.RootIdentity != store.RootIdentity)
            throw new InvalidOperationException("Windows changed the Default-profile location; preserve template files in both locations.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in plan.Files)
            if (file is null || file.RelativePath != ProfileTemplateArchive.NormalizePath(file.RelativePath)
                || !seen.Add(file.RelativePath) || !FileHash.LooksLikeSha256(file.Sha256))
                throw new InvalidDataException("The profile template plan is invalid.");
        return plan;
    }
    private void SavePlan(ProfileTemplatePlan plan)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonStore.Serialize(plan, ProfileTemplatePlan.Migrations));
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        // Plan contains no content or credentials. Persist it before any profile mutation.
        var temporary = PlanPath + ".tmp";
        using (var file = new FileStream(temporary, options))
        { file.Write(bytes); file.Flush(flushToDisk: true); }
        File.Move(temporary, PlanPath, overwrite: false);
    }
    private static IReadOnlyList<string> Directories(ProfileTemplatePlan plan) => plan.Files
        .SelectMany(file =>
        {
            var parts = file.RelativePath.Split('/');
            return Enumerable.Range(1, parts.Length - 1).Select(count => string.Join('/', parts.Take(count)));
        }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Count(c => c == '/'))
        .ThenBy(path => path, StringComparer.Ordinal).ToArray();
    private static string Id(string kind, string path) => "student.template-" + kind + "." + FileHash.Sha256Hex(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));

    private sealed class FileSetting(IProfileTemplateStore store, Action guard, string path) : ISetupSetting
    {
        private byte[]? _last;
        private bool _read;
        public string Id => DefaultProfileTemplate.Id("file", path);
        public byte[]? Read() { guard(); _last = store.ReadFile(path); _read = true; return _last?.ToArray(); }
        public void Write(byte[]? value)
        {
            guard();
            if (!_read) throw new InvalidOperationException("Read the template file before writing it.");
            _read = false;
            if (value is null && _last is not null) store.RemoveFile(path, _last);
            else if (value is not null && _last is null) store.CreateFile(path, value);
            else throw new InvalidOperationException("Template files are never overwritten.");
        }
    }
    private sealed class DirectorySetting(IProfileTemplateStore store, Action guard, string path) : ISetupSetting
    {
        private bool _last;
        private bool _read;
        public string Id => DefaultProfileTemplate.Id("dir", path);
        public byte[]? Read() { guard(); _last = store.DirectoryExists(path); _read = true; return _last ? [1] : null; }
        public void Write(byte[]? value)
        {
            guard();
            if (!_read || value is not null && !value.AsSpan().SequenceEqual(new byte[] { 1 }))
                throw new InvalidDataException("The template directory state is invalid.");
            _read = false;
            if (value is null && _last) store.RemoveEmptyDirectory(path);
            else if (value is not null && !_last) store.CreateDirectory(path);
            else throw new IOException("The template directory changed.");
        }
    }
}
