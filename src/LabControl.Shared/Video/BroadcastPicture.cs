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
/// of order. <see cref="Clear"/> belongs to the same ordered stream as <see cref="Offer"/> —
/// called from the thread that offers the frames, at the point a broadcast starts or stops —
/// so a keyframe offered right after it is never thrown away, and a frame of the previous
/// broadcast still being decoded when it runs never stays on the screen (D-71 item 9).
/// Shared by the session helper and the simulator. Frames are decoded by the codec they name
/// (<see cref="FrameDecoder"/>): JPEG as before, H.264 through this PC's decoder (D-91).
/// </summary>
public sealed class BroadcastPicture : IDisposable
{
    private readonly ILogger _log;
    private readonly BlockingCollection<(VideoFrame Frame, int Generation)> _queue = new(Defaults.BroadcastHelperQueueLength);
    private readonly Thread _thread;
    private readonly Lock _lock = new();
    private readonly FrameDecoder _decoder;
    private bool _awaitingKeyframe = true;
    private bool _disposed;

    // Grows on every Clear: a frame offered before it is never applied after it.
    private int _generation;

    /// <param name="h264">The H.264 decoder (a test's stand-in); the platform's when <c>null</c>.</param>
    public BroadcastPicture(ILogger log, Func<H264.IH264Decoder>? h264 = null)
    {
        _log = log;
        _decoder = new FrameDecoder(h264);
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

            if (_queue.TryAdd((frame, _generation)))
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

            _generation++;
            _awaitingKeyframe = true;
            _decoder.Reset();
            Image.Clear();
        }
    }

    private bool IsCurrent(int generation)
    {
        lock (_lock)
        {
            return generation == _generation;
        }
    }

    private void Run()
    {
        try
        {
            foreach (var (frame, generation) in _queue.GetConsumingEnumerable())
            {
                if (!IsCurrent(generation))
                {
                    continue;
                }

                FrameOutcome outcome;
                try
                {
                    outcome = _decoder.Apply(Image, frame, DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "decoding broadcast frame {Seq} failed: {Message}", frame.Seq, ex.Message);
                    continue;
                }

                lock (_lock)
                {
                    if (generation != _generation)
                    {
                        // Cleared while this frame was being decoded: it belongs to a broadcast
                        // that is over. Only the picture goes; the next broadcast's keyframe is
                        // already behind it in the queue, so the wait for one is not reset.
                        Image.Clear();
                        continue;
                    }
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
        _decoder.Dispose();
        Image.Dispose();
    }
}
