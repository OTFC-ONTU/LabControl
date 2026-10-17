using System.Collections.Concurrent;
using LabControl.Shared;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
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
/// <b>The broadcast</b> (M6 portion 2, D-71) is shown by the same window and the same hooks:
/// <c>BROADCAST_START</c> / <c>BROADCAST_STOP</c> arrive like a lock, the frames separately
/// (<see cref="OfferFrame"/>) into a <see cref="BroadcastPicture"/> that decodes them on its
/// own thread. A lock always wins — it is shown over a running broadcast, and lifting it
/// brings the broadcast back. The service ends the broadcast; this only shows it.
/// </para>
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

    // The broadcast asked for (null when none) and its hard limit on this clock; the picture
    // is decoded whether or not the window shows it, so lifting a lock shows the latest one.
    private volatile Overlay? _broadcast;
    private long _broadcastSinceUnix;
    private long _broadcastHardLimitUnix;
    private readonly BroadcastPicture _picture;

    /// <param name="report">Sends an <c>overlay_state</c> up the pipe, fire-and-forget.</param>
    /// <param name="pcName">The PC's own name for the picture (<c>PC-07</c>).</param>
    public OverlayController(ILogger log, Action<OverlayState> report, string pcName)
    {
        _log = log;
        _report = report;
        _pcName = pcName;
        _picture = new BroadcastPicture(log);
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

    /// <summary>A broadcast frame from the pipe's read loop: decoded on the picture's own thread, never waited for.</summary>
    public void OfferFrame(VideoFrame frame)
    {
        try
        {
            _picture.Offer(frame);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "a broadcast frame was dropped: {Message}", ex.Message);
        }
    }

    private void Run()
    {
        try
        {
            while (!_queue.IsCompleted)
            {
                if (_queue.TryTake(out var overlay, (int)Defaults.OverlayRepaintInterval.TotalMilliseconds))
                {
                    Apply(overlay);
                }

                FollowDesktop();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void FollowDesktop()
    {
        lock (_lock)
        {
            if (_disposed || _window is not { } window)
            {
                return;
            }

            if (_current is not { } current)
            {
                FollowDesktopWithBroadcast(window);
                return;
            }

            try
            {
                var desktop = DesktopAccess.InputDesktopName();
                if (desktop.StartsWith('?') || string.Equals(desktop, window.DesktopName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // HWNDs and hooks cannot move between desktops. Recreate both on fresh
                // threads, preserving the existing deadlines rather than granting more time.
                _log.LogInformation("lock following input desktop {Desktop}", desktop);
                _window = null;
                window.Dispose();
                _block?.Dispose();
                _block = null;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var until = Volatile.Read(ref _untilUnix);
                var hardLimit = Volatile.Read(ref _hardLimitUnix);
                if (hardLimit <= now || until > 0 && until <= now)
                {
                    Lift();
                    return;
                }

                var restated = current.Clone();
                restated.UntilSeconds = until > 0 ? until - now : 0;
                restated.HardLimitSeconds = hardLimit - now;
                Show(restated, until, hardLimit);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "following the input desktop failed: {Message}", ex.Message);
            }
        }

        Report();
    }

    /// <summary>The broadcast window on a new input desktop: recreated there, like the lock's; ended here too if its limit passed.</summary>
    private void FollowDesktopWithBroadcast(OverlayWindow window)
    {
        try
        {
            if (_broadcast is null)
            {
                return;
            }

            if (Volatile.Read(ref _broadcastHardLimitUnix) <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                _log.LogInformation("broadcast past its limit on this clock; giving the screen back");
                StopBroadcast();
                return;
            }

            var desktop = DesktopAccess.InputDesktopName();
            if (desktop.StartsWith('?') || string.Equals(desktop, window.DesktopName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _log.LogInformation("broadcast following input desktop {Desktop}", desktop);
            _window = null;
            window.Dispose();
            _block?.Dispose();
            _block = null;
            ShowBroadcastWindow();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "following the input desktop with the broadcast failed: {Message}", ex.Message);
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

                    case Overlay.Types.Kind.BroadcastStart:
                        StartBroadcast(overlay);
                        break;

                    case Overlay.Types.Kind.BroadcastStop:
                        if (overlay.SessionId.Length == 0 || _broadcast is not { } running || running.SessionId == overlay.SessionId)
                        {
                            StopBroadcast();
                        }

                        break;

                    default:
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
            return BroadcastState();
        }

        var window = _window;
        var block = _block;
        var showing = window is { IsShowing: true, IsBroadcast: false };
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

    /// <summary>What a broadcast without a lock looks like on the screen; nothing when there is none.</summary>
    private OverlayState BroadcastState()
    {
        if (_broadcast is not { } broadcast)
        {
            return new OverlayState { Kind = Overlay.Types.Kind.Unspecified };
        }

        var window = _window;
        var block = _block;
        var showing = window is { IsShowing: true, IsBroadcast: true };
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

        var hardLimit = Volatile.Read(ref _broadcastHardLimitUnix);
        return new OverlayState
        {
            Kind = showing ? Overlay.Types.Kind.BroadcastStart : Overlay.Types.Kind.Unspecified,
            SessionId = broadcast.SessionId,
            SinceUnix = Volatile.Read(ref _broadcastSinceUnix),
            HardLimitUnix = hardLimit,
            RemainingSeconds = Math.Max(0, hardLimit - DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
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

    private void Show(Overlay overlay, long? existingUntil = null, long? existingHardLimit = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var restated = _current is { } current && current.SessionId == overlay.SessionId;

        // Durations onto this clock. The service has already clamped the hard limit; the
        // ceiling here is only a guard against a malformed message, since the service is
        // what lifts the lock and this only counts down.
        var ceiling = (long)Defaults.LockHardLimit.TotalSeconds;
        var until = existingUntil ?? (overlay.UntilSeconds > 0 ? now + overlay.UntilSeconds : 0);
        var hardLimit = existingHardLimit ?? (now + (overlay.HardLimitSeconds > 0 ? Math.Min(overlay.HardLimitSeconds, ceiling) : ceiling));
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
        if (window is { IsShowing: true, IsBroadcast: false })
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
        if (_current is null && (_window is null || _window.IsBroadcast))
        {
            return;
        }

        _current = null;
        _window?.Dispose();
        _window = null;
        _log.LogInformation("lock lifted");

        if (_broadcast is not null)
        {
            // The broadcast was running under the lock: it takes the screen back.
            ShowBroadcastWindow();
            return;
        }

        _block?.Unblock();
    }

    private void StartBroadcast(Overlay overlay)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ceiling = (long)Defaults.BroadcastHardLimit.TotalSeconds;
        var restated = _broadcast is { } running && running.SessionId == overlay.SessionId;
        if (!restated)
        {
            Volatile.Write(ref _broadcastSinceUnix, now);
            _picture.Clear();
        }

        // The service clamped it already; this only guards against a malformed message.
        Volatile.Write(ref _broadcastHardLimitUnix, now + (overlay.HardLimitSeconds > 0 ? Math.Min(overlay.HardLimitSeconds, ceiling) : ceiling));
        _broadcast = overlay;
        _log.LogInformation("broadcast {Session} {What}", overlay.SessionId, restated ? "re-stated" : "started");

        if (_current is not null)
        {
            // A lock is on the screen and stays there; the broadcast shows when it lifts.
            return;
        }

        if (_window is not { IsShowing: true, IsBroadcast: true })
        {
            ShowBroadcastWindow();
        }
    }

    private void ShowBroadcastWindow()
    {
        var block = _block ??= new InputBlock(_log);
        block.Block();
        _window?.Dispose();
        _window = new OverlayWindow(_log, _picture, _pcName);
        _log.LogInformation("broadcast shown, hooks {Hooks}", block.HooksInPlace ? "in place" : "NOT in place");
    }

    private void StopBroadcast()
    {
        if (_broadcast is null)
        {
            return;
        }

        _broadcast = null;
        _picture.Clear();
        _log.LogInformation("broadcast stopped");
        if (_current is not null)
        {
            // The lock stays as it is.
            return;
        }

        _window?.Dispose();
        _window = null;
        _block?.Unblock();
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
            _broadcast = null; // or lifting the lock would show the broadcast again on the way out
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

        _picture.Dispose();
        _queue.Dispose();
    }
}
