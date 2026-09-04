using Avalonia;
using LabControl.Console.Services;

namespace LabControl.Console;

internal static class Program
{
    /// <summary>Parsed once here; <see cref="App"/> reads it when Avalonia is up (D-27).</summary>
    public static ConsoleOptions Options { get; private set; } = new();

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

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine("LabControl console failed to start: " + ex);
            return 1;
        }
    }

    /// <summary>Also used by the Avalonia XAML previewer and by future UI tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
