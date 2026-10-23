using System.Globalization;
using System.Text;
using LabControl.Console.Localization;
using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using LabControl.Shared.Lab;
using LabControl.Shared.Protocol;

namespace LabControl.Console.Services;

/// <summary>Where one PC of a collection stands.</summary>
public enum CollectedState
{
    /// <summary>Not asked yet: an exam that has not ended on this PC.</summary>
    Waiting,
    Collecting,
    Collected,

    /// <summary>The PC answered, and the folder was not there: nothing to collect.</summary>
    Empty,

    /// <summary>The PC was not linked when its work was asked for (owner decision 2026-10-23: no retry).</summary>
    Missing,
    Failed,
}

/// <summary>One PC's row in a collection.</summary>
public sealed class CollectedPc
{
    public required string AgentId { get; init; }

    public required int Number { get; init; }

    public CollectedState State { get; internal set; }

    public string Message { get; internal set; } = string.Empty;

    public int Files { get; internal set; }

    internal string? JobId { get; set; }

    internal FileUploads.Upload? Grant { get; set; }

    internal FileStream? Staging { get; set; }

    internal string? StagingPath { get; set; }

    internal long GrantSize { get; set; }

    internal string GrantSha256 { get; set; } = string.Empty;

    public bool IsFinished => State is CollectedState.Collected or CollectedState.Empty or CollectedState.Missing or CollectedState.Failed;
}

/// <summary>One collection: a folder from a set of PCs into <c>&lt;name date&gt;/PC-07/</c> (owner decision 2026-10-23).</summary>
public sealed class WorkCollection
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The folder on the PCs (<see cref="CollectRequest"/>).</summary>
    public required string Folder { get; init; }

    /// <summary>Where this collection lands on this computer; created on the first answer.</summary>
    public required string Directory { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    internal Dictionary<string, CollectedPc> Pcs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CollectedPc> Rows => Pcs.Values.OrderBy(p => p.Number).ToList();

    internal bool Announced { get; set; }
}

/// <summary>
/// Collected work on the console (M6 portion 5, D-77). A collection is one folder from a set
/// of PCs: each linked PC gets a <c>collect_files</c> job, a PC that is not linked is
/// <i>missing</i> at once (owner decision: no retry). The PC asks <see cref="RequestUpload"/>
/// for its grant once its archive exists; the grant is bound to that PC and that job, which
/// this console delivered (results are bound to the delivering instance, D-57), and the
/// archive is staged beside the destination and unpacked with <see cref="WorkArchive.Extract"/>
/// into the PC's own folder only after its hash was checked. A <c>report.txt</c> beside the
/// PCs' folders says what each PC gave. Exams remember what to collect in memory only, like
/// the lab-wide exam (D-76): a PC is collected when it reports the exam over, and what never
/// reported is asked for once the exam's deadline has passed.
/// </summary>
public sealed class WorkCollections
{
    private readonly Func<IEnumerable<string>, CollectRequest, IReadOnlyList<JobRecord>> _createJobs;
    private readonly Func<string, int> _numberOf;
    private readonly EventLog _events;
    private readonly FileUploads _uploads;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _gate = new();
    private readonly List<WorkCollection> _collections = [];
    private readonly Dictionary<string, (WorkCollection Collection, string AgentId)> _byJob = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExamCollection> _exams = new(StringComparer.Ordinal);

    /// <summary>An exam whose work is collected at its end.</summary>
    private sealed class ExamCollection
    {
        public required string Name { get; init; }

        public required string DestinationRoot { get; init; }

        public required string Folder { get; init; }

        public required HashSet<string> AgentIds { get; init; }

        public HashSet<string> SeenActive { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DateTimeOffset? Deadline { get; set; }

        public WorkCollection? Collection { get; set; }
    }

