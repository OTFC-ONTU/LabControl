using Xunit;

using System.Collections.Concurrent;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using LabControl.Shared.Video.H264;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace LabControl.Shared.Tests;

/// <summary>
/// The producer's *Video* (M7 portion 6, D-91 items 2–4) with a stand-in encoder: whole
/// pictures as H.264 access units, IDRs when a keyframe is due, an even size, a refused frame
/// held instead of re-encoded, JPEG with one event when there is no encoder, and the stream's
/// own decoder on the receiving side.
/// </summary>
public sealed class H264ProducerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Video_sends_an_idr_first_then_slices_only_for_changes_and_an_idr_on_request()
    {
        using var screen = new ChangingScreen(641, 401);
        var frames = new ConcurrentQueue<VideoFrame>();
        var encoders = new List<FakeH264Encoder>();
        await using var producer = Producer(() => screen, frames, encoders);

        screen.Changes = 3;
        producer.Apply(VideoControl());
        await WaitFor(() => frames.Count >= 3);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var count = frames.Count;
        Assert.Equal(3, count); // a still screen sends nothing more

        var list = frames.ToList();
        Assert.All(list, f =>
        {
            Assert.Equal(Defaults.VideoCodecH264, f.Codec);
            Assert.Equal(VideoMode.Full, f.Mode);
            Assert.Equal((640, 400), (f.Width, f.Height));
            Assert.Empty(f.Dirty);
            Assert.Equal(0, f.Quality);
        });
        Assert.True(list[0].Keyframe);
        Assert.True(AnnexB.IsIdr(list[0].Jpeg.Span));
        Assert.NotNull(AnnexB.ParameterSets(list[0].Jpeg.Span));
        Assert.False(list[1].Keyframe);

        var control = VideoControl();
        control.RequestKeyframe = true;
        producer.Apply(control);
        await WaitFor(() => frames.Count > count);
        Assert.True(frames.ToList()[count].Keyframe);
        Assert.Equal(Defaults.VideoCodecH264, producer.LastCodec);
    }

    [Fact]
    public async Task A_refused_h264_frame_is_held_and_sent_again_unchanged()
    {
        using var screen = new ChangingScreen(320, 240) { Changes = 100 };
        var frames = new ConcurrentQueue<VideoFrame>();
        var encoders = new List<FakeH264Encoder>();
        var refusals = 3;
        await using var producer = Producer(() => screen, frames, encoders, frame =>
        {
            if (!frame.Keyframe && refusals > 0)
            {
                refusals--;
                return false;
            }

            return true;
        });

        producer.Apply(VideoControl());
        await WaitFor(() => frames.Count >= 3);

        // The second picture was refused three times and then taken as it was: nothing was
        // encoded in between, so the chain of slices has no hole.
        var list = frames.ToList();
        Assert.Equal(1, list[1].Jpeg[^1] - list[0].Jpeg[^1]);
        Assert.Equal(1, list[2].Jpeg[^1] - list[1].Jpeg[^1]);
    }

    [Fact]
    public async Task No_encoder_means_jpeg_and_one_event()
    {
        using var screen = new ChangingScreen(320, 240) { Changes = 100 };
        var frames = new ConcurrentQueue<VideoFrame>();
        var reports = new ConcurrentQueue<string>();
        await using var producer = new ScreenProducer("test", () => screen, Sink(frames), (_, code, _) => reports.Enqueue(code), NullLogger.Instance,
            h264: (_, _, _) => throw new H264Exception("no codec here"));

        producer.Apply(VideoControl());
        await WaitFor(() => frames.Count >= 3);
        Assert.All(frames, f => Assert.Equal(Defaults.VideoCodecJpeg, f.Codec));
        Assert.True(frames.First().Keyframe);
        Assert.Equal(["video.h264_unavailable"], reports.ToArray());
    }

    [Fact]
    public async Task An_encoder_failing_mid_stream_turns_the_stream_to_jpeg_from_a_keyframe()
    {
        using var screen = new ChangingScreen(320, 240) { Changes = 100 };
        var frames = new ConcurrentQueue<VideoFrame>();
        await using var producer = new ScreenProducer("test", () => screen, Sink(frames), (_, _, _) => { }, NullLogger.Instance,
            h264: (w, h, _) => new FakeH264Encoder(w, h) { FailOn = 3 });

        producer.Apply(VideoControl());
        await WaitFor(() => frames.Count >= 4);
        var list = frames.ToList();
        Assert.Equal(Defaults.VideoCodecH264, list[0].Codec);
        Assert.Equal(Defaults.VideoCodecH264, list[1].Codec);
        Assert.Equal(Defaults.VideoCodecJpeg, list[2].Codec);
        Assert.True(list[2].Keyframe);
    }

    [Fact]
    public async Task An_encoder_that_ignores_a_keyframe_request_is_replaced_by_one_that_starts_with_an_idr()
    {
        using var screen = new ChangingScreen(320, 240) { Changes = 2 };
        var frames = new ConcurrentQueue<VideoFrame>();
        var encoders = new List<FakeH264Encoder>();
        await using var producer = new ScreenProducer("test", () => screen, Sink(frames), (_, _, _) => { }, NullLogger.Instance,
            h264: (w, h, _) =>
            {
                var encoder = new FakeH264Encoder(w, h) { IgnoresKeyframeRequests = true };
                lock (encoders)
                {
                    encoders.Add(encoder);
                }

                return encoder;
            });

        producer.Apply(VideoControl());
        await WaitFor(() => frames.Count >= 2);
        var control = VideoControl();
        control.RequestKeyframe = true;
        producer.Apply(control);
        await WaitFor(() => frames.Count >= 3);
        Assert.True(frames.ToList()[2].Keyframe);
        Assert.True(encoders.Count >= 2);
        Assert.True(encoders[0].Disposed);
    }

    [Fact]
    public async Task Text_never_makes_an_encoder()
    {
        using var screen = new ChangingScreen(320, 240) { Changes = 5 };
        var frames = new ConcurrentQueue<VideoFrame>();
        var made = 0;
        await using var producer = new ScreenProducer("test", () => screen, Sink(frames), (_, _, _) => { }, NullLogger.Instance,
            h264: (w, h, _) =>
            {
                made++;
                return new FakeH264Encoder(w, h);
            });

        var control = VideoSettings.FullControl();
        control.FramesPerSecond = Defaults.VideoMaxFramesPerSecond;
        producer.Apply(control);
        await WaitFor(() => frames.Count >= 3);
        Assert.Equal(0, made);
        Assert.All(frames, f => Assert.Equal(Defaults.VideoCodecJpeg, f.Codec));
    }

    [Fact]
    public void The_settings_ask_for_h264_only_in_full_mode()
    {
        Assert.True(VideoSettings.From(VideoSettings.FullControl(video: true)).WantsH264);
        Assert.False(VideoSettings.From(VideoSettings.FullControl()).WantsH264);
        var thumbnail = VideoSettings.ThumbnailControl();
        thumbnail.Codec = Defaults.VideoCodecH264;
        Assert.False(VideoSettings.From(thumbnail).WantsH264);
        var video = VideoSettings.FullControl(video: true);
        Assert.Equal(Defaults.FullVideoFramesPerSecond, video.FramesPerSecond);
        Assert.Equal(Defaults.FullVideoBitsPerSecond, video.MaxBitsPerSecond);
    }

    [Fact]
    public void A_frame_decoder_follows_the_codec_each_frame_names()
    {
        using var image = new ScreenImage();
        using var decoder = new FrameDecoder(() => new FakeH264Decoder(64, 48));
        var now = DateTimeOffset.UtcNow;

        // A slice before any IDR asks for a keyframe and touches nothing.
        Assert.Equal(FrameOutcome.NeedsKeyframe, decoder.Apply(image, H264Frame(idr: false), now));
        Assert.False(image.HasFrame);

        Assert.Equal(FrameOutcome.Applied, decoder.Apply(image, H264Frame(idr: true), now));
        Assert.Equal((64, 48), (image.Width, image.Height));
        Assert.Equal(Defaults.VideoCodecH264, decoder.LastCodec);
        Assert.Equal(FrameOutcome.Applied, decoder.Apply(image, H264Frame(idr: false), now));

        // Back to Text: a JPEG keyframe replaces the picture; an empty codec (an older build) is JPEG too.
        using var bitmap = new SKBitmap(new SKImageInfo(64, 48, JpegCodec.PixelFormat, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Red);
        var jpeg = new VideoFrame
        {
            Width = 64, Height = 48, Mode = VideoMode.Full, Keyframe = true,
            Jpeg = Google.Protobuf.ByteString.CopyFrom(JpegCodec.Encode(bitmap, 80)),
        };
        jpeg.Dirty.Add(VideoGeometry.Whole(64, 48));
        Assert.Equal(FrameOutcome.Applied, decoder.Apply(image, jpeg, now));
        Assert.Equal(Defaults.VideoCodecJpeg, decoder.LastCodec);

        // After JPEG the H.264 decoder is gone: a slice needs an IDR again.
        Assert.Equal(FrameOutcome.NeedsKeyframe, decoder.Apply(image, H264Frame(idr: false), now));
    }

    [Fact]
    public void Three_failures_in_a_row_give_h264_up_once_and_jpeg_still_works()
    {
        using var image = new ScreenImage();
        var created = 0;
        using var decoder = new FrameDecoder(() =>
        {
            created++;
            throw new H264Exception("broken codec");
        });
        var reasons = new List<string>();
        decoder.BrokeDown += reasons.Add;
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(FrameOutcome.Undecodable, decoder.Apply(image, H264Frame(idr: true), now));
        Assert.Equal(FrameOutcome.Undecodable, decoder.Apply(image, H264Frame(idr: true), now));
        Assert.False(decoder.Broken);
        Assert.Equal(FrameOutcome.Undecodable, decoder.Apply(image, H264Frame(idr: true), now));
        Assert.True(decoder.Broken);
        Assert.Equal(["broken codec"], reasons);

        // Given up: no more attempts, and JPEG goes on.
        Assert.Equal(FrameOutcome.Undecodable, decoder.Apply(image, H264Frame(idr: true), now));
        Assert.Equal(3, created);
        using var bitmap = new SKBitmap(new SKImageInfo(64, 48, JpegCodec.PixelFormat, SKAlphaType.Premul));
        var jpeg = new VideoFrame
        {
            Width = 64, Height = 48, Mode = VideoMode.Full, Keyframe = true, Codec = Defaults.VideoCodecJpeg,
            Jpeg = Google.Protobuf.ByteString.CopyFrom(JpegCodec.Encode(bitmap, 80)),
        };
        jpeg.Dirty.Add(VideoGeometry.Whole(64, 48));
        Assert.Equal(FrameOutcome.Applied, decoder.Apply(image, jpeg, now));
        Assert.Single(reasons);
    }

    [Fact]
    public void A_success_between_failures_starts_the_count_again()
    {
        using var image = new ScreenImage();
        var calls = 0;
        using var decoder = new FrameDecoder(() => new ScriptedDecoder(() => ++calls % 3 == 0));
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 12; i++)
        {
            decoder.Apply(image, H264Frame(idr: true), now);
        }

        Assert.False(decoder.Broken);
    }

    /// <summary>Decodes when <c>succeed</c> says so, throws otherwise.</summary>
    private sealed class ScriptedDecoder(Func<bool> succeed) : IH264Decoder
    {
        private readonly SKBitmap _picture = new(new SKImageInfo(64, 48, JpegCodec.PixelFormat, SKAlphaType.Premul));

        public string Kind => "scripted";

        public SKBitmap? Decode(ReadOnlySpan<byte> accessUnit) => succeed() ? _picture : throw new H264Exception("scripted failure");

        public void Dispose() => _picture.Dispose();
    }

    [Fact]
    public void A_broadcast_picture_decodes_h264_on_its_own_thread()
    {
        using var picture = new BroadcastPicture(NullLogger.Instance, () => new FakeH264Decoder(128, 72));
        picture.Offer(H264Frame(idr: false, 128, 72));   // dropped: no IDR yet
        picture.Offer(H264Frame(idr: true, 128, 72));
        picture.Offer(H264Frame(idr: false, 128, 72));
        var deadline = DateTime.UtcNow + Patience;
        while (picture.Applied < 2 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.Equal(2, picture.Applied);
        Assert.Equal(1, picture.Dropped);
        Assert.Equal((128, 72), (picture.Image.Width, picture.Image.Height));
    }

    private static VideoFrame H264Frame(bool idr, int width = 64, int height = 48) => new()
    {
        Width = width,
        Height = height,
        Mode = VideoMode.Full,
        Keyframe = idr,
        Codec = Defaults.VideoCodecH264,
        Jpeg = Google.Protobuf.ByteString.CopyFrom(idr ? [0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 2, 0, 0, 0, 1, 0x65, 3] : new byte[] { 0, 0, 0, 1, 0x41, 4 }),
    };

    private static VideoControl VideoControl()
    {
        var control = VideoSettings.FullControl(video: true);
        control.FramesPerSecond = Defaults.VideoMaxFramesPerSecond;
        return control;
    }

    private static ScreenProducer Producer(Func<IScreenSource> open, ConcurrentQueue<VideoFrame> frames, List<FakeH264Encoder> encoders, Func<VideoFrame, bool>? accept = null) =>
        new("test", open, Sink(frames, accept), (_, _, _) => { }, NullLogger.Instance, h264: (w, h, _) =>
        {
            var encoder = new FakeH264Encoder(w, h);
            lock (encoders)
            {
                encoders.Add(encoder);
            }

            return encoder;
        });

    private static ScreenProducer.FrameSink Sink(ConcurrentQueue<VideoFrame> frames, Func<VideoFrame, bool>? accept = null) => (frame, _) =>
    {
        var taken = accept?.Invoke(frame) ?? true;
        if (taken)
        {
            frames.Enqueue(frame);
        }

        return ValueTask.FromResult(taken);
    };

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the producer did not get there in time");
            await Task.Delay(20);
        }
    }

    /// <summary>A screen whose next <see cref="Changes"/> captures report a change; then it is still.</summary>
    private sealed class ChangingScreen(int width, int height) : IScreenSource
    {
        private readonly SKBitmap _bitmap = new(new SKImageInfo(width, height, JpegCodec.PixelFormat, SKAlphaType.Premul));

        public volatile int Changes;

        public string Kind => "changing";

        public IScreenFrame? Acquire(TimeSpan timeout)
        {
            IReadOnlyList<Rect> dirty = Interlocked.Decrement(ref Changes) >= 0 ? [new Rect { X = 0, Y = 0, Width = 8, Height = 8 }] : [];
            return new Frame(_bitmap, dirty);
        }

        public void Dispose() => _bitmap.Dispose();

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
