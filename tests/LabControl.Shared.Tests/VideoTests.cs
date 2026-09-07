using Xunit;

using Google.Protobuf;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using SkiaSharp;

namespace LabControl.Shared.Tests;

/// <summary>
/// The pure half of the screen stream (M3, PROTOCOL "Video", D-34): tile alignment, the
/// JPEG codec, the persistent picture that whole frames replace and deltas patch, the
/// bandwidth pacer and the settings a control turns into.
/// </summary>
public class VideoTests
{
    [Fact]
    public void Rectangles_are_grown_to_whole_tiles_and_clipped_to_the_screen()
    {
        var aligned = VideoGeometry.AlignToTiles(new Rect { X = 70, Y = 5, Width = 10, Height = 10 }, 1920, 1080);
        Assert.Equal((64, 0, 64, 64), (aligned.X, aligned.Y, aligned.Width, aligned.Height));

        var edge = VideoGeometry.AlignToTiles(new Rect { X = 1900, Y = 1070, Width = 100, Height = 100 }, 1920, 1080);
        Assert.Equal((1856, 1024, 64, 56), (edge.X, edge.Y, edge.Width, edge.Height));

        var outside = VideoGeometry.AlignToTiles(new Rect { X = 2000, Y = 0, Width = 10, Height = 10 }, 1920, 1080);
        Assert.Equal(0, outside.Width);
    }

