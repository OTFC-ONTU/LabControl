namespace LabControl.Shared.Setup;

/// <summary>
/// The side-by-side version layout on a student PC (ARCHITECTURE §5, D-19):
/// <c>&lt;root&gt;\app\&lt;version&gt;\agent.exe</c>, with <c>app\current</c> and
/// <c>app\previous</c> naming versions. Pure path arithmetic over a configurable root, so
/// the installer, the agent and the tests on the Mac all agree on where things are.
/// </summary>
public sealed class InstallLayout
{
    public InstallLayout(string root) => Root = Path.GetFullPath(root);

    /// <summary>The layout of a real PC.</summary>
    public static InstallLayout Default => new(Defaults.AgentInstallDirectory);

    public string Root { get; }

    public string AppDirectory => Path.Combine(Root, Defaults.AgentAppDirectoryName);

    public string CurrentFile => Path.Combine(AppDirectory, Defaults.CurrentVersionFileName);

    public string PreviousFile => Path.Combine(AppDirectory, Defaults.PreviousVersionFileName);

    public string VersionDirectory(string version) =>
        Path.Combine(AppDirectory, IsValidVersion(version) ? version : throw new ArgumentException($"'{version}' is not a version directory name.", nameof(version)));

    public string AgentExecutable(string version) => Path.Combine(VersionDirectory(version), Defaults.AgentExecutableName);

    public string SessionExecutable(string version) => Path.Combine(VersionDirectory(version), Defaults.SessionExecutableName);

    /// <summary>Every version present on disk, newest first by semantic version where it parses.</summary>
    public IReadOnlyList<string> InstalledVersions()
    {
        if (!Directory.Exists(AppDirectory))
        {
            return [];
        }

        return Directory.GetDirectories(AppDirectory)
            .Select(Path.GetFileName)
            .Where(name => name is not null && IsValidVersion(name))
            .Select(name => name!)
            .OrderByDescending(name => Version.TryParse(name, out var parsed) ? parsed : new Version(0, 0))
            .ThenByDescending(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public string? ReadCurrent() => ReadMarker(CurrentFile);

    public string? ReadPrevious() => ReadMarker(PreviousFile);

    public void WriteCurrent(string version) => WriteMarker(CurrentFile, version);

    public void WritePrevious(string version) => WriteMarker(PreviousFile, version);

    /// <summary>Removes <c>app\previous</c>: the version on trial has been accepted.</summary>
    public void ClearPrevious()
    {
        if (File.Exists(PreviousFile))
        {
            File.Delete(PreviousFile);
        }
    }

    /// <summary>
    /// The version directory a running executable lives in, or <c>null</c> when it runs from
    /// somewhere else (a build directory, a USB stick).
    /// </summary>
    public string? VersionOf(string executablePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        if (directory is null)
        {
            return null;
        }

        var parent = Path.GetDirectoryName(directory);
        if (parent is null || !string.Equals(Path.GetFullPath(parent), Path.GetFullPath(AppDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Path.GetFileName(directory);
        return IsValidVersion(name) ? name : null;
    }

    /// <summary>A version directory name: something like <c>0.2.0</c>, never a path.</summary>
    public static bool IsValidVersion(string version) =>
        !string.IsNullOrWhiteSpace(version)
        && version.Length <= 64
        && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+')
        && version[0] != '.';

    private static string? ReadMarker(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path).Trim();
        return IsValidVersion(text) ? text : null;
    }

    private void WriteMarker(string path, string version)
    {
        if (!IsValidVersion(version))
        {
            throw new ArgumentException($"'{version}' is not a version directory name.", nameof(version));
        }

        Directory.CreateDirectory(AppDirectory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, version + Environment.NewLine);
        File.Move(temporary, path, overwrite: true);
    }
}
