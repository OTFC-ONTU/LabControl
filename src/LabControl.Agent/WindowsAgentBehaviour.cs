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
    private readonly ScriptRunner _scripts;
    private readonly AgentUpdater _updater;
    private readonly UpdateTrialMonitor _trialMonitor;
    private readonly SetupReadinessMonitor _readinessMonitor;

    public WindowsAgentBehaviour(DirectoryAgentStore store, ILogger log)
    {
        _store = store;
        _log = log;
        Link = new AgentLink(store, this, log);
        _scripts = new ScriptRunner(Link, log);
        _updater = new AgentUpdater(Link, log, store.Authority);
        _trialMonitor = new UpdateTrialMonitor(Link, log);
        _readinessMonitor = new SetupReadinessMonitor(Link);

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
        hello.AgentVersion = Program.InstalledVersion;
        hello.BootTimeUnix = _bootedAt.ToUnixTimeSeconds();
        try { hello.UpdateState = UpdateTrialMonitor.Describe(Program.InstalledVersion); }
        catch (Exception ex)
        {
            _log.LogWarning("Could not read update state: {Message}", ex.Message);
            hello.UpdateState = new UpdateState { Phase = UpdateState.Types.Phase.Unspecified,
                Reason = "Update history is temporarily unavailable; the agent will retry." };
        }
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

    public async Task<JobResult> RunJobAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        _log.LogInformation("{Pc}: job {Kind} {Id}", Link.Name, job.Kind, job.Id);

        switch (job.Kind)
        {
            case Job.Types.Kind.Shutdown:
                return Power(job, reboot: false);

            case Job.Types.Kind.Reboot:
                return Power(job, reboot: true);

            case Job.Types.Kind.Logoff:
                return Logoff(job);

            case Job.Types.Kind.RunScript:
                return await _scripts.RunAsync(job, report, token);

            case Job.Types.Kind.SendFile:
                return await new HandoutRunner(Link).RunAsync(job, report, token);

            case Job.Types.Kind.SelfUpdate:
                // The minimal push-and-restart (D-33). On success this never returns: the
                // service is stopped mid-job and the new version answers the re-sent job.
                return await _updater.RunAsync(job, report, token);

            default:
                // Packages, profile reset and files land in M4 (ROADMAP). Until then the
                // console hears "not in this build".
                return new JobResult
                {
                    JobId = job.Id,
                    Ok = false,
                    ExitCode = -1,
                    Message = $"This agent build ({Program.InstalledVersion}) does not run {job.Kind} jobs yet.",
                };
        }
    }

    /// <summary>
    /// Shutdown and reboot answer first and act <see cref="Defaults.PowerJobDelay"/> later, so
    /// the result is on the wire before Windows tears the link down (D-32). What can fail
    /// early — the privilege — is checked before answering; a failure of the call itself is
    /// reported as an event, because by then the result has left.
    /// </summary>
    private JobResult Power(Job job, bool reboot)
    {
        var what = reboot ? "reboot" : "shutdown";
        var problem = PowerControl.PrepareShutdown();
        if (problem is not null)
        {
            _log.LogError("{Pc}: cannot {What}: {Problem}", Link.Name, what, problem);
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = $"Cannot {what}: {problem}" };
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Defaults.PowerJobDelay);
            _log.LogInformation("{Pc}: {What} now (forced, no warning)", Link.Name, what);
            var failure = PowerControl.Shutdown(reboot);
            if (failure is not null)
            {
                _log.LogError("{Pc}: {What} failed: {Failure}", Link.Name, what, failure);
                Link.Report(Event.Types.Severity.Error, "power.failed", $"The {what} did not happen: {failure}");
            }
        });

        return new JobResult
        {
            JobId = job.Id,
            Ok = true,
            ExitCode = 0,
            Message = reboot
                ? $"Rebooting in {Defaults.PowerJobDelay.TotalSeconds:0} s (forced; applications are closed without saving)."
                : $"Shutting down in {Defaults.PowerJobDelay.TotalSeconds:0} s (forced; applications are closed without saving).",
        };
    }

    private JobResult Logoff(Job job)
    {
        var session = InteractiveSession.Snapshot();
        if (!session.HasSession || !session.HasUser)
        {
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = "Nobody is logged on to this PC; there is no session to log off." };
        }

        var failure = PowerControl.Logoff(session.SessionId!.Value);
        if (failure is not null)
        {
            _log.LogError("{Pc}: logoff failed: {Failure}", Link.Name, failure);
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = $"Could not log {session.User} off: {failure}" };
        }

        return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = $"Logging {session.User} off (session {session.SessionId}); the session helper follows the new session." };
    }

    public async ValueTask DisposeAsync()
    {
        await _readinessMonitor.DisposeAsync();
        await _trialMonitor.DisposeAsync();
        await Link.DisposeAsync();
        _store.Dispose();
    }
}
