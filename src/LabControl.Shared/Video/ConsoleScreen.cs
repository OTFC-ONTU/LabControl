namespace LabControl.Shared.Video;

/// <summary>Whether this console can show its screen to the PCs (M6 portion 2, D-72).</summary>
public enum ConsoleScreenAccess
{
    /// <summary>The screen can be captured.</summary>
    Available = 0,

    /// <summary>macOS has not granted the Screen Recording permission yet; the console explains and asks.</summary>
    NeedsPermission = 1,

    /// <summary>This platform has no capture in this build (Linux).</summary>
    Unsupported = 2,
}

/// <summary>
/// The console's own screen for a broadcast, whichever platform it runs on (D-72):
/// CoreGraphics on macOS, GDI on Windows; Linux has none in this build and says so.
/// </summary>
public static class ConsoleScreen
{
    public static ConsoleScreenAccess Access()
    {
        if (OperatingSystem.IsMacOS())
        {
            return CoreGraphicsScreenSource.HasPermission() ? ConsoleScreenAccess.Available : ConsoleScreenAccess.NeedsPermission;
        }

        return OperatingSystem.IsWindows() ? ConsoleScreenAccess.Available : ConsoleScreenAccess.Unsupported;
    }

    /// <summary>Asks for what <see cref="Access"/> lacks (the macOS prompt); true when access is there now.</summary>
    public static bool RequestAccess() => !OperatingSystem.IsMacOS() || CoreGraphicsScreenSource.RequestPermission();

    /// <summary>Opens the primary display at most <paramref name="maxWidth"/> wide; throws <see cref="ScreenCaptureException"/> when it cannot.</summary>
    public static IScreenSource Open(int maxWidth = Defaults.BroadcastWidth)
    {
        if (OperatingSystem.IsMacOS())
        {
            return CoreGraphicsScreenSource.Open(maxWidth);
        }

        if (OperatingSystem.IsWindows())
        {
            return GdiDisplayScreenSource.Open(maxWidth);
        }

        throw new ScreenCaptureException("unsupported", "showing the console's screen is not available on this platform yet");
    }
}
