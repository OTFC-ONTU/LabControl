using System.Globalization;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Policy;
using LabControl.Shared.Protection;
using LabControl.Shared.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace LabControl.Agent;

/// <summary>
/// The Windows service that runs on every student PC as LocalSystem (ARCHITECTURE §2).
/// Started by the service manager it is the agent; started by hand it is either the
/// provisioning half of the installer (<c>--install</c>, INSTALLER.md step 4) or the
/// same agent in the foreground with console logging (<c>--run</c>) for development.
/// </summary>
internal static class Program
{
    private static readonly string Usage =
        $"usage: agent.exe                              run as the '{Defaults.ServiceName}' service (started by Windows)\n" +
        $"       agent.exe {Defaults.AgentForegroundSwitch}                        run in the foreground with console logging\n" +
        $"       agent.exe {Defaults.AgentInstallSwitch} --payload <dir> --number N [--console host[:port]] [--force]\n" +
        "                                             provision this PC from a USB payload (setup.json + ca.crt)\n" +
        $"       agent.exe {Defaults.AgentVersionSwitch}                    print the version\n" +
        $"       agent.exe {Defaults.RestartServiceSwitch}            stop the '{Defaults.ServiceName}' service and start it again (used by a push, D-33)\n" +
        $"       agent.exe {Defaults.PolicyRestoreSwitch}             restore the PC's normal settings and re-apply what policy.json still holds (the watchdog task, D-69)\n" +
        "  --verbose                                  debug logging";

