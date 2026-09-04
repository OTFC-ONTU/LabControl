using LabControl.Shared.Protocol;

namespace LabControl.Shared.Lab;

/// <summary>Where a job stands, as the jobs panel shows it.</summary>
public enum JobState
{
    /// <summary>Created; waiting for the PC to be linked to this console.</summary>
    Pending = 0,

    /// <summary>Handed to the agent on its <c>Link</c> stream.</summary>
    Delivered = 1,

    /// <summary>The agent has reported progress.</summary>
    Running = 2,

    Succeeded = 3,
    Failed = 4,

    /// <summary>Dropped without being delivered: an <c>online_only</c> job for an offline PC.</summary>
    NotDelivered = 5,

    /// <summary>Delivered, but the agent never reported a result inside the timeout.</summary>
    TimedOut = 6,
}

/// <summary>Whether a job waits for an offline PC or is dropped (PROTOCOL, <c>Job</c>).</summary>
public enum JobDelivery
{
    /// <summary>Queued in the console and delivered on the next <c>Link</c>.</summary>
    Queued = 0,

    /// <summary>
    /// Only meaningful right now. The default for <c>shutdown</c>: a PC that comes back
    /// tomorrow morning must not be shut down by yesterday's click.
    /// </summary>
    OnlineOnly = 1,
}

/// <summary>One job for one PC. A batch across the room is N of these sharing a <see cref="BatchId"/>.</summary>
public sealed class JobRecord
{
    public required string Id { get; init; }

    public required string AgentId { get; init; }

    public required Job.Types.Kind Kind { get; init; }

    public Dictionary<string, string> Args { get; init; } = [];

    public int TimeoutSeconds { get; init; }

    public JobDelivery Delivery { get; init; }

    /// <summary>Groups the per-PC rows of one toolbar click into one entry in the jobs panel.</summary>
    public string BatchId { get; init; } = string.Empty;

    public JobState State { get; set; } = JobState.Pending;

    public long CreatedAtUnix { get; init; }

    public long DeliveredAtUnix { get; set; }

    /// <summary>Delivery or the latest <c>JobProgress</c>; the timeout counts from here, not from delivery.</summary>
    public long LastActivityUnix { get; set; }

    public long CompletedAtUnix { get; set; }

    public int Percent { get; set; }

    public bool Ok { get; set; }

    public int ExitCode { get; set; }

    public string Message { get; set; } = string.Empty;

    public string ArtifactRef { get; set; } = string.Empty;

    /// <summary>Captured output lines, as they arrive on <c>JobProgress</c>.</summary>
    public List<string> Output { get; } = [];

    public bool IsFinished => State is JobState.Succeeded or JobState.Failed or JobState.NotDelivered or JobState.TimedOut;

    /// <summary>The wire form handed to the agent.</summary>
    public Job ToMessage()
    {
        var job = new Job { Id = Id, Kind = Kind, TimeoutSeconds = TimeoutSeconds };
        job.Args.Add(Args);
        return job;
    }
}

/// <summary>
/// The console's job book. Every action is a job (PROTOCOL, <c>Job</c>): created here,
/// delivered on the PC's <c>Link</c> stream, and closed by the <c>JobResult</c> the agent
/// sends back. A job for an offline PC waits unless it is <see cref="JobDelivery.OnlineOnly"/>,
/// which is never queued: created for an offline PC it is <see cref="JobState.NotDelivered"/>
/// at once, and one still pending when its PC drops is closed the same way (D-25).
/// </summary>
public sealed class JobQueue
{
    private readonly Dictionary<string, JobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Raised for every state change, so the jobs panel and the log follow along.</summary>
    public event Action<JobRecord>? Updated;

    /// <summary>
    /// <c>shutdown</c> is the one kind that must not wait for a PC to come back
    /// (PROTOCOL, <c>Job</c>). Everything else is worth doing late.
    /// </summary>
    public static JobDelivery DefaultDeliveryFor(Job.Types.Kind kind) =>
        kind == Job.Types.Kind.Shutdown ? JobDelivery.OnlineOnly : JobDelivery.Queued;

    /// <param name="agentOnline">
    /// Whether the PC is linked to this console right now. Decides the fate of an
    /// <c>online_only</c> job on the spot; a queued job ignores it.
    /// </param>
    public JobRecord Create(
        string agentId,
        Job.Types.Kind kind,
        DateTimeOffset now,
        bool agentOnline,
        IReadOnlyDictionary<string, string>? args = null,
        TimeSpan? timeout = null,
        JobDelivery? delivery = null,
        string? batchId = null,
        string? id = null)
    {
        var record = new JobRecord
        {
            Id = id ?? Guid.NewGuid().ToString("d"),
            AgentId = agentId,
            Kind = kind,
            TimeoutSeconds = (int)(timeout ?? TimeSpan.FromMinutes(5)).TotalSeconds,
            Delivery = delivery ?? DefaultDeliveryFor(kind),
            BatchId = batchId ?? string.Empty,
            CreatedAtUnix = now.ToUnixTimeSeconds(),
        };

        if (args is not null)
        {
            foreach (var (key, value) in args)
            {
                record.Args[key] = value;
            }
        }

        if (record.Delivery == JobDelivery.OnlineOnly && !agentOnline)
        {
            MarkNotDelivered(record, now);
        }

        lock (_gate)
        {
            if (_jobs.TryGetValue(record.Id, out var existing))
            {
                // Re-creating a known id is the caller replaying itself; the first one wins.
                return existing;
            }

            _jobs[record.Id] = record;
        }

        Updated?.Invoke(record);
        return record;
    }

