using System.Collections.Concurrent;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace LabControl.Agent.Session;

/// <summary>
/// The helper's side of the lock (M6 portion 1, PROTOCOL "Overlay over the pipe"): owns the
/// <see cref="OverlayWindow"/> and the <see cref="InputBlock"/>, applies every <c>Overlay</c>
/// the service relays, and answers <c>HelperMessage.overlay_state</c> — what is on the
/// screen now, whether the hooks hold, and the reason when something is weaker than it
/// should be — on every change and with every status. The service is the policy owner: it
/// decides what is shown and for how long; this only shows it. Requests are queued for a
/// thread of their own, like input (D-36): creating the window and the hooks waits on their
/// threads, and the pipe's read loop never waits on Windows. Nothing here throws out.
/// </summary>
internal sealed class OverlayController : IDisposable
{
    private readonly ILogger _log;
    private readonly Action<OverlayState> _report;
    private readonly string _pcName;
    private readonly Lock _lock = new();
    private readonly BlockingCollection<Overlay> _queue = new(64);
    private readonly Thread _thread;
    private OverlayWindow? _window;
    private InputBlock? _block;
    private Overlay? _current;
    private long _sinceUnix;
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

    /// <summary>On the controller's own thread: shows or lifts, then reports.</summary>
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

    /// <summary>The state as it stands, for the status loop and every Ping.</summary>
    public OverlayState State()
    {
        lock (_lock)
        {
            if (_current is null)
            {
                return new OverlayState { Kind = Overlay.Types.Kind.Unspecified };
            }

            var reasons = new List<string>();
            if (_window is { IsShowing: false })
            {
                reasons.Add(_window.Problem ?? "the overlay window is not up");
            }

            if (_block is { HooksInPlace: false })
            {
                reasons.Add(_block.Reason.Length > 0 ? _block.Reason : "the input hooks are not in place");
            }

            return new OverlayState
            {
                Kind = _window is { IsShowing: true } ? Overlay.Types.Kind.Lock : Overlay.Types.Kind.Unspecified,
                SessionId = _current.SessionId,
                SinceUnix = _sinceUnix,
                UntilUnix = _current.UntilUnix,
                HardLimitUnix = _current.HardLimitUnix,
                InputBlocked = _block is { HooksInPlace: true },
                Reason = string.Join("; ", reasons),
            };
        }
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
        var restated = _current is not null && _current.SessionId == overlay.SessionId;
        if (!restated)
        {
            _sinceUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        _current = overlay;

        // The hooks first, so no click lands between the request and the picture.
        _block ??= new InputBlock(_log);
        _block.Block();

        if (_window is { IsShowing: true })
        {
            _window.Update(overlay.Message, overlay.UntilUnix, overlay.HardLimitUnix);
        }
        else
        {
            _window?.Dispose();
            _window = new OverlayWindow(_log, overlay.Message, _pcName, overlay.UntilUnix, overlay.HardLimitUnix);
        }

        _log.LogInformation("lock {Session} {What}: until {Until}, hard limit {Limit}, hooks {Hooks}",
            overlay.SessionId, restated ? "re-stated" : "shown", overlay.UntilUnix == 0 ? "lifted" : overlay.UntilUnix, overlay.HardLimitUnix,
            _block.HooksInPlace ? "in place" : "NOT in place");
    }

    private void Lift()
    {
        if (_current is null && _window is null)
        {
            return;
        }

        _block?.Unblock();
        _window?.Dispose();
        _window = null;
        _current = null;
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
