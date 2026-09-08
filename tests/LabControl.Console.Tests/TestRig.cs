using System.Net;
using System.Security.Cryptography.X509Certificates;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protection;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// A console on a random loopback port plus any number of agents dialling it directly
/// (pinned host, no beacons), all in one process. The lab key uses a low PBKDF2 count so
/// a test can create a lab in milliseconds; the format is identical (D-24 item 5).
/// </summary>
internal sealed class TestConsole : IAsyncDisposable
{
    // Each test process uses a separate discovery port, so an open desktop console
    // cannot consume the loopback datagrams on macOS. All test labs still share UDP.
    public static readonly int BeaconPort = ReserveBeaconPort();

    private static int ReserveBeaconPort()
    {
        using var socket = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    public const int Iterations = 1_000;
    public const string HolderName = "Viacheslav";
    public const string Passphrase = "correct horse battery staple";

    private TestConsole(string directory, LabSession session, RecoveryCode? recoveryCode)
    {
        Directory = directory;
        Session = session;
        RecoveryCode = recoveryCode;
    }

    public string Directory { get; }

    public LabSession Session { get; }

    public RecoveryCode? RecoveryCode { get; }

    public int Port => Session.Port;

    public X509Certificate2 Authority => Session.Authority;

    /// <summary>Creates a fresh lab, mints an instance and starts serving. The key starts unlocked.</summary>
    public static async Task<TestConsole> CreateLabAsync(string instanceName = "Test console", TimeSpan? agentCertificateLifetime = null, int port = 0)
    {
        var directory = TempDirectory();
        var lab = LabKey.Create("Test lab", HolderName, Passphrase, out var recoveryCode, iterations: Iterations);
        return new TestConsole(directory, await OpenAsync(directory, lab, instanceName, agentCertificateLifetime, port), recoveryCode);
    }

    /// <summary>A second (or later) console for the same lab, as another teacher machine would be after importing the backup.</summary>
    public static async Task<TestConsole> JoinLabAsync(TestConsole existing, string instanceName, LabDocument? labDocument = null, int port = 0)
    {
        var directory = TempDirectory();
        var document = JsonStore.Parse<LabKeyDocument>(
            JsonStore.Serialize(existing.Session.Vault.Document, LabKeyDocument.Migrations), Defaults.LabKeyFileName, LabKeyDocument.Migrations);

        Assert.True(LabKey.TryUnlock(document, Passphrase, out var lab));

        if (labDocument is not null)
        {
            new LabStore(directory).EnsureDirectories();
            new LabStore(directory).SaveLab(labDocument);
        }

        return new TestConsole(directory, await OpenAsync(directory, lab, instanceName, null, port), null);
    }

    private static async Task<LabSession> OpenAsync(string directory, LabKey lab, string instanceName, TimeSpan? agentCertificateLifetime, int port)
    {
        var store = new LabStore(directory);
        store.EnsureDirectories();
        store.SaveLabKey(lab.Document);

        var instance = ConsoleInstance.Mint(lab, instanceName, new FileSecretProtector());
        store.SaveInstance(instance.Document);

        var vault = new LabKeyVault(store, lab.Document);
        vault.Adopt(lab);

        var options = new ConsoleOptions
        {
            DataDirectory = directory,
            Port = port,
            BeaconPort = BeaconPort,
            BindAddress = IPAddress.Loopback,
            DevelopmentAgentCertificateLifetime = agentCertificateLifetime,
        };

        var session = new LabSession(options, store, vault, instance, instance.Document, TestLogging.Factory);
        await session.StartAsync();
        return session;
    }

    public IReadOnlyList<string> IssueCodes(int count) =>
        Session.Enrollment.Generate(count, "test", DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "labcontrol-tests", Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>A simulated PC: a directory store, a scripted behaviour and an <see cref="AgentLink"/>.</summary>
internal sealed class TestAgent : IAsyncDisposable
{
    public TestAgent(DirectoryAgentStore store, ScriptedBehaviour behaviour, AgentLink link)
    {
        Store = store;
        Behaviour = behaviour;
        Link = link;
    }

    public DirectoryAgentStore Store { get; }

    public ScriptedBehaviour Behaviour { get; }

    public AgentLink Link { get; }

    public string AgentId => Store.Config.AgentId;

    public int Number => Store.Config.Number;

    public List<string> Refusals { get; } = [];

    /// <summary>Installs a PC pointed straight at the console, as Setup would with <c>console_host</c> pinned.</summary>
    public static TestAgent Install(TestConsole console, int number, string? code, string? agentId = null, AgentLinkOptions? options = null, bool pinHost = true)
    {
        var directory = Path.Combine(TestConsole.TempDirectory(), $"PC-{number:00}");
        var config = new AgentConfigDocument
        {
            LabId = console.Session.LabId,
            AgentId = agentId ?? Guid.NewGuid().ToString("d"),
            Number = number,
            Hostname = string.Format(Defaults.MachineNameFormat, number),
            Mac = $"02:00:5E:00:00:{number:X2}",
            EnrollmentCode = code,
            ConsoleHost = pinHost ? IPAddress.Loopback.ToString() : null,
            ConsolePort = console.Port,
        };

        var store = DirectoryAgentStore.Install(directory, config, console.Authority);
        return Open(store, options);
    }

    /// <summary>
    /// Installs a PC that is already enrolled, with a certificate of the given lifetime
    /// issued straight from the lab key — the state a PC is in years after Setup.
    /// </summary>
    public static TestAgent InstallEnrolled(TestConsole console, int number, TimeSpan lifetime)
    {
        var agent = Install(console, number, code: null);
        var csr = LabCertificates.CreateSigningRequest(agent.Store.Key, "test");
        var lab = console.Session.Vault.Peek() ?? throw new InvalidOperationException("the test lab key must be unlocked to issue");
        using var certificate = LabCertificates.IssueAgentFromCsr(lab.Authority, lab.LabId, agent.AgentId, number, csr, DateTimeOffset.UtcNow, lifetime);
        agent.Store.InstallCertificate(agent.Store.Key, certificate);
        return agent;
    }

    public static TestAgent Open(DirectoryAgentStore store, AgentLinkOptions? options = null)
    {
        var behaviour = new ScriptedBehaviour();
        var link = new AgentLink(store, behaviour, TestLogging.Factory.CreateLogger($"PC-{store.Config.Number:00}"), options);
        var agent = new TestAgent(store, behaviour, link);
        link.Refused += reason => agent.Refusals.Add(reason);
        return agent;
    }

    public TestAgent Start()
    {
        Link.Start();
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        await Link.DisposeAsync();
        Store.Dispose();
    }
}

internal sealed class ScriptedBehaviour : IAgentBehaviour
{
    public string Version { get; set; } = "0.1.0-test";

    public List<Job> JobsRun { get; } = [];

    public Func<Job, Task<JobResult>>? OnJob { get; set; }

    public void Describe(Hello hello)
    {
        hello.AgentVersion = Version;
        hello.BootTimeUnix = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
    }

    public Inventory? DescribeInventory() => new() { Hostname = "test-host", LoggedOnUser = "student" };

    public async Task<JobResult> RunJobAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        lock (JobsRun)
        {
            JobsRun.Add(job);
        }

        if (OnJob is not null)
        {
            return await OnJob(job);
        }

        await report(new JobProgress { JobId = job.Id, Percent = 50, Line = "half way" });
        return new JobResult { JobId = job.Id, Ok = true, ExitCode = 0, Message = $"{job.Kind} done" };
    }
}

internal static class TestLogging
{
    public static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new TestOutputProvider()));

    private sealed class TestOutputProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new TestOutputLogger(categoryName);

        public void Dispose()
        {
        }
    }

    private sealed class TestOutputLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff} {logLevel}] {category}: {formatter(state, exception)}";
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine(line);
            }
            catch (InvalidOperationException)
            {
                // Logged from a thread whose test has already finished (the shared beacon
                // socket, a gRPC worker): xunit refuses the line. The file below keeps it.
            }

            // Background threads (the shared beacon socket, gRPC) have no test context; a
            // file keeps their lines. LABCONTROL_TEST_LOG names it.
            var file = Environment.GetEnvironmentVariable("LABCONTROL_TEST_LOG");
            if (!string.IsNullOrEmpty(file))
            {
                lock (FileGate)
                {
                    File.AppendAllText(file, line + Environment.NewLine);
                }
            }
        }

        private static readonly Lock FileGate = new();
    }
}

internal static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> holds or the timeout passes.</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }
}
