using Grpc.Core;
using LabControl.Shared;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
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
/// </summary>
public sealed class BroadcastHub : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<Event.Types.Severity, string, string> _report;
    private readonly ScreenProducer _producer;
    private readonly Dictionary<string, Subscriber> _subscribers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(DateTimeOffset At, int Bytes)> _recent = new();
    private readonly Lock _lock = new();
    private readonly Timer _limit;
    private DateTimeOffset _lastKeyframeRequest = DateTimeOffset.MinValue;
    private long _recentBytes;
    private ulong _seq;
    private bool _disposed;

    /// <param name="sessionId">The <c>Overlay.session_id</c> every PC was told; a <c>PullBroadcast</c> for another one is not this broadcast.</param>
    /// <param name="open">The console's screen (<see cref="ConsoleScreen.Open"/>, a drawn one in the tests).</param>
    /// <param name="report">An event for the teacher: a capture that fails, and recovers.</param>
    /// <param name="limit"><see cref="Defaults.BroadcastHardLimit"/> unless a test wants it sooner.</param>
    public BroadcastHub(string sessionId, Func<IScreenSource> open, ILogger log, Func<DateTimeOffset> clock,
        Action<Event.Types.Severity, string, string> report, TimeSpan? limit = null)
    {
        SessionId = sessionId;
        _log = log;
        _clock = clock;
        _report = report;
        StartedAt = clock();
        Limit = limit ?? Defaults.BroadcastHardLimit;
        Deadline = StartedAt + Limit;
        _producer = new ScreenProducer("broadcast", open, SinkAsync, Report, log);
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

    /// <summary>Frames encoded so far.</summary>
    public long FramesEncoded { get; private set; }

    /// <summary>The JPEG quality of the last frame (D-37 auto quality moves it).</summary>
    public int LastQuality { get; private set; }

    /// <summary>"coregraphics", "gdi", "simulated"; <c>null</c> while no capture is open.</summary>
    public string? SourceKind => _producer.SourceKind;

    /// <summary>Why the screen cannot be captured right now, as the producer said it; <c>null</c> when it can.</summary>
    public string? CaptureProblem { get; private set; }

    /// <summary>The stable code behind <see cref="CaptureProblem"/> (<c>capture.permission</c>, ...).</summary>
    public string? CaptureProblemCode { get; private set; }

    /// <summary>The bandwidth cap of the one encoded stream right now.</summary>
    public int StreamBitsPerSecond => StreamCap(Watching);

    /// <summary>JPEG bytes encoded in the last second — what one PC receives; the network carries it once per PC.</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                Trim(_clock());
                return _recentBytes;
            }
        }
    }

    /// <summary>Frames encoded in the last second.</summary>
    public int FramesPerSecond
    {
        get
        {
            lock (_lock)
            {
                Trim(_clock());
                return _recent.Count;
            }
        }
    }

    /// <summary>The cap for one stream that <paramref name="watching"/> PCs each receive (D-72).</summary>
    public static int StreamCap(int watching) =>
        Math.Min(Defaults.BroadcastBitsPerSecond, Defaults.BroadcastTotalBitsPerSecond / Math.Max(1, watching));

    /// <summary>Starts capturing; frames are encoded whether or not a PC has dialled yet, so the first one gets a picture at once.</summary>
    public void Start()
    {
        _producer.Apply(Control(StreamCap(0), keyframe: true));
        var due = Deadline - _clock();
        _limit.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        _log.LogInformation("broadcast {Session} started; ends by itself at {Deadline:HH:mm:ss}", SessionId, Deadline.ToLocalTime());
    }

    /// <summary>
    /// Serves one PC's <c>PullBroadcast</c> until the broadcast ends, the PC hangs up or the
    /// link drops. A PC that dials again replaces its own earlier stream. Ends normally (the
    /// PC reads the end of the stream) when the hub is disposed.
    /// </summary>
    public async Task ServeAsync(string agentId, IServerStreamWriter<VideoFrame> writer, CancellationToken token)
    {
        Subscriber subscriber;
        Subscriber? replaced;
        int watching;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _subscribers.TryGetValue(agentId, out replaced);
            subscriber = new Subscriber(agentId);
            _subscribers[agentId] = subscriber;
            watching = _subscribers.Count;
        }

        replaced?.Complete();
        Retune(watching);
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

                watching = _subscribers.Count;
            }

            subscriber.Complete();
            if (!_disposed)
            {
                Retune(watching);
            }

            RaiseChanged();
        }
    }

    /// <summary>Frames a PC skipped since it last caught up — for the tests.</summary>
    public (long Sent, long Skipped)? Statistics(string agentId)
    {
        lock (_lock)
        {
            return _subscribers.TryGetValue(agentId, out var s) ? (s.Sent, s.Skipped) : null;
        }
    }

    // ------------------------------------------------------------------ the encoder side

    private ValueTask<bool> SinkAsync(VideoFrame frame, CancellationToken token)
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
            LastQuality = frame.Quality;
            Trim(now);
            _recent.Enqueue((now, frame.Jpeg.Length));
            _recentBytes += frame.Jpeg.Length;
            subscribers = _subscribers.Values.ToArray();
        }

        var keyframeWanted = false;
        foreach (var subscriber in subscribers)
        {
            keyframeWanted |= subscriber.Offer(frame);
        }

        if (keyframeWanted)
        {
            RequestKeyframe(force: false);
        }

        // Taken whatever the PCs did with it: the encoder's dirty state is spent, and a PC that
        // missed it waits for a keyframe instead of holding everyone else back.
        return ValueTask.FromResult(true);
    }

    private void Retune(int watching)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _lastKeyframeRequest = _clock();
        }

        // A new cap restarts the budget and sends a keyframe, which a joining PC needs anyway.
        _producer.Apply(Control(StreamCap(watching), keyframe: true));
    }

    private void RequestKeyframe(bool force)
    {
        var now = _clock();
        int watching;
        lock (_lock)
        {
            if (_disposed || (!force && now - _lastKeyframeRequest < Defaults.BroadcastKeyframeRequestInterval))
            {
                return;
            }

            _lastKeyframeRequest = now;
            watching = _subscribers.Count;
        }

        _producer.Apply(Control(StreamCap(watching), keyframe: true));
    }

    private static VideoControl Control(int bitsPerSecond, bool keyframe) => new()
    {
        Active = true,
        Mode = VideoMode.Full,
        FramesPerSecond = Defaults.BroadcastFramesPerSecond,
        Quality = Defaults.VideoQualityAuto,
        MaxBitsPerSecond = bitsPerSecond,
        RequestKeyframe = keyframe,
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

    private void Trim(DateTimeOffset now)
    {
        while (_recent.Count > 0 && now - _recent.Peek().At > TimeSpan.FromSeconds(1))
        {
            _recentBytes -= _recent.Dequeue().Bytes;
        }
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

        await _producer.DisposeAsync();
        _log.LogInformation("broadcast {Session} ended after {Minutes:0.#} min, {Frames} frames", SessionId, (_clock() - StartedAt).TotalMinutes, FramesEncoded);
    }

    /// <summary>One PC's slot: at most one frame waiting for its stream, latest-wins without ever breaking the picture.</summary>
    private sealed class Subscriber
    {
        private readonly Lock _lock = new();
        private readonly SemaphoreSlim _signal = new(0);
        private VideoFrame? _slot;
        private bool _awaitingKeyframe = true;
        private bool _completed;

        public Subscriber(string agentId) => AgentId = agentId;

        public string AgentId { get; }

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
