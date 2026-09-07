using Xunit;

using System.Collections.Concurrent;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace LabControl.Shared.Tests;

/// <summary>
/// The producer the session helper and the simulator share (M3 portion 2, D-35), driven
/// by a scripted screen: thumbnails only on change, keyframes and deltas in full mode, a
/// refused frame's rectangles carried into the next, a failing source reported and
/// reopened, a stop that closes the source, and the GDI fallback's tile comparison.
/// </summary>
public class ScreenProducerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Thumbnails_go_out_only_when_the_screen_changed()
    {
        var screen = new ScriptedScreen(640, 400);
        screen.Script([Rect(0, 0, 640, 400)], [], [], [Rect(100, 100, 10, 10)]);
        var frames = new ConcurrentQueue<VideoFrame>();
        await using var producer = Producer(() => screen, frames);

        producer.Apply(Fast(VideoSettings.ThumbnailControl()));
        await WaitFor(() => screen.Acquired >= 6);
        producer.Apply(null);

        Assert.Equal(2, frames.Count);
        Assert.All(frames, f =>
        {
            Assert.Equal(VideoMode.Thumbnail, f.Mode);
            Assert.True(f.Keyframe);
            Assert.Equal((640, 400), (f.Width, f.Height));
            using var picture = JpegCodec.Decode(f.Jpeg.Span);
            Assert.NotNull(picture);
            Assert.Equal(VideoGeometry.ThumbnailSize(640, 400), (picture.Width, picture.Height));
        });
    }

    [Fact]
    public async Task Full_mode_starts_with_a_keyframe_then_sends_deltas_and_a_keyframe_on_request()
    {
        var screen = new ScriptedScreen(640, 400);
        screen.Script([Rect(0, 0, 640, 400)], [Rect(70, 70, 10, 10)], []);
        var frames = new ConcurrentQueue<VideoFrame>();
        await using var producer = Producer(() => screen, frames);

        producer.Apply(Fast(VideoSettings.FullControl()));
        await WaitFor(() => frames.Count >= 2);

        var list = frames.ToList();
        Assert.True(list[0].Keyframe);
        Assert.Equal(VideoMode.Full, list[0].Mode);
        Assert.Single(list[0].Dirty);
        Assert.True(VideoGeometry.IsWhole(list[0].Dirty[0], 640, 400));

        Assert.False(list[1].Keyframe);
        var delta = Assert.Single(list[1].Dirty);
        Assert.Equal((64, 64, 64, 64), (delta.X, delta.Y, delta.Width, delta.Height));
        using var box = JpegCodec.Decode(list[1].Jpeg.Span);
        Assert.Equal((64, 64), (box!.Width, box.Height));

        producer.Apply(Fast(VideoSettings.FullControl(requestKeyframe: true)));
        await WaitFor(() => frames.Count >= 3);
        Assert.True(frames.ToList()[2].Keyframe);
        Assert.Equal(1, screen.Opened);
    }

    [Fact]
    public async Task A_refused_delta_stays_owed_and_the_next_one_carries_both_rectangles()
    {
        var screen = new ScriptedScreen(640, 400);
        screen.Script([Rect(0, 0, 640, 400)], [Rect(10, 10, 10, 10)], [Rect(500, 300, 10, 10)], []);
        var frames = new ConcurrentQueue<VideoFrame>();
        var refuseOnce = true;
        await using var producer = Producer(() => screen, frames, frame =>
        {
            if (!frame.Keyframe && refuseOnce)
            {
                refuseOnce = false;
                return false;
            }

            return true;
        });

        producer.Apply(Fast(VideoSettings.FullControl()));
        await WaitFor(() => frames.Count >= 2);

        var delta = frames.ToList()[1];
        Assert.False(delta.Keyframe);
        Assert.Equal(2, delta.Dirty.Count);
        Assert.Contains(delta.Dirty, r => r.X == 0 && r.Y == 0);
        Assert.Contains(delta.Dirty, r => r.X == 448 && r.Y == 256);
        var box = VideoGeometry.BoundingBox(delta.Dirty);
        using var picture = JpegCodec.Decode(delta.Jpeg.Span);
        Assert.Equal((box.Width, box.Height), (picture!.Width, picture.Height));
    }

    [Fact]
    public async Task A_failing_screen_is_reported_once_and_reopened()
    {
        var opened = 0;
        var events = new ConcurrentQueue<(string Code, string Message)>();
        var frames = new ConcurrentQueue<VideoFrame>();
        await using var producer = Producer(
            () =>
            {
                opened++;
                var screen = new ScriptedScreen(640, 400);
                screen.Script([Rect(0, 0, 640, 400)]);
                if (opened == 1)
                {
                    screen.FailAfter(1, new ScreenCaptureException("access_lost", "the desktop switched"));
                }

                return screen;
            },
            frames,
            report: events);

        producer.Apply(Fast(VideoSettings.ThumbnailControl()));
        await WaitFor(() => opened >= 2 && frames.Count >= 2);

        Assert.Contains(events, e => e.Code == "capture.access_lost");
        Assert.Contains(events, e => e.Code == "capture.recovered");
        Assert.Equal(1, events.Count(e => e.Code == "capture.access_lost"));
    }

    [Fact]
    public async Task Stopping_closes_the_source_and_a_mode_change_keeps_it_open()
    {
        var screen = new ScriptedScreen(640, 400);
        screen.Script([Rect(0, 0, 640, 400)]);
        var frames = new ConcurrentQueue<VideoFrame>();
        await using var producer = Producer(() => screen, frames);

        producer.Apply(Fast(VideoSettings.ThumbnailControl()));
        await WaitFor(() => frames.Count >= 1);
        Assert.Equal(VideoMode.Thumbnail, producer.Streaming);
        Assert.Equal("scripted", producer.SourceKind);

        producer.Apply(Fast(VideoSettings.FullControl()));
        await WaitFor(() => frames.Any(f => f.Mode == VideoMode.Full));
        Assert.Equal(1, screen.Opened);
        Assert.True(frames.First(f => f.Mode == VideoMode.Full).Keyframe);

        producer.Apply(VideoSettings.StopControl());
        await WaitFor(() => screen.Disposed && producer.SourceKind is null);
        Assert.Null(producer.Streaming);
    }

    [Fact]
    public void Tile_comparison_reports_only_the_tiles_that_differ()
    {
        const int width = 200;
        const int height = 100;
        var before = new byte[width * height * 4];
        var after = new byte[width * height * 4];
        Assert.Empty(TileDiff.Compare(before, after, width, height, width * 4));

        after[(70 * width + 130) * 4] = 0xFF;
        var one = Assert.Single(TileDiff.Compare(before, after, width, height, width * 4));
        Assert.Equal((128, 64, 64, 36), (one.X, one.Y, one.Width, one.Height));

        after[(10 * width + 10) * 4] = 0xFF;
        after[(10 * width + 100) * 4] = 0xFF;
        var strips = TileDiff.Compare(before, after, width, height, width * 4);
        Assert.Equal(2, strips.Count);
        Assert.Contains(strips, r => r.X == 0 && r.Y == 0 && r.Width == 128 && r.Height == 64);
    }

    [Fact]
    public async Task Auto_quality_steps_down_while_the_cap_bites_and_climbs_back_when_it_does_not()
    {
        // A screen that repaints a big box every tick against a cap that allows about one
        // such frame a second: the pacer makes every delta wait, so the quality falls to the
        // floor; then the screen goes quiet-ish (a tiny change), the frames are free, and
        // after enough of them the quality climbs a step.
        var screen = new ScriptedScreen(640, 400);
        screen.Noise();
        var steps = new List<IReadOnlyList<Rect>> { new[] { Rect(0, 0, 640, 400) } };
        steps.AddRange(Enumerable.Repeat(new[] { Rect(0, 0, 640, 320) }, 10));
        screen.Script(steps.ToArray());
        var frames = new ConcurrentQueue<VideoFrame>();
        await using var producer = Producer(() => screen, frames);

        var control = Fast(VideoSettings.FullControl());
        Assert.Equal(Defaults.VideoQualityAuto, control.Quality);
        control.MaxBitsPerSecond = 2 * 1024 * 1024;
        producer.Apply(control);
        await WaitFor(() => frames.Count(f => !f.Keyframe) >= 8);
        producer.Apply(null);

        var list = frames.ToList();
        Assert.Equal(Defaults.FullJpegQuality, list[0].Quality); // the keyframe, at the starting quality
        var deltas = list.Where(f => !f.Keyframe).Select(f => f.Quality).ToList();
        Assert.Equal(Defaults.FullJpegQuality, deltas[0]);       // the first delta is encoded before any wait was seen
        Assert.True(deltas.Zip(deltas.Skip(1)).All(pair => pair.Second <= pair.First), string.Join(",", deltas));
        Assert.True(deltas.Min() >= Defaults.FullJpegQualityMin);
        Assert.Equal(Defaults.FullJpegQualityMin, deltas[^1]);

        // Manual quality is obeyed as it is, frame after frame.
        var fixedFrames = new ConcurrentQueue<VideoFrame>();
        var quiet = new ScriptedScreen(640, 400);
        quiet.Script([Rect(0, 0, 640, 400)], [Rect(0, 0, 64, 64)], [Rect(0, 0, 64, 64)]);
        await using var fixedProducer = Producer(() => quiet, fixedFrames);
        fixedProducer.Apply(Fast(VideoSettings.FullControl(quality: 60)));
        await WaitFor(() => fixedFrames.Count >= 3);
        fixedProducer.Apply(null);
        Assert.All(fixedFrames, f => Assert.Equal(60, f.Quality));
    }

    [Fact]
    public async Task Auto_quality_climbs_back_after_a_run_of_free_frames()
    {
        var screen = new ScriptedScreen(640, 400);
        screen.Noise();
        var script = new List<IReadOnlyList<Rect>> { new[] { Rect(0, 0, 640, 400) } };
        script.AddRange(Enumerable.Repeat(new[] { Rect(0, 0, 640, 320) }, 6));            // big: the cap bites, quality falls
        script.AddRange(Enumerable.Repeat(new[] { Rect(0, 0, 64, 64) }, Defaults.AutoQualityFreeFrames + 12)); // tiny: free frames
        screen.Script(script.ToArray());
        var frames = new ConcurrentQueue<VideoFrame>();
        await using var producer = Producer(() => screen, frames);

        var control = Fast(VideoSettings.FullControl());
        control.MaxBitsPerSecond = 2 * 1024 * 1024;
        producer.Apply(control);
        await WaitFor(() => frames.Count(f => !f.Keyframe) >= Defaults.AutoQualityFreeFrames + 15);
        producer.Apply(null);

        var deltas = frames.Where(f => !f.Keyframe).Select(f => f.Quality).ToList();
        var lowest = deltas.Min();
        Assert.True(lowest < Defaults.FullJpegQuality, string.Join(",", deltas));
        Assert.True(deltas[^1] > lowest, string.Join(",", deltas));
    }

    [Fact]
    public void A_raw_buffer_is_scaled_and_encoded_without_a_copy()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(640, 400, JpegCodec.PixelFormat, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        var jpeg = JpegCodec.EncodeScaled(bitmap.GetPixelSpan(), 640, 400, bitmap.RowBytes, 320, 200, 50);
        using var decoded = JpegCodec.Decode(jpeg);
        Assert.Equal((320, 200), (decoded!.Width, decoded.Height));
    }

    // ------------------------------------------------------------------ plumbing

    private static ScreenProducer Producer(Func<IScreenSource> open, ConcurrentQueue<VideoFrame> frames, Func<VideoFrame, bool>? accept = null, ConcurrentQueue<(string, string)>? report = null) =>
        new(
            "PC-99",
            open,
            (frame, _) =>
            {
                var taken = accept?.Invoke(frame) ?? true;
                if (taken)
                {
                    frames.Enqueue(frame);
                }

                return ValueTask.FromResult(taken);
            },
            (_, code, message) => report?.Enqueue((code, message)),
            NullLogger.Instance);

    /// <summary>The control at the fastest rate the producer allows, so a test tick is 33 ms.</summary>
    private static VideoControl Fast(VideoControl control)
    {
        control.FramesPerSecond = Defaults.VideoMaxFramesPerSecond;
        return control;
    }

    private static Rect Rect(int x, int y, int width, int height) => new() { X = x, Y = y, Width = width, Height = height };

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the producer did not get there in time");
            await Task.Delay(20);
        }
    }

    /// <summary>A screen whose changes are scripted: each Acquire pops the next dirty list, then nothing changes.</summary>
    private sealed class ScriptedScreen : IScreenSource
    {
        private readonly SKBitmap _bitmap;
        private readonly Queue<IReadOnlyList<Rect>> _script = new();
        private int _failAfter = -1;
        private ScreenCaptureException? _failure;

        public ScriptedScreen(int width, int height)
        {
            _bitmap = new SKBitmap(new SKImageInfo(width, height, JpegCodec.PixelFormat, SKAlphaType.Premul));
            _bitmap.Erase(SKColors.DarkSlateGray);
            Opened++;
        }

        public string Kind => "scripted";

        public int Opened { get; private set; }

        public int Acquired { get; private set; }

        public bool Disposed { get; private set; }

        /// <summary>Random pixels, so a JPEG of the screen is as large as JPEGs get — what a photo or a busy page costs.</summary>
        public void Noise()
        {
            var random = new Random(7);
            random.NextBytes(_bitmap.GetPixelSpan());
        }

        public void Script(params IReadOnlyList<Rect>[] steps)
        {
            foreach (var step in steps)
            {
                _script.Enqueue(step);
            }
        }

        public void FailAfter(int acquires, ScreenCaptureException failure)
        {
            _failAfter = acquires;
            _failure = failure;
        }

        public IScreenFrame? Acquire(TimeSpan timeout)
        {
            if (_failAfter >= 0 && Acquired >= _failAfter)
            {
                throw _failure!;
            }

            Acquired++;
            var dirty = _script.Count > 0 ? _script.Dequeue() : [];
            return new Frame(_bitmap, dirty);
        }

        public void Dispose()
        {
            Disposed = true;
            _bitmap.Dispose();
        }

        private sealed class Frame(SKBitmap bitmap, IReadOnlyList<Rect> dirty) : IScreenFrame
        {
            public int Width => bitmap.Width;

            public int Height => bitmap.Height;

            public int RowBytes => bitmap.RowBytes;

            public ReadOnlySpan<byte> Pixels => bitmap.GetPixelSpan();

            public IReadOnlyList<Rect> Dirty => dirty;

            public void Dispose()
            {
            }
        }
    }
}
