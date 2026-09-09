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

    /// <summary>The lab this job was created in (M5, D-57 item 4); journal rows carry it.</summary>
    public string LabId { get; init; } = string.Empty;

    /// <summary>
    /// The console instance that created and delivers this job (M5, D-57 item 4). The PC
    /// binds the result to it, so only this instance ever receives the outcome.
    /// </summary>
    public string InstanceId { get; init; } = string.Empty;

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

    /// <summary>
    /// True for a row a later session brought back from <c>jobs-inflight.json</c> (M5, D-57
    /// item 4). Such a row gets one chance: it is re-sent on the PC's next link and never
    /// written back to the file, so it cannot outlive two sessions.
    /// </summary>
    public bool RestoredFromDisk { get; init; }

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

    /// <param name="labId">Stamped on every job (M5, D-57 item 4); empty in tests that have no lab.</param>
    /// <param name="instanceId">The console instance every job of this queue is delivered by.</param>
    public JobQueue(string labId = "", string instanceId = "")
    {
        LabId = labId;
        InstanceId = instanceId;
    }

    public string LabId { get; }

    public string InstanceId { get; }

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
            LabId = LabId,
            InstanceId = InstanceId,
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

    /// <summary>
    /// Closes a job from the agent's <c>JobResult</c>. A result for an unknown id is ignored,
    /// and so is a second result for a job that already holds one: after a reconnect the PC
    /// both drains the kept result and answers the re-sent copy from its ledger (D-32 item 7).
    /// A late result still replaces a timeout (D-43).
    /// </summary>
    public JobRecord? Complete(JobResult result, DateTimeOffset now)
    {
        JobRecord? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(result.JobId, out job) || job.State is JobState.Succeeded or JobState.Failed)
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

    /// <summary>Copies a whole batch consistently, including output that may still be arriving.</summary>
    public IReadOnlyList<JobLogSnapshot> SnapshotBatch(string batchId)
    {
        lock (_gate)
        {
            return _jobs.Values.Where(j => j.BatchId == batchId)
                .OrderBy(j => j.AgentId, StringComparer.Ordinal).ThenBy(j => j.Id, StringComparer.Ordinal)
                .Select(j => new JobLogSnapshot(j.Id, j.AgentId, j.Kind, j.State,
                    j.CreatedAtUnix, j.DeliveredAtUnix, j.CompletedAtUnix,
                    j.Percent, j.ExitCode, j.Message, j.Output.ToArray(), j.LabId, j.InstanceId))
                .ToArray();
        }
    }

    /// <summary>
    /// The jobs a PC is working on for this console right now — delivered or running — in the
    /// durable form the console keeps across a switch away from the lab (M5, D-57 items 3–4).
    /// </summary>
    /// <remarks>
    /// A row this session itself restored is left out: it has had its one re-send, and saving
    /// it again would let a job chase the teacher from lesson to lesson for ever.
    /// </remarks>
    public IReadOnlyList<InFlightJob> SnapshotInFlight()
    {
        lock (_gate)
        {
            return _jobs.Values
                .Where(j => (j.State is JobState.Delivered or JobState.Running) && !j.RestoredFromDisk)
                .OrderBy(j => j.CreatedAtUnix).ThenBy(j => j.Id, StringComparer.Ordinal)
                .Select(InFlightJob.Of)
                .ToArray();
        }
    }

    /// <summary>
    /// Brings back what a previous session of this lab, on this same instance, left running on
    /// the PCs (D-57 item 4). A row of another lab or another instance is not this console's
    /// business and is dropped without a word. Everything else becomes a row the teacher can
    /// see: either one that will be sent again on the PC's next link — the agent's ledger
    /// answers it (D-32 item 7) — or one closed at once as <see cref="JobState.TimedOut"/>,
    /// <i>outcome unknown</i>, because sending it again could act on the PC a second time or
    /// because its payload died with the old session. <paramref name="refuseReason"/> decides,
    /// and returns the reason the teacher reads. The inactivity clock restarts at
    /// <paramref name="now"/> — the console, not the PC, was away.
    /// </summary>
    public RestoredJobs Restore(IEnumerable<InFlightJob> jobs, DateTimeOffset now, Func<InFlightJob, string?> refuseReason)
    {
        var resent = new List<JobRecord>();
        var unknown = new List<JobRecord>();
        var foreign = 0;

        foreach (var job in jobs)
        {
            if (job.State is not (JobState.Delivered or JobState.Running)
                || !string.Equals(job.LabId, LabId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(job.InstanceId, InstanceId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(job.Id) || string.IsNullOrEmpty(job.AgentId))
            {
                foreign++;
                continue;
            }

            var refusal = refuseReason(job);
            var record = new JobRecord
            {
                Id = job.Id,
                AgentId = job.AgentId,
                Kind = job.Kind,
                Args = new Dictionary<string, string>(job.Args, StringComparer.Ordinal),
                TimeoutSeconds = job.TimeoutSeconds,
                Delivery = job.Delivery,
                BatchId = job.BatchId,
                LabId = LabId,
                InstanceId = InstanceId,
                RestoredFromDisk = true,
                State = job.State,
                CreatedAtUnix = job.CreatedAtUnix,
                DeliveredAtUnix = job.DeliveredAtUnix,
                LastActivityUnix = now.ToUnixTimeSeconds(),
                Percent = job.Percent,
            };
            record.Output.AddRange(job.Output);

            if (refusal is not null)
            {
                MarkOutcomeUnknown(record, now, refusal);
            }

            lock (_gate)
            {
                if (!_jobs.TryAdd(record.Id, record))
                {
                    continue;
                }
            }

            (refusal is null ? resent : unknown).Add(record);
        }

        foreach (var job in resent.Concat(unknown))
        {
            Updated?.Invoke(job);
        }

        return new RestoredJobs(resent, unknown, foreign);
    }

    /// <summary>
    /// Closes a delivered row whose outcome this console can no longer learn (M5, D-57 item 4):
    /// an honest line in the jobs panel instead of silence. <see cref="JobState.TimedOut"/> and
    /// not <see cref="JobState.Failed"/>, so a result that does arrive later still replaces it
    /// (D-43). Returns <c>null</c> for an unknown or already finished id.
    /// </summary>
    public JobRecord? CloseAsOutcomeUnknown(string jobId, DateTimeOffset now, string reason)
    {
        JobRecord? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out job) || job.IsFinished)
            {
                return null;
            }

            MarkOutcomeUnknown(job, now, reason);
        }

        Updated?.Invoke(job);
        return job;
    }

    private static void MarkOutcomeUnknown(JobRecord job, DateTimeOffset now, string reason)
    {
        job.State = JobState.TimedOut;
        job.Ok = false;
        job.CompletedAtUnix = now.ToUnixTimeSeconds();
        job.Message = $"Outcome unknown — the console left this lab: {reason}";
    }
}

