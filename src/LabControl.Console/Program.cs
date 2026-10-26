using System.Diagnostics;
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

    /// <summary>Why <c>preferences.json</c> could not be read, for <see cref="App"/> to log; <c>null</c> when it was fine or absent.</summary>
    public static string? LanguageProblem { get; private set; }

    /// <summary>
    /// Set by <i>Restart now</i> in the language selector (M7, D-86) and cleared again when the
    /// teacher stays at the departure question: once the console has quit — the lab released,
    /// the data directory's lock let go — <see cref="Main"/> starts it once more.
    /// </summary>
    public static bool RestartAfterExit { get; set; }

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

        // The interface language is fixed for the process (M7, D-86): chosen before the first
        // string is read, including the refusal a second launch may show.
        ConsoleLanguage.Apply(options.DataDirectory, out var languageProblem);
        LanguageProblem = languageProblem;
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
            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            if (exitCode == 0 && RestartAfterExit)
            {
                _lock?.Dispose();
                _lock = null;
                Relaunch(args);
            }

            return exitCode;
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

    /// <summary>
    /// Starts this console again with the same options and none of the documents it was opened
    /// with — they were imported already. Under <c>dotnet LabControl.Console.dll</c> the host is
    /// the process, so the assembly goes first. A start that fails leaves the teacher to open
    /// the console by hand, as after any quit.
    /// </summary>
    private static void Relaunch(string[] args)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                return;
            }

            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                // Assembly.Location is empty in a single-file publish (IL3000); under the dotnet
                // host the assembly sits beside the app's base directory under its own name.
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll"));
            }

            // The options and their values only: a bare argument is a document (or macOS's
            // -psn_ serial), and every "--name" but --import-only takes one value.
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                start.ArgumentList.Add(args[i]);
                if (args[i] != "--import-only" && i + 1 < args.Length)
                {
                    start.ArgumentList.Add(args[++i]);
                }
            }

            using var _ = Process.Start(start);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine("LabControl console could not start again: " + ex.Message);
        }
    }

    /// <summary>Also used by the Avalonia XAML previewer and by future UI tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