    /// <summary>Everything waiting for this PC, marked delivered, oldest first.</summary>
    public IReadOnlyList<JobRecord> TakePending(string agentId, DateTimeOffset now)
    {
        var ready = new List<JobRecord>();

        lock (_gate)
        {
            foreach (var job in PendingFor(agentId))
            {
                job.State = JobState.Delivered;
                job.DeliveredAtUnix = now.ToUnixTimeSeconds();
                job.LastActivityUnix = job.DeliveredAtUnix;
                ready.Add(job);
            }
        }

        foreach (var job in ready)
        {
            Updated?.Invoke(job);
        }

        return ready;
    }

    /// <summary>
    /// Jobs a PC had in hand when its link dropped, to send again on reconnect. Jobs are
    /// idempotent by id (PROTOCOL, <c>Job</c>): the agent answers a finished one from its
    /// ledger and carries on with a running one, so re-sending is always safe. States are
    /// left as they are; the inactivity timeout keeps counting.
    /// </summary>
    public IReadOnlyList<JobRecord> InFlight(string agentId)
    {
        lock (_gate)
        {
            return _jobs.Values
                .Where(j => j.State is JobState.Delivered or JobState.Running &&
                            string.Equals(j.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(j => j.CreatedAtUnix)
                .ToArray();
        }
    }

    /// <summary>
    /// Called when a PC's link ends. An <c>online_only</c> job it had not collected yet is
    /// closed as <see cref="JobState.NotDelivered"/> rather than kept for its return (D-25).
    /// </summary>
    public IReadOnlyList<JobRecord> AgentWentOffline(string agentId, DateTimeOffset now)
    {
        var dropped = new List<JobRecord>();

        lock (_gate)
        {
            foreach (var job in PendingFor(agentId).Where(j => j.Delivery == JobDelivery.OnlineOnly))
            {
                MarkNotDelivered(job, now);
                dropped.Add(job);
            }
        }

        foreach (var job in dropped)
        {
            Updated?.Invoke(job);
        }

        return dropped;
    }

    private IEnumerable<JobRecord> PendingFor(string agentId) =>
        _jobs.Values
            .Where(j => j.State == JobState.Pending &&
                        string.Equals(j.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(j => j.CreatedAtUnix)
            .ToArray();

    private static void MarkNotDelivered(JobRecord job, DateTimeOffset now)
    {
        job.State = JobState.NotDelivered;
        job.CompletedAtUnix = now.ToUnixTimeSeconds();
        job.Message = "The PC was offline; this job is only delivered to a PC that is online.";
    }

    public void Progress(JobProgress progress, DateTimeOffset now)
    {
        JobRecord? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(progress.JobId, out job) || job.IsFinished)
            {
                return;
            }

            job.State = JobState.Running;
            job.LastActivityUnix = now.ToUnixTimeSeconds();
            job.Percent = Math.Clamp(progress.Percent, 0, 100);
            if (progress.Line.Length > 0)
            {
                job.Output.Add(progress.Line);
            }
        }

        Updated?.Invoke(job);
    }

    /// <summary>Closes a job from the agent's <c>JobResult</c>. A result for an unknown id is ignored.</summary>
    public JobRecord? Complete(JobResult result, DateTimeOffset now)
    {
        JobRecord? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(result.JobId, out job))
            {
                return null;
            }

            job.State = result.Ok ? JobState.Succeeded : JobState.Failed;
            job.Ok = result.Ok;
            job.ExitCode = result.ExitCode;
            job.Message = result.Message;
            job.ArtifactRef = result.ArtifactRef;
            job.Percent = result.Ok ? 100 : job.Percent;
            job.CompletedAtUnix = now.ToUnixTimeSeconds();
        }

        Updated?.Invoke(job);
        return job;
    }

    /// <summary>
    /// Closes jobs an agent went silent on, so a row cannot spin forever. The timeout is
    /// one of <i>inactivity</i>: a long installation that keeps reporting progress is never
    /// cut off, one that stops talking is.
    /// </summary>
    public IReadOnlyList<JobRecord> TimeOutStale(DateTimeOffset now)
    {
        var stale = new List<JobRecord>();

        lock (_gate)
        {
            foreach (var job in _jobs.Values.Where(j => j.State is JobState.Delivered or JobState.Running))
            {
                if (now.ToUnixTimeSeconds() - job.LastActivityUnix <= job.TimeoutSeconds)
                {
                    continue;
                }

                job.State = JobState.TimedOut;
                job.CompletedAtUnix = now.ToUnixTimeSeconds();
                job.Message = $"Nothing heard from the PC about this job for {job.TimeoutSeconds} s.";
                stale.Add(job);
            }
        }

        foreach (var job in stale)
        {
            Updated?.Invoke(job);
        }

        return stale;
    }

    public JobRecord? Find(string id)
    {
        lock (_gate)
        {
            return _jobs.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<JobRecord> All()
    {
        lock (_gate)
        {
            return _jobs.Values.OrderBy(j => j.CreatedAtUnix).ToArray();
        }
    }

    public IReadOnlyList<JobRecord> Batch(string batchId)
    {
        lock (_gate)
        {
            return _jobs.Values
                .Where(j => string.Equals(j.BatchId, batchId, StringComparison.Ordinal))
                .OrderBy(j => j.AgentId, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
