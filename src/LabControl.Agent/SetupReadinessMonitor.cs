using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;

namespace LabControl.Agent;

/// <summary>Refreshes the snapshot after USB repair without requiring a service restart.</summary>
internal sealed class SetupReadinessMonitor : IAsyncDisposable
{
    private readonly AgentLink _link;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Task _loop;
    private string? _last;

    public SetupReadinessMonitor(AgentLink link)
    {
        _link = link;
        _link.Linked += OnLinked;
        _loop = RunAsync();
    }

    private void OnLinked(string instanceId, string instanceName) => Publish(force: true);

    private void Publish(bool force)
    {
        lock (_gate)
        {
            SetupReadiness? report;
            try { report = SetupReadiness.Read(Defaults.AgentDataDirectory); }
            catch (Exception) { report = SetupReadiness.Create(["report.unavailable"]); }
            // Legacy installations have no snapshot; absence does not certify readiness.
            if (report is null) return;
            var payload = report.Serialize();
            if (!force && payload == _last) return;
            _last = payload;
            _link.Report(report.Codes.Any(SetupReadiness.NeedsAttention) ? Event.Types.Severity.Warning : Event.Types.Severity.Info,
                SetupReadiness.EventCode, payload);
        }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(Defaults.SetupReadinessPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token)) Publish(force: false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _link.Linked -= OnLinked;
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}
