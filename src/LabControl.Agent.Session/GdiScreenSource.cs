using System.ComponentModel;
using System.Runtime.InteropServices;
using LabControl.Shared;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LabControl.Agent.Session;

/// <summary>
/// The fallback when DXGI Desktop Duplication is not available (D-35) — a basic display
/// adapter, a VM without a WDDM 1.2 driver, a session where duplication is refused: a
/// <c>BitBlt</c> of the primary display into a DIB section. Windows reports nothing about
/// what changed, so two DIBs alternate and <see cref="TileDiff"/> compares them. Costlier
/// than DXGI (a copy plus a compare per frame) but it works everywhere GDI does, and at 2
/// thumbnails a second it is still cheap.
/// </summary>
internal sealed class GdiScreenSource : IScreenSource
{
    private readonly Dib?[] _dibs = new Dib?[2];
    private int _current = -1;
    private int _width;
    private int _height;

    public string Kind => "gdi";

    public static GdiScreenSource Open()
    {
        var source = new GdiScreenSource();
        source.EnsureSize();
        return source;
    }

    public unsafe IScreenFrame? Acquire(TimeSpan timeout)
    {
        try
        {
            DesktopAccess.AttachToInputDesktop();
        }
        catch (Win32Exception ex)
        {
            throw new ScreenCaptureException("no_desktop", ex.Message, ex);
        }

        var resized = EnsureSize();
        var next = (_current + 1) % 2;
        var dib = _dibs[next]!;

        var screen = PInvoke.GetDC(HWND.Null);
        if (screen.IsNull)
        {
            throw new ScreenCaptureException("gdi_failed", "GetDC of the screen failed");
        }

        try
        {
            var memory = PInvoke.CreateCompatibleDC(screen);
            if (memory.IsNull)
            {
                throw new ScreenCaptureException("gdi_failed", "CreateCompatibleDC failed");
            }

            try
            {
                var previous = PInvoke.SelectObject(memory, dib.Bitmap);
                try
                {
                    if (!PInvoke.BitBlt(memory, 0, 0, _width, _height, screen, 0, 0, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT))
                    {
                        throw new ScreenCaptureException("gdi_failed", $"BitBlt failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                    }

                    PInvoke.GdiFlush();
                }
                finally
                {
                    PInvoke.SelectObject(memory, previous);
                }
            }
            finally
            {
                PInvoke.DeleteDC(memory);
            }
        }
        finally
        {
            PInvoke.ReleaseDC(HWND.Null, screen);
        }

        List<Rect> dirty;
        if (_current < 0 || resized)
        {
            dirty = [VideoGeometry.Whole(_width, _height)];
        }
        else
        {
            var before = _dibs[_current]!;
            dirty = TileDiff.Compare(before.Pixels, dib.Pixels, _width, _height, _width * JpegCodec.BytesPerPixel);
        }

        _current = next;
        return new Frame(dib, _width, _height, dirty);
    }

    /// <summary>Reads the primary display's size and (re)creates the DIBs when it changed; true when it did.</summary>
    private bool EnsureSize()
    {
        var width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
        var height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN);
        if (width <= 0 || height <= 0)
        {
            throw new ScreenCaptureException("no_display", "the primary display reports no size");
        }

        if (width == _width && height == _height && _dibs[0] is not null && _dibs[1] is not null)
        {
            return false;
        }

        for (var i = 0; i < _dibs.Length; i++)
        {
            _dibs[i]?.Dispose();
            _dibs[i] = Dib.Create(width, height);
        }

        _width = width;
        _height = height;
        _current = -1;
        return true;
    }

    public void Dispose()
    {
        for (var i = 0; i < _dibs.Length; i++)
        {
            _dibs[i]?.Dispose();
            _dibs[i] = null;
        }
    }

    /// <summary>A top-down 32-bit DIB section: a bitmap GDI draws into and a pointer the CPU reads.</summary>
    private sealed unsafe class Dib : IDisposable
    {
        private readonly int _bytes;
        private void* _bits;

        private Dib(HBITMAP bitmap, void* bits, int bytes)
        {
            Bitmap = bitmap;
            _bits = bits;
            _bytes = bytes;
        }

        public HBITMAP Bitmap { get; private set; }

        public ReadOnlySpan<byte> Pixels => new(_bits, _bytes);

        public static Dib Create(int width, int height)
        {
            var info = new BITMAPINFO();
            info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height; // top-down
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = (uint)BI_COMPRESSION.BI_RGB;

            void* bits;
            var bitmap = PInvoke.CreateDIBSection(HDC.Null, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
            if (bitmap.IsNull || bits is null)
            {
                throw new ScreenCaptureException("gdi_failed", $"CreateDIBSection failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }

            return new Dib(bitmap, bits, width * height * JpegCodec.BytesPerPixel);
        }

        public void Dispose()
        {
            if (!Bitmap.IsNull)
            {
                PInvoke.DeleteObject(Bitmap);
                Bitmap = HBITMAP.Null;
                _bits = null;
            }
        }
    }

    private sealed class Frame(Dib dib, int width, int height, IReadOnlyList<Rect> dirty) : IScreenFrame
    {
        public int Width => width;

        public int Height => height;

        public int RowBytes => width * JpegCodec.BytesPerPixel;

        public ReadOnlySpan<byte> Pixels => dib.Pixels;

        public IReadOnlyList<Rect> Dirty => dirty;

        public void Dispose()
        {
        }
    }
}
