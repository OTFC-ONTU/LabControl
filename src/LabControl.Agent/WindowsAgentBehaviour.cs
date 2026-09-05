using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent;

/// <summary>
/// What a real PC says about itself and does with a job. The link, enrolment, renewal and
/// idempotency are the shared library's; this is only the Windows-specific part (M2). In
/// this build the jobs a PC can run are still arriving one portion at a time, and a job it
/// cannot run yet is answered with a clear failure rather than silence.
/// </summary>
internal sealed class WindowsAgentBehaviour : IAgentBehaviour, IAsyncDisposable
{
    private readonly DirectoryAgentStore _store;
    private readonly ILogger _log;
    private readonly DateTimeOffset _bootedAt = WindowsInventory.BootTime();

    public WindowsAgentBehaviour(DirectoryAgentStore store, ILogger log)
    {
        _store = store;
        _log = log;
        Link = new AgentLink(store, this, log);

        Link.Linked += (_, name) => _log.LogInformation("{Pc}: linked to {Console}", Link.Name, name);
        Link.Unlinked += reason => _log.LogInformation("{Pc}: unlinked — {Reason}", Link.Name, reason);
        Link.Refused += reason => _log.LogWarning("{Pc}: refused — {Reason}", Link.Name, reason);
    }

    public AgentLink Link { get; }

    public void Start()
    {
        // Verified before the first link so the report is waiting for the console (D-29).
        foreach (var problem in DataDirectoryGuard.Check(Defaults.AgentDataDirectory))
        {
            _log.LogWarning("{Pc}: {Problem}", Link.Name, problem);
            Link.Report(Event.Types.Severity.Warning, "agent.data_acl", problem);
        }

        Link.Start();
    }

    // ------------------------------------------------------------------ IAgentBehaviour

    public void Describe(Hello hello)
    {
        hello.AgentVersion = Program.Version;
        hello.BootTimeUnix = _bootedAt.ToUnixTimeSeconds();
        hello.UpdateState = new UpdateState { Phase = UpdateState.Types.Phase.Stable };
    }

    public Inventory? DescribeInventory()
    {
        try
        {
            return WindowsInventory.Collect(_store.Config.Hostname);
        }
        catch (Exception ex)
        {
            // An inventory is nice to have; a failure to gather one must never cost the link.
            _log.LogWarning(ex, "{Pc}: inventory failed: {Message}", Link.Name, ex.Message);
            Link.Report(Event.Types.Severity.Warning, "agent.inventory_failed", $"Could not gather the inventory: {ex.Message}");
            return new Inventory { Hostname = Environment.MachineName };
        }
    }

    public Task<JobResult> RunJobAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        _log.LogInformation("{Pc}: job {Kind} {Id}", Link.Name, job.Kind, job.Id);

        // Power, scripts and the rest land in the next portions of M2 (ROADMAP). Until then
        // the console hears "not in this build" instead of a timeout.
        return Task.FromResult(new JobResult
        {
            JobId = job.Id,
            Ok = false,
            ExitCode = -1,
            Message = $"This agent build ({Program.Version}) does not run {job.Kind} jobs yet.",
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Link.DisposeAsync();
        _store.Dispose();
    }
}
