using LabControl.Shared.Protocol;
using SkiaSharp;

namespace LabControl.Shared.Video;

/// <summary>What <see cref="ScreenImage.Apply"/> made of a frame.</summary>
public enum FrameOutcome
{
    /// <summary>The pixels were updated.</summary>
    Applied = 0,

    /// <summary>The JPEG did not decode; nothing changed.</summary>
    Undecodable = 1,

    /// <summary>
    /// A delta arrived for a screen the image does not hold yet (first frame, or the
    /// resolution changed); nothing changed and the producer should be asked for a keyframe.
    /// </summary>
    NeedsKeyframe = 2,

    /// <summary>The frame's rectangles do not fit the screen it claims; dropped.</summary>
    Malformed = 3,
}

/// <summary>
/// One PC's screen as the console last saw it: a persistent BGRA bitmap that whole frames
/// replace and full-mode deltas patch (PROTOCOL "Video", D-34). Thread-safe: a gRPC thread
/// applies frames while the UI thread copies pixels out. <see cref="Version"/> grows with
/// every applied frame so a view knows when to redraw.
/// </summary>
public sealed class ScreenImage : IDisposable
{
    private readonly Lock _lock = new();
    private SKBitmap? _bitmap;

    /// <summary>Pixel size of the image, 0×0 until the first frame.</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>The size of the screen the last frame came from (a thumbnail is smaller than its screen).</summary>
    public int ScreenWidth { get; private set; }

    public int ScreenHeight { get; private set; }

    /// <summary>Grows by one per applied frame; 0 means no frame yet.</summary>
    public long Version { get; private set; }

    public bool HasFrame => Version > 0;

    /// <summary>When the last frame was applied, by the console's clock.</summary>
    public DateTimeOffset LastFrameAt { get; private set; }

    /// <summary>The bounding box the last applied frame changed, for a view that only redraws what moved.</summary>
    public Rect LastDirty { get; private set; } = new();

    /// <summary>
    /// A whole-image frame (a thumbnail, or a full-mode keyframe) replaces the picture; a
    /// full-mode delta patches the rectangles it lists. Never throws on bad input — the
    /// outcome says what happened.
    /// </summary>
    public FrameOutcome Apply(VideoFrame frame, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var decoded = JpegCodec.Decode(frame.Jpeg.Span);
        if (decoded is null)
        {
            return FrameOutcome.Undecodable;
        }

        var screenWidth = frame.Width > 0 ? frame.Width : decoded.Width;
        var screenHeight = frame.Height > 0 ? frame.Height : decoded.Height;

        lock (_lock)
        {
            var whole = frame.Mode != VideoMode.Full || frame.Keyframe || frame.Dirty.Count == 0
                        || (frame.Dirty.Count == 1 && VideoGeometry.IsWhole(frame.Dirty[0], screenWidth, screenHeight));

            if (whole)
            {
                // Thumbnails may be any size ≤ the screen; a full keyframe is the screen itself.
                if (frame.Mode == VideoMode.Full && (decoded.Width != screenWidth || decoded.Height != screenHeight))
                {
                    return FrameOutcome.Malformed;
                }

                Replace(decoded, screenWidth, screenHeight);
                LastDirty = VideoGeometry.Whole(decoded.Width, decoded.Height);
            }
            else
            {
                if (_bitmap is null || Width != screenWidth || Height != screenHeight)
                {
                    return FrameOutcome.NeedsKeyframe;
                }

                var box = VideoGeometry.BoundingBox(frame.Dirty);
                if (!VideoGeometry.FitsIn(box, screenWidth, screenHeight) || box.Width != decoded.Width || box.Height != decoded.Height)
                {
                    return FrameOutcome.Malformed;
                }

                foreach (var rect in frame.Dirty)
                {
                    if (!VideoGeometry.FitsIn(rect, screenWidth, screenHeight))
                    {
                        return FrameOutcome.Malformed;
                    }
                }

                foreach (var rect in frame.Dirty)
                {
                    JpegCodec.Blit(decoded, rect.X - box.X, rect.Y - box.Y, rect.Width, rect.Height, _bitmap, rect.X, rect.Y);
                }

                LastDirty = box;
            }

            ScreenWidth = screenWidth;
            ScreenHeight = screenHeight;
            Version++;
            LastFrameAt = now;
            return FrameOutcome.Applied;
        }
    }

    /// <summary>Takes a whole picture from elsewhere (a downscaled keyframe feeding the thumbnail).</summary>
    public void Replace(SKBitmap picture, int screenWidth, int screenHeight, DateTimeOffset now)
    {
        lock (_lock)
        {
            Replace(picture, screenWidth, screenHeight);
            LastDirty = VideoGeometry.Whole(picture.Width, picture.Height);
            Version++;
            LastFrameAt = now;
        }
    }

    private void Replace(SKBitmap picture, int screenWidth, int screenHeight)
    {
        if (_bitmap is null || _bitmap.Width != picture.Width || _bitmap.Height != picture.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new SKBitmap(new SKImageInfo(picture.Width, picture.Height, JpegCodec.PixelFormat, SKAlphaType.Premul));
            Width = picture.Width;
            Height = picture.Height;
        }

        JpegCodec.Blit(picture, 0, 0, picture.Width, picture.Height, _bitmap, 0, 0);
        ScreenWidth = screenWidth;
        ScreenHeight = screenHeight;
    }

    /// <summary>
    /// Copies the picture into a caller-owned BGRA buffer with the given row stride (an
    /// Avalonia bitmap's locked framebuffer). Returns <c>false</c> when there is no frame yet
    /// or the buffer is the wrong size.
    /// </summary>
    public unsafe bool CopyTo(IntPtr destination, int width, int height, int rowBytes)
    {
        lock (_lock)
        {
            if (_bitmap is null || width != Width || height != Height || rowBytes < Width * JpegCodec.BytesPerPixel)
            {
                return false;
            }

            var source = _bitmap.GetPixelSpan();
            var rowLength = Width * JpegCodec.BytesPerPixel;
            var target = new Span<byte>((void*)destination, rowBytes * height);
            for (var y = 0; y < Height; y++)
            {
                source.Slice(y * _bitmap.RowBytes, rowLength).CopyTo(target.Slice(y * rowBytes, rowLength));
            }

            return true;
        }
    }

    /// <summary>A copy of the picture, for tests and for scaling into a thumbnail; <c>null</c> before the first frame.</summary>
    public SKBitmap? Snapshot()
    {
        lock (_lock)
        {
            return _bitmap?.Copy();
        }
    }

    /// <summary>The colour at a pixel, for tests; transparent when out of range or empty.</summary>
    public SKColor PixelAt(int x, int y)
    {
        lock (_lock)
        {
            return _bitmap is null || x < 0 || y < 0 || x >= Width || y >= Height ? SKColors.Transparent : _bitmap.GetPixel(x, y);
        }
    }

    /// <summary>Forgets the picture (the PC went away or changed mode); the next frame starts over.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            Width = 0;
            Height = 0;
        }
    }

    public void Dispose() => Clear();
}
