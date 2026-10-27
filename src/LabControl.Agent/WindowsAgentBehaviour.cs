using System.Globalization;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Localization;
using LabControl.Shared.Packages;
using LabControl.Shared.Policy;
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
    private readonly WindowsPackageHost _packages;
    private readonly PackageInstallRunner _installs;

    public WindowsAgentBehaviour(DirectoryAgentStore store, ILogger log)
    {
        _store = store;
        _log = log;
        Link = new AgentLink(store, this, log);
        _scripts = new ScriptRunner(Link, log);
        _updater = new AgentUpdater(Link, log, store.Authority);
        _trialMonitor = new UpdateTrialMonitor(Link, log);
        _readinessMonitor = new SetupReadinessMonitor(Link);
        _packages = new WindowsPackageHost(() => ExamHeld?.Invoke() == true);
        _installs = new PackageInstallRunner(_packages, Link.PullFileAsync);

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

        // An install the service was stopped in the middle of leaves its staged installer (D-89).
        _packages.ClearStaging();
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
                Reason = PcText.Get("UpdateHistoryUnavailable") };
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
            Link.Report(Event.Types.Severity.Warning, "agent.inventory_failed", PcText.Get("InventoryFailed", ex.Message));
            return new Inventory { Hostname = Environment.MachineName };
        }
    }

    /// <summary>True while an internet policy is applied; <c>self_update</c> is refused then (D-69 item 6). Set by the service once the enforcer runs.</summary>
    public Func<bool>? NetworkPolicyHeld { get; set; }

    /// <summary>True while an exam is held; <c>reset_profile</c> is refused then (D-78). Set by the service once the enforcer runs.</summary>
    public Func<bool>? ExamHeld { get; set; }

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

            case Job.Types.Kind.CollectFiles:
                return await new CollectRunner(Link).RunAsync(job, report, token);

            case Job.Types.Kind.InstallPackage:
                return await _installs.RunAsync(job, report, token);

            case Job.Types.Kind.ResetProfile:
                return await new ProfileResetRunner(Link, _log) { ExamHeld = ExamHeld }.RunAsync(job, report, token);

            case Job.Types.Kind.SelfUpdate:
                if (NetworkPolicyHeld?.Invoke() == true)
                {
                    // The agent.exe exemption is pinned to app\<version>\ (D-69 item 6): a new
                    // version would be the one program the firewall blocks, and the PC would drop
                    // off the lab until the policy's hard limit.
                    _log.LogWarning("{Pc}: self_update {Id} refused — an internet policy is in force", Link.Name, job.Id);
                    return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = PolicyEvents.Text("UpdateRefusedPolicy") };
                }

                // The minimal push-and-restart (D-33). On success this never returns: the
                // service is stopped mid-job and the new version answers the re-sent job.
                return await _updater.RunAsync(job, report, token);

            default:
                // A kind this build does not run (set_config, rekey outside Setup): the console hears "not in this build".
                return new JobResult
                {
                    JobId = job.Id,
                    Ok = false,
                    ExitCode = -1,
                    Message = PcText.Get("JobNotInThisBuild", Program.InstalledVersion, job.Kind),
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
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = PcText.Get(reboot ? "PowerCannotReboot" : "PowerCannotShutdown", problem) };
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(Defaults.PowerJobDelay);
            _log.LogInformation("{Pc}: {What} now (forced, no warning)", Link.Name, what);

            // An exam's whitelist pauses so Windows can close everything itself (D-75).
            ExamCoordination.PauseForPowerJob();
            var failure = PowerControl.Shutdown(reboot);
            if (failure is not null)
            {
                _log.LogError("{Pc}: {What} failed: {Failure}", Link.Name, what, failure);
                Link.ReportForJob(job.Id, Event.Types.Severity.Error, "power.failed", PcText.Get(reboot ? "RebootDidNotHappen" : "ShutdownDidNotHappen", failure));
            }
        });

        return new JobResult
        {
            JobId = job.Id,
            Ok = true,
            ExitCode = 0,
            Message = reboot
                ? PcText.Get("PowerRebooting", Defaults.PowerJobDelay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture))
                : PcText.Get("PowerShuttingDown", Defaults.PowerJobDelay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)),
        };
    }

    private JobResult Logoff(Job job)
    {
        var session = InteractiveSession.Snapshot();
        if (!session.HasSession || !session.HasUser)
        {
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = PcText.Get("LogoffNobody") };
        }

        var failure = PowerControl.Logoff(session.SessionId!.Value);
        if (failure is not null)
        {
            _log.LogError("{Pc}: logoff failed: {Failure}", Link.Name, failure);
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = PcText.Get("LogoffFailed", session.User, failure) };
        }

        // The log off runs on in Windows: an exam's whitelist pauses while it closes everything (D-75).
        ExamCoordination.PauseForPowerJob();
        return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = PcText.Get("LogoffStarted", session.User, session.SessionId) };
    }

    public async ValueTask DisposeAsync()
    {
        await _readinessMonitor.DisposeAsync();
        await _trialMonitor.DisposeAsync();
        await Link.DisposeAsync();
        _store.Dispose();
    }
}
