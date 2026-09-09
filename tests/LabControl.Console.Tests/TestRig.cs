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

    private TestConsole(string dataDirectory, LabSession session, RecoveryCode? recoveryCode)
    {
        DataDirectory = dataDirectory;
        Session = session;
        RecoveryCode = recoveryCode;
    }

    /// <summary>The data root: <c>profiles.json</c> and <c>labs/</c> (M5 layout).</summary>
    public string DataDirectory { get; }

    /// <summary>The lab's own directory, <c>labs/&lt;lab_id&gt;/</c>: what a <see cref="LabStore"/> reads.</summary>
    public string Directory => Session.Store.Directory;

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
            JsonStore.Serialize(existing.Session.Vault!.Document, LabKeyDocument.Migrations), Defaults.LabKeyFileName, LabKeyDocument.Migrations);

        Assert.True(LabKey.TryUnlock(document, Passphrase, out var lab));

        if (labDocument is not null)
        {
            var store = new LabStore(new ProfileStore(directory).Directory(lab.LabId));
            store.EnsureDirectories();
            store.SaveLab(labDocument);
        }

        return new TestConsole(directory, await OpenAsync(directory, lab, instanceName, null, port), null);
    }

    /// <summary>
    /// A teacher device for an existing lab (M5, D-56): the administrator exports a lab file,
    /// this device imports it, writes its request, the administrator approves it on its
    /// running session, the grant is imported here, and the lab opens without a key.
    /// </summary>
    public static async Task<TestConsole> JoinAsTeacherAsync(TestConsole admin, string instanceName, int port = 0)
    {
        var directory = TempDirectory();
        var files = TempDirectory();
        var adminBootstrap = admin.Bootstrap;
        var labFile = Path.Combine(files, "room.lclab");
        Assert.True(adminBootstrap.TryExportLabFile(admin.Session, labFile, out var error), error);

        var bootstrap = new ConsoleBootstrap(new ConsoleOptions
        {
            DataDirectory = directory,
            Port = port,
            BeaconPort = BeaconPort,
            BindAddress = IPAddress.Loopback,
        }, TestLogging.Factory, () => new FileSecretProtector());
        var imports = new LabImports(bootstrap, _ => Task.FromResult<BackupSecret?>(null), instanceName, TestLogging.Factory.CreateLogger("imports"));

        var added = Assert.Single(await imports.ImportAsync([labFile]));
        Assert.True(added.Ok, added.Message);
        var labId = admin.Session.LabId;

        var requestPath = Path.Combine(files, imports.Devices.SuggestRequestFileName(labId));
        imports.Devices.WriteRequest(labId, requestPath);

        var adminDevices = new DeviceAccess(adminBootstrap, id => string.Equals(id, labId, StringComparison.OrdinalIgnoreCase) ? admin.Session : null);
        var lab = admin.Session.Vault!.Peek() ?? throw new InvalidOperationException("the administrator's key must be unlocked to approve");
        var approved = adminDevices.ApproveRequest(requestPath, lab);

        var granted = Assert.Single(await imports.ImportAsync([approved.GrantPath]));
        Assert.True(granted.Ok, granted.Message);

        var opened = bootstrap.OpenExisting(labId);
        Assert.Null(opened.Vault);
        var session = bootstrap.Build(opened, opened.Instance);
        await session.StartAsync();
        return new TestConsole(directory, session, null);
    }

    /// <summary>A bootstrap over this console's data directory, with the file keystore tests use.</summary>
    public ConsoleBootstrap Bootstrap => new(Session.Options, TestLogging.Factory, () => new FileSecretProtector());

    private static async Task<LabSession> OpenAsync(string dataDirectory, LabKey lab, string instanceName, TimeSpan? agentCertificateLifetime, int port)
    {
        // The M5 layout: the lab under labs/<lab_id>/ and an administrator entry in profiles.json,
        // so a ConsoleBootstrap built over the same options finds the lab.
        var profiles = new ProfileStore(dataDirectory);
        var store = new LabStore(profiles.Directory(lab.LabId));
        store.EnsureDirectories();
        store.SaveLabKey(lab.Document);

        var instance = ConsoleInstance.Mint(lab, instanceName, new FileSecretProtector());
        store.SaveInstance(instance.Document);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        profiles.Upsert(new ProfileRecord
        {
            LabId = lab.LabId,
            LabName = lab.LabName,
            AuthorityFingerprint = ProfileRecord.AuthorityFingerprintOf(lab.Document.Authority),
            Access = ProfileAccess.Administrator,
            Authorization = ProfileAuthorization.Authorized,
            InstanceId = instance.InstanceId,
            InstanceName = instanceName,
            AddedAtUnix = now,
            LastUsedUnix = now,
            Source = ProfileSource.Created,
        });
        profiles.LastUsedLabId = lab.LabId;
        profiles.Save();

        var vault = new LabKeyVault(store, lab.Document);
        vault.Adopt(lab);

        var options = new ConsoleOptions
        {
            DataDirectory = dataDirectory,
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
            System.IO.Directory.Delete(DataDirectory, recursive: true);
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

/// <summary>
/// A lab saved on a device and not running (M5 portion 2): the files the chooser lists and
/// <see cref="ActiveLabController"/> opens — <c>labs/&lt;id&gt;/</c> with the key, an instance
/// and <c>lab.json</c>, plus the administrator entry in <c>profiles.json</c>. The key stays
/// in the test so PCs can be issued certificates without a running console.
/// </summary>
internal sealed class SavedLab : IDisposable
{
    private SavedLab(string dataDirectory, LabKey key, string instanceId)
    {
        DataDirectory = dataDirectory;
        Key = key;
        InstanceId = instanceId;
    }

    public string DataDirectory { get; }

    public LabKey Key { get; }

    public string LabId => Key.LabId;

    public string LabName => Key.LabName;

    public string InstanceId { get; }

    public LabStore Store => new(new ProfileStore(DataDirectory).Directory(LabId));

    /// <summary>
    /// Writes a lab into <paramref name="dataDirectory"/> exactly as an import or a first run
    /// would leave it, closed. <paramref name="mintedAt"/> back-dates the console leaf: far
    /// enough back and opening the lab asks to re-mint it (ARCHITECTURE §3.8).
    /// </summary>
    public static SavedLab Save(string dataDirectory, string labName, string instanceName = "Test console", DateTimeOffset? mintedAt = null)
    {
        var lab = LabKey.Create(labName, TestConsole.HolderName, TestConsole.Passphrase, out _, iterations: TestConsole.Iterations);
        var profiles = new ProfileStore(dataDirectory);
        var store = new LabStore(profiles.Directory(lab.LabId));
        store.EnsureDirectories();
        store.SaveLabKey(lab.Document);

        using var instance = ConsoleInstance.Mint(lab, instanceName, new FileSecretProtector(), mintedAt);
        instance.Document.RecoveryCodeAcknowledged = true;
        store.SaveInstance(instance.Document);
        store.SaveLab(new LabDocument
        {
            LabId = lab.LabId,
            LabName = lab.LabName,
            Instances = [new InstanceRecord { InstanceId = instance.InstanceId, Name = instanceName, IsThisMachine = true }],
        });

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        profiles.Upsert(new ProfileRecord
        {
            LabId = lab.LabId,
            LabName = lab.LabName,
            AuthorityFingerprint = ProfileRecord.AuthorityFingerprintOf(lab.Document.Authority),
            Access = ProfileAccess.Administrator,
            Authorization = ProfileAuthorization.Authorized,
            InstanceId = instance.InstanceId,
            InstanceName = instanceName,
            AddedAtUnix = now,
            Source = ProfileSource.Created,
        });
        profiles.Save();

        return new SavedLab(dataDirectory, lab, instance.InstanceId);
    }

    /// <summary>Lists the PCs in <c>lab.json</c>, so the lab "knows" them before it is opened (the cached mosaic).</summary>
    public void RecordMachines(IEnumerable<TestAgent> agents)
    {
        var store = Store;
        var document = store.LoadLab(LabId, LabName);
        document.Machines = agents.Select(a => new MachineRecord { AgentId = a.AgentId, Number = a.Number, Hostname = a.Store.Config.Hostname, Mac = a.Store.Config.Mac }).ToList();
        store.SaveLab(document);
    }

    public void Dispose() => Key.Dispose();
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
        var lab = console.Session.Vault!.Peek() ?? throw new InvalidOperationException("the test lab key must be unlocked to issue");
        using var certificate = LabCertificates.IssueAgentFromCsr(lab.Authority, lab.LabId, agent.AgentId, number, csr, DateTimeOffset.UtcNow, lifetime);
        agent.Store.InstallCertificate(agent.Store.Key, certificate);
        return agent;
    }

    /// <summary>
    /// A PC of a lab that is saved on the device but not running (M5): installed with a
    /// certificate issued straight from the lab key, pointed at the controller's port when
    /// <paramref name="pinHost"/> is set, otherwise waiting for a beacon of its own lab.
    /// </summary>
    public static TestAgent InstallEnrolled(LabKey lab, int number, int port, bool pinHost)
    {
        var directory = Path.Combine(TestConsole.TempDirectory(), $"PC-{number:00}");
        var config = new AgentConfigDocument
        {
            LabId = lab.LabId,
            AgentId = Guid.NewGuid().ToString("d"),
            Number = number,
            Hostname = string.Format(Defaults.MachineNameFormat, number),
            Mac = $"02:00:5E:00:00:{number:X2}",
            ConsoleHost = pinHost ? IPAddress.Loopback.ToString() : null,
            ConsolePort = port,
        };

        using var authority = X509CertificateLoader.LoadCertificate(lab.Document.Authority);
        var store = DirectoryAgentStore.Install(directory, config, authority);
        var csr = LabCertificates.CreateSigningRequest(store.Key, "test");
        using var certificate = LabCertificates.IssueAgentFromCsr(lab.Authority, lab.LabId, config.AgentId, number, csr, DateTimeOffset.UtcNow);
        store.InstallCertificate(store.Key, certificate);
        return Open(store);
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

    /// <summary>Like <see cref="OnJob"/>, with the progress callback, for tests about output lines.</summary>
    public Func<Job, Func<JobProgress, Task>, Task<JobResult>>? OnJobProgress { get; set; }

    /// <summary>
    /// What the PC reports as its boot time (M5, D-57 item 4): a later one than a job's
    /// delivery is how a test says the PC restarted and lost its ledger.
    /// </summary>
    public DateTimeOffset BootTime { get; set; } = DateTimeOffset.UtcNow.AddMinutes(-5);

    public void Describe(Hello hello)
    {
        hello.AgentVersion = Version;
        hello.BootTimeUnix = BootTime.ToUnixTimeSeconds();
    }

    public Inventory? DescribeInventory() => new() { Hostname = "test-host", LoggedOnUser = "student" };

    public async Task<JobResult> RunJobAsync(Job job, Func<JobProgress, Task> report, CancellationToken token)
    {
        lock (JobsRun)
        {
            JobsRun.Add(job);
        }

        if (OnJobProgress is not null)
        {
            return await OnJobProgress(job, report);
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
