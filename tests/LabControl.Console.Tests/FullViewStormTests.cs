using System.Diagnostics;
using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// M7 robustness pass, many simultaneous full-view requests (ROADMAP M7): the teacher opens
/// the single-PC window on many PCs of a full room at once, then closes them. The PCs are
/// simulated ones running the real producer (<c>ScreenProducer</c> over <c>FakeScreen</c>),
/// so the frames, pacing and keyframes are those of a real PC (PROTOCOL "Video"). Each PC
/// must stay within the documented full-mode cap, the mosaic must keep moving, closing a
/// window must return its PC to thumbnails with nothing left streaming, and opening and
/// closing again and again must leave nothing behind.
/// </summary>
[Collection(nameof(RobustnessCollection))]
public sealed class FullViewStormTests
{
    /// <summary>How many of the room's windows the teacher opens at once: half the room.</summary>
    private static readonly int Opened = Defaults.MaxStudentPcs / 2;

    [Fact]
    public async Task Half_the_room_in_full_view_at_once_stays_within_the_cap_and_closing_returns_every_pc_to_thumbnails()
    {
        await using var room = await Room.OpenAsync();
        var full = room.Machines.Take(Opened).ToArray();
        var mosaicOnly = room.Machines.Skip(Opened).ToArray();

        // Every PC streams its thumbnail before anything is opened.
        Assert.True(await Wait.UntilAsync(() => room.Machines.All(m => room.Screen(m).Thumbnail.HasFrame), TimeSpan.FromSeconds(30)), "not every PC sent a thumbnail");

        room.Open(full);

        // Every opened PC switches to full mode under the documented cap and delivers a picture.
        Assert.True(await Wait.UntilAsync(() => full.All(m => m.Link.VideoControl is { Mode: VideoMode.Full } && room.Screen(m).Full.HasFrame), TimeSpan.FromSeconds(30)),
            "full view did not start on: " + string.Join(", ", full.Where(m => !room.Screen(m).Full.HasFrame).Select(m => m.Name)));
        Assert.All(full, m => Assert.Equal(Defaults.FullModeBitsPerSecond, m.Link.VideoControl!.MaxBitsPerSecond));
        Assert.All(full, m => Assert.Equal(VideoMode.Full, room.Console.Session.FindLinked(m.AgentId)!.RequestedVideo));
        Assert.All(mosaicOnly, m => Assert.Equal(VideoMode.Thumbnail, m.Link.VideoControl!.Mode));

        // Measured over a few seconds: each full stream stays inside its token bucket (the cap,
        // plus one second of burst), and the mosaic keeps moving for every PC — the ones in
        // full view through the keyframes the console scales into their thumbnails.
        var before = room.Machines.ToDictionary(m => m.AgentId, m => (room.Screen(m).BytesReceived, room.Screen(m).Thumbnail.Version, room.Screen(m).Full.Version));
        var clock = Stopwatch.StartNew();
        await Task.Delay(Defaults.KeyframeInterval + TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var seconds = clock.Elapsed.TotalSeconds;

        foreach (var machine in room.Machines)
        {
            var screen = room.Screen(machine);
            var (bytes, thumbnail, picture) = before[machine.AgentId];
            var allowed = Defaults.FullModeBitsPerSecond / 8.0 * (seconds + 1);
            Assert.True(screen.BytesReceived - bytes <= allowed, $"{machine.Name} sent {screen.BytesReceived - bytes} bytes in {seconds:0.0} s, above its cap");
        }

        // The tiles of PCs in full view move only with the keyframe every KeyframeInterval, so on a
        // busy two-core CI runner one may land a moment after the window: each gets two more
        // intervals rather than a one-second margin (it failed both CI attempts on 2026-10-29).
        foreach (var machine in room.Machines)
        {
            var screen = room.Screen(machine);
            var (_, thumbnail, picture) = before[machine.AgentId];
            Assert.True(await Wait.UntilAsync(() => screen.Thumbnail.Version > thumbnail, Defaults.KeyframeInterval * 2),
                $"{machine.Name}'s mosaic tile did not move in {clock.Elapsed.TotalSeconds:0.0} s");
            if (full.Contains(machine))
            {
                Assert.True(screen.Full.Version > picture, $"{machine.Name}'s full view did not move in {seconds:0.0} s");
            }
        }

        Assert.All(room.Machines, m => Assert.Equal(0, room.Screen(m).FramesRejected));
        var rates = full.Select(m => (room.Screen(m).BytesReceived - before[m.AgentId].BytesReceived) * 8 / seconds / 1_000_000).ToArray();
        TestContext.Current.SendDiagnosticMessage($"{Opened} full views: {rates.Max():0.00} Mbit/s the busiest, {rates.Sum():0.00} Mbit/s together, cap {Defaults.FullModeBitsPerSecond / 1_000_000.0:0.0} Mbit/s each");

        // The windows close: back to thumbnails on every PC, nothing streams in full, the big pictures are gone.
        room.CloseAll();
        await room.AssertAllThumbnailsAsync();
    }

    [Fact]
    public async Task Opening_and_closing_many_full_views_again_and_again_leaves_nothing_behind()
    {
        await using var room = await Room.OpenAsync();
        var full = room.Machines.Take(Opened).ToArray();
        Assert.True(await Wait.UntilAsync(() => room.Machines.All(m => room.Screen(m).Thumbnail.HasFrame), TimeSpan.FromSeconds(30)));
        var baseline = room.Measure();

        for (var round = 1; round <= 4; round++)
        {
            room.Open(full);
            Assert.True(await Wait.UntilAsync(() => full.All(m => room.Screen(m).Full.HasFrame), TimeSpan.FromSeconds(30)), $"round {round}: full view did not start everywhere");

            // Half of the rounds close the windows before the first full picture of some PCs
            // could even arrive: a window shut at once must not leave its PC streaming.
            room.CloseAll();
            await room.AssertAllThumbnailsAsync();
            room.Open(full);
            room.CloseAll();
            await room.AssertAllThumbnailsAsync();
        }

        var after = room.Measure();
        TestContext.Current.SendDiagnosticMessage($"full views: before {baseline}, after {after}");
        Assert.Equal(baseline.ScreenHandlers, after.ScreenHandlers);
        Assert.Equal(0, after.OpenWindows);
        Assert.Equal(Defaults.MaxStudentPcs, after.Screens);
        Assert.True(after.Threads <= baseline.Threads + 40, $"threads grew from {baseline.Threads} to {after.Threads}");
    }

    /// <summary>
    /// A 1080p keyframe is a few hundred KB, a tenth of a second on the wire at the full-mode
    /// cap: the window can close while one is on its way. Arriving after the close it must not
    /// rebuild the native-resolution picture the close released, nor stand in for the thumbnail
    /// when the window opens again — that window shows the thumbnail until a fresh keyframe.
    /// </summary>
    [Fact]
    public async Task A_full_keyframe_still_on_the_wire_when_the_window_closes_is_not_kept()
    {
        await using var console = await TestConsole.CreateLabAsync();
        await using var pc = TestAgent.InstallEnrolled(console, 5, Defaults.AgentCertificateLifetime).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.VideoControl is { Active: true, Mode: VideoMode.Thumbnail }));
        var screen = console.Session.Screens.Get(pc.AgentId);
        Assert.True(await Wait.UntilAsync(() => pc.Link.TryPushVideo(Frame(VideoMode.Thumbnail, 320, 180, SkiaSharp.SKColors.Green))));
        Assert.True(await Wait.UntilAsync(() => screen.Thumbnail.HasFrame));

