using Xunit;

using LabControl.Shared;
using LabControl.Shared.Setup;

namespace LabControl.Shared.Tests;

/// <summary>
/// The side-by-side version layout (D-19) as pure path arithmetic, so the installer, the
/// agent and the update path cannot disagree about where a version lives.
/// </summary>
public sealed class InstallLayoutTests
{
    [Fact]
    public void Paths_follow_the_documented_layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "labcontrol-layout-" + Guid.NewGuid().ToString("n"));
        var layout = new InstallLayout(root);

        Assert.Equal(Path.Combine(root, Defaults.AgentAppDirectoryName), layout.AppDirectory);
        Assert.Equal(Path.Combine(root, Defaults.AgentAppDirectoryName, Defaults.CurrentVersionFileName), layout.CurrentFile);
        Assert.Equal(Path.Combine(root, Defaults.AgentAppDirectoryName, "0.2.0", Defaults.AgentExecutableName), layout.AgentExecutable("0.2.0"));
        Assert.Equal(Path.Combine(root, Defaults.AgentAppDirectoryName, "0.2.0", Defaults.SessionExecutableName), layout.SessionExecutable("0.2.0"));
    }

    [Fact]
    public void Markers_round_trip_and_clear()
    {
        var root = Path.Combine(Path.GetTempPath(), "labcontrol-layout-" + Guid.NewGuid().ToString("n"));
        var layout = new InstallLayout(root);
        try
        {
            Assert.Null(layout.ReadCurrent());
            Assert.Null(layout.ReadPrevious());

            layout.WriteCurrent("0.2.0");
            layout.WritePrevious("0.1.0");
            Assert.Equal("0.2.0", layout.ReadCurrent());
            Assert.Equal("0.1.0", layout.ReadPrevious());

            layout.ClearPrevious();
            Assert.Null(layout.ReadPrevious());
            Assert.Equal("0.2.0", layout.ReadCurrent());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Installed_versions_are_listed_newest_first()
    {
        var root = Path.Combine(Path.GetTempPath(), "labcontrol-layout-" + Guid.NewGuid().ToString("n"));
        var layout = new InstallLayout(root);
        try
        {
            foreach (var version in new[] { "0.1.0", "0.10.0", "0.2.0" })
            {
                Directory.CreateDirectory(layout.VersionDirectory(version));
            }

            Directory.CreateDirectory(Path.Combine(layout.AppDirectory, ".junk"));

            Assert.Equal(["0.10.0", "0.2.0", "0.1.0"], layout.InstalledVersions());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Version_of_a_running_executable_is_read_from_its_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "labcontrol-layout-" + Guid.NewGuid().ToString("n"));
        var layout = new InstallLayout(root);

        Assert.Equal("0.2.0", layout.VersionOf(layout.AgentExecutable("0.2.0")));
        Assert.Null(layout.VersionOf(Path.Combine(root, Defaults.AgentExecutableName)));
        Assert.Null(layout.VersionOf(Path.Combine(Path.GetTempPath(), "elsewhere", Defaults.AgentExecutableName)));
    }

    [Theory]
    [InlineData("0.2.0", true)]
    [InlineData("0.2.0-beta+abc", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData(@"a\b", false)]
    [InlineData(".hidden", false)]
    public void Version_names_are_never_paths(string version, bool valid)
    {
        Assert.Equal(valid, InstallLayout.IsValidVersion(version));
        if (!valid)
        {
            Assert.Throws<ArgumentException>(() => new InstallLayout(Path.GetTempPath()).VersionDirectory(version));
        }
    }

    [Fact]
    public void A_corrupt_marker_reads_as_absent()
    {
        var root = Path.Combine(Path.GetTempPath(), "labcontrol-layout-" + Guid.NewGuid().ToString("n"));
        var layout = new InstallLayout(root);
        try
        {
            Directory.CreateDirectory(layout.AppDirectory);
            File.WriteAllText(layout.CurrentFile, "..\\..\\evil");
            Assert.Null(layout.ReadCurrent());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
