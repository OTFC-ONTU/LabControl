using Google.Protobuf;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace LabControl.Shared.Tests;

/// <summary>
/// The pure halves of a broadcast (M6 portion 2, D-71, D-72): the console's screen source
/// that scales inside itself, finds what changed and draws the pointer; and the PC's picture
/// that decodes frames in order and never patches a delta onto the wrong picture.
/// </summary>
public class BroadcastVideoTests
{
    [Theory]
    [InlineData(2880, 1800, 1280, 1280, 800)]   // a Retina MacBook Air
    [InlineData(1920, 1080, 1280, 1280, 720)]
    [InlineData(1366, 768, 1280, 1280, 720)]
    [InlineData(1024, 768, 1280, 1024, 768)]    // narrower than the broadcast: its own size
    [InlineData(1281, 721, 1280, 1280, 720)]    // both sides even, for the encoder
    public void The_picture_is_at_most_the_broadcast_width_with_the_aspect_kept(int nativeWidth, int nativeHeight, int maxWidth, int width, int height)
    {
        Assert.Equal((width, height), ScaledScreenSource.ScaledSize(nativeWidth, nativeHeight, maxWidth));
    }

    [Fact]
    public void The_source_reports_the_whole_picture_first_then_only_what_changed()
    {
        using var source = new DrawnSource(2560, 1440);
        using (var first = source.Acquire(TimeSpan.Zero)!)
        {
            Assert.Equal(1280, first.Width);
            Assert.Equal(720, first.Height);
            var whole = Assert.Single(first.Dirty);
            Assert.True(VideoGeometry.IsWhole(whole, 1280, 720));
        }

        using (var unchanged = source.Acquire(TimeSpan.Zero)!)
        {
            Assert.Empty(unchanged.Dirty);
        }

        // A square appears at native (1000, 400): scaled by one half, at (500, 200) in the picture.
        source.Square = new SKRectI(1000, 400, 1100, 500);
        using var changed = source.Acquire(TimeSpan.Zero)!;
        Assert.NotEmpty(changed.Dirty);
        Assert.All(changed.Dirty, r => Assert.True(VideoGeometry.Intersects(r, new Rect { X = 448, Y = 192, Width = 128, Height = 128 })));
        Assert.Equal(SKColors.Red, Pixel(changed, 525, 225));
    }

    [Fact]
    public void The_pointer_is_drawn_into_the_picture_and_its_moves_are_changes()
    {
        using var source = new DrawnSource(1280, 720) { Pointer = (100, 100) };
        using (var first = source.Acquire(TimeSpan.Zero)!)
        {
            // White fill just inside the arrow's tip, on a black desktop.
            Assert.Equal(SKColors.White, Pixel(first, 103, 112));
        }

        source.Pointer = (600, 400);
        using var moved = source.Acquire(TimeSpan.Zero)!;
        Assert.Contains(moved.Dirty, r => VideoGeometry.Intersects(r, new Rect { X = 100, Y = 100, Width = 4, Height = 4 }));
        Assert.Contains(moved.Dirty, r => VideoGeometry.Intersects(r, new Rect { X = 600, Y = 400, Width = 4, Height = 4 }));
        Assert.Equal(SKColors.Black, Pixel(moved, 103, 112));
    }

    [Fact]
    public void A_display_that_disappears_is_a_capture_failure_not_a_crash()
    {
        using var source = new DrawnSource(0, 0);
        var failure = Assert.Throws<ScreenCaptureException>(() => source.Acquire(TimeSpan.Zero));
        Assert.Equal("no_display", failure.Reason);
    }

