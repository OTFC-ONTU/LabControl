using System.IO.Compression;
using LabControl.Shared.Packages;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// <c>.zip</c> packages (D-89 addendum): which installer inside is proposed, which names may run,
/// the inspection of the chosen entry under the archive's own hash, the unpacking rules on the PC,
/// and an install that runs the entry from its own folder with its files beside it.
/// </summary>
public sealed class PackageArchiveTests
{
    private static string Zip(params (string Name, byte[] Data)[] entries)
    {
        var directory = Path.Combine(Path.GetTempPath(), "labcontrol-zip-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "driver.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        return path;
    }

    private static byte[] NsisStub() =>
        [.. File.ReadAllBytes(typeof(Google.Protobuf.MessageParser).Assembly.Location), 0, 0, 0, 0, 0xEF, 0xBE, 0xAD, 0xDE, .. "NullsoftInst"u8];

    [Fact]
    public void The_likeliest_installer_comes_first()
    {
        var path = Zip(("Tools/helper.exe", [1]), ("Win10/x64/setup.exe", [1]), ("readme.txt", [1]), ("setup.exe", [1]),
            ("__MACOSX/setup.exe", [1]), ("app.msi", [1]), ("aaa.exe", [1]));
        using var file = File.OpenRead(path);
        Assert.Equal(["setup.exe", "app.msi", "aaa.exe", "Tools/helper.exe", "Win10/x64/setup.exe"], PackageArchive.Candidates(file));
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("Win10/x64/Setup.EXE", true)]
    [InlineData("driver.msi", true)]
    [InlineData("../setup.exe", false)]
    [InlineData("/setup.exe", false)]
    [InlineData("a\\setup.exe", false)]
    [InlineData("readme.txt", false)]
    [InlineData("CON/setup.exe", false)]
    [InlineData("", false)]
    public void Only_an_installer_inside_the_folder_may_run(string run, bool valid) => Assert.Equal(valid, PackageArchive.IsValidRun(run));

    [Fact]
    public async Task An_archive_is_inspected_by_its_chosen_installer_under_its_own_hash()
    {
        var path = Zip(("readme.txt", "x"u8.ToArray()), ("setup.exe", NsisStub()), ("Other/tool.exe", NsisStub()));
        var info = await InstallerInspector.InspectAsync(path, null, TestContext.Current.CancellationToken);

        Assert.True(info.IsArchive);
        Assert.Equal(["setup.exe", "Other/tool.exe"], info.Runs);
        Assert.Equal("setup.exe", info.Run);
        Assert.Equal(InstallerFamily.Nsis, info.Family);
        Assert.Equal("driver.zip", info.FileName);
        Assert.Equal(new FileInfo(path).Length, info.Size);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), info.Sha256);

        var other = await InstallerInspector.InspectArchiveEntryAsync(path, info, "Other/tool.exe", TestContext.Current.CancellationToken);
        Assert.Equal("Other/tool.exe", other.Run);
        Assert.Equal(info.Sha256, other.Sha256);

