using System.Diagnostics;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent;

/// <summary>Only an uninterrupted, monotonic ten-minute linked interval accepts a trial.
/// Every process restart starts a new interval; the external deadline remains on disk.</summary>
internal sealed class UpdateTrialMonitor : IAsyncDisposable
{
    private readonly AgentLink _link;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private long? _linkedAt;
    private readonly Task _loop;
    private readonly UpdateTerminalReport _terminalReport = new();

    public UpdateTrialMonitor(AgentLink link, ILogger log)
    {
        _link = link;
        _log = log;
        link.Linked += OnLinked;
        link.Unlinked += OnUnlinked;
        _loop = Task.Run(RunAsync);
    }

    private void OnLinked(string instanceId, string instanceName)
    {
        lock (_gate)
        {
            _linkedAt = Stopwatch.GetTimestamp();
            _terminalReport.Reset();
        }
    }

    private void OnUnlinked(string reason)
    {
        lock (_gate) _linkedAt = null;
    }

    public static UpdateState Describe(string running)
    {
        var state = new UpdateTrial(Defaults.AgentDataDirectory).Read();
        if (state is null || state.Phase == UpdateTrialPhase.Stable) return new() { Phase = UpdateState.Types.Phase.Stable };
        if (state.Phase == UpdateTrialPhase.RolledBack && running == state.Previous)
            return new() { Phase = UpdateState.Types.Phase.RolledBack, FailedVersion = state.Version,
                Reason = "The update did not finish its linked probation; the previous version was restored." };
        return new() { Phase = UpdateState.Types.Phase.OnProbation, ProbationEndsUnix = state.Deadline.ToUnixTimeSeconds() };
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                try
                {
                    var trial = new UpdateTrial(Defaults.AgentDataDirectory);
                    // Rollback starts the service while holding the journal lock. Hello may
                    // have fallen back; retry here after the durable terminal transition.
                    lock (_gate)
                    {
                        if (_linkedAt is not null && _link.State == LinkState.Linked
                            && _terminalReport.Read(trial.Read, Program.InstalledVersion) is { } status)
                            _link.Report(status.Severity, status.Code, status.Message);
                    }
                    if (trial.Read() is { RecoveryFinalized: false } terminal
                        && terminal.Phase is UpdateTrialPhase.Stable or UpdateTrialPhase.RolledBack)
                        UpdateRecovery.FinalizeTerminal(terminal);
                    bool accepted;
                    lock (_gate)
                    {
                        accepted = _linkedAt is { } at && _link.State == LinkState.Linked
                            && trial.Accept(Program.InstalledVersion,
                                DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(at));
                    }
                    if (accepted)
                    {
                        _log.LogInformation("Update {Version} completed probation", Program.InstalledVersion);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Could not check update probation: {Message}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _link.Linked -= OnLinked;
        _link.Unlinked -= OnUnlinked;
        _stop.Cancel();
        await _loop;
        _stop.Dispose();
    }
}
