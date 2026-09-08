using System.Globalization;
using System.Text;
using Google.Protobuf;
using LabControl.Shared.Files;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Setup;

/// <summary>One file of a build as it lies on the console's disk.</summary>
public sealed record AgentBuildFile(string Name, string Path, long Size, string Sha256);

/// <summary>
/// An agent build on the console's disk, ready to be pushed (ROADMAP M2, D-33): the two
/// executables a version directory holds, hashed, plus the <c>UpdateManifest</c> the agent
/// pulls first. Found in a folder that holds <c>agent.exe</c> and <c>session.exe</c> side by
/// side (as <c>dev-install.ps1</c> expects) or in <c>publish-all.sh</c>'s
/// <c>artifacts/&lt;rid&gt;/</c> layout with one sub-folder per project.
/// </summary>
public sealed class AgentBuild
{
    private AgentBuild(string folder, string version, IReadOnlyList<AgentBuildFile> files, byte[] manifest)
    {
        Folder = folder;
        Version = version;
        Files = files;
        Manifest = manifest;
        ManifestSha256 = FileHash.Sha256Hex(manifest);
    }

    public string Folder { get; }

    /// <summary>
    /// The version directory name on the PC: the version number plus the first
    /// <see cref="Defaults.BuildIdLength"/> hex digits of a domain-separated digest of both executable names and SHA-256 hashes, so that
    /// two builds of the same version number — the normal case while developing — land side
    /// by side, and pushing the very same build twice is recognised as such.
    /// </summary>
    public string Version { get; }

    public IReadOnlyList<AgentBuildFile> Files { get; }

    /// <summary>The serialized <c>UpdateManifest</c>: what the job's <c>ref</c> points at.</summary>
    public byte[] Manifest { get; }

    public string ManifestSha256 { get; }

    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>The publish sub-folders <c>publish-all.sh</c> writes, tried when the folder itself has no <c>agent.exe</c>.</summary>
    public const string AgentPublishFolder = "LabControl.Agent";

    public const string SessionPublishFolder = "LabControl.Agent.Session";

    /// <summary>
    /// Reads a build from <paramref name="folder"/>. <paramref name="baseVersion"/> is the
    /// version number the build was made with (the console's own, normally — both come from
    /// the same tree); the agent checks it against what the pushed <c>agent.exe</c> reports
    /// before installing anything. Returns <c>false</c> with a plain-language reason when the
    /// folder is not a build.
    /// </summary>
    public static bool TryLoad(string folder, string baseVersion, out AgentBuild build, out string error, string minimumInstalledVersion = "0.0.0")
    {
        build = null!;
        error = string.Empty;

        if (!System.Version.TryParse(minimumInstalledVersion, out _) || !InstallLayout.IsValidVersion(minimumInstalledVersion))
        {
            error = "The minimum installed version must be a numeric version.";
            return false;
        }

        baseVersion = baseVersion.Trim();
        if (!InstallLayout.IsValidVersion(baseVersion) || baseVersion.Contains('+'))
        {
            error = $"'{baseVersion}' is not a version number (something like 0.1.0).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            error = $"'{folder}' is not a folder.";
            return false;
        }

        folder = Path.GetFullPath(folder);
        var agentPath = Locate(folder, Defaults.AgentExecutableName, AgentPublishFolder);
        var sessionPath = Locate(folder, Defaults.SessionExecutableName, SessionPublishFolder);

        if (agentPath is null || sessionPath is null)
        {
            error = $"{folder} does not hold {Defaults.AgentExecutableName} and {Defaults.SessionExecutableName} " +
                    $"(side by side, or under {AgentPublishFolder}\\ and {SessionPublishFolder}\\ as publish-all.sh writes them).";
            return false;
        }

        var files = new List<AgentBuildFile>();
        foreach (var path in new[] { agentPath, sessionPath })
        {
            var info = new FileInfo(path);
            if (info.Length == 0)
            {
                error = $"{path} is empty.";
                return false;
            }

            files.Add(new AgentBuildFile(Path.GetFileName(path), path, info.Length, FileHash.Sha256HexOfFile(path)));
        }

        // Hash the whole executable bundle: helper-only fixes must not look already installed.
        // Fixed names cannot contain NUL, making these field separators unambiguous.
        var identity = "LabControl.AgentBundle.v1\0" + string.Concat(files.OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => file.Name + "\0" + file.Sha256 + "\0"));
        var version = baseVersion + "+" + FileHash.Sha256Hex(Encoding.UTF8.GetBytes(identity))[..Defaults.BuildIdLength];

        var manifest = new UpdateManifest { Version = version, MinInstalledVersion = minimumInstalledVersion };
        foreach (var file in files)
        {
            manifest.Files.Add(new UpdateFile { RelativePath = file.Name, Size = file.Size, Sha256 = file.Sha256 });
        }

        build = new AgentBuild(folder, version, files, manifest.ToByteArray());
        return true;
    }

    /// <summary>A one-line description for the dialog and the log: files, sizes, the version directory name.</summary>
    public string Describe() =>
        string.Join(", ", Files.Select(f => $"{f.Name} {Megabytes(f.Size)}")) + $" → app\\{Version}";

    public static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static string? Locate(string folder, string fileName, string publishFolder)
    {
        var flat = Path.Combine(folder, fileName);
        if (File.Exists(flat))
        {
            return flat;
        }

        var published = Path.Combine(folder, publishFolder, fileName);
        return File.Exists(published) ? published : null;
    }
}
