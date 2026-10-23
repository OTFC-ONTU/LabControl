using System.Globalization;
using Google.Protobuf;
using LabControl.Shared;
using LabControl.Shared.Jobs;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging;

namespace LabControl.FakeAgent;

/// <summary>
/// One simulated student PC: a real <see cref="AgentLink"/> over a real store in
/// <c>&lt;data&gt;/PC-NN/</c>, so enrolment, the link, jobs and renewal are the genuine
/// article; only what a Windows PC would <i>do</i> with a job is pretended here.
/// </summary>
public sealed class FakeMachine : IAgentBehaviour, IAsyncDisposable
{
    private readonly DirectoryAgentStore _store;
    private readonly FailureSpec? _failure;
    private readonly ILogger _log;
    private readonly DateTimeOffset _bootedAt = DateTimeOffset.UtcNow;
    private readonly Random _random = new();

    private CancellationTokenSource? _powerCycle;
    private readonly ScreenProducer _screen;
    private readonly FakeMachineState _machineState;
    private readonly PolicyEnforcer _policy;
    private FakeScreen? _desktop;
    private int _sasCount;
    private readonly Timer _browsing;
    private readonly Timer _launching;

    /// <summary>
    /// Names a simulated student's browser asks for (M6 portion 3): the CDN and font hosts a
    /// documentation site typically pulls in, so a whitelist on the simulator refuses a few
    /// and the console's refused-names list has something to show.
    /// </summary>
    private static readonly string[] LaunchedPrograms = ["chrome.exe", "telegram.exe", "notepad.exe", "idea64.exe", "calc.exe"];

    private static readonly string[] BrowsedNames =
    [
        "www.jetbrains.com", "resources.jetbrains.com", "docs.oracle.com", "fonts.googleapis.com", "fonts.gstatic.com",
        "cdn.jsdelivr.net", "www.google-analytics.com", "www.youtube.com", "static.oracle.com", "stackoverflow.com",
    ];

