using System.Globalization;
using System.Security.Cryptography;
using LabControl.Shared;
using LabControl.Shared.Packages;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LabControl.Console.Services;

/// <summary>Whether a catalog entry's installer is on this computer (D-88).</summary>
public enum InstallerState
{
    Present,

    /// <summary>The entry came with a backup or a copied <c>packages/</c> directory without its installer.</summary>
    Missing,

    /// <summary>A file is there, but not of the size the entry records.</summary>
    Changed,
}

/// <summary>One <c>.yaml</c> of the catalog as read: the entry, or why it could not be read.</summary>
public sealed record CatalogItem(string Id, PackageEntry? Entry, InstallerState State, string? Problem);

/// <summary>
/// The package catalog (M7 portion 3, D-07, D-88): <c>packages/&lt;id&gt;.yaml</c> per entry,
/// its installer copied into <c>packages/&lt;id&gt;/&lt;first 12 hex of SHA-256&gt;/&lt;file&gt;</c>
/// so a new version never overwrites the bytes the old entry still names. The YAML is the
/// part a backup carries (<see cref="LabStore.ReadCatalog"/>); the installers stay on the
/// computer that added them. The catalog ships empty and is filled by the wizard; a new
/// version of a package replaces the old entry (owner decision 2026-10-27).
/// </summary>
public sealed class PackageCatalog
{
    private const string PartialSuffix = ".partial";

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly LabStore _store;
    private readonly string _instanceName;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _log;
    private readonly Lock _gate = new();

    public PackageCatalog(LabStore store, string instanceName, Func<DateTimeOffset> clock, ILogger log)
    {
        _store = store;
        _instanceName = instanceName;
        _clock = clock;
        _log = log;
    }

    /// <summary>Raised after every change, on the caller's thread.</summary>
    public event Action? Changed;

    private string Root => _store.PackagesDirectory;

    public string YamlPath(string id) => Path.Combine(Root, id + ".yaml");

    /// <summary>Where an entry's installer lives on this computer.</summary>
    public string InstallerPath(PackageEntry entry) =>
        Path.Combine(Root, entry.Id, entry.Sha256[..12], entry.File);

