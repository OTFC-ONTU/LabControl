using Google.Protobuf;
using LabControl.Shared.Localization;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video.H264;
using Microsoft.Extensions.Logging;

namespace LabControl.Shared.Video;

/// <summary>
/// The producer half of the screen stream (PROTOCOL "Video", D-34, D-35), shared by the
/// session helper and the simulator: follows the console's <c>VideoControl</c>, captures
/// through an <see cref="IScreenSource"/> at the requested rate and turns what it sees into
/// <c>VideoFrame</c>s — thumbnails only when something changed, full mode as
/// dirty-rectangle deltas with a keyframe every <see cref="Defaults.KeyframeInterval"/> or
/// on request — honouring the bandwidth cap through <see cref="VideoPacer"/>. A frame the
/// sink refuses costs nothing: the dirty rectangles behind it stay owed and the next tick
/// re-encodes the current picture with the union (latest wins). A source that fails is
/// reported and reopened every <see cref="Defaults.CaptureRetryInterval"/> while video is
/// wanted; nothing here may throw out of the loop.
/// <para>
/// When the console asks for H.264 (the teacher's *Video*, D-91) full mode encodes every
/// picture whole through an <see cref="IH264Encoder"/> instead — an IDR where a JPEG keyframe
/// would be — and a frame the sink refuses is <b>held</b> and offered again, never re-encoded,
/// because the encoder has already moved past it. An encoder that cannot be made or fails
/// turns the stream back to JPEG until the next control, with one <c>video.h264_unavailable</c>.
/// </para>
/// </summary>
public sealed class ScreenProducer : IAsyncDisposable
{
    /// <summary>Offers a frame to the wire; <c>false</c> means "not taken, try again with what you still owe".</summary>
    public delegate ValueTask<bool> FrameSink(VideoFrame frame, CancellationToken token);

    /// <summary>An event for the console (relayed as-is by whoever hosts the producer).</summary>
    public delegate void Reporter(Event.Types.Severity severity, string code, string message);

    /// <summary>Makes an H.264 encoder for a picture size under the settings; throws <see cref="H264Exception"/> when there is none.</summary>
    public delegate IH264Encoder H264Factory(int width, int height, VideoSettings settings);

    /// <summary>A held H.264 frame older than this is dropped for a fresh IDR: the picture is stale and whoever refused it is far behind.</summary>
    private static readonly TimeSpan HeldFrameLimit = TimeSpan.FromSeconds(2);

    private readonly string _name;
    private readonly Func<IScreenSource> _open;
    private readonly FrameSink _sink;
    private readonly Reporter _report;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly H264Factory _h264;
    private readonly Lock _lock = new();

    private CancellationTokenSource? _running;
    private Task? _loop;
    private VideoSettings? _settings;
    private int _settingsVersion;
    private bool _keyframeWanted;
    private long _keyframeRequests;
    private string? _sourceKind;
    private bool _disposed;

    public ScreenProducer(string name, Func<IScreenSource> open, FrameSink sink, Reporter report, ILogger log, TimeProvider? time = null, H264Factory? h264 = null)
    {
        _name = name;
        _open = open;
        _sink = sink;
        _report = report;
        _log = log;
        _time = time ?? TimeProvider.System;
        _h264 = h264 ?? PlatformEncoder;
    }

    /// <summary>The platform's encoder (<see cref="H264Codecs"/>), aiming under the pacer's cap.</summary>
    public static IH264Encoder PlatformEncoder(int width, int height, VideoSettings settings) =>
        H264Codecs.CreateEncoder(width, height, settings.FramesPerSecond, (int)(settings.MaxBitsPerSecond * Defaults.H264TargetShareOfCap));

    /// <summary>The codec of the last frame the sink took (<c>"jpeg"</c> or <c>"h264"</c>); <c>null</c> before the first.</summary>
    public string? LastCodec { get; private set; }

