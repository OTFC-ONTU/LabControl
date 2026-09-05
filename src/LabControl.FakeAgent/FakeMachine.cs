using System.Globalization;
using Google.Protobuf;
using LabControl.Shared;
using LabControl.Shared.Jobs;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
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

    public FakeMachine(DirectoryAgentStore store, FailureSpec? failure, ILogger log)
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

        // A real PC's supervisor publishes this at start and on every change (M2); the
        // simulator pretends the helper is up and the student is at the desk.
        PublishSession(SessionState.Types.Kind.Unspecified);
    }

    public AgentLink Link { get; }

    public int Number => _store.Config.Number;

    public string Name => string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, Number);

    public string AgentId => _store.Config.AgentId;

    public bool PoweredOn { get; private set; } = true;

    public string? LoggedOnUser { get; private set; } = Defaults.StudentAccountName;

    /// <summary>The console this PC is linked to right now, by name.</summary>
    public string? Console { get; private set; }

    public FailureKind Failure => _failure?.Kind ?? FailureKind.None;

    public string Status =>
        !PoweredOn ? "off"
        : Link.State switch
        {
            LinkState.Linked => $"linked to {Console}",
            LinkState.Connecting => "connecting",
            LinkState.Searching => Link.IsEnrolled ? "searching" : "waiting to enrol",
            _ => "stopped",
        };

    public void Start()
    {
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
    public void OfferBeacon(ReadOnlyMemory<byte> datagram)
    {
        if (PoweredOn)
        {
            Link.OfferBeacon(datagram);
        }
    }

    // ------------------------------------------------------------------ IAgentBehaviour

    public void Describe(Hello hello)
    {
        hello.AgentVersion = typeof(FakeMachine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
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

            case Job.Types.Kind.RunScript:
                return await RunScriptAsync(job, report, token);

            case Job.Types.Kind.SelfUpdate:
                return Ok(job, "Update accepted (simulated; nothing installed).");

            default:
                await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                return Ok(job, $"{job.Kind} done (simulated).");
        }
    }

    private static JobResult Ok(Job job, string message) => new() { JobId = job.Id, Ok = true, ExitCode = 0, Message = message };

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
