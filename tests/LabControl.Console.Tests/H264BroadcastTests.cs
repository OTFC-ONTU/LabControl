using LabControl.Console.Services;
using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using LabControl.Shared.Tests;
using LabControl.Shared.Video;
using LabControl.Shared.Video.H264;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// *Video* end to end (M7 portion 6, D-91): the hub's two streams with a stand-in encoder on
/// every OS — H.264 for the PCs that can show it, JPEG for the one that cannot, a switch
/// moving every PC — and, where this computer has VideoToolbox, the real codec from the
/// console's broadcast into the simulated PCs' pictures and from a simulated PC's full view
/// into the console's. The remembered choice is in <c>preferences.json</c>.
/// </summary>
public sealed class H264BroadcastTests
{
    [Fact]
    public async Task In_video_each_pc_gets_the_stream_it_can_show_and_a_switch_moves_them()
    {
        await using var hub = new BroadcastHub("session", TeacherScreen, TestLogging.Factory.CreateLogger("hub"), () => DateTimeOffset.UtcNow, (_, _, _) => { },
            video: true, h264: (w, h, _) => new FakeH264Encoder(w, h));
        hub.Start();
        var modern = new Writer();
        var older = new Writer();
        using var stop = new CancellationTokenSource();
        var modernTask = hub.ServeAsync("modern", [Defaults.VideoCodecH264], modern, stop.Token);
        var olderTask = hub.ServeAsync("older", [], older, stop.Token);

        Assert.True(await Wait.UntilAsync(() => modern.Frames.Count >= 5 && older.Frames.Count >= 5, TimeSpan.FromSeconds(20)),
            $"modern {modern.Frames.Count}, older {older.Frames.Count}");
        Assert.True(hub.Video);
        Assert.Equal(Defaults.VideoCodecH264, hub.StreamOf("modern"));
        Assert.Equal(Defaults.VideoCodecJpeg, hub.StreamOf("older"));
        Assert.Equal(1, hub.WatchingAsText);
        Assert.All(modern.Frames, f => Assert.Equal(Defaults.VideoCodecH264, f.Codec));
        Assert.All(older.Frames, f => Assert.Equal(Defaults.VideoCodecJpeg, FrameDecoder.CodecOf(f)));
        Assert.True(modern.Frames[0].Keyframe);
        Assert.True(older.Frames[0].Keyframe);
        Assert.Equal(Defaults.VideoCodecH264, hub.StreamCodec);

        // Text: the H.264 PC moves to the JPEG stream, starting at a keyframe.
        Assert.True(hub.SetVideo(false));
        var before = modern.Frames.Count;
        Assert.True(await Wait.UntilAsync(() => modern.Frames.Skip(before).Any(f => f.Codec == Defaults.VideoCodecJpeg), TimeSpan.FromSeconds(20)));
        var firstJpeg = modern.Frames.Skip(before).First(f => f.Codec == Defaults.VideoCodecJpeg);
        Assert.True(firstJpeg.Keyframe);
        Assert.Equal(Defaults.VideoCodecJpeg, hub.StreamOf("modern"));
        Assert.Equal(0, hub.WatchingAsText);

        stop.Cancel();
        await Task.WhenAll(modernTask, olderTask);
    }

    [Fact]
    public async Task With_only_jpeg_pcs_watching_video_encodes_no_h264()
    {
        var made = 0;
        await using var hub = new BroadcastHub("session", TeacherScreen, TestLogging.Factory.CreateLogger("hub"), () => DateTimeOffset.UtcNow, (_, _, _) => { },
            video: true, h264: (w, h, _) =>
            {
                Interlocked.Increment(ref made);
                return new FakeH264Encoder(w, h);
            });
        hub.Start();
        var older = new Writer();
        using var stop = new CancellationTokenSource();
        var serve = hub.ServeAsync("older", [], older, stop.Token);
        Assert.True(await Wait.UntilAsync(() => older.Frames.Count >= 5, TimeSpan.FromSeconds(20)));

        // The H.264 stream may have started before the PC dialled; once it did, it stops.
        var before = hub.FramesEncoded;
        var h264Before = Volatile.Read(ref made);
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.Equal(h264Before, Volatile.Read(ref made));
        Assert.Equal(Defaults.VideoCodecJpeg, hub.StreamOf("older"));
        Assert.Equal(1, hub.WatchingAsText);
        Assert.True(hub.FramesPerSecond > 0);
        Assert.All(older.Frames, f => Assert.Equal(Defaults.VideoCodecJpeg, FrameDecoder.CodecOf(f)));
        Assert.True(hub.FramesEncoded > before);
        stop.Cancel();
        await serve;
    }

    [Fact]
    public async Task Video_needs_an_encoder_on_this_console()
    {
        await using var hub = new BroadcastHub("session", TeacherScreen, TestLogging.Factory.CreateLogger("hub"), () => DateTimeOffset.UtcNow, (_, _, _) => { },
            video: false, h264: (_, _, _) => throw new H264Exception("no codec"));

        // A factory is an encoder as far as the hub knows; it falls back to JPEG frame by frame.
        Assert.True(hub.CanVideo);
        hub.Start();
        Assert.True(hub.SetVideo(true));
        var writer = new Writer();
        using var stop = new CancellationTokenSource();
        var serve = hub.ServeAsync("pc", [Defaults.VideoCodecH264], writer, stop.Token);
        Assert.True(await Wait.UntilAsync(() => writer.Frames.Count >= 3, TimeSpan.FromSeconds(20)));
        Assert.All(writer.Frames, f => Assert.Equal(Defaults.VideoCodecJpeg, f.Codec));
        stop.Cancel();
        await serve;
    }

