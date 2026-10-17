using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Shared.Policy;

/// <summary>
/// The PC's end of a broadcast (M6 portion 2, D-71), owned by the <see cref="PolicyEnforcer"/>:
/// an <c>Overlay BROADCAST_START</c> that passed the ownership gate dials
/// <c>PullBroadcast</c> on the current link and hands every frame to the
/// <see cref="IMachineState"/>; the screen is taken on the first frame, so a stream the
/// console refuses never blanks a student's screen. The broadcast is <b>not</b> a session in
/// <c>policy.json</c> — it ends when its stream ends (the teacher stopped, the console left,
/// the link dropped), on <c>BROADCAST_STOP</c>, when a newer broadcast replaces it, and at
/// the latest at <see cref="Defaults.BroadcastHardLimit"/> on this PC's own clock, whatever
/// the console proposed — and when the stream brings no frame for
/// <see cref="Defaults.BroadcastStallTimeout"/> (D-71 item 10). Nothing survives a service
/// restart; nothing here throws.
/// </summary>
public sealed class BroadcastReceiver : IAsyncDisposable
{
    private readonly AgentLink _link;
    private readonly IMachineState _machine;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _ceiling;
    private readonly TimeSpan _stallTimeout;
    private readonly Lock _lock = new();

    // Held around every show/stop on the machine, so a broadcast that ends never takes down
    // the picture of the one that replaced it.
    private readonly Lock _screen = new();
    private BroadcastSession? _current;
    private CancellationTokenSource? _running;
    private Task? _run;
    private int _generation;
    private long _epoch;
    private bool _showing;
    private bool _disposed;

    /// <param name="ceiling"><see cref="Defaults.BroadcastHardLimit"/> unless a test wants it sooner.</param>
    /// <param name="stallTimeout"><see cref="Defaults.BroadcastStallTimeout"/> unless a test wants it sooner.</param>
    public BroadcastReceiver(AgentLink link, IMachineState machine, ILogger log, Func<DateTimeOffset> clock, TimeSpan? ceiling = null, TimeSpan? stallTimeout = null)
    {
        _stallTimeout = stallTimeout ?? Defaults.BroadcastStallTimeout;
        _link = link;
        _machine = machine;
        _log = log;
        _clock = clock;
        _ceiling = ceiling ?? Defaults.BroadcastHardLimit;
    }

    /// <summary>The broadcast changed what the PC shows: it appeared, ended or was re-asserted. On a worker thread.</summary>
    public event Action? Changed;

    /// <summary>The broadcast the screen shows now, or <c>null</c> — one that was asked for but has no frame yet is not shown.</summary>
    public BroadcastSession? Showing
    {
        get
        {
            lock (_lock)
            {
                return _showing ? _current : null;
            }
        }
    }

