using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Lab;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>The console's grant rules for collected work (D-77), with a collection held in progress.</summary>
public sealed class WorkCollectionsTests
{
    private static (WorkCollections Collections, JobQueue Jobs, FileUploads Uploads, string Destination) Rig()
    {
        var jobs = new JobQueue("lab", "instance");
        var uploads = new FileUploads();
        var events = new EventLog(TestConsole.TempDirectory());
        var collections = new WorkCollections(
            (ids, request) => ids.Select(id => jobs.Create(id, Job.Types.Kind.CollectFiles, DateTimeOffset.UtcNow, agentOnline: true, request.ToArgs())).ToList(),
            id => id == "pc-1" ? 1 : 2, events, uploads, () => DateTimeOffset.UtcNow);
        jobs.Updated += collections.OnJobUpdated;
        return (collections, jobs, uploads, TestConsole.TempDirectory());
    }

    private static readonly string Hash = new('a', 64);

    [Fact]
    public void A_grant_goes_only_to_the_pc_whose_job_it_is_and_the_same_request_gets_the_same_grant()
    {
        var (collections, jobs, uploads, destination) = Rig();
        var collection = collections.Start("Lab", destination, @"Desktop\Exam", ["pc-1", "pc-2"]);
        var job = jobs.All().Single(j => j.AgentId == "pc-1");

        Assert.Empty(collections.RequestUpload("pc-2", new UploadRequest { JobId = job.Id, Size = 10, Sha256 = Hash }).Reference);
        var grant = collections.RequestUpload("pc-1", new UploadRequest { JobId = job.Id, Size = 10, Sha256 = Hash });
        Assert.NotEmpty(grant.Reference);
        Assert.Equal(grant.Reference, collections.RequestUpload("pc-1", new UploadRequest { JobId = job.Id, Size = 10, Sha256 = Hash }).Reference);
        Assert.Equal(1, uploads.InProgressCount);
        Assert.Single(Directory.GetFiles(collection.Directory, "*.partial"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_or_finished_without_upload_collection_gives_its_grant_and_staging_file_back(bool okWithoutTheUpload)
    {
        var (collections, jobs, uploads, destination) = Rig();
        var collection = collections.Start("Lab", destination, @"Desktop\Exam", ["pc-1"]);
        var job = jobs.All().Single();
        var grant = collections.RequestUpload("pc-1", new UploadRequest { JobId = job.Id, Size = 10, Sha256 = Hash });
        Assert.Equal(1, uploads.InProgressCount);

        jobs.Complete(new JobResult { JobId = job.Id, Ok = okWithoutTheUpload, Message = "gone", ArtifactRef = okWithoutTheUpload ? grant.Reference : string.Empty }, DateTimeOffset.UtcNow);

        Assert.True(SpinWait.SpinUntil(() => collection.Rows.Single().IsFinished, TimeSpan.FromSeconds(10)));
        Assert.Equal(CollectedState.Failed, collection.Rows.Single().State);
        Assert.Equal(0, uploads.InProgressCount);
        Assert.Empty(Directory.GetFiles(collection.Directory, "*.partial"));

        // The row settles first and report.txt is written right after, on the unpacking task when
        // the PC said ok — waited for, not read at once (it lost that race on the Linux CI).
        var report = Path.Combine(collection.Directory, Defaults.CollectReportFileName);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(report) && File.ReadAllText(report).Contains("FAILED", StringComparison.Ordinal), TimeSpan.FromSeconds(10)),
            File.Exists(report) ? File.ReadAllText(report) : "no report.txt");
    }

    [Fact]
    public void Closing_the_session_gives_every_grant_back()
    {
        var (collections, jobs, uploads, destination) = Rig();
        var collection = collections.Start("Lab", destination, @"Desktop\Exam", ["pc-1", "pc-2"]);
        foreach (var job in jobs.All())
        {
            collections.RequestUpload(job.AgentId, new UploadRequest { JobId = job.Id, Size = 10, Sha256 = Hash });
        }

        Assert.Equal(2, uploads.InProgressCount);
        collections.Close();
        Assert.Equal(0, uploads.InProgressCount);
        Assert.Empty(Directory.GetFiles(collection.Directory, "*.partial"));
    }
}