    [Fact]
    public void Linux_has_no_console_capture_and_says_so()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Equal(ConsoleScreenAccess.Unsupported, ConsoleScreen.Access());
        Assert.Equal("unsupported", Assert.Throws<ScreenCaptureException>(() => ConsoleScreen.Open()).Reason);
    }

    [Fact]
    public async Task The_pc_picture_waits_for_a_keyframe_and_applies_deltas_in_order()
    {
        using var picture = new BroadcastPicture(NullLogger.Instance);
        using var blue = Solid(64, 64, SKColors.Blue);
        using var green = Solid(64, 64, SKColors.Green);

        // A delta before any keyframe has nothing to patch: dropped.
        picture.Offer(Delta(blue, 1));
        Assert.Equal(1, picture.Dropped);

        picture.Offer(Keyframe(blue, 2));
        picture.Offer(Delta(green, 3));
        for (var i = 0; i < 100 && picture.Applied < 2; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, picture.Applied);
        Assert.Equal(128, picture.Image.Width);
        Assert.InRange(picture.Image.PixelAt(100, 100).Blue, 247, 255);
        Assert.InRange(picture.Image.PixelAt(100, 100).Green, 0, 8);
        Assert.InRange(picture.Image.PixelAt(10, 10).Green, SKColors.Green.Green - 8, SKColors.Green.Green + 8);
        Assert.InRange(picture.Image.PixelAt(10, 10).Blue, 0, 8);

        // A broadcast that ends forgets its picture; the next one starts from its own keyframe.
        picture.Clear();
        Assert.Null(picture.Image.Snapshot());
        picture.Offer(Delta(green, 4));
        Assert.Equal(2, picture.Dropped);
    }

    [Fact]
    public async Task A_frame_of_a_broadcast_that_is_over_never_stays_on_the_screen()
    {
        // D-71 item 9: Clear runs on the thread that offers the frames, at the point the
        // broadcast ends; a frame of it still in the queue or being decoded must not be
        // applied after it — and a keyframe offered right after it must not be lost.
        using var picture = new BroadcastPicture(NullLogger.Instance);
        using var blue = Solid(64, 64, SKColors.Blue);
        using var red = Solid(64, 64, SKColors.Red);
        for (var round = 0; round < 25; round++)
        {
            picture.Offer(Keyframe(blue, (ulong)(round * 2 + 1)));
            picture.Clear();
            await Task.Delay(40, TestContext.Current.CancellationToken);
            Assert.Null(picture.Image.Snapshot());

            var applied = picture.Applied;
            picture.Clear();
            picture.Offer(Keyframe(red, (ulong)(round * 2 + 2)));
            for (var i = 0; i < 100 && picture.Applied == applied; i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.InRange(picture.Image.PixelAt(10, 10).Red, 247, 255);
            Assert.InRange(picture.Image.PixelAt(10, 10).Blue, 0, 8);
            picture.Clear();
        }
    }

    // ------------------------------------------------------------------ helpers

    private static SKColor Pixel(IScreenFrame frame, int x, int y)
    {
        var offset = y * frame.RowBytes + x * 4;
        var p = frame.Pixels;
        return new SKColor(p[offset + 2], p[offset + 1], p[offset], p[offset + 3]);
    }

    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, JpegCodec.PixelFormat, SKAlphaType.Premul));
        bitmap.Erase(color);
        return bitmap;
    }

    private static VideoFrame Keyframe(SKBitmap tile, ulong seq)
    {
        using var whole = new SKBitmap(new SKImageInfo(128, 128, JpegCodec.PixelFormat, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(whole))
        {
            canvas.Clear(tile.GetPixel(0, 0));
        }

        var frame = new VideoFrame { Seq = seq, Width = 128, Height = 128, Mode = VideoMode.Full, Keyframe = true, Jpeg = ByteString.CopyFrom(JpegCodec.Encode(whole, 90)) };
        frame.Dirty.Add(VideoGeometry.Whole(128, 128));
        return frame;
    }

    private static VideoFrame Delta(SKBitmap tile, ulong seq)
    {
        var frame = new VideoFrame { Seq = seq, Width = 128, Height = 128, Mode = VideoMode.Full, Keyframe = false, Jpeg = ByteString.CopyFrom(JpegCodec.Encode(tile, 90)) };
        frame.Dirty.Add(new Rect { X = 0, Y = 0, Width = 64, Height = 64 });
        return frame;
    }

    /// <summary>A display drawn in memory: black, with an optional red square, and a pointer where the test says.</summary>
    private sealed class DrawnSource(int nativeWidth, int nativeHeight) : ScaledScreenSource(1280)
    {
        public SKRectI? Square { get; set; }

        public (double X, double Y)? Pointer { get; set; }

        public override string Kind => "drawn";

        protected override (int Width, int Height) NativeSize() => (nativeWidth, nativeHeight);

        protected override void CaptureInto(SKBitmap target, int width, int height)
        {
            using var canvas = new SKCanvas(target);
            canvas.Clear(SKColors.Black);
            if (Square is { } square)
            {
                using var paint = new SKPaint { Color = SKColors.Red };
                var scale = (float)target.Width / width;
                canvas.DrawRect(square.Left * scale, square.Top * scale, square.Width * scale, square.Height * scale, paint);
            }
        }

        protected override (double X, double Y)? PointerPosition(int width, int height) => Pointer;
    }
}
