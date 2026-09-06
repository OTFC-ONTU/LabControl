using Xunit;

using Google.Protobuf;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;

namespace LabControl.Shared.Tests;

/// <summary>
/// The push-and-restart's shared logic (M2 portion 4, D-33): reading a build folder,
/// naming the version directory, the job's arguments, the agent's reading of the manifest,
/// and the ledger releasing a job the agent stops mid-way. The Windows half — staging,
/// <c>ChangeServiceConfig</c>, the restart — is proved on the VM.
/// </summary>
public sealed class SelfUpdateTests
{
    private static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "labcontrol-build-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteBuild(string folder, string agentFolder = "", string sessionFolder = "")
    {
        Directory.CreateDirectory(Path.Combine(folder, agentFolder));
        Directory.CreateDirectory(Path.Combine(folder, sessionFolder));
        File.WriteAllBytes(Path.Combine(folder, agentFolder, Defaults.AgentExecutableName), new byte[300_000]);
        File.WriteAllText(Path.Combine(folder, sessionFolder, Defaults.SessionExecutableName), "session helper bytes");
    }

    [Fact]
    public void A_flat_build_folder_is_read_hashed_and_named_after_its_agent_exe()
    {
        var folder = TempFolder();
        try
        {
            WriteBuild(folder);

            Assert.True(AgentBuild.TryLoad(folder, "0.1.0", out var build, out var error), error);

            var agentHash = FileHash.Sha256HexOfFile(Path.Combine(folder, Defaults.AgentExecutableName));
            Assert.Equal("0.1.0+" + agentHash[..Defaults.BuildIdLength], build.Version);
            Assert.True(InstallLayout.IsValidVersion(build.Version));
            Assert.Equal("0.1.0", InstallLayout.BaseVersionOf(build.Version));

            Assert.Equal(2, build.Files.Count);
            Assert.Equal(Defaults.AgentExecutableName, build.Files[0].Name);
            Assert.Equal(300_000, build.Files[0].Size);
            Assert.Equal(agentHash, build.Files[0].Sha256);
            Assert.Equal(Defaults.SessionExecutableName, build.Files[1].Name);
            Assert.Equal(300_000 + "session helper bytes".Length, build.TotalBytes);

            var manifest = UpdateManifest.Parser.ParseFrom(build.Manifest);
            Assert.Equal(build.Version, manifest.Version);
            Assert.Equal(build.Files.Select(f => (f.Name, f.Size, f.Sha256)), manifest.Files.Select(f => (f.RelativePath, f.Size, f.Sha256)));
            Assert.Equal(FileHash.Sha256Hex(build.Manifest), build.ManifestSha256);
            Assert.Contains("app\\" + build.Version, build.Describe());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_publish_all_layout_is_read_too_and_the_same_bytes_give_the_same_version()
    {
        var flat = TempFolder();
        var published = TempFolder();
        try
        {
            WriteBuild(flat);
            WriteBuild(published, AgentBuild.AgentPublishFolder, AgentBuild.SessionPublishFolder);

            Assert.True(AgentBuild.TryLoad(flat, "0.1.0", out var first, out var error), error);
            Assert.True(AgentBuild.TryLoad(published, "0.1.0", out var second, out error), error);

            Assert.Equal(first.Version, second.Version);
            Assert.Equal(first.ManifestSha256, second.ManifestSha256);
        }
        finally
        {
            Directory.Delete(flat, recursive: true);
            Directory.Delete(published, recursive: true);
        }
    }

    [Fact]
    public void A_folder_without_the_two_executables_or_a_bad_version_number_is_refused_in_plain_language()
    {
        var folder = TempFolder();
        try
        {
            Assert.False(AgentBuild.TryLoad(folder, "0.1.0", out _, out var error));
            Assert.Contains(Defaults.SessionExecutableName, error);

            WriteBuild(folder);
            Assert.False(AgentBuild.TryLoad(folder, "not a version!", out _, out error));
            Assert.Contains("version number", error);
            Assert.False(AgentBuild.TryLoad(folder, "0.1.0+abc", out _, out error));

            Assert.False(AgentBuild.TryLoad(Path.Combine(folder, "missing"), "0.1.0", out _, out error));
            Assert.Contains("not a folder", error);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_job_arguments_round_trip_and_a_job_missing_them_is_refused()
    {
        var request = new SelfUpdateRequest("0.1.0+1a2b3c4d", new string('a', 64), new string('A', 64));
        var job = new Job { Id = "j1", Kind = Job.Types.Kind.SelfUpdate };
        foreach (var (key, value) in request.ToArgs())
        {
            job.Args[key] = value;
        }

        Assert.True(SelfUpdateRequest.TryParse(job, out var parsed, out var error), error);
        Assert.Equal(request.Version, parsed.Version);
        Assert.Equal(request.ManifestReference, parsed.ManifestReference);
        Assert.Equal(new string('a', 64), parsed.ManifestSha256);

        job.Args[SelfUpdateRequest.VersionKey] = "..\\evil";
        Assert.False(SelfUpdateRequest.TryParse(job, out _, out error));
        Assert.Contains(SelfUpdateRequest.VersionKey, error);

        Assert.False(SelfUpdateRequest.TryParse(new Job { Id = "j2", Kind = Job.Types.Kind.RunScript }, out _, out error));
        Assert.Contains("not a self_update", error);
    }

    [Fact]
    public void The_agent_reads_a_manifest_and_refuses_one_that_lies_or_walks_directories()
    {
        var request = new SelfUpdateRequest("0.1.0+1a2b3c4d", new string('a', 64), new string('a', 64));
        var good = new UpdateManifest { Version = request.Version };
        good.Files.Add(new UpdateFile { RelativePath = Defaults.AgentExecutableName, Size = 10, Sha256 = new string('b', 64) });
        good.Files.Add(new UpdateFile { RelativePath = Defaults.SessionExecutableName, Size = 20, Sha256 = new string('c', 64) });

        Assert.True(UpdateBundle.TryRead(good.ToByteArray(), request, out var manifest, out var error), error);
        Assert.Equal(2, manifest.Files.Count);

        var wrongVersion = good.Clone();
        wrongVersion.Version = "0.9.9";
        Assert.False(UpdateBundle.TryRead(wrongVersion.ToByteArray(), request, out _, out error));
        Assert.Contains("0.9.9", error);

        var traversal = good.Clone();
        traversal.Files[1].RelativePath = "..\\..\\Windows\\session.exe";
        Assert.False(UpdateBundle.TryRead(traversal.ToByteArray(), request, out _, out error));
        Assert.Contains("plain file name", error);

        var noHelper = new UpdateManifest { Version = request.Version };
        noHelper.Files.Add(good.Files[0]);
        Assert.False(UpdateBundle.TryRead(noHelper.ToByteArray(), request, out _, out error));
        Assert.Contains(Defaults.SessionExecutableName, error);

        var empty = new UpdateManifest { Version = request.Version };
        Assert.False(UpdateBundle.TryRead(empty.ToByteArray(), request, out _, out error));

        Assert.False(UpdateBundle.TryRead(new byte[] { 0xff, 0xff, 0xff }, request, out _, out error));
        Assert.Contains("not an UpdateManifest", error);
    }

    [Fact]
    public void Pushed_versions_sort_by_their_number_and_can_be_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), "labcontrol-layout-" + Guid.NewGuid().ToString("n"));
        var layout = new InstallLayout(root);
        try
        {
            foreach (var version in new[] { "0.1.0", "0.1.0+1a2b3c4d", "0.2.0+ffffffff", "0.1.1" })
            {
                Directory.CreateDirectory(layout.VersionDirectory(version));
            }

            var versions = layout.InstalledVersions();
            Assert.Equal("0.2.0+ffffffff", versions[0]);
            Assert.Equal("0.1.1", versions[1]);
            Assert.Equal(4, versions.Count);

            Assert.Equal("0.1.0+1a2b3c4d", layout.VersionOf(layout.AgentExecutable("0.1.0+1a2b3c4d")));

            layout.RemoveVersion("0.1.0+1a2b3c4d");
            Assert.DoesNotContain("0.1.0+1a2b3c4d", layout.InstalledVersions());
            layout.RemoveVersion("0.1.0+1a2b3c4d");  // already gone: not an error
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_forgotten_job_is_admitted_again()
    {
        var ledger = new JobLedger();
        var job = new Job { Id = "update-1", Kind = Job.Types.Kind.SelfUpdate };

        Assert.True(ledger.Admit(job).ShouldRun);
        Assert.True(ledger.Admit(job).DuplicateOfRunning);

        ledger.Forget(job.Id);
        Assert.Equal(0, ledger.RunningCount);
        Assert.True(ledger.Admit(job).ShouldRun);
    }
}
