using System.Collections.Concurrent;
using LabControl.Shared;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent.Session;

/// <summary>
/// The helper's side of the lock (M6 portion 1, PROTOCOL "Overlay over the pipe"): owns the
/// <see cref="OverlayWindow"/> and the <see cref="InputBlock"/>, applies every <c>Overlay</c>
/// the service relays, and answers <c>HelperMessage.overlay_state</c> — what is on the
/// screen now, whether the hooks hold, and the reason (stable <see cref="OverlayReasons"/>
/// codes) when something is weaker than it should be — on every change and with every
/// status. The service is the policy owner: it decides what is shown and for how long; this
/// only shows it. Requests are queued for a thread of its own, like input (D-36): creating
/// the window and the hooks waits on their threads, and the pipe's read loop never waits on
/// Windows. Nothing here throws out.
/// <para>
/// The limits arrive as <b>durations</b> and are stamped onto this clock at arrival (D-70
/// item 6) — the service is on the same machine, so the countdown here agrees with the
/// journal's to the second. <see cref="State"/> is <b>lock-free</b>: the status loop and
/// every <c>Ping</c> read it while <see cref="Apply"/> may be waiting up to ten seconds on
/// the window and hook threads, and a status that waited that long would make the
/// supervisor end the helper as silent (<see cref="Defaults.HelperSilenceTimeout"/>).
/// </para>
/// </summary>
internal sealed class OverlayController : IDisposable
{
    private readonly ILogger _log;
    private readonly Action<OverlayState> _report;
    private readonly string _pcName;
    private readonly Lock _lock = new();
    private readonly BlockingCollection<Overlay> _queue = new(64);
    private readonly Thread _thread;

    // Read by State() on any thread without the lock; written on the controller thread only.
    // _current is assigned last in Show, so a state read mid-apply describes what was showing
    // before, which is still the truth of the screen at that moment.
    private volatile OverlayWindow? _window;
    private volatile InputBlock? _block;
    private volatile Overlay? _current;
    private long _sinceUnix;
    private long _untilUnix;
    private long _hardLimitUnix;
    private bool _disposed;

    /// <param name="report">Sends an <c>overlay_state</c> up the pipe, fire-and-forget.</param>
    /// <param name="pcName">The PC's own name for the picture (<c>PC-07</c>).</param>
    public OverlayController(ILogger log, Action<OverlayState> report, string pcName)
    {
        _log = log;
        _report = report;
        _pcName = pcName;
        _thread = new Thread(Run) { Name = "overlay-control", IsBackground = true };
        _thread.Start();
    }

    /// <summary>From the pipe's read loop: queued in order, never waited for; a full queue drops the oldest, the newest request is what counts.</summary>
    public void Offer(Overlay overlay)
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            while (!_queue.TryAdd(overlay))
            {
                _queue.TryTake(out _);
            }
        }
        catch (InvalidOperationException)
        {
            // Completed underneath us: the helper is exiting.
        }
    }

    private void Run()
    {
        try
        {
            foreach (var overlay in _queue.GetConsumingEnumerable())
            {
                Apply(overlay);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>On the controller's own thread: shows or lifts, then reports. The lock here is against <see cref="Dispose"/> only; <see cref="State"/> never takes it.</summary>
    internal void Apply(Overlay overlay)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                switch (overlay.Kind)
                {
                    case Overlay.Types.Kind.Lock:
                        Show(overlay);
                        break;

                    case Overlay.Types.Kind.Unlock:
                        Lift();
                        break;

                    default:
                        // BROADCAST_START / STOP belong to portion 2 (D-71).
                        _log.LogWarning("overlay {Kind} is not something this helper build shows", overlay.Kind);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "applying overlay {Kind} failed: {Message}", overlay.Kind, ex.Message);
            }
        }

        Report();
    }

    /// <summary>The state as it stands, for the status loop and every Ping — a snapshot of volatile fields, no lock, never waits.</summary>
    public OverlayState State()
    {
        var current = _current;
        if (current is null)
        {
            return new OverlayState { Kind = Overlay.Types.Kind.Unspecified };
        }

        var window = _window;
        var block = _block;
        var showing = window is { IsShowing: true };
        var hooks = block is { HooksInPlace: true };
        var reasons = new List<string>(3);
        if (!showing)
        {
            reasons.Add(OverlayReasons.WindowFailed);
        }

        if (!hooks)
        {
            reasons.AddRange(block is { Reason.Length: > 0 } ? OverlayReasons.Split(block.Reason) : [OverlayReasons.HookRefused]);
        }

        var until = Volatile.Read(ref _untilUnix);
        var hardLimit = Volatile.Read(ref _hardLimitUnix);
        return new OverlayState
        {
            Kind = showing ? Overlay.Types.Kind.Lock : Overlay.Types.Kind.Unspecified,
            SessionId = current.SessionId,
            SinceUnix = Volatile.Read(ref _sinceUnix),
            UntilUnix = until,
            HardLimitUnix = hardLimit,
            RemainingSeconds = OverlayText.Remaining(until, hardLimit, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) ?? 0,
            InputBlocked = hooks,
            Reason = OverlayReasons.Join(reasons),
        };
    }

    public void Report()
    {
        try
        {
            _report(State());
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "could not report the overlay state: {Message}", ex.Message);
        }
    }

    private void Show(Overlay overlay)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var restated = _current is { } current && current.SessionId == overlay.SessionId;

        // Durations onto this clock. The service has already clamped the hard limit; the
        // ceiling here is only a guard against a malformed message, since the service is
        // what lifts the lock and this only counts down.
        var ceiling = (long)Defaults.LockHardLimit.TotalSeconds;
        var until = overlay.UntilSeconds > 0 ? now + overlay.UntilSeconds : 0;
        var hardLimit = now + (overlay.HardLimitSeconds > 0 ? Math.Min(overlay.HardLimitSeconds, ceiling) : ceiling);
        if (!restated)
        {
            Volatile.Write(ref _sinceUnix, now);
        }

        Volatile.Write(ref _untilUnix, until);
        Volatile.Write(ref _hardLimitUnix, hardLimit);

        // The hooks first, so no click lands between the request and the picture.
        var block = _block ??= new InputBlock(_log);
        block.Block();

        var window = _window;
        if (window is { IsShowing: true })
        {
            window.Update(overlay.Message, until, hardLimit);
        }
        else
        {
            window?.Dispose();
            _window = new OverlayWindow(_log, overlay.Message, _pcName, until, hardLimit);
        }

        _current = overlay;
        _log.LogInformation("lock {Session} {What}: until {Until}, hard limit {Limit}, hooks {Hooks}{Detail}",
            overlay.SessionId, restated ? "re-stated" : "shown", until == 0 ? "lifted" : until, hardLimit,
            block.HooksInPlace ? "in place" : "NOT in place", block.Detail.Length > 0 ? $" ({block.Detail})" : string.Empty);
    }

    private void Lift()
    {
        if (_current is null && _window is null)
        {
            return;
        }

        _current = null;
        _block?.Unblock();
        _window?.Dispose();
        _window = null;
        _log.LogInformation("lock lifted");
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _log.LogDebug("the overlay control thread did not finish in time; it is a background thread and dies with the process");
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                Lift();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "lifting the overlay on exit failed: {Message}", ex.Message);
            }

            _block?.Dispose();
            _block = null;
        }

        _queue.Dispose();
    }
}