    /// <summary>What this PC says it runs; a simulated push changes it (D-33).</summary>
    private string _version = typeof(FakeMachine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private string? _previousVersion;

    /// <param name="policyMutexWait">How long a policy pass waits for this PC's mutex; the product default when <c>null</c> — a test that holds the mutex on purpose passes a short one.</param>
    /// <param name="broadcastCeiling">This PC's own broadcast limit; <see cref="Defaults.BroadcastHardLimit"/> when <c>null</c> — a test that wants to see it end passes a short one.</param>
    /// <param name="broadcastStallTimeout">How long this PC waits for a broadcast frame; <see cref="Defaults.BroadcastStallTimeout"/> when <c>null</c>.</param>
    public FakeMachine(DirectoryAgentStore store, FailureSpec? failure, ILogger log, TimeSpan? policyMutexWait = null, TimeSpan? broadcastCeiling = null,
        TimeSpan? broadcastStallTimeout = null)
    {
        _store = store;
        _failure = failure;
        _log = log;

        var options = new AgentLinkOptions
        {
            ProtocolVersion = failure?.Kind == FailureKind.Outdated ? Defaults.MinimumProtocolVersion - 1 : Defaults.ProtocolVersion,
            ExtraRevocations = failure?.Kind == FailureKind.ForgedRevocation ? ForgedRevocation : null,
        };

        Link = new AgentLink(store, this, log, options);
        Link.Linked += (_, name) => Console = name;
        Link.Unlinked += _ => Console = null;

        // A simulated desktop (M3, OpenDesktop); the producer is the one the session helper
        // runs (D-35) and only the source is simulated.
        _screen = new ScreenProducer(
            Link.Name,
            OpenDesktop,
            (frame, _) => ValueTask.FromResult(Link.TryPushVideo(frame)),
            (severity, code, message) => Link.Report(severity, code, message),
            log);
        Link.VideoControlChanged += _screen.Apply;
        Link.InputReceived += OnInput;

        // The policy journal (M6, D-69) is the real one over policy.json in this PC's own
        // directory; only what the machine does with a lock is simulated. The mutex name is
        // per PC so thirty of them in one process never queue behind one another.
        _machineState = new FakeMachineState(() => _desktop, log);
        _policy = new PolicyEnforcer(Link, _machineState, store.Directory, store.Config.LabId, log,
            mutexName: PolicyMutexName(store.Config.AgentId), mutexWait: policyMutexWait, broadcastCeiling: broadcastCeiling,
            broadcastStallTimeout: broadcastStallTimeout);

        // A whitelist on a simulated PC refuses what a real student's browser would ask for.
        var browseEvery = TimeSpan.FromSeconds(15 + _random.Next(15));
        _browsing = new Timer(_ => _machineState.Browse(BrowsedNames[Random.Shared.Next(BrowsedNames.Length)]), null, browseEvery, browseEvery);

        // An exam's whitelist sees what a real student has open and what they try to start
        // (M6 portion 4): a few programs left from the last lesson, and now and then another.
        _machineState.Seed(["explorer.exe", "chrome.exe", "notepad.exe"]);
        var launchEvery = TimeSpan.FromSeconds(40 + _random.Next(40));
        _launching = new Timer(_ => { if (_machineState.Exam is not null) _machineState.Launch(LaunchedPrograms[Random.Shared.Next(LaunchedPrograms.Length)]); }, null, launchEvery, launchEvery);

        // A real PC's supervisor publishes this at start and on every change (M2); the
        // simulator pretends the helper is up and the student is at the desk.
        PublishSession(SessionState.Types.Kind.Unspecified);
    }

    public AgentLink Link { get; }

    /// <summary>The policy enforcer over this PC's <c>policy.json</c> (M6), for the status line and the tests.</summary>
    public PolicyEnforcer Policy => _policy;

    /// <summary>The name of this PC's own policy mutex (per PC, so thirty in one process never queue behind one another); a test may hold it to make a pass report busy.</summary>
    public static string PolicyMutexName(string agentId) => Defaults.PolicyMutexName + "." + agentId.Replace("-", string.Empty, StringComparison.Ordinal);

    /// <summary>The lock this PC holds right now, from its own journal; <c>null</c> when none.</summary>
    public OverlaySession? Lock => _machineState.Overlay;

    /// <summary>The internet policy this PC holds right now (M6 portion 3); <c>null</c> when none.</summary>
    public InternetSession? Internet => _machineState.Internet;

    /// <summary>The exam this PC holds right now (M6 portion 4); <c>null</c> when none.</summary>
    public ExamSession? Exam => _machineState.Exam;

    /// <summary>The simulated student's programs, for the tests.</summary>
    public IReadOnlyList<string> Running => _machineState.Running;

    /// <summary>The simulated student starts a program, for the tests: ended at once when an exam's whitelist does not allow it.</summary>
    public void Launch(string program) => _machineState.Launch(program);

    /// <summary>One simulated browser request, for the tests: refused and noted when a whitelist does not cover it.</summary>
    public void Browse(string name) => _machineState.Browse(name);

    /// <summary>The broadcast this PC shows right now (M6 portion 2); <c>null</c> when none.</summary>
    public BroadcastSession? Broadcast => _machineState.Broadcast;

    /// <summary>The teacher's screen as this PC decoded it, for the tests.</summary>
    public BroadcastPicture BroadcastPicture => _machineState.Picture;

    /// <summary>
    /// A simulated desktop (M3): odd numbers are 1080p, even ones 1366×768 like the older lab
    /// PCs; every third PC sits idle so the mosaic shows screens that do not change. It is
    /// created when the console first asks for video, so a lock applied before that is drawn
    /// onto the desktop that appears later.
    /// </summary>
    private FakeScreen OpenDesktop()
    {
        var number = Number;
        var desktop = number % 2 == 1 ? new FakeScreen(number, 1920, 1080, idle: number % 3 == 0) : new FakeScreen(number, 1366, 768, idle: number % 3 == 0);
        desktop.SetOverlay(_machineState.Overlay);
        desktop.SetExamStrip(_machineState.StripContent());
        if (_machineState.Broadcast is not null)
        {
            desktop.SetBroadcast(_machineState.Picture);
        }

        _desktop = desktop;
        return desktop;
    }

    public string MaterialsDirectory => Path.Combine(_store.Directory, Defaults.MaterialsFolderName);

    public int Number => _store.Config.Number;

    public string Name => string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, Number);

    public string AgentId => _store.Config.AgentId;

    public bool PoweredOn { get; private set; } = true;

    public string? LoggedOnUser { get; private set; } = Defaults.StudentAccountName;

    /// <summary>The console this PC is linked to right now, by name.</summary>
    public string? Console { get; private set; }

    public FailureKind Failure => _failure?.Kind ?? FailureKind.None;