    [Fact]
    public async Task The_real_codec_carries_the_broadcast_to_the_pcs_pictures()
    {
        SkipWithoutH264();
        await using var console = await TestConsole.CreateLabAsync();
        console.Session.BroadcastSource = TeacherScreen;
        var machines = await StartMachinesAsync(console, 3);
        try
        {
            Assert.Equal(3, console.Session.StartBroadcast(machines.Select(m => m.AgentId), video: true));
            var hub = console.Session.Broadcast!;
            Assert.True(await Wait.UntilAsync(() => machines.All(m => m.BroadcastPicture.Image.HasFrame), TimeSpan.FromSeconds(30)));
            Assert.All(machines, m => Assert.Equal(Defaults.VideoCodecH264, hub.StreamOf(m.AgentId)));
            Assert.Equal(Defaults.VideoCodecH264, hub.StreamCodec);
            Assert.Equal((1280, 720), (machines[0].BroadcastPicture.Image.Width, machines[0].BroadcastPicture.Image.Height));

            // The pictures keep moving: frames are decoded, not only the first IDR.
            var applied = machines[0].BroadcastPicture.Applied;
            Assert.True(await Wait.UntilAsync(() => machines[0].BroadcastPicture.Applied >= applied + 10, TimeSpan.FromSeconds(20)));
            Assert.True(machines[0].BroadcastPicture.Dropped <= 1);

            await console.Session.StopBroadcastAsync();
        }
        finally
        {
            foreach (var machine in machines)
            {
                await machine.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task The_real_codec_carries_a_full_view_in_video_and_back_to_text()
    {
        SkipWithoutH264();
        await using var console = await TestConsole.CreateLabAsync();
        var machines = await StartMachinesAsync(console, 1);
        var machine = machines[0];
        try
        {
            var screen = console.Session.Screens.Get(machine.AgentId);
            console.Session.SetScreenMode(machine.AgentId, VideoMode.Full, video: true);
            Assert.True(await Wait.UntilAsync(() => screen.FullDecoder.LastCodec == Defaults.VideoCodecH264 && screen.Full.HasFrame, TimeSpan.FromSeconds(30)),
                $"codec {screen.FullDecoder.LastCodec}, problem {screen.FullDecoder.Problem}");
            Assert.Equal((screen.ScreenWidth & ~1, screen.ScreenHeight & ~1), (screen.Full.Width, screen.Full.Height));

            console.Session.SetScreenMode(machine.AgentId, VideoMode.Full, video: false);
            Assert.True(await Wait.UntilAsync(() => screen.FullDecoder.LastCodec == Defaults.VideoCodecJpeg, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            await machine.DisposeAsync();
        }
    }

    [Fact]
    public void The_choices_are_remembered_separately_on_this_computer()
    {
        var directory = TestConsole.TempDirectory();
        var first = new VideoPreferences(directory);
        Assert.False(first.BroadcastVideo);
        Assert.False(first.FullViewVideo);
        first.BroadcastVideo = true;

        var again = new VideoPreferences(directory);
        Assert.True(again.BroadcastVideo);
        Assert.False(again.FullViewVideo);
        again.FullViewVideo = true;
        again.BroadcastVideo = false;

        var third = new VideoPreferences(directory);
        Assert.False(third.BroadcastVideo);
        Assert.True(third.FullViewVideo);

        // The language stored beside them is kept.
        ConsoleLanguage.Save(directory, LabControl.Shared.Localization.ProductLanguages.English);
        Assert.True(new VideoPreferences(directory).FullViewVideo);
        Assert.Equal("en", ConsoleLanguage.ReadStored(directory, out _));

        // An unreadable file is Text, and the console starts.
        File.WriteAllText(ConsoleLanguage.PreferencesPath(directory), "{ not json");
        Assert.False(new VideoPreferences(directory).FullViewVideo);
    }

    private static void SkipWithoutH264()
    {
        if (!H264Codecs.CanEncode || !H264Codecs.CanDecode)
        {
            Assert.Skip($"no H.264 on this computer: {H264Codecs.WhyNoEncoder ?? H264Codecs.WhyNoDecoder}");
        }
    }

    private static IScreenSource TeacherScreen() => new FakeScreen(90, 1280, 720, idle: false);

    private static async Task<List<FakeMachine>> StartMachinesAsync(TestConsole console, int count)
    {
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: count));
        var root = TestConsole.TempDirectory();
        var machines = new List<FakeMachine>();
        for (var n = 1; n <= count; n++)
        {
            var store = FakeMachine.Install(Path.Combine(root, $"PC-{n:00}"), n, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
            var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger($"PC-{n:00}"));
            machines.Add(machine);
            machine.Start();
        }

        Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count >= count, TimeSpan.FromSeconds(30)), $"{console.Session.Linked.Count} of {count} linked");
        return machines;
    }

    private sealed class Writer : Grpc.Core.IServerStreamWriter<VideoFrame>
    {
        private readonly List<VideoFrame> _frames = [];

        public List<VideoFrame> Frames
        {
            get
            {
                lock (_frames)
                {
                    return [.. _frames];
                }
            }
        }

        public Grpc.Core.WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(VideoFrame message) => WriteAsync(message, CancellationToken.None);

        public Task WriteAsync(VideoFrame message, CancellationToken cancellationToken)
        {
            lock (_frames)
            {
                _frames.Add(message);
            }

            return Task.CompletedTask;
        }
    }
}