        // The window opens, the first keyframe arrives, the window closes.
        var tile = new MachineTileViewModel(console.Session.Registry.Document.Machines.Single(m => m.AgentId == pc.AgentId), screen);
        var window = new ScreenViewModel(console.Session, tile, action => action());
        window.Open();
        Assert.True(await Wait.UntilAsync(() => pc.Link.VideoControl is { Mode: VideoMode.Full }));
        Assert.True(await Wait.UntilAsync(() => pc.Link.TryPushVideo(Frame(VideoMode.Full, 640, 384, SkiaSharp.SKColors.Blue))));
        Assert.True(await Wait.UntilAsync(() => screen.Full.HasFrame));
        window.Close();
        Assert.False(screen.Full.HasFrame);
        Assert.Same(screen.Thumbnail, window.Image);
        Assert.True(await Wait.UntilAsync(() => pc.Link.VideoControl is { Mode: VideoMode.Thumbnail }));

        // The keyframe the PC had already sent lands now.
        var received = screen.FramesReceived;
        Assert.True(await Wait.UntilAsync(() => pc.Link.TryPushVideo(Frame(VideoMode.Full, 640, 384, SkiaSharp.SKColors.Red))));
        Assert.True(await Wait.UntilAsync(() => screen.FramesReceived > received));
        Assert.False(screen.Full.HasFrame, "a full frame that arrived after the window closed rebuilt the full picture");
        Assert.Equal(0, screen.FramesRejected);