    /// <summary>The desktop the producer is drawing right now (M3 portion 3); <c>null</c> while nobody watches.</summary>
    public FakeScreen? Desktop => _desktop;

    /// <summary>Ctrl+Alt+Del requests "sent" so far — a real service would call <c>SendSAS</c>.</summary>
    public int SecureAttentionCount => _sasCount;

    /// <summary>
    /// The teacher's input (D-36): a real agent relays it to the helper; the simulator hands
    /// it to the desktop it draws, and answers Ctrl+Alt+Del with an event, the way the real
    /// service answers a failure — so the console's event list shows the request arrived.
    /// </summary>
    private void OnInput(Input input)
    {
        if (input.Kind == Input.Types.Kind.CtrlAltDel)
        {
            Interlocked.Increment(ref _sasCount);
            Link.Report(Event.Types.Severity.Info, "input.sas", "Ctrl+Alt+Del sent to the interactive session (simulated).");
            return;
        }

        _desktop?.Apply(input);
    }

    public string Status =>
        !PoweredOn ? "off"
        : Link.State switch
        {
            LinkState.Linked => (_screen.Streaming is { } mode ? $"linked to {Console}, streaming {mode.ToString().ToLowerInvariant()}" : $"linked to {Console}") + (Lock is null ? string.Empty : ", locked"),
            LinkState.Connecting => "connecting",
            LinkState.Searching => Link.IsEnrolled ? "searching" : "waiting to enrol",
            _ => "stopped",
        };

    public void Start()
    {
        // Restore first, then re-apply what policy.json still holds (D-69) — before the link
        // dials, so the first OverlayState after Welcome already says whether a lock survived.
        // The simulated helper's hooks always hold; a real one reports this over the pipe.
        _policy.Start();
        _policy.NoteEnforcement(inputBlocked: true, reason: string.Empty);

        switch (Failure)
        {
            case FailureKind.NeverConnects:
                _log.LogInformation("{Pc}: playing a PC that is switched off — never dials", Name);
                PoweredOn = false;
                return;

            case FailureKind.ConnectsLate:
                _log.LogInformation("{Pc}: playing a PC still booting — dials in {Seconds} s", Name, _failure!.Seconds);
                _ = Task.Delay(TimeSpan.FromSeconds(_failure.Seconds)).ContinueWith(_ => Link.Start(), TaskScheduler.Default);
                return;

            default:
                Link.Start();
                return;
        }
    }

    /// <summary>Hands the PC a beacon; a switched-off PC hears nothing.</summary>
    public void OfferBeacon(ReadOnlyMemory<byte> datagram, DateTimeOffset? receivedAt = null)
    {
        if (PoweredOn)
        {
            Link.OfferBeacon(datagram, receivedAt);
        }
    }

    // ------------------------------------------------------------------ IAgentBehaviour

    public void Describe(Hello hello)
    {
        hello.AgentVersion = _version;
        hello.BootTimeUnix = _bootedAt.ToUnixTimeSeconds();
        hello.UpdateState = new UpdateState { Phase = UpdateState.Types.Phase.Stable };
    }

    public Inventory DescribeInventory() => new()
    {
        Hostname = _store.Config.Hostname,
        WindowsBuild = "10.0.19045 (simulated)",
        Cpu = "Simulated CPU",
        MemoryBytes = 8L * 1024 * 1024 * 1024,
        DiskTotalBytes = 256L * 1024 * 1024 * 1024,
        DiskFreeBytes = 120L * 1024 * 1024 * 1024,
        UptimeSeconds = (long)(DateTimeOffset.UtcNow - _bootedAt).TotalSeconds,
        LoggedOnUser = LoggedOnUser ?? string.Empty,
    };

    public async Task<JobResult> RunJobAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        _log.LogInformation("{Pc}: job {Kind} {Id}", Name, job.Kind, job.Id);

        if (Failure == FailureKind.DiesMidJob)
        {
            // The cable comes out just as the job lands; the result never reaches the console
            // on this link. The console re-sends on reconnect and the ledger answers.
            _log.LogWarning("{Pc}: dying mid-job (simulated), back in 5 s", Name);
            Link.Disconnect("simulated crash while running a job");
            await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        if (Failure == FailureKind.JobError)
        {
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = 1, Message = "Simulated failure: this PC refuses every job." };
        }