    /// <summary>The mode being streamed, for a status line; <c>null</c> when off.</summary>
    public VideoMode? Streaming
    {
        get
        {
            lock (_lock)
            {
                return _running is null ? null : _settings?.Mode;
            }
        }
    }

    /// <summary>How the screen is being captured right now ("dxgi", "gdi", "simulated"); <c>null</c> when no source is open.</summary>
    public string? SourceKind
    {
        get
        {
            lock (_lock)
            {
                return _sourceKind;
            }
        }
    }

    /// <summary>Frames the sink accepted, for the tests and a status line.</summary>
    public long FramesSent { get; private set; }

    /// <summary>
    /// The console's word, or <c>null</c> when the link ended: starts, stops or retunes the
    /// loop. A change of mode or rate keeps the source open — the loop reads its settings on
    /// every tick — and starts with a keyframe. Once disposed it does nothing: a caller that
    /// raced the disposal (the broadcast hub asking for a keyframe from inside the loop's own
    /// sink) must not start a loop nobody will stop (D-71 item 12).
    /// </summary>
    public void Apply(VideoControl? control)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (control is not { Active: true })
            {
                Stop();
                return;
            }

            var settings = VideoSettings.From(control);
            if (control.RequestKeyframe)
            {
                WantKeyframe();
            }

            if (_settings != settings)
            {
                _settings = settings;
                _settingsVersion++;
                WantKeyframe();
            }