    /// <summary>The broadcast asked for, shown or still connecting; <c>null</c> when none.</summary>
    public BroadcastSession? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// A <c>BROADCAST_START</c> from the console <paramref name="setByInstance"/> (already past
    /// the gate). The same session again is a no-op; another session replaces the one
    /// running. Returns the reason it was refused, or <c>null</c>.
    /// </summary>
    public string? Start(Overlay request, string setByInstance)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId.Length == 0)
        {
            return PolicyEvents.RefusedNoSession;
        }

        CancellationTokenSource running;
        BroadcastSession session;
        int generation;
        CancellationTokenSource? previous;
        lock (_lock)
        {
            if (_disposed)
            {
                return PolicyEvents.RefusedStopped;
            }

            // The same broadcast on the same link is a re-statement; on a newer link it is a
            // rejoin, and the old stream — dying with its link — must not be kept.
            var epoch = _link.LinkEpoch;
            if (_current is { } current && epoch == _epoch && string.Equals(current.SessionId, request.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _epoch = epoch;

            // Durations onto this clock (D-70 item 6), clamped to this PC's ceiling.
            var now = _clock().ToUnixTimeSeconds();
            var proposed = request.HardLimitSeconds > 0 ? ClassroomPolicy.Add(now, request.HardLimitSeconds) : 0;
            session = new BroadcastSession(request.SessionId, setByInstance, now, ClassroomPolicy.Clamp(proposed, _ceiling, now),
                OverlayText.ClampMessage(request.Message));

            previous = _running;
            running = new CancellationTokenSource();
            generation = ++_generation;
            _current = session;
            _running = running;
            _showing = false;
        }

        Cancel(previous);
        _log.LogInformation("broadcast {Session} from {Console} asked for; ends by {Limit} at the latest", session.SessionId, setByInstance, session.HardLimitUnix);
        var run = Task.Run(() => RunAsync(session, generation, running));
        lock (_lock)
        {
            _run = run;
        }

        return null;
    }

    /// <summary>A <c>BROADCAST_STOP</c>: ends the broadcast it names, or whichever is running when it names none.</summary>
    public void Stop(string sessionId)
    {
        CancellationTokenSource? running = null;
        lock (_lock)
        {
            if (_current is { } current && (sessionId.Length == 0 || string.Equals(current.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
            {
                running = _running;
            }
        }

        Cancel(running);
    }

    /// <summary>Shows the running broadcast again on a machine that lost it — a session helper restarted at logon or logoff (D-30).</summary>
    public void Reassert()
    {
        BroadcastSession? showing;
        MachineResult result;
        lock (_screen)
        {
            showing = Showing;
            if (showing is null)
            {
                return;
            }

            result = Guard(() => _machine.ShowBroadcast(showing));
        }

        if (!result.Succeeded)
        {
            _log.LogWarning("re-asserting broadcast {Session} failed: {Problem}", showing.SessionId, result.Problem);
        }
    }

    private async Task RunAsync(BroadcastSession session, int generation, CancellationTokenSource running)
    {
        var limitReached = false;
        var stalled = false;
        var taken = false;
        (BroadcastPullEnd End, string Detail) outcome = (BroadcastPullEnd.Cancelled, string.Empty);
        try
        {
            var left = TimeSpan.FromSeconds(Math.Max(0, session.HardLimitUnix - _clock().ToUnixTimeSeconds()));
            using var limit = new CancellationTokenSource(left);

            // Pushed back by every frame; a stream that brings none for this long is over.
            using var stall = new CancellationTokenSource(_stallTimeout);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(running.Token, limit.Token, stall.Token);
            var first = true;
            outcome = await _link.PullBroadcastAsync(session.SessionId, async (frame, token) =>
            {
                stall.CancelAfter(_stallTimeout);
                if (first)
                {
                    first = false;
                    taken = TakeScreen(session, generation);
                    if (!taken)
                    {
                        running.Cancel();
                        return;
                    }
                }

                await _machine.BroadcastFrameAsync(frame, token);
            }, both.Token);
            limitReached = limit.IsCancellationRequested && !running.IsCancellationRequested;
            stalled = stall.IsCancellationRequested && !limitReached && !running.IsCancellationRequested;
        }
        catch (Exception ex)
        {
            outcome = (BroadcastPullEnd.Failed, ex.Message);
            _log.LogWarning(ex, "broadcast {Session} failed: {Message}", session.SessionId, ex.Message);
        }
        finally
        {
            Finish(session, generation, running, taken);
        }

        if (limitReached)
        {
            Report(Event.Types.Severity.Info, PolicyEvents.BroadcastExpired, PolicyEvents.Text("BroadcastExpired"));
        }
        else if (stalled)
        {
            Report(Event.Types.Severity.Warning, PolicyEvents.BroadcastStalled,
                PolicyEvents.Text("BroadcastStalled", (int)Math.Round(_stallTimeout.TotalSeconds)));
        }
        else if (outcome.End is BroadcastPullEnd.Failed or BroadcastPullEnd.Unsupported)
        {
            Report(Event.Types.Severity.Warning, PolicyEvents.BroadcastFailed, PolicyEvents.Text("BroadcastFailed", outcome.Detail));
        }

        _log.LogInformation("broadcast {Session} over: {End}{Detail}", session.SessionId, limitReached ? "hard limit" : stalled ? "stalled" : outcome.End.ToString(),
            outcome.Detail.Length > 0 ? $" ({outcome.Detail})" : string.Empty);
    }

    /// <summary>On the first frame: the screen is the broadcast's from now on. False when the machine cannot show it.</summary>
    private bool TakeScreen(BroadcastSession session, int generation)
    {
        MachineResult result;
        lock (_screen)
        {
            lock (_lock)
            {
                if (generation != _generation || _disposed)
                {
                    return false;
                }
            }

            // Marked first: the helper may report the picture before ShowBroadcast returns, and
            // the supervisor must not mistake it for a broadcast nobody is running.
            lock (_lock)
            {
                _showing = true;
            }

            result = Guard(() => _machine.ShowBroadcast(session));
            if (!result.Succeeded)
            {
                lock (_lock)
                {
                    if (generation == _generation)
                    {
                        _showing = false;
                    }
                }
            }
        }

        if (!result.Succeeded)
        {
            Report(Event.Types.Severity.Warning, PolicyEvents.BroadcastFailed, PolicyEvents.Text("BroadcastFailed", result.Problem));
            return false;
        }

        RaiseChanged();
        return true;
    }

    /// <param name="taken">This run showed its picture; it gives the screen back unless a newer broadcast already shows its own.</param>
    private void Finish(BroadcastSession session, int generation, CancellationTokenSource running, bool taken)
    {
        var stop = false;
        lock (_screen)
        {
            lock (_lock)
            {
                if (generation == _generation)
                {
                    stop = _showing;
                    _current = null;
                    _showing = false;
                    _running = null;
                }
                else
                {
                    // Replaced: the newer one's picture, once it has one, is what counts.
                    stop = taken && !_showing;
                }
            }

            if (stop)
            {
                var result = Guard(_machine.StopBroadcast);
                if (!result.Succeeded)
                {
                    _log.LogWarning("giving the screen back after broadcast {Session} failed: {Problem}", session.SessionId, result.Problem);
                }
            }
        }

        running.Dispose();
        RaiseChanged();
    }

    private MachineResult Guard(Func<MachineResult> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            return MachineResult.Failed(ex.Message);
        }
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Report(Event.Types.Severity severity, string code, string message)
    {
        try
        {
            _link.Report(severity, code, message);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "could not report {Code}: {Message}", code, ex.Message);
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

    /// <summary>Ends any broadcast and gives the screen back. Never throws.</summary>
    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? running;
        Task? run;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            running = _running;
            run = _run;
        }

        Cancel(running);
        if (run is not null)
        {
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "the broadcast did not end in time: {Message}", ex.Message);
            }
        }
    }
}
