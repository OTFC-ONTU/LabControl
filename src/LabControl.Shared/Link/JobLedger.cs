using LabControl.Shared.Protocol;

namespace LabControl.Shared.Link;

/// <summary>
/// The agent's side of job idempotency (PROTOCOL, <c>Job</c>): <i>jobs are idempotent by
/// id; a re-sent job with a known id returns the cached result</i>. This matters because
/// the link drops and reconnects — a console that never saw the result will send the job
/// again, and a PC must not reboot twice because of it.
/// </summary>
/// <remarks>
/// Since M5 (D-57 item 4) every entry also remembers which console <b>instance</b> delivered
/// the job. A result belongs to that instance: it is answered only to the same instance id,
/// so a teacher who takes the room next never sees the previous teacher's output, and the
/// delivering console gets it when it comes back and re-sends the job. The instance id is
/// the validated peer certificate's, not anything the console claims in a message.
/// </remarks>
public sealed class JobLedger
{
    private readonly Dictionary<string, JobLedgerEntry> _completed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _running = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>How many finished results to remember; older ones fall off oldest-first.</summary>
    private readonly int _capacity;

    private readonly Queue<string> _order = new();

    public JobLedger(int capacity = 500) => _capacity = capacity;

    /// <summary>
    /// Decides what to do with an incoming job from the console instance <paramref name="instanceId"/>:
    /// run it, do nothing because it is already running, answer at once with the result it
    /// produced last time, or refuse because another instance delivered it.
    /// </summary>
    public JobAdmission Admit(Job job, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        lock (_gate)
        {
            if (_completed.TryGetValue(job.Id, out var cached))
            {
                return SameInstance(cached.InstanceId, instanceId)
                    ? JobAdmission.Cached(cached.Result)
                    : JobAdmission.DeliveredByAnotherInstance(cached.InstanceId);
            }

            if (_running.TryGetValue(job.Id, out var runningFor))
            {
                return SameInstance(runningFor, instanceId)
                    ? JobAdmission.AlreadyRunning()
                    : JobAdmission.DeliveredByAnotherInstance(runningFor);
            }

            _running.Add(job.Id, instanceId);
            return JobAdmission.Run();
        }
    }

    /// <summary>
    /// Records the outcome, so a re-sent job answers from here instead of running again. The
    /// entry keeps the instance the job was admitted for; a result completed without a prior
    /// <see cref="Admit"/> takes <paramref name="instanceId"/>, or belongs to no instance.
    /// </summary>
    public JobLedgerEntry Complete(JobResult result, string? instanceId = null)
    {
        lock (_gate)
        {
            if (_running.Remove(result.JobId, out var admittedFor))
            {
                instanceId ??= admittedFor;
            }

            var entry = new JobLedgerEntry(result, instanceId ?? string.Empty);
            if (_completed.TryAdd(result.JobId, entry))
            {
                _order.Enqueue(result.JobId);
            }
            else
            {
                entry = _completed[result.JobId];
            }

            while (_order.Count > _capacity)
            {
                _completed.Remove(_order.Dequeue());
            }

            return entry;
        }
    }

    /// <summary>
    /// The job did not finish and will not: the agent is stopping mid-job (a <c>self_update</c>
    /// ends this way by design, D-33). Its id is released so the copy the console re-sends
    /// after the restart is admitted again rather than ignored as a duplicate.
    /// </summary>
    public void Forget(string jobId)
    {
        lock (_gate)
        {
            _running.Remove(jobId);
        }
    }

    public bool IsRunning(string jobId)
    {
        lock (_gate)
        {
            return _running.ContainsKey(jobId);
        }
    }

    /// <summary>The instance a job was delivered by, whether it is running or finished; <c>null</c> for an unknown id.</summary>
    public string? DeliveringInstanceOf(string jobId)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(jobId, out var running))
            {
                return running;
            }

            return _completed.TryGetValue(jobId, out var completed) ? completed.InstanceId : null;
        }
    }

    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
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

    internal static bool SameInstance(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A finished job and the console instance that delivered it.</summary>
public sealed record JobLedgerEntry(JobResult Result, string InstanceId);

/// <summary>What the ledger decided about an incoming job.</summary>
/// <param name="OtherInstanceId">
/// Set when the job id is known but was delivered by a different console instance (D-57
/// item 4): the caller must refuse it without revealing the result, running it again or
/// treating it as a duplicate the other console may wait for.
/// </param>
public sealed record JobAdmission(bool ShouldRun, JobResult? CachedResult, bool DuplicateOfRunning, string? OtherInstanceId = null)
{
    public static JobAdmission Run() => new(true, null, false);

    public static JobAdmission Cached(JobResult result) => new(false, result, false);

    public static JobAdmission AlreadyRunning() => new(false, null, true);

    public static JobAdmission DeliveredByAnotherInstance(string instanceId) => new(false, null, false, instanceId);

    public bool BelongsToAnotherInstance => OtherInstanceId is not null;
}
