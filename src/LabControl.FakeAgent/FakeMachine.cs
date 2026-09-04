using System.Globalization;
using Google.Protobuf;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
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
                _ = Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ => LoggedOnUser = Defaults.StudentAccountName, TaskScheduler.Default);
                return Ok(job, "Logged the student off (simulated; auto-logon brings them back in 5 s).");

            case Job.Types.Kind.RunScript:
                var lines = job.Args.TryGetValue("lines", out var count) && int.TryParse(count, out var n) ? n : 5;
                var exit = job.Args.TryGetValue("exit", out var code) && int.TryParse(code, out var e) ? e : 0;
                for (var i = 1; i <= lines; i++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 + _random.Next(300)), token);
                    await report(new JobProgress { JobId = job.Id, Percent = i * 100 / lines, Line = $"line {i} of {lines} from {Name}" });
                }

                return new JobResult { JobId = job.Id, Ok = exit == 0, ExitCode = exit, Message = $"exit {exit}" };

            case Job.Types.Kind.SelfUpdate:
                return Ok(job, "Update accepted (simulated; nothing installed).");

            default:
                await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                return Ok(job, $"{job.Kind} done (simulated).");
        }
    }

    private static JobResult Ok(Job job, string message) => new() { JobId = job.Id, Ok = true, ExitCode = 0, Message = message };

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
        var code = burnedCode
            ? payload.UsedCodes.FirstOrDefault() ?? payload.TakeCode()
            : payload.TakeCode();

        var config = new AgentConfigDocument
        {
            LabId = payload.Document.LabId,
            AgentId = Guid.NewGuid().ToString("d"),
            Number = number,
            Hostname = string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, number),
            Mac = $"02:00:5E:00:00:{number:X2}",
            EnrollmentCode = code,
            ConsoleHost = consoleHost ?? payload.Document.ConsoleHost,
            ConsolePort = consolePort,
        };

        return DirectoryAgentStore.Install(directory, config, payload.Authority);
    }
}
