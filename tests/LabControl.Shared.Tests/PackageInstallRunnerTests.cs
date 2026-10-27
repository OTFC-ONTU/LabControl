using LabControl.Shared.Link;
using LabControl.Shared.Packages;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// <c>install_package</c>'s PC-side rules (M7 portion 4, D-89) over a host that only pretends:
/// the modes, the exam refusal, the exit codes, the rule after the installer, the timeout that
/// ends a stuck installer, the "still installing" lines, and the staged file always removed.
/// </summary>
public sealed class PackageInstallRunnerTests
{
    private static readonly PackageEntry Entry = new()
    {
        Id = "tool", Name = "Tool", Version = "2.0", Family = InstallerFamily.Msi, File = "tool.msi", Size = 4,
        Sha256 = new string('a', 64), Silent = "/qn /norestart", Detect = new DetectRule(DetectKind.UninstallName, "Tool"), TimeoutMinutes = 15,
    };

    private sealed class Host : IPackageHost
    {
        public bool ExamHeld { get; set; }

        public string StagingDirectory { get; } = Path.Combine(Path.GetTempPath(), "labcontrol-staging-" + Guid.NewGuid().ToString("n"));

        public bool Installed { get; set; }

        public Func<InstallPackageRequest, Task<InstallerExit>> Run { get; set; } = _ => Task.FromResult(new InstallerExit(0, false));

        public bool InstallsOnSuccess { get; set; } = true;

        public int Runs { get; private set; }

        public string? StagedSeen { get; private set; }

        public DetectFinding Detect(DetectRule rule) => new(Installed, Installed ? "2.0.1" : null);

        public async Task<InstallerExit> RunInstallerAsync(InstallPackageRequest request, string stagedPath, CancellationToken token)
        {
            Runs++;
            StagedSeen = stagedPath;
            Assert.True(File.Exists(stagedPath));
            var exit = await Run(request);
            if (exit.ExitCode is 0 or 3010 or 1641 && !exit.TimedOut && InstallsOnSuccess) Installed = true;
            return exit;
        }
    }

    private static Task<long> Pull(string reference, string sha, Stream destination, CancellationToken token, Func<long, Task>? progress)
    {
        destination.Write("data"u8);
        return Task.FromResult(4L);
    }

    private static Job JobFor(InstallMode mode, PackageEntry? entry = null)
    {
        var job = new Job { Id = Guid.NewGuid().ToString("n"), Kind = Job.Types.Kind.InstallPackage };
        job.Args.Add(InstallPackageRequest.For(entry ?? Entry, mode).ToArgs());
        return job;
    }

