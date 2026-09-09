using Avalonia;
using LabControl.Console.Services;

namespace LabControl.Console;

internal static class Program
{
    /// <summary>How long a second launch waits for the running console to answer before giving up.</summary>
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Parsed once here; <see cref="App"/> reads it when Avalonia is up (D-27).</summary>
    public static ConsoleOptions Options { get; private set; } = new();

    /// <summary>The raw command line, for <see cref="App"/> to recognise the arguments AppKit echoes back as opened files.</summary>
    public static IReadOnlyList<string> Arguments { get; private set; } = [];

    private static ConsoleLock? _lock;

    /// <summary>
    /// The data directory's lock, when <see cref="Main"/> took it before Avalonia started;
    /// <see cref="App"/> collects it exactly once and keeps it for the process lifetime.
    /// </summary>
    public static ConsoleLock? TakeLock()
    {
        var taken = _lock;
        _lock = null;
        return taken;
    }

    // Avalonia needs an STA thread and must be initialised before anything touches UI types.
    [STAThread]
    public static int Main(string[] args)
    {
        if (!ConsoleOptions.TryParse(args, out var options, out var error))
        {
            System.Console.Error.WriteLine(error);
            System.Console.Error.WriteLine(ConsoleOptions.Usage);
            return 2;
        }

        Options = options;
        // The full command line, not `args`: under `dotnet`, the host strips the assembly path
        // that AppKit will nevertheless report back as an opened file.
        Arguments = Environment.GetCommandLineArgs();

        // One console per data directory (D-59 item 5): the lock says whether this launch is
        // the console or a second one. A second launch hands its documents — or nothing, for
        // a bare double-click — to the running console and exits before Avalonia starts.
        _lock = ConsoleLock.TryAcquire(options.DataDirectory, out _);
        if (_lock is null)
        {
            var outcome = SingleInstance.ForwardAsync(options.DataDirectory, options.FilesToOpen, ForwardTimeout).GetAwaiter().GetResult();
            if (outcome.Delivered)
            {
                foreach (var rejected in outcome.Rejected)
                {
                    System.Console.Error.WriteLine("Not opened: " + rejected);
                }

                return 0;
            }

            if (options.ImportOnly)
            {
                System.Console.Error.WriteLine($"No LabControl console is answering for {options.DataDirectory} ({outcome.Error}).");
                return 1;
            }

            // Nothing answered although the directory is held: the app shows why it cannot start.
        }
        else if (options.ImportOnly)
        {
            _lock.Dispose();
            _lock = null;
            System.Console.Error.WriteLine($"No LabControl console is running for {options.DataDirectory}; start the console to add these files.");
            return 1;
        }

        try
        {
            // The lifetime's exit code, not a flat 0: a launch the data directory refuses
            // (App shows why and shuts down with 1) must not look to a script like a console
            // that ran and quit normally.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine("LabControl console failed to start: " + ex);
            return 1;
        }
        finally
        {
            _lock?.Dispose();
        }
    }

    /// <summary>Also used by the Avalonia XAML previewer and by future UI tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
