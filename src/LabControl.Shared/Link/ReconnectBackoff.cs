namespace LabControl.Shared.Link;

/// <summary>
/// Exponential backoff with jitter for an agent trying to reach the console. The lab has
/// up to thirty PCs that all lose the link at the same moment when the teacher closes the
/// console, so the jitter matters: without it they would retry in lockstep for ever.
/// </summary>
public sealed class ReconnectBackoff
{
    private readonly TimeSpan _minimum;
    private readonly TimeSpan _maximum;
    private int _attempts;

    public ReconnectBackoff(TimeSpan? minimum = null, TimeSpan? maximum = null)
    {
        _minimum = minimum ?? Defaults.ReconnectDelayMin;
        _maximum = maximum ?? Defaults.ReconnectDelayMax;
    }

    public int Attempts => _attempts;

    /// <summary>The delay before the next attempt, doubling up to the ceiling.</summary>
    public TimeSpan Next()
    {
        var doubled = _minimum * Math.Pow(2, Math.Min(_attempts, 16));
        _attempts++;

        var capped = doubled < _maximum ? doubled : _maximum;

        // Up to +25%, so a room full of agents does not retry in step.
        return capped + capped * (Random.Shared.NextDouble() * 0.25);
    }

    /// <summary>Called after a successful connection, so the next outage starts fast again.</summary>
    public void Reset() => _attempts = 0;
}
