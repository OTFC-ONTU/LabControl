using Xunit;

using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using LabControl.Shared.Lab;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;

namespace LabControl.Console.Tests;

/// <summary>
/// The push-and-restart as the console sees it (M2 portion 4, D-33): the build's files and
/// manifest are offered, the agent pulls and verifies them, stops mid-job without a result,
/// comes back as the new version, and the re-sent job is answered by that version. The
/// behaviour here does what <c>FakeMachine</c> does; the Windows install is proved on the VM.
/// </summary>
public sealed class PushBuildTests
{
    private static string WriteBuild()
    {
        var folder = Path.Combine(Path.GetTempPath(), "labcontrol-push-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);
        var random = new Random(42);
        var agent = new byte[200_000];
        random.NextBytes(agent);
        File.WriteAllBytes(Path.Combine(folder, Defaults.AgentExecutableName), agent);
        File.WriteAllText(Path.Combine(folder, Defaults.SessionExecutableName), "session helper bytes");
        return folder;
    }

    [Fact]
    public async Task A_pushed_build_is_pulled_verified_and_confirmed_by_the_new_version()
    {
        var folder = WriteBuild();
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 6, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        try
        {
            Assert.True(AgentBuild.TryLoad(folder, "0.1.0", out var build, out var error), error);

            var pulled = new List<(string Name, long Bytes)>();
            var runs = 0;
            pc.Behaviour.OnJob = async job =>
            {
                Assert.True(SelfUpdateRequest.TryParse(job, out var request, out var problem), problem);
                Interlocked.Increment(ref runs);

                // The new version: answers the re-sent job by recognising its own version.
                if (request.Version == pc.Behaviour.Version)
                {
                    return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = $"Running {request.Version} now." };
                }

                using var manifestBytes = new MemoryStream();
                await pc.Link.PullFileAsync(request.ManifestReference, request.ManifestSha256, manifestBytes, CancellationToken.None);
                Assert.True(UpdateBundle.TryRead(manifestBytes.ToArray(), request, out var manifest, out problem), problem);

                foreach (var file in manifest.Files)
                {
                    var bytes = await pc.Link.PullFileAsync(file.Sha256, file.Sha256, Stream.Null, CancellationToken.None);
                    Assert.Equal(file.Size, bytes);
                    pulled.Add((file.RelativePath, bytes));
                }

                // The outgoing version: the service is stopped mid-job, no result is sent.
                pc.Behaviour.Version = request.Version;
                pc.Link.Disconnect("restarting after the update (test)");
                throw new OperationCanceledException();
            };

            var job = console.Session.PushAgentBuild([pc.AgentId], build).Single();
            Assert.Equal(Job.Types.Kind.SelfUpdate, job.Kind);
            Assert.Equal(build.Version, job.Args[SelfUpdateRequest.VersionKey]);
            Assert.Equal((int)Defaults.SelfUpdateJobTimeout.TotalSeconds, job.TimeoutSeconds);
            Assert.True(FileHash.LooksLikeSha256(job.Args[SelfUpdateRequest.ReferenceKey]));
            Assert.Equal(3, console.Session.Files.Count);  // agent.exe, session.exe, the manifest

            Assert.True(await Wait.UntilAsync(() => job.State == JobState.Succeeded, TimeSpan.FromSeconds(30)));
            Assert.Equal($"Running {build.Version} now.", job.Message);
            Assert.Equal(2, runs);
            Assert.Equal([(Defaults.AgentExecutableName, 200_000L), (Defaults.SessionExecutableName, (long)"session helper bytes".Length)], pulled);

            // The tile now says the new version: Hello carried it on the second link.
            var machine = console.Session.Registry.Document.Machines.Single(m => m.AgentId == pc.AgentId);
            Assert.Equal(build.Version, machine.AgentVersion);
            Assert.Equal(0, pc.Link.RunningJobs);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_bundle_the_agent_cannot_verify_fails_the_job_and_installs_nothing()
    {
        var folder = WriteBuild();
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.Install(console, 7, console.IssueCodes(1)[0]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        try
        {
            Assert.True(AgentBuild.TryLoad(folder, "0.1.0", out var build, out var error), error);

            // The console lies about the manifest's hash; the agent's pull must refuse it.
            pc.Behaviour.OnJob = async job =>
            {
                Assert.True(SelfUpdateRequest.TryParse(job, out var request, out _));
                try
                {
                    await pc.Link.PullFileAsync(request.ManifestReference, new string('0', 64), Stream.Null, CancellationToken.None);
                    return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = "should not get here" };
                }
                catch (FilePullException ex)
                {
                    return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = ex.Message + " Nothing was installed." };
                }
            };

            var job = console.Session.PushAgentBuild([pc.AgentId], build).Single();
            Assert.True(await Wait.UntilAsync(() => job.State == JobState.Failed, TimeSpan.FromSeconds(20)));
            Assert.Contains("hash check", job.Message);
            Assert.Contains("Nothing was installed", job.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
