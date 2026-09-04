using System.Globalization;
using LabControl.Shared;
using LabControl.Shared.Discovery;
using LabControl.Shared.Link;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Log = Serilog.Log;
using LoggerConfiguration = Serilog.LoggerConfiguration;

namespace LabControl.FakeAgent;

/// <summary>
/// A cross-platform stand-in for a room full of Windows PCs, so the console can be
/// developed on the Mac (CLAUDE.md, "Development environment realities"). Every machine
/// runs the real link library over a real store; see <see cref="FakeMachine"/>.
/// </summary>
internal static class Program
{
    private static readonly string Usage =
        "usage: LabControl.FakeAgent [--count N] [--payload <dir>] [--data <dir>] [--console host[:port]]\n" +
        "                            [--fail SPEC]... [--reinstall N]... [--verbose]\n" +
        "  --count N        how many PCs to simulate, 1..30 (default 14)\n" +
        "  --payload <dir>  the USB payload written by the console (setup.json + ca.crt); needed to install new PCs\n" +
        "  --data <dir>     where the PCs keep their state (default ~/.labcontrol-fake)\n" +
        "  --console h[:p]  pin the console address instead of listening for beacons (a Mac with no network)\n" +
        "  --fail SPEC      " + FailureSpec.Help + "\n" +
        "  --reinstall N    wipe PC-N's state first: a new agent id with the same number (D-25)\n" +
        "  --verbose        debug logging";

    private static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out var options, out var error))
        {
            System.Console.Error.WriteLine(error);
            System.Console.Error.WriteLine(Usage);
            return 2;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(options.Verbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        using var loggers = new SerilogLoggerFactory(Log.Logger);
        var log = loggers.CreateLogger("FakeAgent");

        try
        {
            var machines = await InstallOrOpenAsync(options, loggers, log);
            if (machines.Count == 0)
            {
                return 1;
            }

            using var listener = new BeaconListener();
            listener.Received += (datagram, _) =>
            {
                foreach (var machine in machines)
                {
                    machine.OfferBeacon(datagram);
                }
            };
            listener.Failed += message => log.LogWarning("{Message}", message);
            listener.Start();

            foreach (var machine in machines)
            {
                machine.Start();
            }

            log.LogInformation("Simulating {Count} PCs from {Data}; {Mode}. Ctrl+C to stop.",
                machines.Count, options.DataDirectory,
                options.ConsoleHost is null ? "listening for beacons on UDP " + Defaults.BeaconPort : "pinned to " + options.ConsoleHost);

            using var stopping = new CancellationTokenSource();
            System.Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stopping.Cancel();
            };

            await ReportAsync(machines, log, stopping.Token);

            foreach (var machine in machines)
            {
                await machine.DisposeAsync();
            }

            log.LogInformation("Stopped.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
        {
            log.LogError("{Message}", ex.Message);
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static Task<List<FakeMachine>> InstallOrOpenAsync(Options options, ILoggerFactory loggers, ILogger log)
    {
        var machines = new List<FakeMachine>();
        SetupPayload? payload = null;

        foreach (var number in options.Reinstall)
        {
            var directory = MachineDirectory(options, number);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                log.LogInformation("PC-{Number:00}: state wiped — this run is a reinstall with a new agent id", number);
            }
        }

        for (var number = 1; number <= options.Count; number++)
        {
            var directory = MachineDirectory(options, number);
            var failure = options.Failures.GetValueOrDefault(number);
            var machineLog = loggers.CreateLogger(string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, number));

            DirectoryAgentStore store;
            if (DirectoryAgentStore.Exists(directory))
            {
                store = DirectoryAgentStore.Open(directory);
                if (options.ConsoleHost is not null)
                {
                    store.Config.ConsoleHost = options.ConsoleHost;
                    store.Config.ConsolePort = options.ConsolePort;
                    store.SaveConfig();
                }
            }
            else
            {
                if (payload is null)
                {
                    if (options.PayloadDirectory is null)
                    {
                        log.LogError("PC-{Number:00} is not installed yet and no --payload was given. Write a USB payload from the console and pass its directory.", number);
                        return Task.FromResult(new List<FakeMachine>());
                    }

                    payload = SetupPayload.Open(options.PayloadDirectory);
                    log.LogInformation("Installing new PCs from payload for lab '{Lab}' ({Codes} codes available)",
                        payload.Document.LabName, payload.Document.EnrollmentCodes.Count);
                }

                store = FakeMachine.Install(directory, number, payload, options.ConsoleHost,
                    options.ConsolePort, burnedCode: failure?.Kind == FailureKind.BurnedCode);
            }

            var machine = new FakeMachine(store, failure, machineLog);
            machines.Add(machine);
            log.LogInformation("  {Name}  agent {AgentId}  {Enrolled}{Failure}",
                machine.Name, machine.AgentId, machine.Link.IsEnrolled ? "enrolled" : "not enrolled yet",
                failure is null ? string.Empty : $"  [{failure.Kind}]");
        }

        return Task.FromResult(machines);
    }

    private static string MachineDirectory(Options options, int number) =>
        Path.Combine(options.DataDirectory, string.Format(CultureInfo.InvariantCulture, Defaults.MachineNameFormat, number));

    private static async Task ReportAsync(List<FakeMachine> machines, ILogger log, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var groups = machines.GroupBy(m => m.Status).OrderByDescending(g => g.Count());
                log.LogInformation("status: {Summary}", string.Join(", ", groups.Select(g => $"{g.Count()} {g.Key}")));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ------------------------------------------------------------------ command line

    private sealed class Options
    {
        public int Count { get; set; } = 14;

        public string? PayloadDirectory { get; set; }

        public string DataDirectory { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".labcontrol-fake");

        public string? ConsoleHost { get; set; }

        public int ConsolePort { get; set; } = Defaults.ConsolePort;

        public Dictionary<int, FailureSpec> Failures { get; } = [];

        public List<int> Reinstall { get; } = [];

        public bool Verbose { get; set; }
    }

    private static bool TryParse(string[] args, out Options options, out string error)
    {
        options = new Options();
        error = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name == "--verbose")
            {
                options.Verbose = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                error = $"'{name}' needs a value";
                return false;
            }

            var value = args[++i];
            switch (name)
            {
                case "--count":
                case "-c":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                    {
                        error = $"'{value}' is not a number";
                        return false;
                    }

                    if (count < 1 || count > Defaults.MaxStudentPcs)
                    {
                        error = $"--count must be between 1 and {Defaults.MaxStudentPcs} (D-17)";
                        return false;
                    }

                    options.Count = count;
                    break;

                case "--payload":
                    options.PayloadDirectory = Path.GetFullPath(value);
                    break;

                case "--data":
                    options.DataDirectory = Path.GetFullPath(value);
                    break;

                case "--console":
                    var colon = value.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                    {
                        options.ConsoleHost = value[..colon];
                        options.ConsolePort = port;
                    }
                    else
                    {
                        options.ConsoleHost = value;
                    }

                    break;

                case "--fail":
                    if (!FailureSpec.TryParse(value, out var spec, out error))
                    {
                        return false;
                    }

                    options.Failures[spec.Number] = spec;
                    break;

                case "--reinstall":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var reinstall))
                    {
                        error = $"'{value}' is not a PC number";
                        return false;
                    }

                    options.Reinstall.Add(reinstall);
                    break;

                default:
                    error = $"unknown argument '{name}'";
                    return false;
            }
        }

        return true;
    }
}
