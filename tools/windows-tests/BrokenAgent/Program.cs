using System.Runtime.InteropServices;
using LabControl.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

// This executable is a signed-bundle test fixture, never part of production publish.
// The chosen behavior is compiled in; no runtime fault switches exist in the agent.
if (args.Length == 1 && args[0] == Defaults.AgentVersionSwitch)
{
    System.Console.WriteLine(typeof(BrokenService).Assembly.GetName().Version!.ToString(3));
    return 0;
}
if (!OperatingSystem.IsWindows() || args.Length != 0 || !WindowsServiceHelpers.IsWindowsService()) return 2;
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
builder.Services.AddWindowsService(options => options.ServiceName = Defaults.ServiceName);
builder.Logging.ClearProviders();
builder.Services.AddHostedService<BrokenService>();
using var host = builder.Build();
await host.RunAsync();
return 0;

internal sealed class BrokenService(IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
#if BROKEN_CRASH
        // Allow SCM's service start to complete, then terminate without reporting
        // SERVICE_STOPPED. This exercises the actual configured failure actions.
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        if (!TerminateProcess(GetCurrentProcess(), 1067)) Environment.FailFast("Disposable service could not terminate.");
#elif BROKEN_NO_LINK
        // A real running Windows service with no console connection or probation
        // monitor. Only the known-good executable's scheduled deadline can recover it.
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
#else
#error A compile-time FixtureMode is required.
#endif
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
