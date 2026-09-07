using LabControl.Shared.Protocol;
using SkiaSharp;

namespace LabControl.Shared.Video;

/// <summary>
/// JPEG in and out of BGRA pixel buffers with SkiaSharp (D-11): the one encoder every
/// producer uses and the one decoder the console uses. Pixels are always
/// <see cref="SKColorType.Bgra8888"/>, premultiplied, top-down, tightly packed unless a row
/// stride says otherwise — the layout Avalonia's bitmaps and Windows' capture both use.
/// </summary>
public static class JpegCodec
{
    public const SKColorType PixelFormat = SKColorType.Bgra8888;

    public const int BytesPerPixel = 4;

    /// <summary>Encodes a rectangle of a bitmap; the rectangle must lie inside it.</summary>
    public static byte[] Encode(SKBitmap bitmap, Rect region, int quality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (!VideoGeometry.FitsIn(region, bitmap.Width, bitmap.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(region), $"{region.Width}×{region.Height} at {region.X},{region.Y} is not inside a {bitmap.Width}×{bitmap.Height} bitmap");
        }

        using var whole = bitmap.PeekPixels();
        using var subset = whole.ExtractSubset(new SKRectI(region.X, region.Y, region.X + region.Width, region.Y + region.Height))
            ?? throw new InvalidOperationException("the region could not be extracted from the bitmap");
        using var data = subset.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100))
            ?? throw new InvalidOperationException("SkiaSharp could not encode the region as JPEG");
        return data.ToArray();
    }

    /// <summary>Encodes a whole bitmap.</summary>
    public static byte[] Encode(SKBitmap bitmap, int quality) =>
        Encode(bitmap, VideoGeometry.Whole(bitmap.Width, bitmap.Height), quality);

    /// <summary>
    /// Encodes a rectangle of a raw BGRA buffer with the given row stride, without copying
    /// it first — what a capture that hands out a mapped surface needs.
    /// </summary>
    public static unsafe byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int rowBytes, Rect region, int quality)
    {
        if (rowBytes < width * BytesPerPixel || pixels.Length < (long)rowBytes * height)
        {
            throw new ArgumentException("the pixel buffer is smaller than its declared size", nameof(pixels));
        }

        if (!VideoGeometry.FitsIn(region, width, height))
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        fixed (byte* start = pixels)
        {
            var info = new SKImageInfo(width, height, PixelFormat, SKAlphaType.Premul);
            using var whole = new SKPixmap(info, (IntPtr)start, rowBytes);
            using var subset = whole.ExtractSubset(new SKRectI(region.X, region.Y, region.X + region.Width, region.Y + region.Height))
                ?? throw new InvalidOperationException("the region could not be extracted from the buffer");
            using var data = subset.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100))
                ?? throw new InvalidOperationException("SkiaSharp could not encode the region as JPEG");
            return data.ToArray();
        }
    }

    /// <summary>Decodes a JPEG into a fresh BGRA bitmap; <c>null</c> when the bytes are not a picture.</summary>
    public static SKBitmap? Decode(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.IsEmpty)
        {
            return null;
        }

        try
        {
            using var codec = SKCodec.Create(SKData.CreateCopy(jpeg));
            if (codec is null)
            {
                return null;
            }

            var info = codec.Info.WithColorType(PixelFormat).WithAlphaType(SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or AccessViolationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Draws one bitmap into another at the given offset, clipped to the target — a plain
    /// row copy, no scaling. Both bitmaps must be BGRA.
    /// </summary>
    public static void Blit(SKBitmap source, int sourceX, int sourceY, int width, int height, SKBitmap target, int targetX, int targetY)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Clip to both bitmaps so a stale delta on a resized screen can never write out of bounds.
        var clipLeft = Math.Max(0, Math.Max(-sourceX, -targetX));
        var clipTop = Math.Max(0, Math.Max(-sourceY, -targetY));
        var clipRight = Math.Min(width, Math.Min(source.Width - sourceX, target.Width - targetX));
        var clipBottom = Math.Min(height, Math.Min(source.Height - sourceY, target.Height - targetY));
        if (clipRight <= clipLeft || clipBottom <= clipTop)
        {
            return;
        }

        var rowLength = (clipRight - clipLeft) * BytesPerPixel;
        var from = source.GetPixelSpan();
        var to = target.GetPixelSpan();
        for (var y = clipTop; y < clipBottom; y++)
        {
            var sourceOffset = (sourceY + y) * source.RowBytes + (sourceX + clipLeft) * BytesPerPixel;
            var targetOffset = (targetY + y) * target.RowBytes + (targetX + clipLeft) * BytesPerPixel;
            from.Slice(sourceOffset, rowLength).CopyTo(to.Slice(targetOffset, rowLength));
        }
    }

    /// <summary>Scales a whole bitmap into a fresh one of the given size (bilinear).</summary>
    public static SKBitmap Scale(SKBitmap source, int width, int height)
    {
        var info = new SKImageInfo(Math.Max(1, width), Math.Max(1, height), PixelFormat, SKAlphaType.Premul);
        var scaled = new SKBitmap(info);
        if (!source.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
        {
            using var canvas = new SKCanvas(scaled);
            canvas.DrawBitmap(source, new SKRect(0, 0, scaled.Width, scaled.Height));
        }

        return scaled;
    }
}
