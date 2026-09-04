using LabControl.Shared;
using Serilog;

namespace LabControl.FakeAgent;

/// <summary>
/// A cross-platform stand-in for a room full of Windows PCs, so the console can be
/// developed on the Mac (CLAUDE.md, "Development environment realities").
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            if (!TryParseCount(args, out var count, out var error))
            {
                Log.Error("{Error}", error);
                Log.Information("usage: LabControl.FakeAgent [--count N]   (1..{Max}, default 14)", Defaults.MaxStudentPcs);
                return 2;
            }

            var machines = Enumerable.Range(1, count).Select(n => new FakeMachine(n)).ToArray();
            Log.Information("Simulating {Count} student PCs ({First} … {Last})",
                machines.Length, machines[0].Name, machines[^1].Name);

            foreach (var machine in machines)
            {
                Log.Information("  {Name}  agent {AgentId}  mac {Mac}", machine.Name, machine.AgentId, machine.Mac);
            }

            Log.Information("M0: no networking yet — the link to the console arrives in M1. Ctrl+C to stop.");

            using var stopping = new CancellationTokenSource();
            System.Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stopping.Cancel();
            };

            await RunHeartbeatAsync(machines, stopping.Token);
            Log.Information("Stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "FakeAgent crashed");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static async Task RunHeartbeatAsync(FakeMachine[] machines, CancellationToken token)
    {
        using var timer = new PeriodicTimer(Defaults.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var online = machines.Count(m => m.PoweredOn);
                Log.Debug("heartbeat: {Online}/{Total} powered on, uptime {Uptime:hh\\:mm\\:ss}",
                    online, machines.Length, machines[0].Uptime);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C: the expected way to stop.
        }
    }

    private static bool TryParseCount(string[] args, out int count, out string error)
    {
        count = 14;
        error = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not ("--count" or "-c"))
            {
                error = $"unknown argument '{args[i]}'";
                return false;
            }

            if (i + 1 >= args.Length)
            {
                error = "--count needs a value";
                return false;
            }

            if (!int.TryParse(args[++i], out count))
            {
                error = $"'{args[i]}' is not a number";
                return false;
            }

            if (count < 1 || count > Defaults.MaxStudentPcs)
            {
                error = $"--count must be between 1 and {Defaults.MaxStudentPcs} (D-17)";
                return false;
            }
        }

        return true;
    }
}
