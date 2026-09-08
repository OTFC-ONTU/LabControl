using LabControl.Shared.Protocol;

namespace LabControl.Shared.Setup;

/// <summary>Eventually refreshes terminal status when Hello raced an external rollback lock.
/// Failed reads never consume the report; reconnect resets delivery even for the same job.</summary>
public sealed class UpdateTerminalReport
{
    public const string StableCode = "update.stable";
    public const string RolledBackCode = "update.rolled_back";
    private string? _sent;

    public void Reset() => _sent = null;

    public Event? Read(Func<UpdateTrialDocument?> read, string running)
    {
        var state = read();
        if (!InstallLayout.IsValidVersion(running)) return null;
        Event report;
        if (state is null || state.Phase == UpdateTrialPhase.Stable && state.Version == running)
            report = new() { Code = StableCode, Severity = Event.Types.Severity.Info, Message = "The running version is stable." };
        else if (state.Phase == UpdateTrialPhase.RolledBack && state.Previous == running && InstallLayout.IsValidVersion(state.Version))
            report = new() { Code = RolledBackCode, Severity = Event.Types.Severity.Warning, Message = state.Version };
        else return null; // Pending recovery is never presented as completed restoration.
        var identity = (state?.JobId ?? "") + "\0" + report.Code + "\0" + running + "\0" + report.Message;
        if (_sent == identity) return null;
        _sent = identity;
        return report;
    }
}
