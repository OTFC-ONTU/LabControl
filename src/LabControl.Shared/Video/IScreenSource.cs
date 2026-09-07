using LabControl.Shared.Protocol;

namespace LabControl.Shared.Video;

/// <summary>
/// Where a <see cref="ScreenProducer"/> gets its pixels (M3 portion 2, D-35): DXGI Desktop
/// Duplication or a GDI <c>BitBlt</c> on a real PC, a drawn desktop in the simulator. A
/// source is opened when the console wants video and disposed when it stops, so an idle PC
/// holds no duplication and does no work. Everything Windows-specific stays behind this
/// interface; the producer on top of it is pure code and is tested on the Mac.
/// </summary>
public interface IScreenSource : IDisposable
{
    /// <summary>"dxgi", "gdi", "simulated" — for the event that says how a PC is being captured.</summary>
    string Kind { get; }

    /// <summary>
    /// The current picture of the screen together with what changed on it since the previous
    /// call, waiting at most <paramref name="timeout"/> for a change. Returns <c>null</c> while
    /// there is no picture at all yet (nothing has been delivered since the source opened);
    /// afterwards it always returns a frame, whose <see cref="IScreenFrame.Dirty"/> is empty
    /// when nothing changed. Throws <see cref="ScreenCaptureException"/> when the screen can no
    /// longer be captured; the producer then disposes the source and opens a new one later.
    /// </summary>
    IScreenFrame? Acquire(TimeSpan timeout);
}

/// <summary>
/// One look at the screen. The pixels are BGRA, top-down, <see cref="RowBytes"/> apart, and
/// valid only until the frame is disposed — a mapped GPU surface, a DIB section, or the
/// simulator's bitmap.
/// </summary>
public interface IScreenFrame : IDisposable
{
    int Width { get; }

    int Height { get; }

    int RowBytes { get; }

    ReadOnlySpan<byte> Pixels { get; }

    /// <summary>
    /// What changed since the previous frame, in screen pixels, not necessarily tile-aligned
    /// or disjoint; empty when nothing did. The first frame after opening reports the whole
    /// screen.
    /// </summary>
    IReadOnlyList<Rect> Dirty { get; }
}

/// <summary>The screen cannot be captured (any more); <see cref="Reason"/> is a short code for the console.</summary>
public sealed class ScreenCaptureException : Exception
{
    public ScreenCaptureException(string reason, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>A stable code: <c>no_duplication</c>, <c>access_lost</c>, <c>no_desktop</c>, <c>gdi_failed</c>…</summary>
    public string Reason { get; }
}
