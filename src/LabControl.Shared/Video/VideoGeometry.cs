using LabControl.Shared.Protocol;

namespace LabControl.Shared.Video;

/// <summary>
/// The arithmetic behind full-mode frames (PROTOCOL "Video", D-34): a screen is a grid of
/// <see cref="Defaults.VideoTileSize"/> squares, a change is reported as the tiles it
/// touches, and a frame carries one JPEG of the bounding box of those tiles. Pure functions,
/// shared by every producer (the simulator, the session helper) and the console.
/// </summary>
public static class VideoGeometry
{
    /// <summary>Grows a rectangle to whole tiles, clipped to the screen; an empty result means nothing to send.</summary>
    public static Rect AlignToTiles(Rect rect, int screenWidth, int screenHeight, int tile = Defaults.VideoTileSize)
    {
        var left = Math.Max(0, rect.X / tile * tile);
        var top = Math.Max(0, rect.Y / tile * tile);
        var right = Math.Min(screenWidth, (rect.X + rect.Width + tile - 1) / tile * tile);
        var bottom = Math.Min(screenHeight, (rect.Y + rect.Height + tile - 1) / tile * tile);
        return right <= left || bottom <= top
            ? new Rect()
            : new Rect { X = left, Y = top, Width = right - left, Height = bottom - top };
    }

    /// <summary>The smallest rectangle containing every given one; empty when there are none.</summary>
    public static Rect BoundingBox(IEnumerable<Rect> rects)
    {
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        var any = false;

        foreach (var rect in rects)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            any = true;
            left = Math.Min(left, rect.X);
            top = Math.Min(top, rect.Y);
            right = Math.Max(right, rect.X + rect.Width);
            bottom = Math.Max(bottom, rect.Y + rect.Height);
        }

        return any ? new Rect { X = left, Y = top, Width = right - left, Height = bottom - top } : new Rect();
    }

    /// <summary>
    /// Merges overlapping or touching tile-aligned rectangles so a frame lists each changed
    /// area once. The result is not minimal — two rectangles that only touch at a corner stay
    /// apart — but it never lists a pixel twice, which is what the blit needs.
    /// </summary>
    public static List<Rect> Merge(IEnumerable<Rect> rects)
    {
        var result = new List<Rect>();
        foreach (var rect in rects)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            var current = rect;
            var merged = true;
            while (merged)
            {
                merged = false;
                for (var i = result.Count - 1; i >= 0; i--)
                {
                    if (Intersects(result[i], current) || Contains(result[i], current) || Contains(current, result[i]))
                    {
                        current = BoundingBox([result[i], current]);
                        result.RemoveAt(i);
                        merged = true;
                    }
                }
            }

            result.Add(current);
        }

        return result;
    }

    /// <summary>The rectangle covering the whole screen.</summary>
    public static Rect Whole(int width, int height) => new() { X = 0, Y = 0, Width = width, Height = height };

    public static bool IsWhole(Rect rect, int width, int height) =>
        rect.X == 0 && rect.Y == 0 && rect.Width == width && rect.Height == height;

    public static bool Intersects(Rect a, Rect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    public static bool Contains(Rect outer, Rect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    /// <summary>Whether the rectangle lies inside a screen of the given size.</summary>
    public static bool FitsIn(Rect rect, int width, int height) =>
        rect.X >= 0 && rect.Y >= 0 && rect.Width > 0 && rect.Height > 0 && rect.X + rect.Width <= width && rect.Y + rect.Height <= height;

    /// <summary>
    /// The thumbnail size for a screen: <see cref="Defaults.ThumbnailWidth"/> wide (never
    /// upscaled), the height following the aspect ratio, both at least one pixel.
    /// </summary>
    public static (int Width, int Height) ThumbnailSize(int screenWidth, int screenHeight, int maxWidth = Defaults.ThumbnailWidth)
    {
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            return (1, 1);
        }

        if (screenWidth <= maxWidth)
        {
            return (screenWidth, screenHeight);
        }

        var height = (int)Math.Round((double)screenHeight * maxWidth / screenWidth);
        return (maxWidth, Math.Max(1, height));
    }
}