            if (_running is null)
            {
                _running = new CancellationTokenSource();
                _loop = Task.Run(() => LoopAsync(_running.Token));
                _log.LogInformation("{Pc}: streaming {Mode} at {Fps} fps, {Quality}, ≤ {Kbit} kbit/s",
                    _name, settings.Mode, settings.FramesPerSecond, settings.IsAdaptive ? "auto quality" : $"q{settings.Quality}", settings.MaxBitsPerSecond / 1024);
            }
            else
            {
                _log.LogInformation("{Pc}: video now {Mode} at {Fps} fps, {Quality}", _name, settings.Mode, settings.FramesPerSecond, settings.IsAdaptive ? "auto quality" : $"q{settings.Quality}");
            }
        }
    }

    /// <summary>
    /// The console asks for a keyframe. Every request is counted, so one that arrives while a
    /// keyframe is on its way is not taken as served by it. The loop's own wants (a reopened
    /// source, a new resolution) are served by the frame in hand and are not counted. Under <see cref="_lock"/>.
    /// </summary>
    private void WantKeyframe()
    {
        _keyframeWanted = true;
        _keyframeRequests++;
    }

    private void Stop()
    {
        if (_running is null)
        {
            return;
        }

        _running.Cancel();
        _running.Dispose();
        _running = null;
        _settings = null;
        _log.LogInformation("{Pc}: video off", _name);
    }

    // ------------------------------------------------------------------ the loop

    private async Task LoopAsync(CancellationToken token)
    {
        IScreenSource? source = null;
        var state = new LoopState();
        string? lastFailure = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var tickStarted = _time.GetTimestamp();

                VideoSettings settings;
                int version;
                bool keyframeWanted;
                long keyframeRequests;
                lock (_lock)
                {
                    settings = _settings!;
                    version = _settingsVersion;
                    keyframeWanted = _keyframeWanted;
                    keyframeRequests = _keyframeRequests;
                }

                if (state.SettingsVersion != version)
                {
                    // A new control: a new budget, and a keyframe so the console starts clean.
                    state.SettingsVersion = version;
                    state.Pacer = new VideoPacer(settings.MaxBitsPerSecond, _time.GetUtcNow());
                    state.Quality = Defaults.FullJpegQuality;
                    state.FreeFrames = 0;
                    state.DropEncoder();
                    state.H264Failed = false;
                    state.Held = null;
                }

                if (state.Held is { } held)
                {
                    // An H.264 frame the sink refused: nothing new is encoded until it is taken.
                    if (_time.GetUtcNow() - state.HeldSince > HeldFrameLimit)
                    {
                        state.Held = null;
                        state.DropEncoder();
                        lock (_lock)
                        {
                            _keyframeWanted = true;
                        }
                    }
                    else
                    {
                        if (await OfferAsync(held, settings, keyframeWanted, keyframeRequests, held.Keyframe, state, token))
                        {
                            state.Held = null;
                        }

                        await WaitRestOfTickAsync(settings, tickStarted, token);
                        continue;
                    }
                }

                if (source is null)
                {
                    source = TryOpen(ref lastFailure, state);
                    if (source is null)
                    {
                        await Task.Delay(Defaults.CaptureRetryInterval, _time, token);
                        continue;
                    }
                }

                IScreenFrame? frame;
                try
                {
                    frame = source.Acquire(Defaults.CaptureAcquireTimeout);
                }
                catch (ScreenCaptureException ex)
                {
                    Fail(ex.Reason, ex.Message, ref lastFailure);
                    CloseSource(ref source);
                    state.Reset();
                    await Task.Delay(Defaults.CaptureRetryInterval, _time, token);
                    continue;
                }

                if (frame is null)
                {
                    if (!state.FirstFrameWarned && _time.GetElapsedTime(state.OpenedAt) > Defaults.CaptureFirstFrameWarning)
                    {
                        state.FirstFrameWarned = true;
                        _log.LogWarning("{Pc}: the {Kind} screen has delivered no picture for {Seconds:0} s", _name, source.Kind, Defaults.CaptureFirstFrameWarning.TotalSeconds);
                    }

                    await WaitRestOfTickAsync(settings, tickStarted, token);
                    continue;
                }

                VideoFrame? video;
                bool isKeyframe;
                using (frame)
                {
                    video = Produce(frame, settings, keyframeWanted, state, out isKeyframe);
                }

                if (video is not null && !await OfferAsync(video, settings, keyframeWanted, keyframeRequests, isKeyframe, state, token)
                    && video.Codec == Defaults.VideoCodecH264)
                {
                    state.Held = video;
                    state.HeldSince = _time.GetUtcNow();
                }

                await WaitRestOfTickAsync(settings, tickStarted, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Pc}: the screen producer stopped: {Message}", _name, ex.Message);
            Report(Event.Types.Severity.Error, "capture.failed", PcText.Get("CaptureFailed", ex.Message));
        }
        finally
        {
            CloseSource(ref source);
            state.DropEncoder();
        }
    }

    /// <summary>Offers one frame to the sink and, when it is taken, does the bookkeeping: what is owed, the keyframe, the pacer, auto quality.</summary>
    private async Task<bool> OfferAsync(VideoFrame video, VideoSettings settings, bool keyframeWanted, long keyframeRequests, bool isKeyframe, LoopState state, CancellationToken token)
    {
        var now = _time.GetUtcNow();
        bool taken;
        try
        {
            taken = await _sink(video, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The wire is the host's problem; keep what is owed and try again later.
            _log.LogDebug("{Pc}: the frame sink failed: {Message}", _name, ex.Message);
            taken = false;
        }

        if (!taken)
        {
            return false;
        }

        FramesSent++;
        LastCodec = video.Codec;
        state.Pending.Clear();
        if (isKeyframe)
        {
            state.LastKeyframe = now;
        }

        lock (_lock)
        {
            // Consumed — unless a newer request arrived while this one was on its way,
            // even one that changed no setting (a PC joining a broadcast, D-71 item 13).
            if (keyframeWanted && isKeyframe && _keyframeRequests == keyframeRequests)
            {
                _keyframeWanted = false;
            }
        }

        var wait = state.Pacer!.Account(video.Jpeg.Length, now);
        if (settings.IsAdaptive && video.Codec != Defaults.VideoCodecH264 && !isKeyframe && !state.AfterKeyframe)
        {
            // The delta right behind a keyframe waits for the keyframe's bytes,
            // not its own: it does not vote either.
            Adapt(state, wait > TimeSpan.Zero);
        }

        state.AfterKeyframe = isKeyframe;

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, token);
        }

        return true;
    }

    private IScreenSource? TryOpen(ref string? lastFailure, LoopState state)
    {
        try
        {
            var source = _open();
            state.Reset();
            state.OpenedAt = _time.GetTimestamp();
            lock (_lock)
            {
                _sourceKind = source.Kind;
                _keyframeWanted = true;
            }

            _log.LogInformation("{Pc}: capturing the screen with {Kind}", _name, source.Kind);
            if (lastFailure is not null)
            {
                Report(Event.Types.Severity.Info, "capture.recovered", PcText.Get("CaptureRecovered", source.Kind));
                lastFailure = null;
            }

            return source;
        }
        catch (ScreenCaptureException ex)
        {
            Fail(ex.Reason, ex.Message, ref lastFailure);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail("open_failed", ex.Message, ref lastFailure);
            return null;
        }
    }

    private void Fail(string reason, string message, ref string? lastFailure)
    {
        var text = $"[{reason}] {message}";
        if (text == lastFailure)
        {
            return;
        }

        lastFailure = text;
        _log.LogWarning("{Pc}: cannot capture the screen ({Reason}): {Message}", _name, reason, message);
        Report(Event.Types.Severity.Warning, $"capture.{reason}", PcText.Get("CaptureProblem", message));
    }

    private void CloseSource(ref IScreenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogDebug("{Pc}: closing the {Kind} screen: {Message}", _name, source.Kind, ex.Message);
        }

        source = null;
        lock (_lock)
        {
            _sourceKind = null;
        }
    }

    private void Report(Event.Types.Severity severity, string code, string message)
    {
        try
        {
            _report(severity, code, message);
        }
        catch (Exception ex)
        {
            _log.LogDebug("{Pc}: reporting {Code}: {Message}", _name, code, ex.Message);
        }
    }

    private async Task WaitRestOfTickAsync(VideoSettings settings, long tickStarted, CancellationToken token)
    {
        var remaining = settings.FrameInterval - _time.GetElapsedTime(tickStarted);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, _time, token);
        }
    }

    // ------------------------------------------------------------------ frames

    /// <summary>
    /// Turns the current picture and what is owed into the frame the mode calls for, or
    /// <c>null</c> when there is nothing to say. Pure apart from <paramref name="state"/>.
    /// </summary>
    private VideoFrame? Produce(IScreenFrame frame, VideoSettings settings, bool keyframeWanted, LoopState state, out bool isKeyframe)
    {
        isKeyframe = false;
        var width = frame.Width;
        var height = frame.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        if (width != state.Width || height != state.Height)
        {
            // A new resolution: nothing owed applies any more, and the console needs a keyframe.
            state.Width = width;
            state.Height = height;
            state.Pending.Clear();
            keyframeWanted = true;
            lock (_lock)
            {
                _keyframeWanted = true;
            }
        }

        if (frame.Dirty.Count > 0)
        {
            state.Pending.AddRange(frame.Dirty
                .Select(r => VideoGeometry.AlignToTiles(r, width, height))
                .Where(r => r.Width > 0 && r.Height > 0));
            var merged = VideoGeometry.Merge(state.Pending);
            state.Pending.Clear();
            state.Pending.AddRange(merged);
        }

        if (settings.Mode == VideoMode.Thumbnail)
        {
            if (state.Pending.Count == 0 && !keyframeWanted)
            {
                return null;
            }

            var (thumbWidth, thumbHeight) = VideoGeometry.ThumbnailSize(width, height);
            isKeyframe = true;
            return new VideoFrame
            {
                Width = width,
                Height = height,
                Mode = VideoMode.Thumbnail,
                Keyframe = true,
                Codec = Defaults.VideoCodecJpeg,
                Jpeg = ByteString.CopyFrom(JpegCodec.EncodeScaled(frame.Pixels, width, height, frame.RowBytes, thumbWidth, thumbHeight, settings.Quality)),
                Quality = settings.Quality,
            };
        }

        var keyframe = keyframeWanted || _time.GetUtcNow() - state.LastKeyframe >= Defaults.KeyframeInterval;
        if (settings.WantsH264 && !state.H264Failed)
        {
            var h264 = ProduceH264(frame, settings, keyframe, state, out isKeyframe);
            if (h264 is not null || !state.H264Failed)
            {
                return h264;
            }

            // The encoder just failed: this picture goes out as a JPEG keyframe.
            keyframe = true;
        }

        var quality = state.QualityFor(settings);
        if (keyframe)
        {
            isKeyframe = true;
            var whole = VideoGeometry.Whole(width, height);
            var full = new VideoFrame
            {
                Width = width,
                Height = height,
                Mode = VideoMode.Full,
                Keyframe = true,
                Codec = Defaults.VideoCodecJpeg,
                Jpeg = ByteString.CopyFrom(JpegCodec.Encode(frame.Pixels, width, height, frame.RowBytes, whole, quality)),
                Quality = quality,
            };
            full.Dirty.Add(whole);
            return full;
        }

        if (state.Pending.Count == 0)
        {
            return null;
        }

        var box = VideoGeometry.BoundingBox(state.Pending);
        var delta = new VideoFrame
        {
            Width = width,
            Height = height,
            Mode = VideoMode.Full,
            Keyframe = false,
            Codec = Defaults.VideoCodecJpeg,
            Jpeg = ByteString.CopyFrom(JpegCodec.Encode(frame.Pixels, width, height, frame.RowBytes, box, quality)),
            Quality = quality,
        };
        delta.Dirty.AddRange(state.Pending);
        return delta;
    }

    /// <summary>
    /// The picture as one H.264 access unit (D-91): the whole screen, rounded down to an even
    /// size, an IDR when a keyframe is due. Nothing new and no keyframe due is no frame, as
    /// for JPEG. A failure marks the stream JPEG-only until the next control.
    /// </summary>
    private VideoFrame? ProduceH264(IScreenFrame frame, VideoSettings settings, bool keyframe, LoopState state, out bool isKeyframe)
    {
        isKeyframe = false;
        var width = frame.Width & ~1;
        var height = frame.Height & ~1;
        if (width < 16 || height < 16)
        {
            FailH264(state, $"the screen is too small ({frame.Width}×{frame.Height})");
            return null;
        }

        if (!keyframe && state.Pending.Count == 0 && state.Encoder is not null)
        {
            return null;
        }

        try
        {
            if (state.Encoder is null || state.Encoder.Width != width || state.Encoder.Height != height)
            {
                state.DropEncoder();
                state.Encoder = _h264(width, height, settings);
                keyframe = true;
                _log.LogInformation("{Pc}: encoding H.264 {Width}×{Height} with {Kind}", _name, width, height, state.Encoder.Kind);
            }

            var unit = state.Encoder.Encode(frame.Pixels, frame.RowBytes, keyframe, out isKeyframe);
            if (keyframe && unit is not null && !isKeyframe)
            {
                // The encoder ignored the request (no ICodecAPI): a fresh one starts with an IDR.
                state.DropEncoder();
                state.Encoder = _h264(width, height, settings);
                unit = state.Encoder.Encode(frame.Pixels, frame.RowBytes, true, out isKeyframe);
            }

            if (unit is null || unit.Length == 0)
            {
                return null;
            }

            if (unit.Length > Defaults.VideoFrameMaxBytes)
            {
                // Never sent; the next picture starts again from an IDR.
                state.DropEncoder();
                isKeyframe = false;
                return null;
            }

            return new VideoFrame
            {
                Width = width,
                Height = height,
                Mode = VideoMode.Full,
                Keyframe = isKeyframe,
                Codec = Defaults.VideoCodecH264,
                Jpeg = ByteString.CopyFrom(unit),
            };
        }
        catch (Exception ex) when (ex is H264Exception or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            isKeyframe = false;
            FailH264(state, ex.Message);
            return null;
        }
    }

    private void FailH264(LoopState state, string why)
    {
        state.DropEncoder();
        state.H264Failed = true;
        _log.LogWarning("{Pc}: no H.264, sending JPEG: {Why}", _name, why);
        Report(Event.Types.Severity.Info, "video.h264_unavailable", PcText.Get("H264Unavailable", why));
    }

    /// <summary>
    /// Auto quality (D-37): a frame that had to wait for the cap costs one step of quality,
    /// a run of frames that did not earns one back — so a scrolling page drops towards
    /// <see cref="Defaults.FullJpegQualityMin"/> and a still desktop climbs back to
    /// <see cref="Defaults.FullJpegQuality"/>. Keyframes are big by nature and do not vote.
    /// </summary>
    private void Adapt(LoopState state, bool waited)
    {
        if (waited)
        {
            state.FreeFrames = 0;
            var lowered = Math.Max(Defaults.FullJpegQualityMin, state.Quality - Defaults.AutoQualityStepDown);
            if (lowered != state.Quality)
            {
                state.Quality = lowered;
                _log.LogDebug("{Pc}: auto quality down to q{Quality}", _name, lowered);
            }

            return;
        }

        if (++state.FreeFrames < Defaults.AutoQualityFreeFrames)
        {
            return;
        }

        state.FreeFrames = 0;
        var raised = Math.Min(Defaults.FullJpegQuality, state.Quality + Defaults.AutoQualityStepUp);
        if (raised != state.Quality)
        {
            state.Quality = raised;
            _log.LogDebug("{Pc}: auto quality up to q{Quality}", _name, raised);
        }
    }

    /// <summary>What one run of the loop carries between ticks.</summary>
    private sealed class LoopState
    {
        /// <summary>The quality auto mode is at right now; a manual control ignores it.</summary>
        public int Quality { get; set; } = Defaults.FullJpegQuality;

        /// <summary>Frames in a row that did not wait for the cap (auto mode).</summary>
        public int FreeFrames { get; set; }

        /// <summary>The last frame sent was a keyframe, so the next one's wait is the keyframe's doing.</summary>
        public bool AfterKeyframe { get; set; }

        /// <summary>What to encode the next full-mode frame at.</summary>
        public int QualityFor(VideoSettings settings) => settings.IsAdaptive ? Quality : settings.Quality;

        public List<Rect> Pending { get; } = [];

        public int Width { get; set; }

        public int Height { get; set; }

        public DateTimeOffset LastKeyframe { get; set; } = DateTimeOffset.MinValue;

        public VideoPacer? Pacer { get; set; }

        public int SettingsVersion { get; set; } = -1;

        public long OpenedAt { get; set; }

        public bool FirstFrameWarned { get; set; }

        /// <summary>The H.264 encoder for the current size, while the console asks for H.264.</summary>
        public IH264Encoder? Encoder { get; set; }

        /// <summary>H.264 failed under the current control: JPEG until the next one.</summary>
        public bool H264Failed { get; set; }

        /// <summary>An H.264 frame the sink refused, offered again before anything new.</summary>
        public VideoFrame? Held { get; set; }

        public DateTimeOffset HeldSince { get; set; }

        public void DropEncoder()
        {
            try
            {
                Encoder?.Dispose();
            }
            catch (Exception)
            {
            }

            Encoder = null;
        }

        /// <summary>A fresh source: nothing is owed, nothing is known about the screen.</summary>
        public void Reset()
        {
            Pending.Clear();
            Width = 0;
            Height = 0;
            LastKeyframe = DateTimeOffset.MinValue;
            FirstFrameWarned = false;
            Held = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_lock)
        {
            _disposed = true;
            Stop();
            loop = _loop;
            _loop = null;
        }

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (Exception)
            {
            }
        }
    }
}
