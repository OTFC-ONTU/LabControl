using LabControl.Shared.Video;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent.Session;

/// <summary>
/// Which capture a PC gets (D-35): DXGI Desktop Duplication when the display driver
/// offers it, the GDI <c>BitBlt</c> fallback when it does not. The choice is made every
/// time the producer opens the screen, so a PC whose duplication comes back after a driver
/// update or a desktop switch gets it back too. The fallback is announced once per reason.
/// </summary>
internal sealed class ScreenSourceFactory
{
    private readonly ILogger _log;
    private readonly ScreenProducer.Reporter _report;
    private string? _announcedFallback;

    public ScreenSourceFactory(ILogger log, ScreenProducer.Reporter report)
    {
        _log = log;
        _report = report;
    }

    public IScreenSource Open()
    {
        string why;
        try
        {
            var dxgi = DxgiScreenSource.Open();
            _announcedFallback = null;
            return dxgi;
        }
        catch (ScreenCaptureException ex)
        {
            why = $"{ex.Reason}: {ex.Message}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SharpGen.Runtime.SharpGenException or BadImageFormatException)
        {
            why = ex.Message;
        }

        if (_announcedFallback != why)
        {
            _announcedFallback = why;
            _log.LogWarning("desktop duplication is not available ({Why}); capturing with GDI instead", why);
            _report(Shared.Protocol.Event.Types.Severity.Info, "capture.fallback", $"Desktop duplication is not available on this PC ({why}); the screen is captured with GDI, which costs more CPU.");
        }

        return GdiScreenSource.Open();
    }
}
