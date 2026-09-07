using Google.Protobuf;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using SkiaSharp;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// The screen stream end to end (M3 portion 1, PROTOCOL "Video", D-34): the console asks
/// every linked PC for thumbnails, a PC's frames land in the screen store over a real
/// <c>PushVideo</c> call, the full view switches the PC to full mode and back, and a delta
/// the console cannot use makes it ask for a keyframe.
/// </summary>
public class VideoTests
{
    [Fact]
    public async Task A_linked_pc_is_asked_for_thumbnails_and_its_frames_reach_the_screen_store()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var code = console.IssueCodes(1)[0];
        await using var pc = TestAgent.Install(console, 4, code).Start();

        VideoControl? received = null;
        pc.Link.VideoControlChanged += control => received ??= control;

        Assert.True(await Wait.UntilAsync(() => pc.Link.VideoControl is { Active: true, Mode: VideoMode.Thumbnail }));
        Assert.NotNull(received);
        Assert.Equal(Defaults.ThumbnailFramesPerSecond, received.FramesPerSecond);

        var screen = console.Session.Screens.Get(pc.AgentId);
        Assert.Equal(VideoMode.Thumbnail, screen.RequestedMode);
        Assert.False(screen.Thumbnail.HasFrame);

        // Two thumbnails: the picture is replaced each time, the counters move.
        Assert.True(pc.Link.TryPushVideo(Thumbnail(SKColors.Green)));
        Assert.True(await Wait.UntilAsync(() => screen.Thumbnail.Version == 1));
        Assert.Equal((320, 180, 1920, 1080), (screen.Thumbnail.Width, screen.Thumbnail.Height, screen.Thumbnail.ScreenWidth, screen.Thumbnail.ScreenHeight));
        Assert.True(screen.Thumbnail.PixelAt(10, 10).Green > 100, screen.Thumbnail.PixelAt(10, 10).ToString());

        Assert.True(await Wait.UntilAsync(() => pc.Link.TryPushVideo(Thumbnail(SKColors.Red))));
        Assert.True(await Wait.UntilAsync(() => screen.Thumbnail.Version == 2));
        Assert.True(screen.Thumbnail.PixelAt(10, 10).Red > 200);
        Assert.Equal(2, screen.FramesReceived);
        Assert.True(screen.BytesReceived > 0);
        Assert.True(await Wait.UntilAsync(() => pc.Link.VideoStats.Frames == 2));

        // The link drops: the producer is told to stop, the last picture stays.
        pc.Link.Disconnect("simulated cable pull");
        Assert.True(await Wait.UntilAsync(() => pc.Link.VideoControl is null));
        Assert.False(pc.Link.TryPushVideo(Thumbnail(SKColors.Blue)));
        Assert.True(screen.Thumbnail.HasFrame);

