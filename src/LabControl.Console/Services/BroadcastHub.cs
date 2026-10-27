using Grpc.Core;
using LabControl.Shared;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using LabControl.Shared.Video.H264;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Services;

/// <summary>
/// One broadcast of the teacher's screen (M6 portion 2, D-71, D-72): the console's own
/// screen captured through an <see cref="IScreenSource"/> that already scales it to
/// <see cref="Defaults.BroadcastWidth"/>, encoded <b>once</b> by the same
/// <see cref="ScreenProducer"/> the PCs use, and fanned out to every PC that dialled
/// <c>PullBroadcast</c> for this session. Each PC has a one-frame, latest-wins slot on its
/// own HTTP/2 stream, so gRPC flow control holds back a slow PC and nobody else: a keyframe
/// replaces whatever the slot holds, and a delta that finds the slot still full breaks that
/// PC's chain — it skips deltas until the next keyframe, which the hub asks the encoder for
/// at most every <see cref="Defaults.BroadcastKeyframeRequestInterval"/>. The stream's
/// bandwidth cap is <c>min(BroadcastBitsPerSecond, BroadcastTotalBitsPerSecond / watching)</c>,
/// retuned whenever a PC joins or leaves, and D-37's auto quality follows its waits. The
/// hub ends at <see cref="Deadline"/> by itself (<see cref="Expired"/>), and every stream
/// ends with it.
/// <para>
/// In *Video* (D-91 item 6) a second producer encodes H.264 at
/// <see cref="Defaults.BroadcastVideoFramesPerSecond"/> for every PC that listed
/// <c>"h264"</c> in its <c>BroadcastRequest</c>; the JPEG producer runs only while a watching
/// PC did not (or in *Text*), so each PC receives exactly one stream and both count against
/// the same total cap. Switching moves every PC to the other stream at its next keyframe.
/// </para>
/// </summary>
public sealed class BroadcastHub : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<Event.Types.Severity, string, string> _report;
    private readonly Stream _jpeg;
    private readonly Stream _h264;
    private readonly bool _canEncode;
    private readonly Dictionary<string, Subscriber> _subscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly Timer _limit;
    private ulong _seq;
    private bool _video;
    private bool _started;
    private bool _disposed;

    /// <param name="sessionId">The <c>Overlay.session_id</c> every PC was told; a <c>PullBroadcast</c> for another one is not this broadcast.</param>
    /// <param name="open">The console's screen (<see cref="ConsoleScreen.Open"/>, a drawn one in the tests).</param>
    /// <param name="report">An event for the teacher: a capture that fails, and recovers.</param>
    /// <param name="limit"><see cref="Defaults.BroadcastHardLimit"/> unless a test wants it sooner.</param>
    /// <param name="video">Start in *Video* (D-91); ignored where this console has no H.264 encoder.</param>
    /// <param name="h264">The H.264 encoder (a test's stand-in); the platform's when <c>null</c>.</param>
    public BroadcastHub(string sessionId, Func<IScreenSource> open, ILogger log, Func<DateTimeOffset> clock,
        Action<Event.Types.Severity, string, string> report, TimeSpan? limit = null, bool video = false, ScreenProducer.H264Factory? h264 = null)
    {
        SessionId = sessionId;
        _log = log;
        _clock = clock;
        _report = report;
        StartedAt = clock();
        Limit = limit ?? Defaults.BroadcastHardLimit;
        Deadline = StartedAt + Limit;
        _canEncode = h264 is not null || H264Codecs.CanEncode;
        _video = video && _canEncode;
        _jpeg = new Stream(h264: false);
        _h264 = new Stream(h264: true);
        _jpeg.Producer = new ScreenProducer("broadcast", open, (frame, token) => SinkAsync(_jpeg, frame), Report, log);
        _h264.Producer = new ScreenProducer("broadcast-video", open, (frame, token) => SinkAsync(_h264, frame), Report, log, h264: h264);
        _limit = new Timer(_ => OnLimit(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string SessionId { get; }

    public DateTimeOffset StartedAt { get; }

    public TimeSpan Limit { get; }

    /// <summary>When the broadcast ends by itself, by this console's clock; every PC also clamps it on its own.</summary>
    public DateTimeOffset Deadline { get; }

    /// <summary>The hard limit passed; the session stops the broadcast. On a timer thread.</summary>
    public event Action<BroadcastHub>? Expired;

    /// <summary>Something changed that a status line shows: a PC joined or left, the capture failed or recovered.</summary>
    public event Action? Changed;

    /// <summary>The teacher's *Video* is on (D-91); <c>false</c> in *Text* and where there is no encoder.</summary>
    public bool Video
    {
        get
        {
            lock (_lock)
            {
                return _video;
            }
        }
    }

    /// <summary>Whether *Video* can be chosen on this console at all.</summary>
    public bool CanVideo => _canEncode;

    /// <summary>PCs receiving the picture right now.</summary>
    public int Watching
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>The agent ids receiving the picture right now.</summary>
    public IReadOnlyCollection<string> WatchingAgents
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Keys.ToArray();
            }
        }
    }

    /// <summary>Watching PCs that get JPEG while *Video* is on: they cannot decode H.264 (D-91 item 6).</summary>
    public int WatchingAsText
    {
        get
        {
            lock (_lock)
            {
                return _video ? _subscribers.Values.Count(s => !s.CanH264) : 0;
            }
        }
    }

    /// <summary>Frames encoded so far, both streams.</summary>
    public long FramesEncoded { get; private set; }

    /// <summary>The JPEG quality of the last JPEG frame (D-37 auto quality moves it).</summary>
    public int LastQuality { get; private set; }

    /// <summary>The codec of the last frame of the stream the teacher chose: <c>"h264"</c> when *Video* really runs as H.264.</summary>
    public string? StreamCodec => Primary.Producer.LastCodec;

    /// <summary>"coregraphics", "gdi", "simulated"; <c>null</c> while no capture is open.</summary>
    public string? SourceKind => Primary.Producer.SourceKind ?? _jpeg.Producer.SourceKind;

    /// <summary>Why the screen cannot be captured right now, as the producer said it; <c>null</c> when it can.</summary>
    public string? CaptureProblem { get; private set; }

    /// <summary>The stable code behind <see cref="CaptureProblem"/> (<c>capture.permission</c>, ...).</summary>
    public string? CaptureProblemCode { get; private set; }

    /// <summary>The bandwidth cap of the one encoded stream right now.</summary>
    public int StreamBitsPerSecond => StreamCap(Watching);

    /// <summary>Bytes the teacher's chosen stream encoded in the last second — what one PC receives; the network carries it once per PC.</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                var primary = Primary;
                primary.Trim(_clock());
                return primary.RecentBytes;
            }
        }
    }

    /// <summary>Frames the teacher's chosen stream encoded in the last second.</summary>
    public int FramesPerSecond
    {
        get
        {
            lock (_lock)
            {
                var primary = Primary;
                primary.Trim(_clock());
                return primary.Recent.Count;
            }
        }
    }

    /// <summary>The stream *Text* or *Video* names. Under <see cref="_lock"/> or for a moment's read.</summary>
    private Stream Primary => _video ? _h264 : _jpeg;

    /// <summary>The cap for one stream that <paramref name="watching"/> PCs each receive (D-72).</summary>
    public static int StreamCap(int watching) =>
        Math.Min(Defaults.BroadcastBitsPerSecond, Defaults.BroadcastTotalBitsPerSecond / Math.Max(1, watching));

    /// <summary>Starts capturing; frames are encoded whether or not a PC has dialled yet, so the first one gets a picture at once.</summary>
    public void Start()
    {
        lock (_lock)
        {
            _started = true;
        }

        Reconcile(keyframe: true);
        var due = Deadline - _clock();
        _limit.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        _log.LogInformation("broadcast {Session} started in {Mode}; ends by itself at {Deadline:HH:mm:ss}", SessionId, Video ? "video" : "text", Deadline.ToLocalTime());
    }

    /// <summary>
    /// The teacher switched between *Text* and *Video* (D-91 item 7): every PC moves to the
    /// other stream at its next keyframe. <c>false</c> when *Video* is asked for and this
    /// console has no H.264 encoder.
    /// </summary>
    public bool SetVideo(bool video)
    {
        if (video && !_canEncode)
        {
            return false;
        }

        lock (_lock)
        {
            if (_disposed || _video == video)
            {
                return true;
            }

            _video = video;
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Restart();
            }
        }

        _log.LogInformation("broadcast {Session} now in {Mode}", SessionId, video ? "video" : "text");
        Reconcile(keyframe: true);
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Serves one PC's <c>PullBroadcast</c> until the broadcast ends, the PC hangs up or the
    /// link drops. A PC that dials again replaces its own earlier stream. Ends normally (the
    /// PC reads the end of the stream) when the hub is disposed. <paramref name="codecs"/> is
    /// <c>BroadcastRequest.codecs</c>: what the PC can decode besides JPEG.
    /// </summary>
    public async Task ServeAsync(string agentId, IReadOnlyCollection<string> codecs, IServerStreamWriter<VideoFrame> writer, CancellationToken token)
    {
        Subscriber subscriber;
        Subscriber? replaced;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _subscribers.TryGetValue(agentId, out replaced);
            subscriber = new Subscriber(agentId, codecs.Contains(Defaults.VideoCodecH264, StringComparer.OrdinalIgnoreCase));
            _subscribers[agentId] = subscriber;
        }

        replaced?.Complete();
        Reconcile(keyframe: true);
        RaiseChanged();

        try
        {
            while (await subscriber.TakeAsync(token) is { } frame)
            {
                await writer.WriteAsync(frame, token);
                subscriber.Sent++;
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested || ex is IOException or InvalidOperationException)
        {
            // The PC hung up, or the link went; the broadcast goes on for the others.
        }
        finally
        {
            lock (_lock)
            {
                if (_subscribers.TryGetValue(agentId, out var current) && ReferenceEquals(current, subscriber))
                {
                    _subscribers.Remove(agentId);
                }
            }

            subscriber.Complete();
            if (!_disposed)
            {
                Reconcile(keyframe: true);
            }

            RaiseChanged();
        }
    }

    /// <summary>A PC that names no codecs: JPEG only, as every agent before M7 portion 6.</summary>
    public Task ServeAsync(string agentId, IServerStreamWriter<VideoFrame> writer, CancellationToken token) =>
        ServeAsync(agentId, [], writer, token);

    /// <summary>Frames a PC skipped since it last caught up — for the tests.</summary>
    public (long Sent, long Skipped)? Statistics(string agentId)
    {
        lock (_lock)
        {
            return _subscribers.TryGetValue(agentId, out var s) ? (s.Sent, s.Skipped) : null;
        }
    }

    /// <summary>Which stream the PC is on right now: <c>"h264"</c> or <c>"jpeg"</c>; <c>null</c> when it is not watching. For the tests.</summary>
    public string? StreamOf(string agentId)
    {
        lock (_lock)
        {
            return _subscribers.TryGetValue(agentId, out var s) ? (InVideo(s) ? Defaults.VideoCodecH264 : Defaults.VideoCodecJpeg) : null;
        }
    }

    // ------------------------------------------------------------------ the encoder side

    /// <summary>Under <see cref="_lock"/>: the PC is on the H.264 stream.</summary>
    private bool InVideo(Subscriber subscriber) => _video && subscriber.CanH264;

    private ValueTask<bool> SinkAsync(Stream stream, VideoFrame frame)
    {
        Subscriber[] subscribers;
        var now = _clock();
        lock (_lock)
        {
            if (_disposed)
            {
                return ValueTask.FromResult(false);
            }

            frame.Seq = ++_seq;
            frame.AtUnixMs = now.ToUnixTimeMilliseconds();
            FramesEncoded++;
            if (FrameDecoder.CodecOf(frame) == Defaults.VideoCodecJpeg)
            {
                LastQuality = frame.Quality;
            }

            stream.Trim(now);
            stream.Recent.Enqueue((now, frame.Jpeg.Length));
            stream.RecentBytes += frame.Jpeg.Length;
            subscribers = _subscribers.Values.Where(s => InVideo(s) == stream.H264).ToArray();
        }

        var keyframeWanted = false;
        foreach (var subscriber in subscribers)
        {
            keyframeWanted |= subscriber.Offer(frame);
        }

        if (keyframeWanted)
        {
            RequestKeyframe(stream);
        }

        // Taken whatever the PCs did with it: the encoder's state is spent, and a PC that
        // missed it waits for a keyframe instead of holding everyone else back.
        return ValueTask.FromResult(true);
    }

    /// <summary>
    /// Runs the streams the PCs need and stops the others: H.264 in *Video*, JPEG in *Text* or
    /// while a watching PC cannot decode H.264. Every running stream gets the cap for the PCs
    /// watching now; a new cap restarts its budget and sends a keyframe, which a joining PC
    /// needs anyway.
    /// </summary>
    private void Reconcile(bool keyframe)
    {
        bool runJpeg;
        bool runH264;
        int watching;
        lock (_lock)
        {
            if (_disposed || !_started)
            {
                return;
            }

            watching = _subscribers.Count;
            runH264 = _video;
            runJpeg = !_video || _subscribers.Values.Any(s => !s.CanH264);
            var now = _clock();
            _jpeg.LastKeyframeRequest = now;
            _h264.LastKeyframeRequest = now;
        }

        var cap = StreamCap(watching);
        _jpeg.Producer.Apply(runJpeg ? JpegControl(cap, keyframe) : null);
        _h264.Producer.Apply(runH264 ? VideoControl(cap, keyframe) : null);
    }

    private void RequestKeyframe(Stream stream)
    {
        var now = _clock();
        int watching;
        lock (_lock)
        {
            if (_disposed || now - stream.LastKeyframeRequest < Defaults.BroadcastKeyframeRequestInterval)
            {
                return;
            }

            stream.LastKeyframeRequest = now;
            watching = _subscribers.Count;
            if (stream.H264 ? !_video : _video && _subscribers.Values.All(s => s.CanH264))
            {
                return; // not running
            }
        }

        var cap = StreamCap(watching);
        stream.Producer.Apply(stream.H264 ? VideoControl(cap, keyframe: true) : JpegControl(cap, keyframe: true));
    }

    private static VideoControl JpegControl(int bitsPerSecond, bool keyframe) => new()
    {
        Active = true,
        Mode = VideoMode.Full,
        FramesPerSecond = Defaults.BroadcastFramesPerSecond,
        Quality = Defaults.VideoQualityAuto,
        MaxBitsPerSecond = bitsPerSecond,
        RequestKeyframe = keyframe,
    };

    private static VideoControl VideoControl(int bitsPerSecond, bool keyframe) => new()
    {
        Active = true,
        Mode = VideoMode.Full,
        FramesPerSecond = Defaults.BroadcastVideoFramesPerSecond,
        Quality = Defaults.VideoQualityAuto,
        MaxBitsPerSecond = bitsPerSecond,
        RequestKeyframe = keyframe,
        Codec = Defaults.VideoCodecH264,
    };

    private void Report(Event.Types.Severity severity, string code, string message)
    {
        if (code == "capture.recovered")
        {
            CaptureProblem = null;
            CaptureProblemCode = null;
        }
        else if (severity >= Event.Types.Severity.Warning)
        {
            CaptureProblem = message;
            CaptureProblemCode = code;
        }

        try
        {
            _report(severity, code, message);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "reporting {Code} failed", code);
        }

        RaiseChanged();
    }

    private void OnLimit()
    {
        _log.LogInformation("broadcast {Session} reached its {Minutes} min limit", SessionId, Limit.TotalMinutes);
        try
        {
            Expired?.Invoke(this);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ending broadcast {Session} at its limit failed: {Message}", SessionId, ex.Message);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "a broadcast listener failed: {Message}", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Subscriber[] subscribers;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscribers = _subscribers.Values.ToArray();
        }

        await _limit.DisposeAsync();
        foreach (var subscriber in subscribers)
        {
            subscriber.Complete();
        }

        await _jpeg.Producer.DisposeAsync();
        await _h264.Producer.DisposeAsync();
        _log.LogInformation("broadcast {Session} ended after {Minutes:0.#} min, {Frames} frames", SessionId, (_clock() - StartedAt).TotalMinutes, FramesEncoded);
    }

    /// <summary>One encoded stream: its producer, what it sent in the last second, when it was last asked for a keyframe.</summary>
    private sealed class Stream(bool h264)
    {
        public bool H264 { get; } = h264;

        public ScreenProducer Producer { get; set; } = null!;

        public Queue<(DateTimeOffset At, int Bytes)> Recent { get; } = new();

        public long RecentBytes { get; set; }

        public DateTimeOffset LastKeyframeRequest { get; set; } = DateTimeOffset.MinValue;

        public void Trim(DateTimeOffset now)
        {
            while (Recent.Count > 0 && now - Recent.Peek().At > TimeSpan.FromSeconds(1))
            {
                RecentBytes -= Recent.Dequeue().Bytes;
            }
        }
    }

    /// <summary>One PC's slot: at most one frame waiting for its stream, latest-wins without ever breaking the picture.</summary>
    private sealed class Subscriber
    {
        private readonly Lock _lock = new();
        private readonly SemaphoreSlim _signal = new(0);
        private VideoFrame? _slot;
        private bool _awaitingKeyframe = true;
        private bool _completed;

        public Subscriber(string agentId, bool canH264)
        {
            AgentId = agentId;
            CanH264 = canH264;
        }

        public string AgentId { get; }

        /// <summary>The PC listed <c>"h264"</c> in its <c>BroadcastRequest</c>.</summary>
        public bool CanH264 { get; }

        public long Sent { get; set; }

        public long Skipped { get; private set; }

        /// <summary>Offers a frame; <c>true</c> when this PC is waiting for a keyframe the encoder should be asked for.</summary>
        public bool Offer(VideoFrame frame)
        {
            lock (_lock)
            {
                if (_completed)
                {
                    return false;
                }

                if (frame.Keyframe)
                {
                    // A whole picture: whatever was waiting is superseded, and the chain is whole again.
                    var wasEmpty = _slot is null;
                    _slot = frame;
                    _awaitingKeyframe = false;
                    if (wasEmpty)
                    {
                        _signal.Release();
                    }

                    return false;
                }

                if (_awaitingKeyframe)
                {
                    Skipped++;
                    return true;
                }

                if (_slot is not null)
                {
                    // The stream has not taken the last frame yet: this delta cannot be kept
                    // without the one before it, so the PC skips to the next keyframe.
                    Skipped++;
                    _awaitingKeyframe = true;
                    return true;
                }

                _slot = frame;
                _signal.Release();
                return false;
            }
        }

        /// <summary>The next frame for the stream, or <c>null</c> once the broadcast (or this stream) is over.</summary>
        public async Task<VideoFrame?> TakeAsync(CancellationToken token)
        {
            while (true)
            {
                lock (_lock)
                {
                    if (_completed)
                    {
                        return null;
                    }

                    if (_slot is { } frame)
                    {
                        _slot = null;
                        return frame;
                    }
                }

                await _signal.WaitAsync(token);
            }
        }

        /// <summary>The PC moves to the other stream: nothing it holds belongs to it, it waits for that stream's keyframe.</summary>
        public void Restart()
        {
            lock (_lock)
            {
                _slot = null;
                _awaitingKeyframe = true;
            }
        }

        public void Complete()
        {
            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                _slot = null;
            }

            _signal.Release();
        }
    }
}
