using System.Collections.Concurrent;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Shared.Video;

/// <summary>
/// The teacher's screen as a PC holds it during a broadcast (M6 portion 2, D-71): frames
/// arrive in order and are decoded on a thread of their own into one
/// <see cref="ScreenImage"/> — a keyframe replaces it, a delta patches it — so the pipe's
/// read loop and the input hooks never wait on JPEG decoding (D-70 item 3). At most
/// <see cref="Defaults.BroadcastHelperQueueLength"/> frames wait; beyond that the deltas are
/// dropped and the picture waits for the next keyframe rather than ever being patched out
/// of order. Shared by the session helper and the simulator.
/// </summary>
public sealed class BroadcastPicture : IDisposable
{
    private readonly ILogger _log;
    private readonly BlockingCollection<VideoFrame> _queue = new(Defaults.BroadcastHelperQueueLength);
    private readonly Thread _thread;
    private readonly Lock _lock = new();
    private bool _awaitingKeyframe = true;
    private bool _disposed;

    public BroadcastPicture(ILogger log)
    {
        _log = log;
        _thread = new Thread(Run) { Name = "broadcast-decode", IsBackground = true };
        _thread.Start();
    }

    /// <summary>The picture; empty until the first keyframe.</summary>
    public ScreenImage Image { get; } = new();

    /// <summary>A frame was applied to <see cref="Image"/>. On the decode thread; return quickly.</summary>
    public event Action? Updated;

    /// <summary>Frames applied and frames dropped so far, for a log line and the tests.</summary>
    public long Applied { get; private set; }

    public long Dropped { get; private set; }

    /// <summary>From the pipe or the stream, in order; never waits.</summary>
    public void Offer(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (frame.Keyframe)
            {
                _awaitingKeyframe = false;
            }
            else if (_awaitingKeyframe)
            {
                Dropped++;
                return;
            }

            if (_queue.TryAdd(frame))
            {
                return;
            }

            // Behind: every queued delta is still good, but this one cannot be patched on
            // without them in order, and the queue is full — wait for a whole picture.
            Dropped++;
            _awaitingKeyframe = true;
        }
    }

    /// <summary>Forgets the picture: the broadcast ended, the next one starts from its own keyframe.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            while (_queue.TryTake(out _))
            {
            }

            _awaitingKeyframe = true;
            Image.Clear();
        }
    }

    private void Run()
    {
        try
        {
            foreach (var frame in _queue.GetConsumingEnumerable())
            {
                FrameOutcome outcome;
                try
                {
                    outcome = Image.Apply(frame, DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "decoding broadcast frame {Seq} failed: {Message}", frame.Seq, ex.Message);
                    continue;
                }

                if (outcome != FrameOutcome.Applied)
                {
                    lock (_lock)
                    {
                        Dropped++;
                        _awaitingKeyframe |= outcome == FrameOutcome.NeedsKeyframe;
                    }

                    continue;
                }

                Applied++;
                try
                {
                    Updated?.Invoke();
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "a broadcast picture listener failed: {Message}", ex.Message);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        Image.Dispose();
    }
}