    public WorkCollections(Func<IEnumerable<string>, CollectRequest, IReadOnlyList<JobRecord>> createJobs, Func<string, int> numberOf,
        EventLog events, FileUploads uploads, Func<DateTimeOffset> clock)
    {
        _createJobs = createJobs;
        _numberOf = numberOf;
        _events = events;
        _uploads = uploads;
        _clock = clock;
    }

    /// <summary>Raised when a row changed; the console redraws.</summary>
    public event Action? Changed;

    /// <summary>The default destination (owner decision 2026-10-23): <c>Documents/LabControl/Collected</c>.</summary>
    public static string DefaultDestination =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Defaults.CollectedWorkRelativePath);

    public IReadOnlyList<WorkCollection> All()
    {
        lock (_gate)
        {
            return _collections.ToList();
        }
    }

    /// <summary>Starts a collection now: every PC in <paramref name="agentIds"/> is asked, a PC that is not linked is missing.</summary>
    public WorkCollection Start(string name, string destinationRoot, string folder, IEnumerable<string> agentIds)
    {
        if (!CollectRequest.TryNormalize(folder, out var normalized))
        {
            throw new ArgumentException(Strings.Get("Collect.FolderInvalid"));
        }

        var collection = Create(name, destinationRoot, normalized, agentIds);
        Collect(collection, collection.Pcs.Keys.ToArray());
        return collection;
    }

