namespace LabControl.Shared.Video;

/// <summary>
/// The per-PC bandwidth cap (PROTOCOL "Video"): a token bucket in bits. A producer accounts
/// each frame it sends and waits as long as the pacer says before capturing the next one,
/// so a busy screen in full mode degrades to fewer frames instead of flooding the Wi-Fi
/// the console hangs on. One second of burst is allowed so a keyframe goes out at once.
/// </summary>
public sealed class VideoPacer
{
    private readonly long _bitsPerSecond;
    private readonly long _capacity;
    private double _tokens;
    private DateTimeOffset _last;

    public VideoPacer(long bitsPerSecond, DateTimeOffset now)
    {
        _bitsPerSecond = Math.Max(1, bitsPerSecond);
        _capacity = _bitsPerSecond;
        _tokens = _capacity;
        _last = now;
    }

    /// <summary>
    /// Spends the frame's bits and returns how long to wait before the next frame may go.
    /// A frame larger than a whole second's budget is still allowed — the wait just grows.
    /// </summary>
    public TimeSpan Account(long bytes, DateTimeOffset now)
    {
        Refill(now);
        _tokens -= bytes * 8.0;
        if (_tokens >= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(-_tokens / _bitsPerSecond);
    }

    private void Refill(DateTimeOffset now)
    {
        var elapsed = (now - _last).TotalSeconds;
        if (elapsed > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + elapsed * _bitsPerSecond);
            _last = now;
        }
    }
}
