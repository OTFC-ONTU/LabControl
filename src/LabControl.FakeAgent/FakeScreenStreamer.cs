using Google.Protobuf;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using Microsoft.Extensions.Logging;

namespace LabControl.FakeAgent;

/// <summary>
/// The producer half of M3 on a simulated PC: follows the console's <c>VideoControl</c>
/// through <see cref="AgentLink.VideoControlChanged"/>, draws the <see cref="FakeScreen"/>
/// at the requested rate and pushes frames through the link exactly as the session helper
/// will (PROTOCOL "Video", D-34) — thumbnails only when something changed, full mode as
/// dirty-rectangle deltas with a keyframe every <see cref="Defaults.KeyframeInterval"/> or
/// on request, and the bandwidth cap honoured through <see cref="VideoPacer"/>.
/// </summary>
internal sealed class FakeScreenStreamer : IAsyncDisposable
{
    private readonly AgentLink _link;
    private readonly FakeScreen _screen;
    private readonly ILogger _log;
    private readonly Lock _lock = new();

    private CancellationTokenSource? _running;
    private Task? _loop;
    private VideoSettings? _settings;
    private bool _keyframeWanted;

    public FakeScreenStreamer(AgentLink link, FakeScreen screen, ILogger log)
    {
        _link = link;
        _screen = screen;
        _log = log;
        link.VideoControlChanged += OnVideoControl;
    }

    /// <summary>The mode being streamed, for the simulator's status line; <c>null</c> when off.</summary>
    public VideoMode? Streaming => _settings?.Mode;

    private void OnVideoControl(VideoControl? control)
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

            if (_settings is not null && _settings != settings)
            {
                // A mode or rate change restarts the loop cleanly rather than mid-frame.
                Stop();
            }

            if (_running is null)
            {
                _settings = settings;
                _keyframeWanted = true;
                _running = new CancellationTokenSource();
                var token = _running.Token;
                _loop = Task.Run(() => LoopAsync(settings, token));
                _log.LogInformation("{Pc}: streaming {Mode} at {Fps} fps, q{Quality}, ≤ {Kbit} kbit/s (simulated {Width}×{Height})",
                    _link.Name, settings.Mode, settings.FramesPerSecond, settings.Quality, settings.MaxBitsPerSecond / 1024, _screen.Width, _screen.Height);
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
        _log.LogInformation("{Pc}: video off", _link.Name);
    }

    private async Task LoopAsync(VideoSettings settings, CancellationToken token)
    {
        var pacer = new VideoPacer(settings.MaxBitsPerSecond, DateTimeOffset.UtcNow);
        var lastKeyframe = DateTimeOffset.MinValue;
        VideoFrame? held = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var frame = held ?? Produce(settings, now, ref lastKeyframe);
                held = null;

                if (frame is not null)
                {
                    if (_link.TryPushVideo(frame))
                    {
                        _screen.MarkSent();
                        var wait = pacer.Account(frame.Jpeg.Length, now);
                        if (wait > TimeSpan.Zero)
                        {
                            await Task.Delay(wait, token);
                        }
                    }
                    else
                    {
                        // The wire is busy: keep the frame (and the dirty state behind it) for
                        // the next tick rather than dropping what changed.
                        held = frame;
                    }
                }

                await Task.Delay(settings.FrameInterval, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Pc}: the simulated screen failed: {Message}", _link.Name, ex.Message);
            _link.Report(Event.Types.Severity.Error, "capture.failed", $"The simulated screen stopped: {ex.Message}");
        }
    }

    private VideoFrame? Produce(VideoSettings settings, DateTimeOffset now, ref DateTimeOffset lastKeyframe)
    {
        IReadOnlyList<Rect> dirty;
        lock (_screen)
        {
            dirty = _screen.Advance(now);
        }

        if (settings.Mode == VideoMode.Thumbnail)
        {
            if (dirty.Count == 0)
            {
                return null;
            }

            using var thumbnail = _screen.Thumbnail();
            return new VideoFrame
            {
                Width = _screen.Width,
                Height = _screen.Height,
                Mode = VideoMode.Thumbnail,
                Keyframe = true,
                Codec = Defaults.VideoCodecJpeg,
                Jpeg = ByteString.CopyFrom(JpegCodec.Encode(thumbnail, settings.Quality)),
            };
        }

        bool keyframe;
        lock (_lock)
        {
            keyframe = _keyframeWanted || now - lastKeyframe >= Defaults.KeyframeInterval;
            _keyframeWanted = false;
        }

        if (keyframe)
        {
            lastKeyframe = now;
            var frame = new VideoFrame
            {
                Width = _screen.Width,
                Height = _screen.Height,
                Mode = VideoMode.Full,
                Keyframe = true,
                Codec = Defaults.VideoCodecJpeg,
                Jpeg = ByteString.CopyFrom(JpegCodec.Encode(_screen.Bitmap, settings.Quality)),
            };
            frame.Dirty.Add(VideoGeometry.Whole(_screen.Width, _screen.Height));
            return frame;
        }

        if (dirty.Count == 0)
        {
            return null;
        }

        var box = VideoGeometry.BoundingBox(dirty);
        var delta = new VideoFrame
        {
            Width = _screen.Width,
            Height = _screen.Height,
            Mode = VideoMode.Full,
            Keyframe = false,
            Codec = Defaults.VideoCodecJpeg,
            Jpeg = ByteString.CopyFrom(JpegCodec.Encode(_screen.Bitmap, box, settings.Quality)),
        };
        delta.Dirty.AddRange(dirty);
        return delta;
    }

    public async ValueTask DisposeAsync()
    {
        _link.VideoControlChanged -= OnVideoControl;
        Task? loop;
        lock (_lock)
        {
            Stop();
            loop = _loop;
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

        _screen.Dispose();
    }
}
