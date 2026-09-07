using System.Collections.Concurrent;
using LabControl.Shared;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;

namespace LabControl.Console.Services;

/// <summary>
/// One PC's screen as the console holds it (M3): the thumbnail for the mosaic, the full
/// picture for the single-PC view, and the numbers the view shows next to them. Owned by
/// <see cref="ScreenStore"/>; frames are applied on gRPC threads, the UI only reads.
/// </summary>
public sealed class AgentScreen : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Queue<(DateTimeOffset At, int Bytes)> _recent = new();
    private long _recentBytes;

    public AgentScreen(string agentId) => AgentId = agentId;

    public string AgentId { get; }

    /// <summary>The mosaic picture: a thumbnail, or a downscaled full keyframe while the full view is open.</summary>
    public ScreenImage Thumbnail { get; } = new();

    /// <summary>The native-resolution picture; empty until the full view has been opened.</summary>
    public ScreenImage Full { get; } = new();

    /// <summary>What the console last asked this PC for; <see cref="VideoMode.Unspecified"/> when nothing.</summary>
    public VideoMode RequestedMode { get; internal set; }

    public long FramesReceived { get; private set; }

    public long BytesReceived { get; private set; }

    /// <summary>Frames the console could not use (undecodable, malformed, delta without a keyframe).</summary>
    public long FramesRejected { get; private set; }

    public DateTimeOffset? LastFrameAt { get; private set; }

    /// <summary>The screen size the PC last reported in a frame.</summary>
    public int ScreenWidth { get; private set; }

    public int ScreenHeight { get; private set; }

    /// <summary>Frames in the last second, from the timestamps the console applied them at.</summary>
    public double FramesPerSecond
    {
        get
        {
            lock (_lock)
            {
                return _recent.Count;
            }
        }
    }

    /// <summary>Bytes of JPEG received in the last second.</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                return _recentBytes;
            }
        }
    }

    /// <summary>Linked, asked for video, and nothing has arrived for <see cref="Defaults.VideoStallTimeout"/>.</summary>
    public bool IsStalled(DateTimeOffset now, bool linked) =>
        linked && RequestedMode != VideoMode.Unspecified && (LastFrameAt is null || now - LastFrameAt > Defaults.VideoStallTimeout);

    internal void Trim(DateTimeOffset now)
    {
        lock (_lock)
        {
            while (_recent.Count > 0 && now - _recent.Peek().At > TimeSpan.FromSeconds(1))
            {
                _recentBytes -= _recent.Dequeue().Bytes;
            }
        }
    }

    internal void Count(VideoFrame frame, FrameOutcome outcome, DateTimeOffset now)
    {
        lock (_lock)
        {
            Trim(now);
            _recent.Enqueue((now, frame.Jpeg.Length));
            _recentBytes += frame.Jpeg.Length;
            FramesReceived++;
            BytesReceived += frame.Jpeg.Length;
            LastFrameAt = now;
            if (outcome != FrameOutcome.Applied)
            {
                FramesRejected++;
            }

            if (frame.Width > 0 && frame.Height > 0)
            {
                ScreenWidth = frame.Width;
                ScreenHeight = frame.Height;
            }
        }
    }

    public void Dispose()
    {
        Thumbnail.Dispose();
        Full.Dispose();
    }
}

/// <summary>
/// Every PC's screen, keyed by agent id (M3, PROTOCOL "Video"). Frames arrive on
/// <c>PushVideo</c> and land here; the store applies them and tells the UI which PC changed.
/// A delta the picture cannot take asks the session for a keyframe.
/// </summary>
public sealed class ScreenStore : IDisposable
{
    private readonly ConcurrentDictionary<string, AgentScreen> _screens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    public ScreenStore(Func<DateTimeOffset> clock) => _clock = clock;

    /// <summary>A frame was applied (or refused) for this PC. On a gRPC thread.</summary>
    public event Action<AgentScreen, FrameOutcome>? Updated;

    /// <summary>The picture needs a keyframe before deltas make sense (first frame, or the screen changed size).</summary>
    public event Action<AgentScreen>? KeyframeNeeded;

    public AgentScreen Get(string agentId) => _screens.GetOrAdd(agentId, id => new AgentScreen(id));

    public AgentScreen? Find(string agentId) => _screens.GetValueOrDefault(agentId);

    public IReadOnlyCollection<AgentScreen> All => _screens.Values.ToArray();

    /// <summary>Total JPEG bytes per second over every PC, for the status line.</summary>
    public long TotalBytesPerSecond
    {
        get
        {
            var now = _clock();
            long total = 0;
            foreach (var screen in _screens.Values)
            {
                screen.Trim(now);
                total += screen.BytesPerSecond;
            }

            return total;
        }
    }

    public FrameOutcome Apply(string agentId, VideoFrame frame)
    {
        var now = _clock();
        var screen = Get(agentId);
        var outcome = frame.Mode == VideoMode.Full
            ? ApplyFull(screen, frame, now)
            : screen.Thumbnail.Apply(frame, now);

        screen.Count(frame, outcome, now);

        if (outcome == FrameOutcome.NeedsKeyframe)
        {
            KeyframeNeeded?.Invoke(screen);
        }

        Updated?.Invoke(screen, outcome);
        return outcome;
    }

    private static FrameOutcome ApplyFull(AgentScreen screen, VideoFrame frame, DateTimeOffset now)
    {
        var outcome = screen.Full.Apply(frame, now);
        if (outcome != FrameOutcome.Applied || !(frame.Keyframe || frame.Dirty.Count == 0))
        {
            return outcome;
        }

        // The mosaic keeps moving while the full view is open: every keyframe (one per
        // KeyframeInterval) is scaled into the thumbnail, which the PC stopped sending.
        using var picture = screen.Full.Snapshot();
        if (picture is null)
        {
            return outcome;
        }

        var (width, height) = VideoGeometry.ThumbnailSize(picture.Width, picture.Height);
        using var scaled = JpegCodec.Scale(picture, width, height);
        screen.Thumbnail.Replace(scaled, picture.Width, picture.Height, now);
        return outcome;
    }

    /// <summary>The PC was removed from the lab; its pictures go with it.</summary>
    public void Remove(string agentId)
    {
        if (_screens.TryRemove(agentId, out var screen))
        {
            screen.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var screen in _screens.Values)
        {
            screen.Dispose();
        }

        _screens.Clear();
    }
}