    private static async Task<(JobResult Result, List<string> Lines)> RunAsync(Host host, Job job, TimeSpan? heartbeat = null, PackageInstallRunner.PullFile? pull = null)
    {
        var lines = new List<string>();
        var runner = new PackageInstallRunner(host, pull ?? Pull, heartbeat);
        var result = await runner.RunAsync(job, p => { lock (lines) lines.Add(p.Line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        return (result, lines);
    }

    [Fact]
    public async Task An_install_runs_the_installer_and_reports_what_the_rule_then_finds()
    {
        var host = new Host();
        var (result, lines) = await RunAsync(host, JobFor(InstallMode.Install));
        Assert.True(result.Ok);
        Assert.Equal(PackageOutcomes.Installed, result.Outcome);
        Assert.Contains("2.0.1", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, host.Runs);
        Assert.EndsWith("tool.msi", host.StagedSeen, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(host.StagedSeen)));
        Assert.Contains(lines, l => l.Contains("tool.msi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_skips_a_present_program_and_check_never_pulls()
    {
        var host = new Host { Installed = true };
        var pulled = false;
        Task<long> Spy(string r, string s, Stream d, CancellationToken t, Func<long, Task>? p) { pulled = true; return Task.FromResult(0L); }

        var (missing, _) = await RunAsync(host, JobFor(InstallMode.Missing), pull: Spy);
        Assert.Equal(PackageOutcomes.Present, missing.Outcome);
        Assert.True(missing.Ok);

        var (check, _) = await RunAsync(host, JobFor(InstallMode.Check), pull: Spy);
        Assert.Equal(PackageOutcomes.Present, check.Outcome);
        host.Installed = false;
        var (absent, _) = await RunAsync(host, JobFor(InstallMode.Check), pull: Spy);
        Assert.Equal(PackageOutcomes.Absent, absent.Outcome);
        Assert.True(absent.Ok);

        Assert.False(pulled);
        Assert.Equal(0, host.Runs);
    }

    [Fact]
    public async Task An_exam_refuses_before_anything_happens()
    {
        var host = new Host { ExamHeld = true };
        var (result, _) = await RunAsync(host, JobFor(InstallMode.Check));
        Assert.False(result.Ok);
        Assert.Equal(PackageOutcomes.ExamHeld, result.Outcome);
        Assert.Equal(0, host.Runs);
    }

    [Theory]
    [InlineData(1603, PackageOutcomes.Failed, false)]
    [InlineData(1618, PackageOutcomes.Failed, false)]
    [InlineData(3010, PackageOutcomes.RebootRequired, true)]
    [InlineData(1641, PackageOutcomes.RebootRequired, true)]
    public async Task Exit_codes_become_outcomes(int code, string outcome, bool ok)
    {
        var host = new Host { Run = _ => Task.FromResult(new InstallerExit(code, false)) };
        var (result, _) = await RunAsync(host, JobFor(InstallMode.Install));
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(ok, result.Ok);
        Assert.Equal(code, result.ExitCode);
        if (!ok) Assert.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_without_the_program_found_is_not_success()
    {
        var host = new Host { InstallsOnSuccess = false };
        var (result, _) = await RunAsync(host, JobFor(InstallMode.Install));
        Assert.False(result.Ok);
        Assert.Equal(PackageOutcomes.NotDetected, result.Outcome);
    }

    [Fact]
    public async Task A_stuck_installer_is_reported_with_heartbeats_on_the_way()
    {
        var host = new Host
        {
            Run = async _ =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400));
                return new InstallerExit(-1, TimedOut: true);
            },
        };
        var (result, lines) = await RunAsync(host, JobFor(InstallMode.Install), heartbeat: TimeSpan.FromMilliseconds(50));
        Assert.False(result.Ok);
        Assert.Equal(PackageOutcomes.TimedOut, result.Outcome);
        Assert.Contains("15", result.Message, StringComparison.Ordinal);
        Assert.True(lines.Count(l => l.Contains("15", StringComparison.Ordinal)) >= 2);
        Assert.False(Directory.Exists(Path.GetDirectoryName(host.StagedSeen)));
    }

    [Fact]
    public async Task A_failed_pull_runs_nothing_and_leaves_nothing()
    {
        var host = new Host();
        Task<long> Broken(string r, string s, Stream d, CancellationToken t, Func<long, Task>? p) => throw new FilePullException("hash mismatch");
        var (result, _) = await RunAsync(host, JobFor(InstallMode.Install), pull: Broken);
        Assert.Equal(PackageOutcomes.NotRun, result.Outcome);
        Assert.Contains("hash mismatch", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.Runs);
        Assert.Empty(Directory.Exists(host.StagingDirectory) ? Directory.EnumerateFileSystemEntries(host.StagingDirectory) : []);
    }

    [Fact]
    public void The_request_travels_whole_and_a_bad_one_is_refused()
    {
        var job = JobFor(InstallMode.Missing);
        Assert.True(InstallPackageRequest.TryParse(job, out var request, out _));
        Assert.Equal(InstallPackageRequest.For(Entry, InstallMode.Missing), request);

        foreach (var (key, value) in new[] { ("ref", "x"), ("file", "../tool.msi"), ("mode", "uninstall"), ("timeout_seconds", "10"),
                     ("detect_kind", "registry"), ("detect", ""), ("package", "Tool!"), ("silent", "/qn\n/x") })
        {
            var bad = job.Clone();
            bad.Args[key] = value;
            Assert.False(InstallPackageRequest.TryParse(bad, out _, out _), key);
        }
    }

    [Fact]
    public void An_msi_runs_through_msiexec_and_anything_else_as_itself()
    {
        Assert.Equal((@"C:\Windows\System32\msiexec.exe", "/i \"C:\\s\\tool.msi\" /qn /norestart"),
            InstallPackageRequest.CommandLine(InstallerFamily.Msi, @"C:\s\tool.msi", "/qn /norestart", @"C:\Windows\System32").Normalize());
        Assert.Equal((@"C:\s\setup.exe", "/S"), InstallPackageRequest.CommandLine(InstallerFamily.Nsis, @"C:\s\setup.exe", "/S", @"C:\Windows\System32"));
    }
}

internal static class CommandLineTestExtensions
{
    /// <summary>Path.Combine on macOS joins with '/', a PC with '\'; the test compares in Windows form.</summary>
    public static (string, string) Normalize(this (string Executable, string Arguments) line) => (line.Executable.Replace('/', '\\'), line.Arguments);
}