        // Opened again: the thumbnail stands in until the PC's new keyframe, never a stale or empty full picture.
        var again = new ScreenViewModel(console.Session, tile, action => action());
        again.Open();
        Assert.Same(screen.Thumbnail, again.Image);
        again.Close();
    }

    private static VideoFrame Frame(VideoMode mode, int width, int height, SkiaSharp.SKColor colour)
    {
        using var picture = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, LabControl.Shared.Video.JpegCodec.PixelFormat, SkiaSharp.SKAlphaType.Premul));
        picture.Erase(colour);
        var frame = new VideoFrame
        {
            Mode = mode,
            Width = mode == VideoMode.Full ? width : 1920,
            Height = mode == VideoMode.Full ? height : 1080,
            Keyframe = true,
            Codec = Defaults.VideoCodecJpeg,
            Jpeg = Google.Protobuf.ByteString.CopyFrom(LabControl.Shared.Video.JpegCodec.Encode(picture, Defaults.FullJpegQuality)),
        };
        if (mode == VideoMode.Full)
        {
            frame.Dirty.Add(LabControl.Shared.Video.VideoGeometry.Whole(width, height));
        }

        return frame;
    }

    /// <summary>A console, its main view model and a full room of simulated PCs.</summary>
    private sealed class Room : IAsyncDisposable
    {
        // No dispatcher in a test: a lock stands in for the UI thread (as in OwnershipTests).
        private readonly Lock _ui = new();
        private readonly List<ScreenViewModel> _windows = [];

        private Room(TestConsole console, IReadOnlyList<FakeMachine> machines)
        {
            Console = console;
            Machines = machines;
            View = new MainViewModel(console.Session, console.Bootstrap, new OwnershipTests.SilentDialogs
            {
                // What ScreenWindow does when it opens (ScreenWindow.axaml.cs): the view model starts listening and asks for full mode.
                OnShowScreen = screen =>
                {
                    lock (_windows)
                    {
                        _windows.Add(screen);
                    }

                    screen.Closed += () =>
                    {
                        lock (_windows)
                        {
                            _windows.Remove(screen);
                        }
                    };
                    screen.Open();
                },
            }, action =>
            {
                lock (_ui)
                {
                    action();
                }
            });
        }

        public TestConsole Console { get; }

        public IReadOnlyList<FakeMachine> Machines { get; }

        public MainViewModel View { get; }

        public static async Task<Room> OpenAsync()
        {
            var console = await TestConsole.CreateLabAsync();
            var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: Defaults.MaxStudentPcs));
            var root = TestConsole.TempDirectory();
            var machines = new List<FakeMachine>();
            for (var n = 1; n <= Defaults.MaxStudentPcs; n++)
            {
                var store = FakeMachine.Install(Path.Combine(root, $"PC-{n:00}"), n, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
                machines.Add(new FakeMachine(store, null, TestLogging.Factory.CreateLogger($"PC-{n:00}")));
            }

            foreach (var machine in machines)
            {
                machine.Start();
            }

            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == Defaults.MaxStudentPcs, TimeSpan.FromSeconds(30)),
                $"only {console.Session.Linked.Count} of {Defaults.MaxStudentPcs} linked");
            return new Room(console, machines);
        }

        public AgentScreen Screen(FakeMachine machine) => Console.Session.Screens.Get(machine.AgentId);

        /// <summary>Double-clicks the tiles of <paramref name="machines"/>, one after the other as fast as the teacher can.</summary>
        public void Open(IEnumerable<FakeMachine> machines)
        {
            lock (_ui)
            {
                foreach (var machine in machines)
                {
                    var tile = View.Machines.Single(t => t.AgentId == machine.AgentId);
                    View.OpenScreenCommand.Execute(tile);
                }
            }
        }

        /// <summary>Closes every open single-PC window, as its close button would.</summary>
        public void CloseAll()
        {
            ScreenViewModel[] open;
            lock (_windows)
            {
                open = _windows.ToArray();
            }

            lock (_ui)
            {
                foreach (var window in open)
                {
                    window.Close();
                }
            }
        }

        /// <summary>
        /// Every PC streams thumbnails again: the PC's own producer says so, the console asked
        /// for nothing else, and no full picture is held or rebuilt by a frame that was still
        /// on its way when the window closed.
        /// </summary>
        public async Task AssertAllThumbnailsAsync()
        {
            var session = Console.Session;
            var back = await Wait.UntilAsync(() => Machines.All(m =>
                m.Link.VideoControl is { Mode: VideoMode.Thumbnail } &&
                m.Status.Contains("streaming thumbnail", StringComparison.Ordinal) &&
                Screen(m).RequestedMode == VideoMode.Thumbnail &&
                session.FindLinked(m.AgentId)?.RequestedVideo == VideoMode.Thumbnail), TimeSpan.FromSeconds(20));
            Assert.True(back, "still streaming full: " + string.Join(", ", Machines.Where(m => !m.Status.Contains("streaming thumbnail", StringComparison.Ordinal)).Select(m => $"{m.Name} ({m.Status})")));

            // Let anything that was in flight land, then nothing may have rebuilt a full picture.
            await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
            var held = Machines.Where(m => Screen(m).Full.HasFrame).Select(m => m.Name).ToArray();
            Assert.True(held.Length == 0, "a full picture is still held after its window closed: " + string.Join(", ", held));
            lock (_windows)
            {
                Assert.Empty(_windows);
            }
        }

        public Snapshot Measure()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            int windows;
            lock (_windows)
            {
                windows = _windows.Count;
            }

            return new Snapshot(process.Threads.Count, HandlerCount(Console.Session.Screens), windows, Console.Session.Screens.All.Count);
        }

        /// <summary>Subscribers of <see cref="ScreenStore.Updated"/>: every open window adds one and must take it away again.</summary>
        private static int HandlerCount(ScreenStore store)
        {
            var field = typeof(ScreenStore).GetField(nameof(ScreenStore.Updated), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (field?.GetValue(store) as Delegate)?.GetInvocationList().Length ?? 0;
        }

        public async ValueTask DisposeAsync()
        {
            lock (_ui)
            {
                View.Detach();
            }

            await Parallel.ForEachAsync(Machines, async (machine, _) => await machine.DisposeAsync());
            await Console.DisposeAsync();
        }
    }

    private readonly record struct Snapshot(int Threads, int ScreenHandlers, int OpenWindows, int Screens);
}
