using System.ComponentModel;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace LabControl.Shared.Video;

/// <summary>
/// A Windows console's primary display for a broadcast (M6 portion 2, D-72): one
/// <c>StretchBlt</c> with <c>HALFTONE</c> from the screen DC into a top-down 32-bit DIB
/// section at the broadcast width — the capture and the downscale in a single GDI call —
/// then copied into the source's picture. Plain <c>DllImport</c>, so the console stays a
/// single <c>net10.0</c> project; DXGI waits until a measurement says GDI is too slow.
/// </summary>
public sealed class GdiDisplayScreenSource : ScaledScreenSource
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int Halftone = 4;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CursorShowing = 0x00000001;

    private nint _dib;
    private nint _bits;
    private int _dibWidth;
    private int _dibHeight;

    private GdiDisplayScreenSource(int maxWidth)
        : base(maxWidth)
    {
    }

    public override string Kind => "gdi";

    public static GdiDisplayScreenSource Open(int maxWidth)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ScreenCaptureException("unsupported", "GDI capture exists only on Windows");
        }

        return new GdiDisplayScreenSource(maxWidth);
    }

    protected override (int Width, int Height) NativeSize() => (GetSystemMetrics(SmCxScreen), GetSystemMetrics(SmCyScreen));

    protected override unsafe void CaptureInto(SKBitmap target, int nativeWidth, int nativeHeight)
    {
        var screen = GetDC(0);
        if (screen == 0)
        {
            throw new ScreenCaptureException("gdi_failed", "GetDC of the screen failed");
        }

        try
        {
            EnsureDib(screen, target.Width, target.Height);
            var memory = CreateCompatibleDC(screen);
            if (memory == 0)
            {
                throw new ScreenCaptureException("gdi_failed", "CreateCompatibleDC failed");
            }

            try
            {
                var previous = SelectObject(memory, _dib);
                try
                {
                    SetStretchBltMode(memory, Halftone);
                    SetBrushOrgEx(memory, 0, 0, 0);
                    // No CAPTUREBLT (D-72 addendum): with it Windows hides the teacher's cursor for every
                    // frame, so it flickers on the teacher's own screen. Since Windows 8 the desktop is
                    // always composed by DWM and a plain copy already includes layered windows; the
                    // pointer the students see is drawn into the picture by ScaledScreenSource.
                    if (!StretchBlt(memory, 0, 0, target.Width, target.Height, screen, 0, 0, nativeWidth, nativeHeight, SrcCopy))
                    {
                        throw new ScreenCaptureException("gdi_failed", $"StretchBlt failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                    }

                    GdiFlush();
                }
                finally
                {
                    SelectObject(memory, previous);
                }
            }
            finally
            {
                DeleteDC(memory);
            }
        }
        finally
        {
            ReleaseDC(0, screen);
        }

        // The DIB is top-down BGRX; the alpha GDI leaves is meaningless, so it is set opaque.
        var rowLength = target.Width * JpegCodec.BytesPerPixel;
        var source = new ReadOnlySpan<byte>((void*)_bits, rowLength * target.Height);
        var pixels = target.GetPixelSpan();
        for (var y = 0; y < target.Height; y++)
        {
            var from = source.Slice(y * rowLength, rowLength);
            var to = pixels.Slice(y * target.RowBytes, rowLength);
            from.CopyTo(to);
            for (var x = 3; x < rowLength; x += 4)
            {
                to[x] = 0xFF;
            }
        }

        target.NotifyPixelsChanged();
    }

    protected override (double X, double Y)? PointerPosition(int nativeWidth, int nativeHeight)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0)
        {
            return null;
        }

        return info.X < 0 || info.Y < 0 || info.X >= nativeWidth || info.Y >= nativeHeight ? null : (info.X, info.Y);
    }

    private unsafe void EnsureDib(nint screen, int width, int height)
    {
        if (_dib != 0 && _dibWidth == width && _dibHeight == height)
        {
            return;
        }

        FreeDib();
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
            Compression = 0, // BI_RGB
        };

        _dib = CreateDIBSection(screen, ref header, 0, out _bits, 0, 0);
        if (_dib == 0 || _bits == 0)
        {
            _dib = 0;
            throw new ScreenCaptureException("gdi_failed", "CreateDIBSection failed");
        }

        _dibWidth = width;
        _dibHeight = height;
    }

    private void FreeDib()
    {
        if (_dib != 0)
        {
            DeleteObject(_dib);
            _dib = 0;
            _bits = 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        FreeDib();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public uint Flags;
        public nint Cursor;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint dc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBrushOrgEx(nint dc, int x, int y, nint previous);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
