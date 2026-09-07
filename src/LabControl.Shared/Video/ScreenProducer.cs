using Google.Protobuf;
using LabControl.Shared.Protocol;
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
/// </summary>
public sealed class ScreenProducer : IAsyncDisposable
{
    /// <summary>Offers a frame to the wire; <c>false</c> means "not taken, try again with what you still owe".</summary>
    public delegate ValueTask<bool> FrameSink(VideoFrame frame, CancellationToken token);

    /// <summary>An event for the console (relayed as-is by whoever hosts the producer).</summary>
    public delegate void Reporter(Event.Types.Severity severity, string code, string message);

    private readonly string _name;
    private readonly Func<IScreenSource> _open;
    private readonly FrameSink _sink;
    private readonly Reporter _report;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    private CancellationTokenSource? _running;
    private Task? _loop;
    private VideoSettings? _settings;
    private int _settingsVersion;
    private bool _keyframeWanted;
    private string? _sourceKind;

    public ScreenProducer(string name, Func<IScreenSource> open, FrameSink sink, Reporter report, ILogger log, TimeProvider? time = null)
    {
        _name = name;
        _open = open;
        _sink = sink;
        _report = report;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

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
    /// every tick — and starts with a keyframe.
    /// </summary>
    public void Apply(VideoControl? control)
    {
        lock (_lock)
        {
            if (control is not { Active: true })
            {
                Stop();
                return;
            }

            var settings = VideoSettings.From(control);
            _keyframeWanted |= control.RequestKeyframe;

            if (_settings != settings)
            {
                _settings = settings;
                _settingsVersion++;
                _keyframeWanted = true;
            }

            if (_running is null)
            {
                _running = new CancellationTokenSource();
                _loop = Task.Run(() => LoopAsync(_running.Token));
                _log.LogInformation("{Pc}: streaming {Mode} at {Fps} fps, q{Quality}, ≤ {Kbit} kbit/s",
                    _name, settings.Mode, settings.FramesPerSecond, settings.Quality, settings.MaxBitsPerSecond / 1024);
            }
            else
            {
                _log.LogInformation("{Pc}: video now {Mode} at {Fps} fps, q{Quality}", _name, settings.Mode, settings.FramesPerSecond, settings.Quality);
            }
        }
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
                lock (_lock)
                {
                    settings = _settings!;
                    version = _settingsVersion;
                    keyframeWanted = _keyframeWanted;
                }

                if (state.SettingsVersion != version)
                {
                    // A new control: a new budget, and a keyframe so the console starts clean.
                    state.SettingsVersion = version;
                    state.Pacer = new VideoPacer(settings.MaxBitsPerSecond, _time.GetUtcNow());
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

                if (video is not null)
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

                    if (taken)
                    {
                        FramesSent++;
                        state.Pending.Clear();
                        if (isKeyframe)
                        {
                            state.LastKeyframe = now;
                        }

                        lock (_lock)
                        {
                            // Consumed — unless a newer request arrived while this one was on its way.
                            if (keyframeWanted && _settingsVersion == version)
                            {
                                _keyframeWanted = false;
                            }
                        }

                        var wait = state.Pacer!.Account(video.Jpeg.Length, now);
                        if (wait > TimeSpan.Zero)
                        {
                            await Task.Delay(wait, _time, token);
                        }
                    }
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
            Report(Event.Types.Severity.Error, "capture.failed", $"Screen capture stopped: {ex.Message}");
        }
        finally
        {
            CloseSource(ref source);
        }
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
                Report(Event.Types.Severity.Info, "capture.recovered", $"Screen capture is back ({source.Kind}).");
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
        Report(Event.Types.Severity.Warning, $"capture.{reason}", $"Cannot capture the screen: {message}");
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
            };
        }

        var keyframe = keyframeWanted || _time.GetUtcNow() - state.LastKeyframe >= Defaults.KeyframeInterval;
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
                Jpeg = ByteString.CopyFrom(JpegCodec.Encode(frame.Pixels, width, height, frame.RowBytes, whole, settings.Quality)),
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
            Jpeg = ByteString.CopyFrom(JpegCodec.Encode(frame.Pixels, width, height, frame.RowBytes, box, settings.Quality)),
        };
        delta.Dirty.AddRange(state.Pending);
        return delta;
    }

    /// <summary>What one run of the loop carries between ticks.</summary>
    private sealed class LoopState
    {
        public List<Rect> Pending { get; } = [];

        public int Width { get; set; }

        public int Height { get; set; }

        public DateTimeOffset LastKeyframe { get; set; } = DateTimeOffset.MinValue;

        public VideoPacer? Pacer { get; set; }

        public int SettingsVersion { get; set; } = -1;

        public long OpenedAt { get; set; }

        public bool FirstFrameWarned { get; set; }

        /// <summary>A fresh source: nothing is owed, nothing is known about the screen.</summary>
        public void Reset()
        {
            Pending.Clear();
            Width = 0;
            Height = 0;
            LastKeyframe = DateTimeOffset.MinValue;
            FirstFrameWarned = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_lock)
        {
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