    /// <summary>Every entry, by name; an unreadable file is listed with its problem rather than hidden.</summary>
    public IReadOnlyList<CatalogItem> List()
    {
        var items = new List<CatalogItem>();
        if (!Directory.Exists(Root))
        {
            return items;
        }

        foreach (var path in Directory.EnumerateFiles(Root, "*.yaml", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var entry = Parse(id, File.ReadAllText(path));
                items.Add(new CatalogItem(id, entry, StateOf(entry), null));
            }
            catch (Exception ex) when (ex is InvalidDataException or YamlException or IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("package catalog: {File} unreadable: {Message}", Path.GetFileName(path), ex.Message);
                items.Add(new CatalogItem(id, null, InstallerState.Missing, ex.Message));
            }
        }

        return items
            .OrderBy(i => i.Entry?.Name ?? i.Id, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public PackageEntry? Find(string id) => List().FirstOrDefault(i => i.Id == id)?.Entry;

    /// <summary>The entry a new installer named <paramref name="name"/> would replace: same name, or the same id.</summary>
    public PackageEntry? Matching(string name)
    {
        var id = PackageEntry.IdFor(name);
        return List().Select(i => i.Entry).FirstOrDefault(e => e is not null
            && (string.Equals(e.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase) || e.Id == id));
    }

    /// <summary>The newest change across the catalog — the <i>last changed on</i> line (ARCHITECTURE §3.6).</summary>
    public (string By, DateTimeOffset At)? LastChange()
    {
        var newest = List().Select(i => i.Entry).Where(e => e is not null && e.ChangedAtUnix > 0).MaxBy(e => e!.ChangedAtUnix);
        return newest is null ? null : (newest.ChangedBy, DateTimeOffset.FromUnixTimeSeconds(newest.ChangedAtUnix));
    }

    public InstallerState StateOf(PackageEntry entry)
    {
        var info = new FileInfo(InstallerPath(entry));
        if (!info.Exists) return InstallerState.Missing;
        return entry.Size > 0 && info.Length != entry.Size ? InstallerState.Changed : InstallerState.Present;
    }

    /// <summary>
    /// Adds a package from <paramref name="sourcePath"/>: the file is copied into the catalog
    /// while it is hashed, and refused if its hash is no longer the one the wizard inspected.
    /// When <paramref name="replaces"/> is given, its entry and installer give way to the new
    /// one only after the new one is complete — a failure leaves the old entry as it was.
    /// </summary>
    public async Task<PackageEntry> AddAsync(string sourcePath, PackageEntry draft, string? replaces, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var id = replaces ?? UniqueId(draft.Name);
        var entry = draft with { Id = id, ChangedBy = _instanceName, ChangedAtUnix = _clock().ToUnixTimeSeconds() };
        if (entry.Validate() is not PackageProblem.None and var problem)
        {
            throw new InvalidOperationException(problem.ToString());
        }

        var target = InstallerPath(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + PartialSuffix;
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var copy = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                long written = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    await copy.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    written += n;
                    progress?.Report(entry.Size > 0 ? Math.Min(1, (double)written / entry.Size) : 0);
                }

                await copy.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), entry.Sha256, StringComparison.OrdinalIgnoreCase) || written != entry.Size)
                {
                    throw new IOException(Localization.Strings.Get("Packages.SourceChanged"));
                }
            }

            File.Move(partial, target, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            RemoveIfEmpty(Path.GetDirectoryName(target)!);
            if (!File.Exists(YamlPath(id)))
            {
                RemoveIfEmpty(Path.Combine(Root, id));
            }

            throw;
        }

        lock (_gate)
        {
            try
            {
                WriteYaml(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The installer is in place but no entry names it — a disk that filled up with the
                // copy. Take it back out, unless the entry on disk already names this very file
                // (the same installer added again): an orphan of hundreds of megabytes nobody can
                // see in the tab is exactly what the teacher cannot afford on a full disk.
                if (Find(id) is not { } current || !string.Equals(Path.GetFullPath(InstallerPath(current)), Path.GetFullPath(target), StringComparison.Ordinal))
                {
                    TryDelete(target);
                    RemoveIfEmpty(Path.GetDirectoryName(target)!);
                    if (!File.Exists(YamlPath(id)))
                    {
                        RemoveIfEmpty(Path.Combine(Root, id));
                    }
                }

                throw;
            }

            RemoveOtherInstallers(entry);
        }

        _log.LogInformation("package catalog: {Id} {Version} added ({Size} bytes, {Family})", entry.Id, entry.Version, entry.Size, entry.Family);
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Saves the teacher's edits to an existing entry; its installer, id and hash stay.</summary>
    public PackageProblem Update(PackageEntry edited)
    {
        lock (_gate)
        {
            var current = Find(edited.Id);
            if (current is null)
            {
                return PackageProblem.NameMissing;
            }

            var entry = current with
            {
                Name = edited.Name.Trim(),
                Version = edited.Version.Trim(),
                Silent = edited.Silent.Trim(),
                Detect = edited.Detect with { Value = edited.Detect.Value.Trim() },
                TimeoutMinutes = edited.TimeoutMinutes,
                ChangedBy = _instanceName,
                ChangedAtUnix = _clock().ToUnixTimeSeconds(),
            };
            if (entry.Validate() is not PackageProblem.None and var problem)
            {
                return problem;
            }

            WriteYaml(entry);
        }

        Changed?.Invoke();
        return PackageProblem.None;
    }

    /// <summary>Removes the entry and its installer from this computer.</summary>
    public void Delete(string id)
    {
        if (!PackageEntry.IsValidId(id))
        {
            return;
        }

        lock (_gate)
        {
            TryDelete(YamlPath(id));
            var directory = Path.Combine(Root, id);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        _log.LogInformation("package catalog: {Id} removed", id);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ YAML

    /// <summary>The on-disk shape: the README's keys, snake case.</summary>
    private sealed class PackageYaml
    {
        public int? SchemaVersion { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Family { get; set; }
        public string? File { get; set; }
        public string? Run { get; set; }
        public long? Size { get; set; }
        public string? Sha256 { get; set; }
        public string? Silent { get; set; }
        public DetectYaml? Detect { get; set; }
        public int? TimeoutMinutes { get; set; }
        public string? ChangedBy { get; set; }
        public string? ChangedAt { get; set; }
    }

    private sealed class DetectYaml
    {
        public string? UninstallName { get; set; }
        public string? Path { get; set; }
    }

    public static string Serialize(PackageEntry entry) => Serializer.Serialize(new PackageYaml
    {
        SchemaVersion = Defaults.PackageCatalogSchemaVersion,
        Name = entry.Name,
        Version = entry.Version,
        Family = InstallPackageRequest.FamilyCode(entry.Family),
        File = entry.File,
        Run = entry.Run.Length > 0 ? entry.Run : null,
        Size = entry.Size,
        Sha256 = entry.Sha256,
        Silent = entry.Silent,
        Detect = entry.Detect.Kind == DetectKind.Path ? new DetectYaml { Path = entry.Detect.Value } : new DetectYaml { UninstallName = entry.Detect.Value },
        TimeoutMinutes = entry.TimeoutMinutes,
        ChangedBy = entry.ChangedBy,
        ChangedAt = DateTimeOffset.FromUnixTimeSeconds(entry.ChangedAtUnix).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    });

    /// <summary>
    /// Reads one entry. Keys this build does not know are ignored, a missing family is
    /// taken from the extension, a missing size means "not checked"; a newer schema or an
    /// entry that does not validate is refused (D-20).
    /// </summary>
    public static PackageEntry Parse(string id, string yaml)
    {
        var raw = Deserializer.Deserialize<PackageYaml>(yaml) ?? throw new InvalidDataException("empty package file");
        if (raw.SchemaVersion > Defaults.PackageCatalogSchemaVersion)
        {
            throw new InvalidDataException($"written by a newer console (schema {raw.SchemaVersion})");
        }

        if (!PackageEntry.IsValidId(id))
        {
            throw new InvalidDataException("file name is not a package id");
        }

        DetectRule? detect = raw.Detect switch
        {
            { UninstallName: { } name } when !string.IsNullOrWhiteSpace(name) => new DetectRule(DetectKind.UninstallName, name.Trim()),
            { Path: { } path } when !string.IsNullOrWhiteSpace(path) => new DetectRule(DetectKind.Path, path.Trim()),
            _ => null,
        };
        var file = raw.File?.Trim() ?? string.Empty;
        var entry = new PackageEntry
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(raw.Name) ? id : raw.Name.Trim(),
            Version = raw.Version?.Trim() ?? string.Empty,
            Family = ParseFamily(raw.Family, file),
            File = file,
            Run = raw.Run?.Trim() ?? string.Empty,
            Size = raw.Size ?? 0,
            Sha256 = raw.Sha256?.Trim().ToLowerInvariant() ?? string.Empty,
            Silent = raw.Silent?.Trim() ?? string.Empty,
            Detect = detect ?? new DetectRule(DetectKind.UninstallName, string.Empty),
            TimeoutMinutes = raw.TimeoutMinutes ?? (int)Defaults.PackageInstallTimeout.TotalMinutes,
            ChangedBy = raw.ChangedBy ?? string.Empty,
            ChangedAtUnix = DateTimeOffset.TryParse(raw.ChangedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeSeconds() : 0,
        };
        return entry.Validate() is PackageProblem.None ? entry : throw new InvalidDataException(entry.Validate().ToString());
    }

    private static InstallerFamily ParseFamily(string? value, string file) =>
        string.IsNullOrWhiteSpace(value) && file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
            ? InstallerFamily.Msi
            : InstallPackageRequest.ParseFamily(value);

    private void WriteYaml(PackageEntry entry)
    {
        Directory.CreateDirectory(Root);
        var path = YamlPath(entry.Id);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, Serialize(entry));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // A torn half left by a full disk is not an entry, and must not hold the space either.
            TryDelete(temporary);
            throw;
        }
    }

    private void RemoveOtherInstallers(PackageEntry entry)
    {
        var keep = Path.GetDirectoryName(InstallerPath(entry))!;
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(Root, entry.Id)))
        {
            if (!string.Equals(Path.GetFullPath(directory), Path.GetFullPath(keep), StringComparison.Ordinal))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException ex)
                {
                    _log.LogWarning("package catalog: old installer of {Id} not removed: {Message}", entry.Id, ex.Message);
                }
            }
        }
    }

    private string UniqueId(string name)
    {
        var baseId = PackageEntry.IdFor(name);
        var id = baseId;
        for (var n = 2; File.Exists(YamlPath(id)) || Directory.Exists(Path.Combine(Root, id)); n++)
        {
            id = $"{baseId[..Math.Min(baseId.Length, 44)]}-{n}";
        }

        return id;
    }

    private static void RemoveIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
