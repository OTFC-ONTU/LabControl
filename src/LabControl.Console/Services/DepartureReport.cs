using LabControl.Shared.Protocol;

namespace LabControl.Console.Services;

/// <summary>What happens to a running job of a given kind when the console leaves the lab (M5, D-57 item 3).</summary>
public enum DepartureConsequence
{
    /// <summary><c>run_script</c>, <c>send_file</c>: the PC keeps going; the result is shown when the console returns.</summary>
    ContinuesOnPc = 0,

    /// <summary>Power jobs: the PC completes them by itself.</summary>
    Completes = 1,

    /// <summary><c>self_update</c>: cannot be aborted from here; leaving is safe.</summary>
    CannotBeAborted = 2,
}

/// <summary>Running jobs of one kind and their fate.</summary>
public sealed record DepartureJobGroup(Job.Types.Kind Kind, int Count, DepartureConsequence Consequence);

/// <summary>
/// The truthful answer to "what am I interrupting?" before <i>Disconnect</i> (M5, D-57 item 3).
/// Every number is observed state of the session, never a guess; the dialog turns it into
/// words and offers <i>Leave anyway</i>, <i>Stay</i> or <i>Wait for N jobs</i>.
/// </summary>
/// <param name="RunningJobs">Delivered or running jobs, grouped by kind.</param>
/// <param name="QueuedJobs">Jobs waiting for an offline PC; the next session never re-creates them.</param>
/// <param name="UploadsInProgress">Upload grants that would fail with the session.</param>
/// <param name="ProbationPcs">PC numbers running a new agent build on probation; they report on return.</param>
/// <param name="PendingWakes">PC numbers whose Wake-on-LAN is still being watched; the watch is dropped.</param>
public sealed record DepartureReport(
    IReadOnlyList<DepartureJobGroup> RunningJobs,
    int QueuedJobs,
    int UploadsInProgress,
    IReadOnlyList<int> ProbationPcs,
    IReadOnlyList<int> PendingWakes)
{
    public static readonly DepartureReport Empty = new([], 0, 0, [], []);

    public int RunningJobCount => RunningJobs.Sum(g => g.Count);

    /// <summary>Nothing would be interrupted: the dialog is skipped.</summary>
    public bool IsEmpty =>
        RunningJobs.Count == 0 && QueuedJobs == 0 && UploadsInProgress == 0 && ProbationPcs.Count == 0 && PendingWakes.Count == 0;

    public static DepartureConsequence ConsequenceOf(Job.Types.Kind kind) => kind switch
    {
        Job.Types.Kind.Shutdown or Job.Types.Kind.Reboot or Job.Types.Kind.Logoff => DepartureConsequence.Completes,
        Job.Types.Kind.SelfUpdate or Job.Types.Kind.Rekey => DepartureConsequence.CannotBeAborted,
        _ => DepartureConsequence.ContinuesOnPc,
    };
}