/// <summary>
/// What one session made of the saved in-flight file (M5, D-57 item 4): the rows it will send
/// again, the rows it closed as <i>outcome unknown</i>, and how many belonged to another lab
/// or another console instance and were dropped.
/// </summary>
public sealed record RestoredJobs(IReadOnlyList<JobRecord> Resent, IReadOnlyList<JobRecord> Unknown, int Foreign);

/// <summary>One delivered-but-unfinished job as <c>jobs-inflight.json</c> keeps it (M5, D-57 item 4).</summary>
public sealed class InFlightJob
{
    public string Id { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public Job.Types.Kind Kind { get; set; }
    public Dictionary<string, string> Args { get; set; } = [];
    public int TimeoutSeconds { get; set; }

    /// <summary>Preserved across the switch: an <c>online_only</c> job must not become a queued one.</summary>
    public JobDelivery Delivery { get; set; }

    public string BatchId { get; set; } = string.Empty;
    public string LabId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public JobState State { get; set; }
    public long CreatedAtUnix { get; set; }
    public long DeliveredAtUnix { get; set; }
    public int Percent { get; set; }
    public List<string> Output { get; set; } = [];

    public static InFlightJob Of(JobRecord job) => new()
    {
        Id = job.Id,
        AgentId = job.AgentId,
        Kind = job.Kind,
        Args = new Dictionary<string, string>(job.Args, StringComparer.Ordinal),
        TimeoutSeconds = job.TimeoutSeconds,
        Delivery = job.Delivery,
        BatchId = job.BatchId,
        LabId = job.LabId,
        InstanceId = job.InstanceId,
        State = job.State,
        CreatedAtUnix = job.CreatedAtUnix,
        DeliveredAtUnix = job.DeliveredAtUnix,
        Percent = job.Percent,
        Output = job.Output.ToList(),
    };
}
