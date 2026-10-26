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

    /// <summary><c>collect_files</c>: the work belongs to this session (D-77), so it is not collected; collect it again on return (D-79 item 2).</summary>
    NotCollected = 3,
}

/// <summary>
/// One kind of classroom control that outlives the departure (M6, D-79 item 1): the PCs that
/// hold it by their own last word and the latest moment one of them lifts it by itself, on this
/// console's clock. A lock and an internet policy stay until a console of the lab ends them or
/// their limit passes; an exam until its timer or its limit.
/// </summary>
/// <param name="Pcs">PC numbers, ascending.</param>
/// <param name="EndsBy">The latest self-lift among them; never later than the PCs' own hard limits.</param>
public sealed record DeparturePolicy(IReadOnlyList<int> Pcs, DateTimeOffset? EndsBy)
{
    public static readonly DeparturePolicy None = new([], null);

    public bool IsEmpty => Pcs.Count == 0;
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

    /// <summary>PCs showing this console's screen: the broadcast ends with the departure (D-71).</summary>
    public IReadOnlyList<int> BroadcastPcs { get; init; } = [];

    /// <summary>Locks the PCs hold; they stay (D-70) unless the teacher ends them on the way out.</summary>
    public DeparturePolicy Locks { get; init; } = DeparturePolicy.None;

    /// <summary>Standalone internet policies the PCs hold; they stay (D-73) unless the teacher lifts them on the way out.</summary>
    public DeparturePolicy Internet { get; init; } = DeparturePolicy.None;

    /// <summary>Exams the PCs hold; they continue to their timer or limit, and leaving never ends one (owner decision 2026-10-26).</summary>
    public DeparturePolicy Exams { get; init; } = DeparturePolicy.None;

    /// <summary>PCs whose exam this console would collect at its end: the work stays on the PC (owner decision 2026-10-26).</summary>
    public IReadOnlyList<int> UncollectedExamPcs { get; init; } = [];

    public int RunningJobCount => RunningJobs.Sum(g => g.Count);

    /// <summary>A lock or a standalone internet policy is held: the dialog offers to end them and leave.</summary>
    public bool CanEndPolicies => !Locks.IsEmpty || !Internet.IsEmpty;

    /// <summary>Nothing would be interrupted or left behind: the dialog is skipped.</summary>
    public bool IsEmpty =>
        RunningJobs.Count == 0 && QueuedJobs == 0 && UploadsInProgress == 0 && ProbationPcs.Count == 0 && PendingWakes.Count == 0
        && BroadcastPcs.Count == 0 && Locks.IsEmpty && Internet.IsEmpty && Exams.IsEmpty && UncollectedExamPcs.Count == 0;

    public static DepartureConsequence ConsequenceOf(Job.Types.Kind kind) => kind switch
    {
        Job.Types.Kind.Shutdown or Job.Types.Kind.Reboot or Job.Types.Kind.Logoff => DepartureConsequence.Completes,
        Job.Types.Kind.SelfUpdate or Job.Types.Kind.Rekey => DepartureConsequence.CannotBeAborted,
        Job.Types.Kind.CollectFiles => DepartureConsequence.NotCollected,
        _ => DepartureConsequence.ContinuesOnPc,
    };
}