    [Fact]
    public void Merging_joins_overlapping_rectangles_and_keeps_separate_ones_apart()
    {
        var merged = VideoGeometry.Merge(
        [
            new Rect { X = 0, Y = 0, Width = 64, Height = 64 },
            new Rect { X = 32, Y = 32, Width = 64, Height = 64 },
            new Rect { X = 512, Y = 512, Width = 64, Height = 64 },
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, r => r.X == 0 && r.Y == 0 && r.Width == 96 && r.Height == 96);
        Assert.Contains(merged, r => r.X == 512 && r.Width == 64);

        var box = VideoGeometry.BoundingBox(merged);
        Assert.Equal((0, 0, 576, 576), (box.X, box.Y, box.Width, box.Height));
    }

    [Fact]
    public void Thumbnail_size_keeps_the_aspect_ratio_and_never_upscales()
    {
        Assert.Equal((320, 180), VideoGeometry.ThumbnailSize(1920, 1080));
        Assert.Equal((320, 200), VideoGeometry.ThumbnailSize(1280, 800));
        Assert.Equal((300, 200), VideoGeometry.ThumbnailSize(300, 200));
    }

    [Fact]
    public void A_region_survives_the_jpeg_round_trip()
    {
        using var picture = Paint(256, 128, SKColors.Blue);
        using (var canvas = new SKCanvas(picture))
        using (var paint = new SKPaint { Color = SKColors.Red })
        {
            canvas.DrawRect(64, 0, 64, 64, paint);
        }

        var bytes = JpegCodec.Encode(picture, new Rect { X = 64, Y = 0, Width = 64, Height = 64 }, 90);
        Assert.True(bytes.Length > 100);

        using var decoded = JpegCodec.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal((64, 64), (decoded.Width, decoded.Height));
        AssertClose(SKColors.Red, decoded.GetPixel(10, 10));

        Assert.Null(JpegCodec.Decode("not a picture"u8));
    }

    [Fact]
    public void A_thumbnail_replaces_the_picture_and_a_full_delta_patches_it()
    {
        var now = DateTimeOffset.UtcNow;
        var image = new ScreenImage();
        Assert.False(image.HasFrame);

        using var thumbnail = Paint(320, 180, SKColors.Green);
        Assert.Equal(FrameOutcome.Applied, image.Apply(Frame(VideoMode.Thumbnail, 1920, 1080, JpegCodec.Encode(thumbnail, 50), keyframe: true), now));
        Assert.Equal((320, 180, 1920, 1080), (image.Width, image.Height, image.ScreenWidth, image.ScreenHeight));
        AssertClose(SKColors.Green, image.PixelAt(100, 100));

        // A delta before any full keyframe has nothing to land on.
        var full = new ScreenImage();
        var delta = Frame(VideoMode.Full, 640, 384, JpegCodec.Encode(Paint(64, 64, SKColors.Red), 75), keyframe: false,
            new Rect { X = 128, Y = 64, Width = 64, Height = 64 });
        Assert.Equal(FrameOutcome.NeedsKeyframe, full.Apply(delta, now));

        using var screen = Paint(640, 384, SKColors.Blue);
        Assert.Equal(FrameOutcome.Applied, full.Apply(Frame(VideoMode.Full, 640, 384, JpegCodec.Encode(screen, 75), keyframe: true), now));
        Assert.Equal(1, full.Version);

        Assert.Equal(FrameOutcome.Applied, full.Apply(delta, now));
        Assert.Equal(2, full.Version);
        AssertClose(SKColors.Red, full.PixelAt(150, 100));
        AssertClose(SKColors.Blue, full.PixelAt(100, 100));
        Assert.Equal((128, 64), (full.LastDirty.X, full.LastDirty.Y));

        // Two dirty rectangles in one frame: the JPEG is their bounding box, only they are blitted.
        using var box = Paint(192, 64, SKColors.Yellow);
        var two = Frame(VideoMode.Full, 640, 384, JpegCodec.Encode(box, 75), keyframe: false,
            new Rect { X = 0, Y = 256, Width = 64, Height = 64 }, new Rect { X = 128, Y = 256, Width = 64, Height = 64 });
        Assert.Equal(FrameOutcome.Applied, full.Apply(two, now));
        AssertClose(SKColors.Yellow, full.PixelAt(10, 260));
        AssertClose(SKColors.Blue, full.PixelAt(100, 260), "the gap between the two rectangles is not touched");
        AssertClose(SKColors.Yellow, full.PixelAt(150, 260));

        // A malformed delta (JPEG smaller than its box) and one off the screen change nothing.
        var wrong = Frame(VideoMode.Full, 640, 384, JpegCodec.Encode(Paint(10, 10, SKColors.Black), 75), keyframe: false,
            new Rect { X = 0, Y = 0, Width = 64, Height = 64 });
        Assert.Equal(FrameOutcome.Malformed, full.Apply(wrong, now));
        var outside = Frame(VideoMode.Full, 640, 384, JpegCodec.Encode(Paint(64, 64, SKColors.Black), 75), keyframe: false,
            new Rect { X = 640, Y = 0, Width = 64, Height = 64 });
        Assert.Equal(FrameOutcome.Malformed, full.Apply(outside, now));
        Assert.Equal(3, full.Version);

        // A screen that changed size wants a keyframe again.
        var resized = Frame(VideoMode.Full, 1280, 720, JpegCodec.Encode(Paint(64, 64, SKColors.Black), 75), keyframe: false,
            new Rect { X = 0, Y = 0, Width = 64, Height = 64 });
        Assert.Equal(FrameOutcome.NeedsKeyframe, full.Apply(resized, now));
    }

    [Fact]
    public void The_picture_copies_into_a_stride_aligned_buffer()
    {
        var image = new ScreenImage();
        using var picture = Paint(30, 4, SKColors.Red);
        Assert.Equal(FrameOutcome.Applied, image.Apply(Frame(VideoMode.Thumbnail, 30, 4, JpegCodec.Encode(picture, 100), keyframe: true), DateTimeOffset.UtcNow));

        const int stride = 30 * 4 + 8;
        var buffer = new byte[stride * 4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            Assert.True(image.CopyTo(pinned.AddrOfPinnedObject(), 30, 4, stride));
            Assert.False(image.CopyTo(pinned.AddrOfPinnedObject(), 31, 4, stride));
        }
        finally
        {
            pinned.Free();
        }

        // BGRA: red is 0,0,255 in the first three bytes of the last row.
        var last = stride * 3;
        Assert.True(buffer[last + 2] > 200 && buffer[last] < 60, "the last row was copied at its stride");
        Assert.Equal(0, buffer[stride - 1]);
    }

    [Fact]
    public void The_pacer_lets_a_burst_through_and_then_throttles()
    {
        var start = DateTimeOffset.UtcNow;
        var pacer = new VideoPacer(8 * 1024 * 1024, start);

        // One second's budget is a megabyte; a 900 KB keyframe goes out at once.
        Assert.Equal(TimeSpan.Zero, pacer.Account(900 * 1024, start));

        // The budget is 1024 KiB; the next 300 KiB overdraw it by 176 KiB ≈ 0.17 s.
        var wait = pacer.Account(300 * 1024, start);
        Assert.InRange(wait.TotalSeconds, 0.17, 0.18);

        // After a second the bucket is full again.
        Assert.Equal(TimeSpan.Zero, pacer.Account(900 * 1024, start.AddSeconds(1.2)));
    }

    [Fact]
    public void Video_settings_fill_the_blanks_from_defaults()
    {
        var thumbnail = VideoSettings.From(new VideoControl { Active = true, Mode = VideoMode.Thumbnail });
        Assert.Equal((VideoMode.Thumbnail, Defaults.ThumbnailFramesPerSecond, Defaults.ThumbnailJpegQuality, Defaults.ThumbnailModeBitsPerSecond),
            (thumbnail.Mode, thumbnail.FramesPerSecond, thumbnail.Quality, thumbnail.MaxBitsPerSecond));

        var full = VideoSettings.From(new VideoControl { Active = true, Mode = VideoMode.Full, FramesPerSecond = 500, Quality = 200 });
        Assert.Equal(Defaults.VideoMaxFramesPerSecond, full.FramesPerSecond);
        Assert.Equal(100, full.Quality);
        Assert.Equal(Defaults.FullModeBitsPerSecond, full.MaxBitsPerSecond);

        Assert.Equal(VideoMode.Thumbnail, VideoSettings.From(new VideoControl { Active = true }).Mode);
        Assert.True(VideoSettings.FullControl().RequestKeyframe);
        Assert.False(VideoSettings.StopControl().Active);
    }

    private static SKBitmap Paint(int width, int height, SKColor colour)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, JpegCodec.PixelFormat, SKAlphaType.Premul));
        bitmap.Erase(colour);
        return bitmap;
    }

    private static VideoFrame Frame(VideoMode mode, int width, int height, byte[] jpeg, bool keyframe, params Rect[] dirty)
    {
        var frame = new VideoFrame { Mode = mode, Width = width, Height = height, Keyframe = keyframe, Jpeg = ByteString.CopyFrom(jpeg), Codec = Defaults.VideoCodecJpeg };
        frame.Dirty.AddRange(dirty);
        return frame;
    }

    private static void AssertClose(SKColor expected, SKColor actual, string? because = null)
    {
        // JPEG is lossy; a solid colour comes back within a few units.
        Assert.True(Math.Abs(expected.Red - actual.Red) < 24 && Math.Abs(expected.Green - actual.Green) < 24 && Math.Abs(expected.Blue - actual.Blue) < 24,
            $"expected about {expected}, got {actual}{(because is null ? string.Empty : ": " + because)}");
    }
}
