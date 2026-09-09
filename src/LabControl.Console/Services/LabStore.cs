using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Persistence;

namespace LabControl.Console.Services;

/// <summary>
/// One lab's directory (ARCHITECTURE §4): where every document of that lab lives and how it
/// is read and written. Since M5 (D-55) that is <c>labs/&lt;lab_id&gt;/</c> under the data
/// directory, one per saved lab, found through <see cref="ProfileStore"/>. Nothing here
/// interprets the documents; it only knows the paths and that every file goes through
/// <see cref="JsonStore"/> with its migration chain.
/// </summary>
public sealed class LabStore
{
    public LabStore(string directory) => Directory = directory;

    public string Directory { get; }

    public string LabKeyPath => Path.Combine(Directory, Defaults.LabKeyFileName);

    public string LabPath => Path.Combine(Directory, Defaults.LabFileName);

    public string InstancePath => Path.Combine(Directory, Defaults.InstanceFileName);

    public string EnrollmentPath => Path.Combine(Directory, Defaults.EnrollmentFileName);

    public string PackagesDirectory => Path.Combine(Directory, Defaults.PackagesDirectoryName);

    public string ScriptsPath => Path.Combine(Directory, Defaults.ScriptsFileName);

    public string LogsDirectory => Path.Combine(Directory, Defaults.LogsDirectoryName);

    /// <summary>Delivered, unanswered jobs kept across a switch away from this lab (M5, D-57 item 4).</summary>
    public string InFlightJobsPath => Path.Combine(LogsDirectory, Defaults.InFlightJobsFileName);

    /// <summary>A teacher profile's authorization state (M5 portion 3); the path is reserved here.</summary>
    public string AccessPath => Path.Combine(Directory, Defaults.AccessFileName);

    /// <summary>
    /// A lab can be opened here once this machine's instance is on disk together with what
    /// authorizes it: the key (administrator) or <c>access.json</c> (teacher, M5 D-56).
    /// </summary>
    public bool HasLab => File.Exists(InstancePath) && (File.Exists(LabKeyPath) || File.Exists(AccessPath));

    public bool HasLabKey => File.Exists(LabKeyPath);

    /// <summary>A teacher profile: the lab arrived as a lab file and <c>access.json</c> holds its authorization state.</summary>
    public bool HasAccess => File.Exists(AccessPath);

    public void EnsureDirectories()
    {
        System.IO.Directory.CreateDirectory(Directory);
        System.IO.Directory.CreateDirectory(PackagesDirectory);
        System.IO.Directory.CreateDirectory(LogsDirectory);
    }

    // ------------------------------------------------------------------ lab key

    public LabKeyDocument LoadLabKey() => JsonStore.Load<LabKeyDocument>(LabKeyPath, LabKeyDocument.Migrations);

    public void SaveLabKey(LabKeyDocument document) => JsonStore.Save(LabKeyPath, document, LabKeyDocument.Migrations, ownerOnly: true);

    /// <summary>What the backup check compares (D-25): the file as it is on disk right now.</summary>
    public string LabKeyFingerprint() => JsonStore.Fingerprint(File.ReadAllText(LabKeyPath));

    // ------------------------------------------------------------------ documents

    public InstanceDocument? LoadInstance() => JsonStore.LoadIfExists<InstanceDocument>(InstancePath, InstanceDocument.Migrations);

    public void SaveInstance(InstanceDocument document) => JsonStore.Save(InstancePath, document, InstanceDocument.Migrations, ownerOnly: true);

    public LabDocument LoadLab(string labId, string labName) =>
        JsonStore.LoadIfExists<LabDocument>(LabPath, LabDocument.Migrations) ?? new LabDocument { LabId = labId, LabName = labName };

    public void SaveLab(LabDocument document) => JsonStore.Save(LabPath, document, LabDocument.Migrations);

    public EnrollmentDocument LoadEnrollment(string labId) =>
        JsonStore.LoadIfExists<EnrollmentDocument>(EnrollmentPath, EnrollmentDocument.Migrations) ?? new EnrollmentDocument { LabId = labId };

    public void SaveEnrollment(EnrollmentDocument document) => JsonStore.Save(EnrollmentPath, document, EnrollmentDocument.Migrations);

    public AccessDocument? LoadAccess() => JsonStore.LoadIfExists<AccessDocument>(AccessPath, AccessDocument.Migrations);

    /// <summary>Owner-only: it holds the pending device key's keystore reference.</summary>
    public void SaveAccess(AccessDocument document) => JsonStore.Save(AccessPath, document, AccessDocument.Migrations, ownerOnly: true);

    /// <summary>The script library, or null before the first run imported the seed (D-31 item 4).</summary>
    public ScriptsDocument? LoadScripts() => JsonStore.LoadIfExists<ScriptsDocument>(ScriptsPath, ScriptsDocument.Migrations);

    public void SaveScripts(ScriptsDocument document) => JsonStore.Save(ScriptsPath, document, ScriptsDocument.Migrations);

    // ------------------------------------------------------------------ catalog

    /// <summary>The package catalog as text files by relative path — what a backup carries.</summary>
    public IReadOnlyDictionary<string, string> ReadCatalog()
    {
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!System.IO.Directory.Exists(PackagesDirectory))
        {
            return catalog;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(PackagesDirectory, "*.yaml", SearchOption.AllDirectories))
        {
            catalog[Path.GetRelativePath(PackagesDirectory, file)] = File.ReadAllText(file);
        }

        return catalog;
    }

    public void WriteCatalog(IReadOnlyDictionary<string, string> catalog)
    {
        foreach (var (relativePath, text) in catalog)
        {
            var path = Path.Combine(PackagesDirectory, relativePath);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
    }

    /// <summary>Removes this console's lab entirely — the migration test's "delete the data directory".</summary>
    public void DeleteEverything()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