        // Back: thumbnails are asked for again on the new link.
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(pc.AgentId) && pc.Link.VideoControl is { Active: true }, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task The_full_view_switches_the_pc_to_full_mode_and_a_stray_delta_asks_for_a_keyframe()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var code = console.IssueCodes(1)[0];
        await using var pc = TestAgent.Install(console, 6, code).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.VideoControl is { Active: true, Mode: VideoMode.Thumbnail }));

        var controls = new List<VideoControl>();
        pc.Link.VideoControlChanged += control =>
        {
            lock (controls)
            {
                if (control is not null)
                {
                    controls.Add(control);
                }
            }
        };

        console.Session.SetScreenMode(pc.AgentId, VideoMode.Full);
        Assert.True(await Wait.UntilAsync(() => pc.Link.VideoControl is { Mode: VideoMode.Full, RequestKeyframe: true }));
        var screen = console.Session.Screens.Get(pc.AgentId);
        Assert.Equal(VideoMode.Full, screen.RequestedMode);
        Assert.Equal(VideoMode.Full, console.Session.FindLinked(pc.AgentId)!.RequestedVideo);

        // A delta with no keyframe behind it: refused, and the console asks for a keyframe.
        var delta = Full(640, 384, Paint(64, 64, SKColors.Red), keyframe: false, new Rect { X = 64, Y = 64, Width = 64, Height = 64 });
        Assert.True(pc.Link.TryPushVideo(delta));
        Assert.True(await Wait.UntilAsync(() => screen.FramesRejected == 1));
        Assert.False(screen.Full.HasFrame);
        Assert.True(await Wait.UntilAsync(() => { lock (controls) { return controls.Count(c => c.Mode == VideoMode.Full && c.RequestKeyframe) >= 2; } }));

        // The keyframe lands, and the mosaic thumbnail is refreshed from it while the PC no longer sends thumbnails.
        Assert.True(await Wait.UntilAsync(() => pc.Link.TryPushVideo(Full(640, 384, Paint(640, 384, SKColors.Blue), keyframe: true))));
        Assert.True(await Wait.UntilAsync(() => screen.Full.Version == 1));
        Assert.Equal((640, 384), (screen.Full.Width, screen.Full.Height));
        Assert.True(screen.Thumbnail.HasFrame);
        Assert.Equal((320, 192), (screen.Thumbnail.Width, screen.Thumbnail.Height));
        Assert.True(screen.Thumbnail.PixelAt(5, 5).Blue > 200);

        // Now the delta patches the picture.
        Assert.True(await Wait.UntilAsync(() => pc.Link.TryPushVideo(delta.Clone())));
        Assert.True(await Wait.UntilAsync(() => screen.Full.Version == 2));
        Assert.True(screen.Full.PixelAt(70, 70).Red > 200);
        Assert.True(screen.Full.PixelAt(10, 10).Blue > 200);

        // The window closes: back to thumbnails.
        console.Session.SetScreenMode(pc.AgentId, VideoMode.Thumbnail);
        Assert.True(await Wait.UntilAsync(() => pc.Link.VideoControl is { Mode: VideoMode.Thumbnail }));
        Assert.Equal(VideoMode.Thumbnail, screen.RequestedMode);
    }

    [Fact]
    public async Task A_frame_that_names_another_agent_ends_the_stream()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var codes = console.IssueCodes(2);
        await using var pc = TestAgent.Install(console, 7, codes[0]).Start();
        await using var other = TestAgent.Install(console, 8, codes[1]).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.VideoControl is { Active: true } && other.Link.VideoControl is { Active: true }));

        // The uplink stamps agent_id itself, so the only way to lie is to drive the call by
        // hand: PC-07's certificate on the wire, PC-08's id in the frame.
        using var certificate = System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions.CopyWithPrivateKey(pc.Store.Certificate!, pc.Store.Key);
        using var channel = ConsoleChannel.Open(System.Net.IPAddress.Loopback.ToString(), console.Port, console.Authority, console.Session.LabId, certificate);
        var client = new AgentService.AgentServiceClient(channel.Channel);
        using var call = client.PushVideo(cancellationToken: TestContext.Current.CancellationToken);
        var forged = Thumbnail(SKColors.Red);
        forged.AgentId = other.AgentId;
        forged.Seq = 1;
        await call.RequestStream.WriteAsync(forged, TestContext.Current.CancellationToken);

        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
        {
            await call.RequestStream.CompleteAsync();
            await call.ResponseAsync;
        });
        Assert.Equal(Grpc.Core.StatusCode.PermissionDenied, refused.StatusCode);
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "video.identity_mismatch" && e.Number == 7);
        Assert.Equal(0, console.Session.Screens.Get(other.AgentId).FramesReceived);
        Assert.Equal(0, console.Session.Screens.Get(pc.AgentId).FramesReceived);
    }

    [Fact]
    public async Task A_simulated_pc_streams_thumbnails_by_itself_and_full_mode_deltas_on_request()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var payload = console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1);
        var store = LabControl.FakeAgent.FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), "PC-09"), 9,
            LabControl.Shared.Setup.SetupPayload.Open(payload), System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        await using var machine = new LabControl.FakeAgent.FakeMachine(store, null, TestLogging.Factory.CreateLogger("PC-09"));
        machine.Start();

        // The simulator's odd-numbered screens are 1080p, and PC-09 is one of the idle ones:
        // its clock still ticks, so a thumbnail arrives about once a second.
        var screen = console.Session.Screens.Get(machine.AgentId);
        Assert.True(await Wait.UntilAsync(() => screen.Thumbnail.Version >= 2, TimeSpan.FromSeconds(20)), "no thumbnails from the simulated PC");
        Assert.Equal((320, 180, 1920, 1080), (screen.Thumbnail.Width, screen.Thumbnail.Height, screen.Thumbnail.ScreenWidth, screen.Thumbnail.ScreenHeight));
        Assert.Equal(0, screen.FramesRejected);
        Assert.Contains("streaming thumbnail", machine.Status);

        // Full mode: a keyframe first, then deltas for the clock; nothing is rejected.
        console.Session.SetScreenMode(machine.AgentId, VideoMode.Full);
        Assert.True(await Wait.UntilAsync(() => screen.Full.Version >= 3, TimeSpan.FromSeconds(20)), "no full-mode frames");
        Assert.Equal((1920, 1080), (screen.Full.Width, screen.Full.Height));
        Assert.Equal(0, screen.FramesRejected);
        Assert.True(screen.Full.LastDirty.Width < 1920, "the frames after the keyframe are deltas, not whole screens");
        Assert.True(screen.BytesReceived < 4 * 1024 * 1024, $"{screen.BytesReceived} bytes for a few frames is not the dirty-rectangle stream");

        console.Session.SetScreenMode(machine.AgentId, VideoMode.Thumbnail);
        Assert.True(await Wait.UntilAsync(() => machine.Status.Contains("streaming thumbnail", StringComparison.Ordinal), TimeSpan.FromSeconds(10)));
    }

    private static VideoFrame Thumbnail(SKColor colour)
    {
        using var picture = Paint(320, 180, colour);
        return new VideoFrame
        {
            Mode = VideoMode.Thumbnail,
            Width = 1920,
            Height = 1080,
            Keyframe = true,
            Codec = Defaults.VideoCodecJpeg,
            Jpeg = ByteString.CopyFrom(JpegCodec.Encode(picture, Defaults.ThumbnailJpegQuality)),
        };
    }

    private static VideoFrame Full(int width, int height, SKBitmap picture, bool keyframe, params Rect[] dirty)
    {
        using (picture)
        {
            var frame = new VideoFrame
            {
                Mode = VideoMode.Full,
                Width = width,
                Height = height,
                Keyframe = keyframe,
                Codec = Defaults.VideoCodecJpeg,
                Jpeg = ByteString.CopyFrom(JpegCodec.Encode(picture, Defaults.FullJpegQuality)),
            };
            frame.Dirty.AddRange(keyframe ? [VideoGeometry.Whole(width, height)] : dirty);
            return frame;
        }
    }

    private static SKBitmap Paint(int width, int height, SKColor colour)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, JpegCodec.PixelFormat, SKAlphaType.Premul));
        bitmap.Erase(colour);
        return bitmap;
    }
}
