using Avalonia;

namespace LabControl.Console;

internal static class Program
{
    // Avalonia needs an STA thread and must be initialised before anything touches UI types.
    [STAThread]
    public static int Main(string[] args)
    {
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