        switch (job.Kind)
        {
            case Job.Types.Kind.Shutdown:
                _ = PowerCycleAsync(TimeSpan.FromSeconds(30), "shut down");
                return Ok(job, "Shutting down (simulated; back in 30 s as if someone pressed the button).");

            case Job.Types.Kind.Reboot:
                _ = PowerCycleAsync(TimeSpan.FromSeconds(8), "rebooted");
                return Ok(job, "Rebooting (simulated).");

            case Job.Types.Kind.Logoff:
                LoggedOnUser = null;
                PublishSession(SessionState.Types.Kind.Logoff);
                _ = Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ =>
                {
                    LoggedOnUser = Defaults.StudentAccountName;
                    PublishSession(SessionState.Types.Kind.Logon);
                }, TaskScheduler.Default);
                return Ok(job, "Logged the student off (simulated; auto-logon brings them back in 5 s).");

            case Job.Types.Kind.SendFile:
                return await SendFileAsync(job, report, token);

            case Job.Types.Kind.RunScript:
                return await RunScriptAsync(job, report, token);

            case Job.Types.Kind.SelfUpdate:
                return await SelfUpdateAsync(job, report, token);

            default:
                await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                return Ok(job, $"{job.Kind} done (simulated).");
        }
    }

    private async Task<JobResult> SendFileAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        JobResult Fail(string message) => new() { JobId = job.Id, ExitCode = -1, Message = message };
        if (!SendFileRequest.TryParse(job, out var request, out var error)) return Fail(error);
        var temporary = Path.Combine(MaterialsDirectory, Guid.NewGuid().ToString("n") + ".partial");
        try
        {
            Directory.CreateDirectory(MaterialsDirectory);
            var lastReport = DateTimeOffset.MinValue;
            long received;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                Defaults.FileChunkBytes, FileOptions.Asynchronous))
            {
                received = await Link.PullFileAsync(request.Reference, request.Sha256, stream, token, async bytes =>
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - lastReport < TimeSpan.FromSeconds(1)) return;
                    lastReport = now;
                    await report(new JobProgress { JobId = job.Id, Line = $"Receiving {request.Name}: {bytes} bytes." });
                });
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(MaterialsDirectory, request.Name), overwrite: true);
            await report(new JobProgress { JobId = job.Id, Percent = 100, Line = $"Delivered {request.Name}: {received} verified bytes." });
            return Ok(job, request.MayOpen
                ? $"Delivered {request.Name}; opening in the student session (simulated)."
                : $"Delivered {request.Name}; not opened (simulated PC)." );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilePullException)
        {
            return Fail($"Could not deliver {request.Name}: {ex.Message}");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Link.ReportForJob(job.Id, Event.Types.Severity.Warning, "files.cleanup_failed", ex.Message);
            }
        }
    }

    private static JobResult Ok(Job job, string message) => new() { JobId = job.Id, Ok = true, ExitCode = 0, Message = message };

    /// <summary>
    /// <c>self_update</c> on a simulated PC (D-33): the manifest and every file are really
    /// pulled and hash-checked, as the Windows agent does; the install is pretended. Then the
    /// PC does what a real one does — drops the link and stops the job without a result — and
    /// comes back claiming the new version, so the re-sent job is answered by "the new
    /// version". This is the console's whole view of a push, exercised on the Mac.
    /// </summary>
    private async Task<JobResult> SelfUpdateAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        if (!SelfUpdateRequest.TryParse(job, out var request, out var error))
        {
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = $"This job cannot run: {error}." };
        }

        if (string.Equals(request.Version, _version, StringComparison.Ordinal))
        {
            return Ok(job, _previousVersion is null
                ? $"Running {_version} now. (simulated)"
                : $"Running {_version} now (was {_previousVersion}; app\\previous still names it). (simulated)");
        }

        UpdateManifest manifest;
        try
        {
            using var buffer = new MemoryStream();
            await Link.PullFileAsync(request.ManifestReference, request.ManifestSha256, buffer, token);
            if (!UpdateBundle.TryReadVerified(buffer.ToArray(), request, _store.Authority, _version, out manifest, out var problem))
            {
                return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = $"The update was refused: {problem}." };
            }

            var total = manifest.Files.Sum(f => f.Size);
            long done = 0;
            foreach (var file in manifest.Files)
            {
                var received = await Link.PullFileAsync(file.Sha256, file.Sha256, Stream.Null, token);
                if (received != file.Size)
                {
                    return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = $"{file.RelativePath} is {received} bytes but the manifest says {file.Size}; nothing was installed." };
                }

                done += received;
                await report(new JobProgress { JobId = job.Id, Percent = total > 0 ? (int)(done * 100 / total) : 0, Line = $"pulled {file.RelativePath} ({AgentBuild.Megabytes(received)})" });
            }
        }
        catch (FilePullException ex)
        {
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = ex.Message + " Nothing was installed." };
        }

        await report(new JobProgress { JobId = job.Id, Percent = 100, Line = $"installed into app\\{request.Version}; service repointed, restarting it now (simulated)" });
        _log.LogInformation("{Pc}: restarting as {Version} (simulated)", Name, request.Version);

        _previousVersion = _version;
        _version = request.Version;
        Link.Disconnect("restarting after the update (simulated)");

        // No result from the outgoing version: the link forgets the job, and the re-sent copy
        // is answered above once the "new version" is linked.
        throw new OperationCanceledException("the agent is restarting (simulated)");
    }

    /// <summary>
    /// <c>run_script</c> on a simulated PC: the file channel is real — the script is pulled
    /// through <c>PullFile</c> and hash-checked exactly as the Windows agent does — and only
    /// the shell is pretended. The pretence understands what the console's built-in test
    /// scripts contain (<see cref="TestScripts"/>): a printed line, <c>exit N</c> and a sleep,
    /// with the same inactivity timeout the real agent enforces, so the console can be
    /// developed against the acceptance scripts on the Mac.
    /// </summary>
    private async Task<JobResult> RunScriptAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        if (!RunScriptRequest.TryParse(job, out var request, out var error))
        {
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = $"This job cannot run: {error}." };
        }

        string text;
        try
        {
            using var buffer = new MemoryStream();
            var bytes = await Link.PullFileAsync(request.Reference, request.Sha256, buffer, token);
            text = System.Text.Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
            _log.LogInformation("{Pc}: pulled {Name} ({Bytes} bytes), running as {RunAs} with {Shell} (simulated)", Name, request.Name, bytes, request.RunAs, request.Shell);
        }
        catch (FilePullException ex)
        {
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = ex.Message };
        }

        if (request.RunAs == ScriptRunAs.User && LoggedOnUser is null)
        {
            return new JobResult { JobId = job.Id, Ok = false, ExitCode = -1, Message = "The script was to run as the logged-on user, but nobody is logged on to this PC right now." };
        }

        var started = DateTimeOffset.UtcNow;
        var lines = 0;
        var exitCode = 0;
        var user = request.RunAs == ScriptRunAs.User ? LoggedOnUser! : "SYSTEM";

        async Task Print(string line)
        {
            lines++;
            await report(new JobProgress { JobId = job.Id, Percent = 0, Line = line });
            await Task.Delay(TimeSpan.FromMilliseconds(5 + _random.Next(20)), token);
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || line.StartsWith('@') || line.StartsWith(':'))
            {
                continue;
            }

            // for ($i = 1; $i -le N; $i++) { Write-Output "line $i of N" }  |  for /l %%i in (1,1,N) do echo line %%i of N
            var count = System.Text.RegularExpressions.Regex.Match(line, @"(?:-le|in \(1,1,)\s*(\d+)");
            if (count.Success && (line.Contains("Write-Output", StringComparison.Ordinal) || line.Contains("echo", StringComparison.Ordinal)))
            {
                var n = int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture);
                for (var i = 1; i <= n; i++)
                {
                    await Print($"line {i} of {n}");
                }

                continue;
            }

            var exit = System.Text.RegularExpressions.Regex.Match(line, @"^exit(?: /b)?\s+(\d+)");
            if (exit.Success)
            {
                exitCode = int.Parse(exit.Groups[1].Value, CultureInfo.InvariantCulture);
                break;
            }

            if (line.Contains("Start-Sleep", StringComparison.Ordinal) || line.StartsWith("timeout ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("goto", StringComparison.OrdinalIgnoreCase))
            {
                // The hang: silence until the agent's inactivity timeout kills the tree.
                await Task.Delay(request.Timeout, token);
                return new JobResult
                {
                    JobId = job.Id,
                    Ok = false,
                    ExitCode = -1,
                    Message = $"Killed after {request.Timeout.TotalSeconds:0} s without output ({lines} line(s) received, {(DateTimeOffset.UtcNow - started).TotalSeconds:0} s in total). The script and everything it started were terminated. (simulated)",
                };
            }

            var print = System.Text.RegularExpressions.Regex.Match(line, @"^(?:Write-Output\s+""(?<text>.*)""|echo\s+(?<text>.*))$");
            if (print.Success)
            {
                var value = print.Groups["text"].Value
                    .Replace("$env:USERNAME", user, StringComparison.Ordinal)
                    .Replace("%USERNAME%", user, StringComparison.Ordinal)
                    .Replace("$PWD", "~/.labcontrol-fake (simulated)", StringComparison.Ordinal)
                    .Replace("%CD%", "~/.labcontrol-fake (simulated)", StringComparison.Ordinal);
                value = System.Text.RegularExpressions.Regex.Replace(value, @"\$\([^)]*\)|\$[A-Za-z_:]+|%[^%]+%", "(simulated)");
                await Print(value);
                continue;
            }

            if (line.StartsWith("for /f", StringComparison.OrdinalIgnoreCase))
            {
                await Print($"account: {Name.ToLowerInvariant()}\\{user.ToLowerInvariant()} (simulated)");
            }
        }

        var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        return new JobResult { JobId = job.Id, Ok = exitCode == 0, ExitCode = exitCode, Message = $"exit {exitCode} after {elapsed} s, {lines} line(s) (simulated)" };
    }

    private void PublishSession(SessionState.Types.Kind kind) => Link.PublishSessionState(new SessionState
    {
        Kind = kind,
        User = LoggedOnUser ?? string.Empty,
        SessionId = 1,
        HelperAlive = true,
        Locked = false,
    });

    /// <summary>Goes dark for a while, then "boots" and dials again.</summary>
    private async Task PowerCycleAsync(TimeSpan off, string what)
    {
        _powerCycle?.Cancel();
        var cycle = _powerCycle = new CancellationTokenSource();

        // Let the result leave first, as a real shutdown gives the service a moment.
        await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        PoweredOn = false;
        Link.Disconnect($"the PC {what}");
        _log.LogInformation("{Pc}: off for {Seconds:0} s", Name, off.TotalSeconds);

        try
        {
            await Task.Delay(off, cycle.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        PoweredOn = true;
        LoggedOnUser = Defaults.StudentAccountName;
        PublishSession(SessionState.Types.Kind.Unspecified);
        _log.LogInformation("{Pc}: booted", Name);
    }

    private static IEnumerable<RevocationEntry> ForgedRevocation() =>
    [
        new RevocationEntry
        {
            Serial = "DEADBEEF",
            RevokedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Reason = "forged by a simulated PC",
            Signature = ByteString.CopyFrom(new byte[64]),
        },
    ];

    public async ValueTask DisposeAsync()
    {
        _powerCycle?.Cancel();
        Link.VideoControlChanged -= _screen.Apply;
        Link.InputReceived -= OnInput;
        // The OnStop restore (D-69 item 5): the machine is opened, the document is kept.
        await _browsing.DisposeAsync();
        await _launching.DisposeAsync();
        await _policy.DisposeAsync();
        _machineState.Dispose();
        await _screen.DisposeAsync();
        await Link.DisposeAsync();
        _store.Dispose();
    }

    // ------------------------------------------------------------------ installation

    /// <summary>
    /// What Setup.exe does with the stick, for one machine: takes a code from
    /// <c>setup.json</c>, generates the keypair, pins <c>ca.crt</c>, writes <c>agent.json</c>.
    /// </summary>
    public static DirectoryAgentStore Install(string directory, int number, SetupPayload payload, string? consoleHost, int consolePort, bool burnedCode)
    {
        // A burned code is one the stick already spent on another PC — in this run or an
        // earlier one, setup.json remembers both. A stick that spent nothing yet cannot fake it.
        var code = burnedCode
            ? payload.Document.UsedEnrollmentCodes.FirstOrDefault() ?? payload.TakeCode()
            : payload.TakeCode();

        // Locally administered MAC (02:…), so a simulated PC can never collide with a real one.
        return AgentProvisioning.Install(directory, payload, code, number,
            hostname: AgentProvisioning.NameOf(number),
            mac: MachineFacts.FormatMac([0x02, 0x00, 0x5E, 0x00, 0x00, (byte)number]),
            consoleHost, consolePort);
    }
}