    /// <summary>Remembers an exam whose work is collected at its end; the folder is the one the PCs were told to create.</summary>
    public void RememberExam(string sessionId, string name, string destinationRoot, string folder, IEnumerable<string> agentIds, DateTimeOffset? deadline)
    {
        if (!CollectRequest.TryNormalize(folder, out var normalized))
        {
            throw new ArgumentException(Strings.Get("Collect.FolderInvalid"));
        }

        lock (_gate)
        {
            _exams[sessionId] = new ExamCollection
            {
                Name = name, DestinationRoot = destinationRoot, Folder = normalized, Deadline = deadline,
                AgentIds = new HashSet<string>(agentIds, StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    /// <summary>A PC that joined a lab-wide exam late is collected with it.</summary>
    public void AddToExam(string sessionId, string agentId)
    {
        lock (_gate)
        {
            if (_exams.TryGetValue(sessionId, out var exam))
            {
                exam.AgentIds.Add(agentId);
                if (exam.Collection is { } collection && !collection.Pcs.ContainsKey(agentId))
                {
                    collection.Pcs[agentId] = new CollectedPc { AgentId = agentId, Number = _numberOf(agentId) };
                }
            }
        }
    }

    /// <summary>The extension moved the exam's deadline.</summary>
    public void MoveExamDeadline(string sessionId, DateTimeOffset? deadline)
    {
        lock (_gate)
        {
            if (_exams.TryGetValue(sessionId, out var exam))
            {
                exam.Deadline = deadline;
            }
        }
    }

    /// <summary>
    /// The teacher ended the exam on <paramref name="agentIds"/>: every remembered exam of those
    /// PCs is over, and what does not report its end within the grace is asked for anyway. Judged
    /// by the PCs, not by what they last reported — a PC that is off reports nothing, and it must
    /// still end up missing (VM drill 2026-10-23).
    /// </summary>
    public void ExamEndedByTeacher(IEnumerable<string> agentIds)
    {
        var now = _clock();
        var ids = new HashSet<string>(agentIds, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var exam in _exams.Values)
            {
                if (exam.AgentIds.Overlaps(ids))
                {
                    exam.Deadline = now;
                }
            }
        }
    }

    /// <summary>
    /// What a PC said about its exam: holding <paramref name="sessionId"/> marks it seen; not
    /// holding a remembered exam it was seen in means that exam is over there — collect it.
    /// </summary>
    public void ExamStateOf(string agentId, bool active, string sessionId)
    {
        List<(WorkCollection, string)> due = [];
        lock (_gate)
        {
            foreach (var (id, exam) in _exams)
            {
                if (!exam.AgentIds.Contains(agentId))
                {
                    continue;
                }

                if (active && id == sessionId)
                {
                    exam.SeenActive.Add(agentId);
                    continue;
                }

                if (exam.SeenActive.Remove(agentId))
                {
                    exam.Collection ??= Create(exam.Name, exam.DestinationRoot, exam.Folder, exam.AgentIds);
                    due.Add((exam.Collection, agentId));

                    // An exam without a timer that ended on a PC by itself (its hard limit, another
                    // console) is over: the rest are asked after the grace, and it is forgotten.
                    exam.Deadline ??= _clock();
                }
            }
        }

        foreach (var (collection, id) in due)
        {
            Collect(collection, [id]);
        }
    }

    /// <summary>Once a second: exams past their deadline and grace collect what has not answered, and are forgotten.</summary>
    public void Tick()
    {
        var now = _clock();
        List<(WorkCollection, string[])> due = [];
        lock (_gate)
        {
            foreach (var (id, exam) in _exams.ToArray())
            {
                if (exam.Deadline is not { } deadline || now < deadline + Defaults.CollectAfterDeadlineGrace)
                {
                    continue;
                }

                exam.Collection ??= Create(exam.Name, exam.DestinationRoot, exam.Folder, exam.AgentIds);
                var collection = exam.Collection;
                due.Add((collection, collection.Pcs.Values.Where(p => p.State == CollectedState.Waiting).Select(p => p.AgentId).ToArray()));
                _exams.Remove(id);
            }
        }

        foreach (var (collection, ids) in due)
        {
            Collect(collection, ids);
        }
    }

    /// <summary>
    /// The PC's request for its grant (D-77): only for a collect job this console delivered to
    /// that PC and that is still running, within the cap. A repeated request for the same
    /// archive — the PC lost the answer — gets the same grant.
    /// </summary>
    public UploadGrant RequestUpload(string agentId, UploadRequest request)
    {
        static UploadGrant Refuse(string problem) => new() { Problem = problem };
        if (request.Size < 0 || request.Size > Defaults.CollectMaxBytes + Defaults.CollectMaxBytes / 16 || !FileHash.LooksLikeSha256(request.Sha256))
        {
            return Refuse(Strings.Get("Collect.GrantRefusedSize"));
        }

        lock (_gate)
        {
            if (!_byJob.TryGetValue(request.JobId, out var owner) || !string.Equals(owner.AgentId, agentId, StringComparison.OrdinalIgnoreCase)
                || owner.Collection.Pcs[owner.AgentId] is not { State: CollectedState.Collecting } pc)
            {
                return Refuse(Strings.Get("Collect.GrantRefusedJob"));
            }

            if (pc.Grant is { } existing && pc.GrantSize == request.Size && FileHash.Matches(pc.GrantSha256, request.Sha256))
            {
                return new UploadGrant { Reference = existing.Reference };
            }

            DropGrantLocked(pc);
            try
            {
                System.IO.Directory.CreateDirectory(owner.Collection.Directory);
                pc.StagingPath = Path.Combine(owner.Collection.Directory, $".{PcFolder(pc.Number)}.{Guid.NewGuid():n}.zip.partial");
                pc.Staging = new FileStream(pc.StagingPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, Defaults.FileChunkBytes, FileOptions.Asynchronous);
                pc.Grant = _uploads.Expect(agentId, pc.Staging, request.Size, request.Sha256);
                pc.GrantSize = request.Size;
                pc.GrantSha256 = request.Sha256;
                return new UploadGrant { Reference = pc.Grant.Reference };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DropGrantLocked(pc);
                return Refuse(Strings.Format("Collect.GrantRefusedDisk", ex.Message));
            }
        }
    }

    /// <summary>A job changed; a finished collect job of ours settles its PC's row.</summary>
    public void OnJobUpdated(JobRecord job)
    {
        if (job.Kind != Job.Types.Kind.CollectFiles || !job.IsFinished)
        {
            return;
        }

        WorkCollection collection;
        CollectedPc pc;
        lock (_gate)
        {
            if (!_byJob.Remove(job.Id, out var owner))
            {
                return;
            }

            collection = owner.Collection;
            pc = collection.Pcs[owner.AgentId];
        }

        if (job.State == JobState.NotDelivered)
        {
            Settle(collection, pc, CollectedState.Missing, Strings.Get("Collect.Missing"));
            return;
        }

        if (!job.Ok)
        {
            Settle(collection, pc, CollectedState.Failed, job.Message);
            return;
        }

        if (job.ArtifactRef.Length == 0)
        {
            Settle(collection, pc, CollectedState.Empty, job.Message);
            return;
        }

        // Unpacking a few hundred megabytes does not belong on the gRPC thread that reported the result.
        _ = Task.Run(() => Unpack(collection, pc, job));
    }

    private void Unpack(WorkCollection collection, CollectedPc pc, JobRecord job)
    {
        FileStream? staging;
        lock (_gate)
        {
            staging = pc.Grant is { IsComplete: true } grant && grant.Reference == job.ArtifactRef ? pc.Staging : null;
        }

        if (staging is null)
        {
            lock (_gate)
            {
                DropGrantLocked(pc);
            }

            Settle(collection, pc, CollectedState.Failed, Strings.Get("Collect.NoUpload"));
            return;
        }

        try
        {
            staging.Position = 0;
            var files = WorkArchive.Extract(staging, Path.Combine(collection.Directory, PcFolder(pc.Number)), Defaults.CollectMaxBytes, Defaults.CollectMaxFiles, CancellationToken.None);
            pc.Files = files;
            Settle(collection, pc, CollectedState.Collected, job.Message);
        }
        catch (Exception ex)
        {
            // Any failure settles the row: an exception escaping this task would leave it collecting forever.
            Settle(collection, pc, CollectedState.Failed, Strings.Format("Collect.UnpackFailed", ex.Message));
        }
        finally
        {
            lock (_gate)
            {
                DropGrantLocked(pc);
            }
        }
    }

    private WorkCollection Create(string name, string destinationRoot, string folder, IEnumerable<string> agentIds)
    {
        var now = _clock();
        var stem = SafeDirectoryName(name) + " " + now.ToLocalTime().ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            var directory = Path.Combine(destinationRoot, stem);
            for (var n = 2; System.IO.Directory.Exists(directory) || _collections.Any(c => c.Directory == directory); n++)
            {
                directory = Path.Combine(destinationRoot, $"{stem} ({n})");
            }

            var collection = new WorkCollection { Id = Guid.NewGuid().ToString("n"), Name = name, Folder = folder, Directory = directory, CreatedAt = now };
            foreach (var id in agentIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                collection.Pcs[id] = new CollectedPc { AgentId = id, Number = _numberOf(id) };
            }

            _collections.Add(collection);
            return collection;
        }
    }

    private void Collect(WorkCollection collection, IReadOnlyCollection<string> agentIds)
    {
        string[] ids;
        lock (_gate)
        {
            ids = agentIds.Where(id => collection.Pcs.TryGetValue(id, out var pc) && pc.State == CollectedState.Waiting).ToArray();
            foreach (var id in ids)
            {
                collection.Pcs[id].State = CollectedState.Collecting;
            }
        }

        if (ids.Length == 0)
        {
            return;
        }

        var jobs = _createJobs(ids, new CollectRequest(collection.Folder));
        foreach (var job in jobs)
        {
            var pc = collection.Pcs[job.AgentId];
            pc.JobId = job.Id;
            lock (_gate)
            {
                _byJob[job.Id] = (collection, job.AgentId);
            }

            // A PC that is not linked is closed on the spot (online only); its Updated may have fired before the map knew it.
            if (job.IsFinished)
            {
                OnJobUpdated(job);
            }
        }

        WriteReport(collection);
        Changed?.Invoke();
    }

    private void Settle(WorkCollection collection, CollectedPc pc, CollectedState state, string message)
    {
        bool done;
        lock (_gate)
        {
            if (state != CollectedState.Collected)
            {
                // A failed, timed-out or missing PC keeps no grant and no staging file (review 2026-10-23).
                DropGrantLocked(pc);
            }

            pc.State = state;
            pc.Message = message;
            done = !collection.Announced && collection.Pcs.Values.All(p => p.IsFinished);
            if (done)
            {
                collection.Announced = true;
            }
        }

        if (state is CollectedState.Failed)
        {
            _events.Warning("collect.failed", Strings.Format("Event.CollectFailed", PcFolder(pc.Number), message), pc.AgentId, pc.Number);
        }

        WriteReport(collection);
        if (done)
        {
            var rows = collection.Rows;
            var notCollected = rows.Where(r => r.State is CollectedState.Missing or CollectedState.Failed or CollectedState.Empty).Select(r => PcFolder(r.Number)).ToArray();
            _events.Info("collect.done", notCollected.Length == 0
                ? Strings.Format("Event.CollectDoneAll", collection.Name, rows.Count, collection.Directory)
                : Strings.Format("Event.CollectDone", collection.Name, rows.Count(r => r.State == CollectedState.Collected), rows.Count,
                    string.Join(", ", notCollected), collection.Directory));
        }

        Changed?.Invoke();
    }

    /// <summary>The summary beside the PCs' folders, rewritten on every change; a write that fails is an event, never a lost collection.</summary>
    private void WriteReport(WorkCollection collection)
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine(Strings.Format("Collect.ReportTitle", collection.Name, collection.Folder, collection.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            text.AppendLine();
            foreach (var row in collection.Rows)
            {
                text.Append(PcFolder(row.Number)).Append("  ").Append(Strings.Get("Collect.State." + row.State));
                if (row.State == CollectedState.Collected)
                {
                    text.Append("  ").Append(Strings.Format("Collect.Files", row.Files));
                }

                // A collected PC's message names what it left out (open files, links, odd names): the teacher must see that too.
                if (row.Message.Length > 0)
                {
                    text.Append("  — ").Append(row.Message);
                }

                text.AppendLine();
            }

            System.IO.Directory.CreateDirectory(collection.Directory);
            File.WriteAllText(Path.Combine(collection.Directory, Defaults.CollectReportFileName), text.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _events.Warning("collect.report_failed", Strings.Format("Event.CollectReportFailed", collection.Directory, ex.Message));
        }
    }

    private void DropGrantLocked(CollectedPc pc)
    {
        var grant = pc.Grant;
        var staging = pc.Staging;
        var path = pc.StagingPath;
        pc.Grant = null;
        pc.Staging = null;
        pc.StagingPath = null;
        if (grant is not null)
        {
            // The grant before the stream (D-42); disposal waits for an upload in progress.
            grant.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        staging?.Dispose();
        if (path is not null)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The session closes: every grant and staging file goes; a collection in progress is lost (D-77 item 1).</summary>
    public void Close()
    {
        lock (_gate)
        {
            foreach (var pc in _collections.SelectMany(c => c.Pcs.Values))
            {
                DropGrantLocked(pc);
            }

            _byJob.Clear();
            _exams.Clear();
        }
    }

    internal static string PcFolder(int number) => string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, number);

    /// <summary>A collection's name as one folder name on macOS, Windows and Linux alike.</summary>
    internal static string SafeDirectoryName(string name)
    {
        var cleaned = new string((name ?? string.Empty).Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length > 60)
        {
            cleaned = cleaned[..60].TrimEnd();
        }

        return cleaned.Length == 0 || cleaned.StartsWith('.') ? Strings.Get("Collect.DefaultName") : cleaned;
    }
}
