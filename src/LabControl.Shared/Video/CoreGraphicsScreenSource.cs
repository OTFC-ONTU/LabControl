using System.Runtime.InteropServices;
using SkiaSharp;

namespace LabControl.Shared.Video;

/// <summary>
/// The Mac console's main display for a broadcast (M6 portion 2, D-72):
/// <c>CGDisplayCreateImage</c> through <c>DllImport</c> — no NuGet package — drawn by
/// <c>CGContextDrawImage</c> into a bitmap context over the source's own BGRA picture at the
/// broadcast width, so the scaling is CoreGraphics' high-quality interpolation and the
/// pixels arrive in the order Skia and the JPEG encoder use. macOS gives a process without
/// the <i>Screen Recording</i> permission only the wallpaper, so <see cref="Open"/> refuses
/// with <c>permission</c> until <see cref="HasPermission"/> is true; the console explains
/// and asks once (<see cref="RequestPermission"/>), and the grant takes effect after a
/// restart of the console.
/// </summary>
public sealed class CoreGraphicsScreenSource : ScaledScreenSource
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little: B, G, R, A in memory.
    private const uint BitmapInfoBgraPremultiplied = 2 | (2 << 12);
    private const int InterpolationHigh = 3;

    private readonly uint _display;
    private nint _colorSpace;

    // The picture NativeSize measured, drawn by the CaptureInto that follows: one
    // CGDisplayCreateImage per frame, not two.
    private nint _pending;

    private CoreGraphicsScreenSource(int maxWidth, uint display)
        : base(maxWidth)
    {
        _display = display;
        _colorSpace = CGColorSpaceCreateDeviceRGB();
    }

    public override string Kind => "coregraphics";

    /// <summary>The Screen Recording permission is granted to this process (macOS 10.15+); asking does not prompt.</summary>
    public static bool HasPermission()
    {
        try
        {
            return CGPreflightScreenCaptureAccess();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Older than 10.15: there is no such permission.
            return true;
        }
    }

    /// <summary>
    /// Asks macOS for the Screen Recording permission: the first time, the system shows its
    /// prompt and adds the console to the list in System Settings; afterwards it only answers.
    /// The grant reaches a running process only after it restarts.
    /// </summary>
    public static bool RequestPermission()
    {
        try
        {
            return CGRequestScreenCaptureAccess();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return true;
        }
    }

    public static CoreGraphicsScreenSource Open(int maxWidth)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new ScreenCaptureException("unsupported", "CoreGraphics capture exists only on macOS");
        }

        if (!HasPermission())
        {
            throw new ScreenCaptureException("permission", "macOS has not given this console the Screen Recording permission");
        }

        try
        {
            return new CoreGraphicsScreenSource(maxWidth, CGMainDisplayID());
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            throw new ScreenCaptureException("unsupported", $"CoreGraphics is not available: {ex.Message}", ex);
        }
    }

    protected override (int Width, int Height) NativeSize()
    {
        ReleasePending();
        try
        {
            _pending = CGDisplayCreateImage(_display);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new ScreenCaptureException("unsupported", "this macOS no longer offers CGDisplayCreateImage", ex);
        }

        return _pending == 0 ? (0, 0) : ((int)CGImageGetWidth(_pending), (int)CGImageGetHeight(_pending));
    }

    protected override void CaptureInto(SKBitmap target, int nativeWidth, int nativeHeight)
    {
        var image = _pending;
        _pending = 0;
        if (image == 0)
        {
            throw new ScreenCaptureException("capture_failed", "CGDisplayCreateImage returned no picture");
        }

        try
        {
            var context = CGBitmapContextCreate(target.GetPixels(), (nuint)target.Width, (nuint)target.Height, 8, (nuint)target.RowBytes, _colorSpace, BitmapInfoBgraPremultiplied);
            if (context == 0)
            {
                throw new ScreenCaptureException("capture_failed", "CGBitmapContextCreate failed");
            }

            try
            {
                CGContextSetInterpolationQuality(context, InterpolationHigh);
                CGContextDrawImage(context, new CGRect(0, 0, target.Width, target.Height), image);
            }
            finally
            {
                CGContextRelease(context);
            }
        }
        finally
        {
            CGImageRelease(image);
        }

        target.NotifyPixelsChanged();
    }

    protected override (double X, double Y)? PointerPosition(int nativeWidth, int nativeHeight)
    {
        var bounds = CGDisplayBounds(_display);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        var cgEvent = CGEventCreate(0);
        if (cgEvent == 0)
        {
            return null;
        }

        try
        {
            // Global coordinates in points, top-left origin; the picture is in pixels.
            var location = CGEventGetLocation(cgEvent);
            var x = (location.X - bounds.X) * nativeWidth / bounds.Width;
            var y = (location.Y - bounds.Y) * nativeHeight / bounds.Height;
            return x < 0 || y < 0 || x >= nativeWidth || y >= nativeHeight ? null : (x, y);
        }
        finally
        {
            CFRelease(cgEvent);
        }
    }

    private void ReleasePending()
    {
        if (_pending != 0)
        {
            CGImageRelease(_pending);
            _pending = 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        ReleasePending();
        if (_colorSpace != 0)
        {
            CGColorSpaceRelease(_colorSpace);
            _colorSpace = 0;
        }

        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGPoint(double X, double Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    [DllImport(CoreGraphics)]
    private static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    private static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreGraphics)]
    private static extern nint CGDisplayCreateImage(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGImageGetWidth(nint image);

    [DllImport(CoreGraphics)]
    private static extern nuint CGImageGetHeight(nint image);

    [DllImport(CoreGraphics)]
    private static extern void CGImageRelease(nint image);

    [DllImport(CoreGraphics)]
    private static extern nint CGColorSpaceCreateDeviceRGB();

    [DllImport(CoreGraphics)]
    private static extern void CGColorSpaceRelease(nint space);

    [DllImport(CoreGraphics)]
    private static extern nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);

    [DllImport(CoreGraphics)]
    private static extern void CGContextSetInterpolationQuality(nint context, int quality);

    [DllImport(CoreGraphics)]
    private static extern void CGContextDrawImage(nint context, CGRect rect, nint image);

    [DllImport(CoreGraphics)]
    private static extern void CGContextRelease(nint context);

    [DllImport(CoreGraphics)]
    private static extern nint CGEventCreate(nint source);

    [DllImport(CoreGraphics)]
    private static extern CGPoint CGEventGetLocation(nint cgEvent);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGPreflightScreenCaptureAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRequestScreenCaptureAccess();

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint reference);
}
