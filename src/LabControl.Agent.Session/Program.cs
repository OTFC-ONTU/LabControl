using System.Globalization;
using System.IO.Pipes;
using LabControl.Shared;
using LabControl.Shared.Protocol;
using LabControl.Shared.Session;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace LabControl.Agent.Session;

/// <summary>
/// The session helper (ARCHITECTURE §2, D-06): spawned by the agent service into the
/// interactive session with a SYSTEM token, because a session-0 service can neither see the
/// desktop nor inject input. It connects to the service's named pipe, says hello, and
/// reports what it sees every <see cref="Defaults.HelperStatusInterval"/>. It never talks to
/// the network, and it exits the moment the pipe closes — the service restarts it (M2). In
/// M2 that is all it does; capture (M3), input (M3) and the overlay (M5) build on it.
/// </summary>
internal static class Program
{
    private static readonly string Usage =
        $"usage: session.exe                started by the LabControl service; connects to \\\\.\\pipe\\{Defaults.SessionPipeName}\n" +
        $"       session.exe {Defaults.SessionProbeSwitch}        print what this process sees of its session and exit\n" +
        "  --verbose                        debug logging";

    public static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // Exit codes the service sees in its log.
    private const int ExitPipeClosed = 0;
    private const int ExitUsage = 2;
    private const int ExitCouldNotConnect = 3;
    private const int ExitProtocolError = 4;
    private const int ExitUnexpected = 5;

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(Usage);
            return ExitUsage;
        }

        if (args.Contains(Defaults.SessionProbeSwitch, StringComparer.OrdinalIgnoreCase))
        {
            return Probe();
        }

        var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        using var loggers = ConfigureLogging(verbose);
        var log = loggers.CreateLogger("session");

        try
        {
            return await RunAsync(log);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "The session helper failed: {Message}", ex.Message);
            return ExitUnexpected;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>A hand check on a PC: what session, which desktop, which screen.</summary>
    private static int Probe()
    {
        var status = DesktopProbe.Status();
        Console.WriteLine($"session helper {Version}");
        Console.WriteLine($"session id    : {DesktopProbe.OwnSessionId()?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
        Console.WriteLine($"input desktop : {status.InputDesktop}");
        Console.WriteLine($"screen        : {status.ScreenWidth}x{status.ScreenHeight}");
        Console.WriteLine($"user          : {Environment.UserDomainName}\\{Environment.UserName}");
        Console.WriteLine($"pipe          : \\\\.\\pipe\\{Defaults.SessionPipeName}");
        return 0;
    }

    private static async Task<int> RunAsync(Microsoft.Extensions.Logging.ILogger log)
    {
        var session = DesktopProbe.OwnSessionId();
        var pid = Environment.ProcessId;
        log.LogInformation("session helper {Version} starting: pid {Pid}, session {Session}, desktop {Desktop}",
            Version, pid, session?.ToString(CultureInfo.InvariantCulture) ?? "?", DesktopProbe.InputDesktopName());

        // CurrentUserOnly makes the connect fail unless the server runs as the same account
        // (SYSTEM), so a pipe squatted by another user is refused rather than obeyed.
        await using var pipe = new NamedPipeClientStream(".", Defaults.SessionPipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)Defaults.HelperConnectTimeout.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            log.LogError("could not connect to the service's pipe within {Seconds:0} s: {Message}", Defaults.HelperConnectTimeout.TotalSeconds, ex.Message);
            return ExitCouldNotConnect;
        }

        var writeLock = new SemaphoreSlim(1, 1);
        using var stopping = new CancellationTokenSource();
        var token = stopping.Token;

        async Task SendAsync(HelperMessage message)
        {
            await writeLock.WaitAsync(token);
            try
            {
                await PipeFraming.WriteAsync(pipe, message, token);
            }
            finally
            {
                writeLock.Release();
            }
        }

        await SendAsync(new HelperMessage { Hello = new HelperHello { SessionId = session ?? 0, ProcessId = (uint)pid, Version = Version } });
        log.LogInformation("connected to the service");

        var statusLoop = Task.Run(async () =>
        {
            var lastDesktop = string.Empty;
            while (!token.IsCancellationRequested)
            {
                var status = DesktopProbe.Status();
                if (status.InputDesktop != lastDesktop)
                {
                    log.LogInformation("input desktop: {Desktop} ({Width}x{Height})", status.InputDesktop, status.ScreenWidth, status.ScreenHeight);
                    lastDesktop = status.InputDesktop;
                }

                await SendAsync(new HelperMessage { Status = status });
                await Task.Delay(Defaults.HelperStatusInterval, token);
            }
        }, token);

        var exit = ExitPipeClosed;
        try
        {
            while (await PipeFraming.ReadAsync(pipe, ServiceMessage.Parser, token) is { } message)
            {
                switch (message.PayloadCase)
                {
                    case ServiceMessage.PayloadOneofCase.Ping:
                        await SendAsync(new HelperMessage { Status = DesktopProbe.Status() });
                        break;

                    default:
                        // VideoControl, Input and Overlay arrive in M3/M5; until then say so once per message.
                        log.LogWarning("the service sent {What}, which this build does not do yet", message.PayloadCase);
                        await SendAsync(new HelperMessage
                        {
                            Event = new Event
                            {
                                Severity = Event.Types.Severity.Warning,
                                Code = "session.not_in_this_build",
                                Message = $"The session helper {Version} does not handle {message.PayloadCase} yet.",
                                AtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            },
                        });
                        break;
                }
            }

            log.LogInformation("the service closed the pipe; exiting");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            log.LogInformation("the pipe ended ({Message}); exiting", ex.Message);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            log.LogError("protocol error on the pipe: {Message}", ex.Message);
            exit = ExitProtocolError;
        }
        finally
        {
            stopping.Cancel();
            try
            {
                await statusLoop;
            }
            catch (Exception)
            {
                // Cancelled or the pipe went away underneath it; either way we are leaving.
            }
        }

        return exit;
    }

    /// <summary>
    /// The helper's own rolling file next to the agent's, under
    /// <c>ProgramData\LabControl\logs\</c> (SYSTEM can write there; the student cannot read
    /// it), plus the console when a person started it by hand.
    /// </summary>
    private static Serilog.Extensions.Logging.SerilogLoggerFactory ConfigureLogging(bool verbose)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(Defaults.AgentDataDirectory, Defaults.LogsDirectoryName, Defaults.SessionLogFilePattern),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Defaults.AgentLogRetentionDays,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        if (!Console.IsOutputRedirected && Environment.UserInteractive)
        {
            configuration = configuration.WriteTo.Console(
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = configuration.CreateLogger();
        return new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger, dispose: false);
    }
}
