using LabControl.Shared.Protocol;

namespace LabControl.Shared.Link;

/// <summary>
/// The agent's side of job idempotency (PROTOCOL, <c>Job</c>): <i>jobs are idempotent by
/// id; a re-sent job with a known id returns the cached result</i>. This matters because
/// the link drops and reconnects — a console that never saw the result will send the job
/// again, and a PC must not reboot twice because of it.
/// </summary>
public sealed class JobLedger
{
    private readonly Dictionary<string, JobResult> _completed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>How many finished results to remember; older ones fall off oldest-first.</summary>
    private readonly int _capacity;

    private readonly Queue<string> _order = new();

    public JobLedger(int capacity = 500) => _capacity = capacity;

    /// <summary>
    /// Decides what to do with an incoming job: run it, do nothing because it is already
    /// running, or answer at once with the result it produced last time.
    /// </summary>
    public JobAdmission Admit(Job job)
    {
        lock (_gate)
        {
            if (_completed.TryGetValue(job.Id, out var cached))
            {
                return JobAdmission.Cached(cached);
            }

            return _running.Add(job.Id) ? JobAdmission.Run() : JobAdmission.AlreadyRunning();
        }
    }

    /// <summary>Records the outcome, so a re-sent job answers from here instead of running again.</summary>
    public void Complete(JobResult result)
    {
        lock (_gate)
        {
            _running.Remove(result.JobId);

            if (_completed.TryAdd(result.JobId, result))
            {
                _order.Enqueue(result.JobId);
            }

            while (_order.Count > _capacity)
            {
                _completed.Remove(_order.Dequeue());
            }
        }
    }

    public bool IsRunning(string jobId)
    {
        lock (_gate)
        {
            return _running.Contains(jobId);
        }
    }

    public int CompletedCount
    {
        get
        {
            lock (_gate)
            {
                return _completed.Count;
            }
        }
    }
}

/// <summary>What the ledger decided about an incoming job.</summary>
public sealed record JobAdmission(bool ShouldRun, JobResult? CachedResult, bool DuplicateOfRunning)
{
    public static JobAdmission Run() => new(true, null, false);

    public static JobAdmission Cached(JobResult result) => new(false, result, false);

    public static JobAdmission AlreadyRunning() => new(false, null, true);
}
