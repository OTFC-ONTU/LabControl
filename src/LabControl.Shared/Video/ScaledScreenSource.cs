using LabControl.Shared.Protocol;
using SkiaSharp;

namespace LabControl.Shared.Video;

/// <summary>
/// The console's own screen for a broadcast (M6 portion 2, D-72): a platform capture drawn
/// straight into a picture at most <see cref="MaxWidth"/> pixels wide — the downscale happens
/// here, inside the source, so the <see cref="ScreenProducer"/> on top is exactly the one the
/// PCs use and encodes a 1280-wide picture once for every PC. Neither CoreGraphics nor GDI
/// says what changed, so two pictures alternate and <see cref="TileDiff"/> compares them, as
/// the helper's GDI fallback does (D-35). Neither capture shows the mouse pointer either, and
/// a teacher points at things, so the pointer is drawn into the picture before it is compared.
/// </summary>
public abstract class ScaledScreenSource : IScreenSource
{
    private readonly SKBitmap?[] _pictures = new SKBitmap?[2];
    private int _current = -1;
    private bool _disposed;

    protected ScaledScreenSource(int maxWidth)
    {
        MaxWidth = Math.Max(16, maxWidth);
    }

    /// <summary>The widest picture this source produces; a narrower screen is captured at its own size.</summary>
    public int MaxWidth { get; }

    public abstract string Kind { get; }

    /// <summary>The display's size in pixels right now; (0, 0) when there is no display to capture.</summary>
    protected abstract (int Width, int Height) NativeSize();

    /// <summary>
    /// Draws the whole display, scaled, into <paramref name="target"/> (BGRA, premultiplied,
    /// top-down). Throws <see cref="ScreenCaptureException"/> when the screen cannot be captured.
    /// </summary>
    protected abstract void CaptureInto(SKBitmap target, int nativeWidth, int nativeHeight);

    /// <summary>Where the pointer is, in the display's pixels; <c>null</c> when it is unknown or hidden.</summary>
    protected virtual (double X, double Y)? PointerPosition(int nativeWidth, int nativeHeight) => null;

    /// <summary>The picture size for a display: at most <see cref="MaxWidth"/> wide, the aspect ratio kept, both sides even.</summary>
    public static (int Width, int Height) ScaledSize(int nativeWidth, int nativeHeight, int maxWidth)
    {
        if (nativeWidth <= 0 || nativeHeight <= 0)
        {
            return (0, 0);
        }

        var width = Math.Min(nativeWidth, maxWidth);
        var height = (int)Math.Round((double)nativeHeight * width / nativeWidth);
        return (Math.Max(2, width & ~1), Math.Max(2, height & ~1));
    }

    public IScreenFrame? Acquire(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var (nativeWidth, nativeHeight) = NativeSize();
        if (nativeWidth <= 0 || nativeHeight <= 0)
        {
            throw new ScreenCaptureException("no_display", "there is no display to capture");
        }

        var (width, height) = ScaledSize(nativeWidth, nativeHeight, MaxWidth);
        var next = (_current + 1) % 2;
        var previous = _current >= 0 ? _pictures[_current] : null;
        if (previous is not null && (previous.Width != width || previous.Height != height))
        {
            // A new resolution: nothing compares with the old pictures any more.
            DisposePictures();
            previous = null;
        }

        var picture = _pictures[next] ??= new SKBitmap(new SKImageInfo(width, height, JpegCodec.PixelFormat, SKAlphaType.Premul));
        CaptureInto(picture, nativeWidth, nativeHeight);

        try
        {
            if (PointerPosition(nativeWidth, nativeHeight) is { } pointer)
            {
                DrawPointer(picture, (float)(pointer.X * width / nativeWidth), (float)(pointer.Y * height / nativeHeight), (float)width / nativeWidth);
            }
        }
        catch (Exception ex) when (ex is not ScreenCaptureException)
        {
            // The pointer is a courtesy; a picture without it is still the picture.
        }

        var dirty = previous is null
            ? [VideoGeometry.Whole(width, height)]
            : TileDiff.Compare(previous.GetPixelSpan(), picture.GetPixelSpan(), width, height, picture.RowBytes);
        _current = next;
        return new Frame(picture, dirty);
    }

    /// <summary>A plain arrow pointer, white with a dark outline, scaled with the picture but never smaller than legible.</summary>
    public static void DrawPointer(SKBitmap picture, float x, float y, float scale)
    {
        var size = Math.Max(14f, 22f * Math.Max(scale, 0.5f) * 1.4f);
        using var canvas = new SKCanvas(picture);
        using var path = new SKPath();
        path.MoveTo(x, y);
        path.LineTo(x, y + size);
        path.LineTo(x + size * 0.28f, y + size * 0.74f);
        path.LineTo(x + size * 0.46f, y + size * 1.1f);
        path.LineTo(x + size * 0.6f, y + size * 1.04f);
        path.LineTo(x + size * 0.42f, y + size * 0.68f);
        path.LineTo(x + size * 0.72f, y + size * 0.68f);
        path.Close();
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var outline = new SKPaint { Color = new SKColor(0, 0, 0, 230), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, size / 14f) };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, outline);
    }

    private void DisposePictures()
    {
        for (var i = 0; i < _pictures.Length; i++)
        {
            _pictures[i]?.Dispose();
            _pictures[i] = null;
        }

        _current = -1;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposePictures();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private sealed class Frame : IScreenFrame
    {
        private readonly SKBitmap _picture;

        public Frame(SKBitmap picture, IReadOnlyList<Rect> dirty)
        {
            _picture = picture;
            Dirty = dirty;
        }

        public int Width => _picture.Width;

        public int Height => _picture.Height;

        public int RowBytes => _picture.RowBytes;

        public ReadOnlySpan<byte> Pixels => _picture.GetPixelSpan();

        public IReadOnlyList<Rect> Dirty { get; }

        public void Dispose()
        {
            // The picture belongs to the source and is reused two captures later.
        }
    }
}
