using LabControl.Shared.Protocol;

namespace LabControl.Shared.Video;

/// <summary>
/// Dirty rectangles for a capture that has none of its own (the GDI fallback, D-35): two
/// pictures of the same size compared tile by tile, the changed tiles reported as one
/// rectangle per run of tiles in a row. A pixel-exact comparison of a 1080p screen is a few
/// megabytes of vectorised <c>SequenceEqual</c> — cheap next to the JPEG that follows.
/// </summary>
public static class TileDiff
{
    /// <summary>Tile-aligned rectangles where <paramref name="after"/> differs from <paramref name="before"/>.</summary>
    public static List<Rect> Compare(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int width, int height, int rowBytes, int tile = Defaults.VideoTileSize)
    {
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        var needed = (long)rowBytes * height;
        if (before.Length < needed || after.Length < needed)
        {
            throw new ArgumentException("a pixel buffer is smaller than its declared size");
        }

        var columns = (width + tile - 1) / tile;
        var rows = (height + tile - 1) / tile;
        var changed = new bool[rows * columns];
        var rowLength = width * JpegCodec.BytesPerPixel;

        for (var y = 0; y < height; y++)
        {
            var a = before.Slice(y * rowBytes, rowLength);
            var b = after.Slice(y * rowBytes, rowLength);
            if (a.SequenceEqual(b))
            {
                continue;
            }

            var tileRow = y / tile;
            for (var column = 0; column < columns; column++)
            {
                if (changed[tileRow * columns + column])
                {
                    continue;
                }

                var from = column * tile * JpegCodec.BytesPerPixel;
                var length = Math.Min(tile * JpegCodec.BytesPerPixel, rowLength - from);
                if (!a.Slice(from, length).SequenceEqual(b.Slice(from, length)))
                {
                    changed[tileRow * columns + column] = true;
                }
            }
        }

        var result = new List<Rect>();
        for (var tileRow = 0; tileRow < rows; tileRow++)
        {
            var column = 0;
            while (column < columns)
            {
                if (!changed[tileRow * columns + column])
                {
                    column++;
                    continue;
                }

                var start = column;
                while (column < columns && changed[tileRow * columns + column])
                {
                    column++;
                }

                var left = start * tile;
                var top = tileRow * tile;
                result.Add(new Rect
                {
                    X = left,
                    Y = top,
                    Width = Math.Min(width, column * tile) - left,
                    Height = Math.Min(height, top + tile) - top,
                });
            }
        }

        return result;
    }
}