    public static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// What this agent reports in <c>Hello</c>: the name of the version directory it runs
    /// from when installed (<c>0.1.0+1a2b3c4d</c> for a pushed build, D-33), otherwise the
    /// assembly version. A pushed <c>self_update</c> names its version the same way, so the
    /// new version recognises the job that installed it by this string alone.
    /// </summary>
    public static string InstalledVersion =>
        Environment.ProcessPath is { Length: > 0 } path ? InstallLayout.Default.VersionOf(path) ?? Version : Version;

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains(Defaults.AgentVersionSwitch, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(Version);
            return 0;
        }

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);
        var asService = WindowsServiceHelpers.IsWindowsService();

        if (args.Contains(Defaults.AgentInstallSwitch, StringComparer.OrdinalIgnoreCase))
        {
            using var installLog = ConfigureLogging(interactive: true, verbose);
            return Provisioner.Run(args, installLog.CreateLogger("install"), Usage);
        }

        if (args.Contains(Defaults.RollbackSwitch, StringComparer.OrdinalIgnoreCase)
            || args.Contains(Defaults.RollbackDeadlineSwitch, StringComparer.OrdinalIgnoreCase))
        {
            using var recoveryLog = ConfigureLogging(interactive: false, verbose);
            var logger = recoveryLog.CreateLogger("rollback");
            try
            {
                var switchIndex = Array.FindIndex(args, argument =>
                    argument.Equals(Defaults.RollbackSwitch, StringComparison.OrdinalIgnoreCase)
                    || argument.Equals(Defaults.RollbackDeadlineSwitch, StringComparison.OrdinalIgnoreCase));
                if (switchIndex + 1 >= args.Length || args[switchIndex + 1].Length is 0 or > 4096)
                    throw new InvalidOperationException("Recovery requires its scheduled update job identity.");
                var expectedJobId = Uri.UnescapeDataString(args[switchIndex + 1]);
                UpdateRecovery.RollBack(args.Contains(Defaults.RollbackDeadlineSwitch, StringComparer.OrdinalIgnoreCase),
                    line => logger.LogInformation("{Line}", line), expectedJobId);
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError("Update recovery failed: {Message}", ex.Message);
                return 1;
            }
            finally { await Log.CloseAndFlushAsync(); }
        }

        if (args.Contains(Defaults.PolicyRestoreSwitch, StringComparer.OrdinalIgnoreCase))
        {
            // The watchdog task's entry point (D-69 item 6) and a hand tool: the same pass the
            // service runs at start, under the same mutex, with no service and no link. Exit 0
            // only when the machine was proven open; the task retries a nonzero exit.
            using var restoreLog = ConfigureLogging(interactive: !asService, verbose);
            var restoreLogger = restoreLog.CreateLogger("restore-policy");
            try
            {
                return RestorePolicy(restoreLogger);
            }
            finally
            {
                await Log.CloseAndFlushAsync();
            }
        }

        if (args.Contains(Defaults.RestartServiceSwitch, StringComparer.OrdinalIgnoreCase))
        {
            // Spawned by the outgoing agent after a push (AgentUpdater, D-33); it has no console,
            // so everything goes to the rolling file log next to the agent's own lines.
            using var restartLog = ConfigureLogging(interactive: false, verbose);
            var restartLogger = restartLog.CreateLogger("restart");
            var failure = ServiceControl.Restart(line => restartLogger.LogInformation("{Line}", line));
            if (failure is not null)
            {
                restartLogger.LogError("The service restart failed: {Failure}", failure);
                await Log.CloseAndFlushAsync();
                return 1;
            }

            restartLogger.LogInformation("The service was restarted.");
            await Log.CloseAndFlushAsync();
            return 0;
        }

        if (!asService && !args.Contains(Defaults.AgentForegroundSwitch, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(Usage);
            return 2;
        }

        using var loggers = ConfigureLogging(interactive: !asService, verbose);
        var log = loggers.CreateLogger("agent");

        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                DisableDefaults = true,
            });

            builder.Services.AddWindowsService(options => options.ServiceName = Defaults.ServiceName);

            // After AddWindowsService, which registers the Windows Event Log provider: at OS
            // shutdown that provider throws once the Event Log service is gone, and the throw
            // would escape the agent loop (seen on the VM, ROADMAP M2 portion 2). Serilog only.
            builder.Logging.ClearProviders();
            builder.Logging.AddSerilog(Log.Logger, dispose: false);
            builder.Services.AddSingleton<SessionChangeSource>();
            if (asService)
            {
                // Replaces the stock lifetime with one that also receives logon/logoff/lock/unlock.
                builder.Services.AddSingleton<IHostLifetime, SessionChangeLifetime>();
            }

            builder.Services.AddHostedService<AgentService>();

            using var host = builder.Build();
            log.LogInformation("LabControl agent {Version} starting as {Mode} (protocol {Protocol})",
                Version, asService ? "a service" : "a foreground process", Defaults.ProtocolVersion);
            await host.RunAsync();
            log.LogInformation("LabControl agent stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            // Nothing escapes Main unlogged: a service that dies silently is a walk to the PC.
            log.LogCritical(ex, "The agent host failed: {Message}", ex.Message);
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// <c>agent.exe --restore-policy</c>: D-69 steps 0–6 against a <see cref="WindowsMachineState"/>
    /// with no helper (a lock that is still valid is recorded and shown by the service's next
    /// helper), under <see cref="PolicyMutex"/>. Returns 0 when the machine is proven open,
    /// 1 when a restore step failed, 2 when the PC is not provisioned, 3 when the mutex was
    /// held elsewhere for the whole wait.
    /// </summary>
    private static int RestorePolicy(Microsoft.Extensions.Logging.ILogger log)
    {
        string labId;
        try
        {
            using var store = DirectoryAgentStore.Open(Defaults.AgentDataDirectory, MachineKeyProtection.Dpapi);
            labId = store.Config.LabId;
        }
        catch (Exception ex)
        {
            log.LogError("Cannot open {Directory}: {Message}. Nothing to restore for.", Defaults.AgentDataDirectory, ex.Message);
            return 2;
        }

        using var lease = PolicyMutex.TryAcquire();
        if (lease is null)
        {
            log.LogWarning("{Message}", PolicyEvents.Text("Busy", (long)Defaults.PolicyMutexWait.TotalSeconds));
            return 3;
        }

        var path = Path.Combine(Defaults.AgentDataDirectory, Defaults.PolicyFileName);
        var machine = new WindowsMachineState(_ => false, log);
        var journal = new PolicyJournal(machine, labId, PolicyJournal.FileReader(path), PolicyJournal.FileWriter(path));
        journal.Reported += reported => log.Log(reported.Severity switch
        {
            Shared.Protocol.Event.Types.Severity.Error => LogLevel.Error,
            Shared.Protocol.Event.Types.Severity.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        }, "{Code}: {Message}", reported.Code, reported.Message);

        var outcome = journal.Start(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        log.LogInformation("policy restore: machine {Open}, {Reapplied} session(s) re-applied, {Dropped} dropped",
            outcome.MachineOpen ? "open" : "NOT proven open", outcome.Reapplied.Count, outcome.Dropped.Count);
        return outcome.MachineOpen ? 0 : 1;
    }

    /// <summary>
    /// A rolling file under <c>ProgramData\LabControl\logs\</c>, kept for
    /// <see cref="Defaults.AgentLogRetentionDays"/> days, plus the console when a person
    /// is watching. Never the student password, never key material (CLAUDE.md).
    /// </summary>
    private static Serilog.Extensions.Logging.SerilogLoggerFactory ConfigureLogging(bool interactive, bool verbose)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(Defaults.AgentDataDirectory, Defaults.LogsDirectoryName, Defaults.AgentLogFilePattern),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Defaults.AgentLogRetentionDays,
                // `--restart-service` (D-33) writes to the same file while the service still
                // holds it; without sharing its lines would be dropped silently.
                shared: true,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        if (interactive)
        {
            configuration = configuration.WriteTo.Console(
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = configuration.CreateLogger();
        return new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger, dispose: false);
    }
}
