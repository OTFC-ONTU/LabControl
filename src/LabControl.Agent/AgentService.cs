using LabControl.Shared;
using LabControl.Shared.Discovery;
using LabControl.Shared.Link;
using LabControl.Shared.Localization;
using LabControl.Shared.Policy;
using LabControl.Shared.Protection;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent;

/// <summary>
/// The service loop: open the PC's store, start the link, feed it beacons, and stay alive
/// whatever happens. Nothing here may throw out of <see cref="ExecuteAsync"/> — a service
/// that stops is restarted by its recovery action and, after enough restarts, rolled back
/// (D-19), so an unprovisioned or misconfigured PC waits and says why instead of exiting.
/// </summary>
internal sealed class AgentService : BackgroundService
{
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly SessionChangeSource _sessionChanges;

    public AgentService(ILoggerFactory loggers, SessionChangeSource sessionChanges)
    {
        _loggers = loggers;
        _sessionChanges = sessionChanges;
        _log = loggers.CreateLogger<AgentService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            var store = await OpenStoreAsync(stopping);
            if (store is null)
            {
                return;
            }

            await using var behaviour = new WindowsAgentBehaviour(store, _loggers.CreateLogger("pc"));
            await using var link = behaviour.Link;

            // The helper lives next to agent.exe in app\<version>\ (D-19); the supervisor keeps
            // one alive in the interactive session and reports that session to the console.
            var helperPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, Defaults.SessionExecutableName);
            await using var supervisor = new SessionSupervisor(link, _sessionChanges, _loggers.CreateLogger("session"), helperPath);

            // The policy journal (M6, D-69): restore first, then re-apply what policy.json
            // still holds — before the link dials, so the first OverlayState after Welcome is
            // already the truth, and before the helper runs, so a re-applied lock is what the
            // first helper is told. Declared after the supervisor, so it is disposed before it:
            // OnStop's restore pass runs first, then the supervisor ends the helper and the
            // overlay dies with it (the overlay needs no restore of its own, D-69 item 2).
            // Guarded on its own: whatever the enforcer does or fails to do, the link dials —
            // a corrupt policy.json is a warning on the console, never an offline PC.
            await using var policy = StartPolicy(link, supervisor, store.Config.LabId);
            behaviour.NetworkPolicyHeld = () => policy?.HoldsNetworkPolicy ?? false;
            behaviour.ExamHeld = () => policy?.HoldsExam ?? false;

            behaviour.Start();

            if (File.Exists(helperPath))
            {
                supervisor.Start();
            }
            else
            {
                var message = PcText.Get("HelperMissing", Defaults.SessionExecutableName, helperPath);
                _log.LogError("{Message}", message);
                link.Report(Event.Types.Severity.Error, "session.helper_missing", message);
            }

            using var listener = new BeaconListener();
            listener.Received += (datagram, _, receivedAt) => link.OfferBeacon(datagram, receivedAt);
            listener.Failed += message =>
            {
                _log.LogError("{Message}", message);
                link.Report(Event.Types.Severity.Error, "agent.beacon_socket", message);
            };
            listener.Start();

            _log.LogInformation("{Pc}: agent {AgentId}, {Enrolled}; listening for beacons on UDP {Port}{Pinned}",
                link.Name, store.Config.AgentId, link.IsEnrolled ? "enrolled" : "not enrolled yet",
                Defaults.BeaconPort,
                string.IsNullOrWhiteSpace(store.Config.ConsoleHost) ? string.Empty : $", console pinned to {store.Config.ConsoleHost}:{store.Config.ConsolePort}");

            try
            {
                await Task.Delay(Timeout.Infinite, stopping);
            }
            catch (OperationCanceledException)
            {
            }

            _log.LogInformation("{Pc}: stopping", link.Name);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Logged, not thrown: the host would otherwise stop the service.
            _log.LogCritical(ex, "The agent loop failed and will not recover until the service is restarted: {Message}", ex.Message);
            try
            {
                await Task.Delay(Timeout.Infinite, stopping);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Builds and starts the enforcer, or returns <c>null</c> and reports when that fails.
    /// <see cref="PolicyEnforcer.Start"/> itself never throws (D-69); this is the net under
    /// the constructor and the wiring, so the service loop around it cannot be taken down by
    /// the policy code whatever it does.
    /// </summary>
    private PolicyEnforcer? StartPolicy(AgentLink link, SessionSupervisor supervisor, string labId)
    {
        try
        {
            var machine = new WindowsMachineState(supervisor.SendToHelper, supervisor.SendToHelperAsync, _loggers.CreateLogger("policy"), () => supervisor.HelperVideoDecoders);
            var policy = new PolicyEnforcer(link, machine, Defaults.AgentDataDirectory, labId, _loggers.CreateLogger("policy"));
            supervisor.Policy = policy;
            policy.Start();
            return policy;
        }
        catch (Exception ex)
        {
            var message = PolicyEvents.Text("EnforcerFailed", "start", ex.Message);
            _log.LogError(ex, "{Message}", message);
            link.Report(Event.Types.Severity.Error, PolicyEvents.EnforcerFailed, message);
            return null;
        }
    }

    /// <summary>
    /// Opens <c>ProgramData\LabControl</c>. An installed-but-unprovisioned PC (no
    /// <c>agent.json</c>) is not an error — Setup writes the binaries first and the trust
    /// material second — so this waits and looks again rather than exiting.
    /// </summary>
    private async Task<DirectoryAgentStore?> OpenStoreAsync(CancellationToken stopping)
    {
        var complained = false;

        while (!stopping.IsCancellationRequested)
        {
            if (DirectoryAgentStore.Exists(Defaults.AgentDataDirectory))
            {
                try
                {
                    var store = DirectoryAgentStore.Open(Defaults.AgentDataDirectory, MachineKeyProtection.Dpapi);
                    return store;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException or LabControl.Shared.Persistence.SchemaVersionException)
                {
                    if (!complained)
                    {
                        _log.LogError(ex, "Cannot open {Directory}: {Message}. Run Setup again on this PC. Retrying every {Seconds:0} s.",
                            Defaults.AgentDataDirectory, ex.Message, Defaults.UnprovisionedRetryInterval.TotalSeconds);
                    }
                }
            }
            else if (!complained)
            {
                _log.LogWarning("This PC is not provisioned: {File} is missing in {Directory}. Run Setup (or `agent.exe {Switch}`) on this PC. Retrying every {Seconds:0} s.",
                    Defaults.AgentConfigFileName, Defaults.AgentDataDirectory, Defaults.AgentInstallSwitch, Defaults.UnprovisionedRetryInterval.TotalSeconds);
            }

            complained = true;
            try
            {
                await Task.Delay(Defaults.UnprovisionedRetryInterval, stopping);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }
}