        var empty = await InstallerInspector.InspectAsync(Zip(("readme.txt", [1])), null, TestContext.Current.CancellationToken);
        Assert.True(empty.IsArchive);
        Assert.Empty(empty.Runs!);
        Assert.Null(empty.Run);
    }

    [Theory]
    [InlineData("../evil.exe")]
    [InlineData("a/../../evil.exe")]
    [InlineData("C:/evil.exe")]
    public async Task An_entry_that_would_land_outside_is_refused(string name)
    {
        var path = Zip(("setup.exe", [1]), (name, [2]));
        var target = Path.Combine(Path.GetTempPath(), "labcontrol-unzip-" + Guid.NewGuid().ToString("n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageArchive.ExtractAsync(path, target, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(target)!, "evil.exe")));
    }

    [Fact]
    public async Task A_link_inside_the_archive_is_refused()
    {
        var directory = Path.Combine(Path.GetTempPath(), "labcontrol-zip-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "links.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var link = zip.CreateEntry("data");
            link.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using var stream = link.Open();
            stream.Write("/etc/passwd"u8);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => PackageArchive.ExtractAsync(path, Path.Combine(directory, "out"), TestContext.Current.CancellationToken));
    }

    private sealed class Host : IPackageHost
    {
        public bool ExamHeld => false;

        public string StagingDirectory { get; } = Path.Combine(Path.GetTempPath(), "labcontrol-staging-" + Guid.NewGuid().ToString("n"));

        public string? Ran { get; private set; }

        public bool SiblingThere { get; private set; }

        private bool _installed;

        public DetectFinding Detect(DetectRule rule) => new(_installed, null);

        public Task<InstallerExit> RunInstallerAsync(InstallPackageRequest request, string stagedPath, CancellationToken token)
        {
            Ran = stagedPath;
            SiblingThere = File.Exists(Path.Combine(Path.GetDirectoryName(stagedPath)!, "data1.cab"));
            _installed = true;
            return Task.FromResult(new InstallerExit(0, false));
        }
    }

    [Fact]
    public async Task An_archive_package_runs_its_installer_beside_its_files_and_leaves_nothing()
    {
        var path = Zip(("Win10/setup.exe", [1, 2, 3]), ("Win10/data1.cab", [4]), ("readme.txt", [5]));
        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var entry = new PackageEntry
        {
            Id = "realtek", Name = "Realtek", Family = InstallerFamily.InstallShield, File = "driver.zip", Run = "Win10/setup.exe", Size = bytes.Length,
            Sha256 = sha, Silent = "-s", Detect = new DetectRule(DetectKind.UninstallName, "Realtek Ethernet"),
        };
        Assert.Equal(PackageProblem.None, entry.Validate());
        Assert.Equal(PackageProblem.RunInvalid, (entry with { Run = "" }).Validate());
        Assert.Equal(PackageProblem.RunInvalid, (entry with { File = "setup.exe" }).Validate());

        var job = new Job { Id = "j1", Kind = Job.Types.Kind.InstallPackage };
        job.Args.Add(InstallPackageRequest.For(entry, InstallMode.Install).ToArgs());
        Assert.True(InstallPackageRequest.TryParse(job, out var request, out _));
        Assert.Equal("Win10/setup.exe", request.Run);

        var host = new Host();
        var runner = new PackageInstallRunner(host, (r, s, d, t, p) => { d.Write(bytes); return Task.FromResult((long)bytes.Length); });
        var result = await runner.RunAsync(job, _ => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(PackageOutcomes.Installed, result.Outcome);
        Assert.EndsWith(Path.Combine("x", "Win10", "setup.exe"), host.Ran, StringComparison.Ordinal);
        Assert.True(host.SiblingThere);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.StagingDirectory));
    }

    [Fact]
    public async Task A_broken_archive_is_reported_and_nothing_runs()
    {
        var bytes = "PK\u0003\u0004 not really a zip"u8.ToArray();
        var entry = new PackageEntry
        {
            Id = "broken", Name = "Broken", File = "broken.zip", Run = "setup.exe", Size = bytes.Length,
            Sha256 = new string('b', 64), Silent = "-s", Detect = new DetectRule(DetectKind.UninstallName, "Broken"),
        };
        var job = new Job { Id = "j2", Kind = Job.Types.Kind.InstallPackage };
        job.Args.Add(InstallPackageRequest.For(entry, InstallMode.Install).ToArgs());
        var host = new Host();
        var runner = new PackageInstallRunner(host, (r, s, d, t, p) => { d.Write(bytes); return Task.FromResult((long)bytes.Length); });
        var result = await runner.RunAsync(job, _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Equal(PackageOutcomes.NotRun, result.Outcome);
        Assert.Null(host.Ran);
    }
}
