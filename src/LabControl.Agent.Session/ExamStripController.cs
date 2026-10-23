using LabControl.Shared;
using LabControl.Shared.Policy;
using LabControl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LabControl.Agent.Session;

/// <summary>
/// The helper's side of an exam (M6 portion 4, D-75): shows the strip the service relays
/// (<c>ServiceMessage.exam_strip</c>) and carries out the opening sweep's close request
/// (<c>close_programs</c>). The service owns the exam — its limits, the whitelist, what is
/// ended — and this only shows and asks. The strip is hidden while nothing is to be shown
/// (no timer, no message, no notice), while a lock or a broadcast covers the screen, and off
/// the student's own desktop; it follows the input desktop like the lock does, and holds the
/// monitor on while it shows (D-84's <see cref="DisplayKeeper"/>). Durations are
/// stamped onto this clock at arrival (D-70 item 6). A thread of its own, so the pipe's read
/// loop never waits on Windows. Nothing here throws out.
/// </summary>
internal sealed class ExamStripController : IDisposable
{
    private readonly ILogger _log;
    private readonly Func<bool> _covered;
    private readonly Lock _lock = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private ExamStrip? _strip;
    private long _untilUnix;
    private string _notice = string.Empty;
    private DateTimeOffset _noticeUntil;
    private ExamStripWindow? _window;
    private readonly AutoResetEvent _changed = new(false);
    private readonly DisplayKeeper _display;

    /// <param name="covered">True while a lock or a broadcast shows; the strip waits underneath.</param>
    public ExamStripController(ILogger log, Func<bool> covered)
    {
        _log = log;
        _covered = covered;
        _display = new DisplayKeeper(log);
        _thread = new Thread(Run) { Name = "exam-strip-control", IsBackground = true };
        _thread.Start();
    }

    /// <summary>From the pipe's read loop: the latest strip, never waited for.</summary>
    public void Offer(ExamStrip strip)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            _strip = strip.Show ? strip : null;
            _untilUnix = strip.Show && strip.UntilSeconds > 0 ? now.ToUnixTimeSeconds() + strip.UntilSeconds : 0;
            if (!strip.Show)
            {
                _notice = string.Empty;
            }
            else if (strip.Notice.Length > 0)
            {
                _notice = strip.Notice;
                _noticeUntil = now + Defaults.ExamNoticeDuration;
            }
        }

        _changed.Set();
    }

    /// <summary>From the pipe's read loop: asks the windows of these processes to close, on a short-lived thread on the input desktop.</summary>
    public void Close(ClosePrograms request)
    {
        var ids = request.ProcessIds.ToHashSet();
        if (ids.Count == 0)
        {
            return;
        }

        // The desktop handle is closed only after the thread that joined it has exited (D-81).
        nint desktop = 0;
        var thread = new Thread(() => desktop = ProgramCloser.CloseWindowsOf(ids, _log)) { Name = "exam-close", IsBackground = true };
        thread.Start();
        _ = Task.Run(() =>
        {
            if (thread.Join(TimeSpan.FromSeconds(30)))
            {
                DesktopAccess.CloseExitedThreadDesktop(desktop);
            }
        });
    }

    private ExamStripContent Content()
    {
        lock (_lock)
        {
            if (_strip is not { } strip)
            {
                return new ExamStripContent(string.Empty, null, string.Empty);
            }

            var now = DateTimeOffset.UtcNow;
            long? remaining = _untilUnix > 0 ? Math.Max(0, _untilUnix - now.ToUnixTimeSeconds()) : null;
            return new ExamStripContent(strip.Message, remaining, now < _noticeUntil ? _notice : string.Empty);
        }
    }

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                _changed.WaitOne(Defaults.OverlayRepaintInterval);
                if (_stop.IsCancellationRequested)
                {
                    break;
                }

                Follow();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "the exam strip failed: {Message}", ex.Message);
            }
        }

        _window?.Dispose();
        _window = null;
    }

    /// <summary>Shows, hides, refreshes or moves the window to the input desktop — whatever the strip, the screen and the desktop say now.</summary>
    private void Follow()
    {
        var wanted = !Content().IsEmpty && !_covered();
        string desktop;
        try
        {
            desktop = DesktopAccess.InputDesktopName();
        }
        catch (Exception)
        {
            desktop = "?";
        }

        // Only on the student's own desktop: never over the logon screen or a UAC prompt.
        wanted &= string.Equals(desktop, "Default", StringComparison.OrdinalIgnoreCase);

        // A student thinking over a question for twenty minutes must not lose the countdown to the
        // display timeout (D-84's keeper; under a lock or a broadcast the overlay's keeper holds it).
        _display.Want(wanted);

        if (!wanted)
        {
            if (_window is not null)
            {
                _window.Dispose();
                _window = null;
                _log.LogInformation("exam strip hidden");
            }

            return;
        }

        if (_window is { IsShowing: true } window && string.Equals(window.DesktopName, desktop, StringComparison.OrdinalIgnoreCase))
        {
            window.Refresh();
            return;
        }

        // A window that could not be created is tried again after a pause, not every second.
        if (_window is { Problem: not null } && DateTimeOffset.UtcNow - _createdAt < FailedRetry)
        {
            return;
        }

        _window?.Dispose();
        _window = new ExamStripWindow(_log, Content);
        _createdAt = DateTimeOffset.UtcNow;
    }

    private static readonly TimeSpan FailedRetry = TimeSpan.FromSeconds(30);
    private DateTimeOffset _createdAt;

    public void Dispose()
    {
        _stop.Cancel();
        _changed.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(4)))
        {
            _log.LogDebug("the exam strip thread did not finish in time; it is a background thread and dies with the process");
        }

        _changed.Dispose();
        _display.Dispose();
    }
}

/// <summary>
/// The opening sweep's polite half (D-75): <c>WM_CLOSE</c> to every visible, unowned top-level
/// window of the named processes on the input desktop, so a program open before the exam can
/// save or ask; the service ends whatever is still running after <see cref="Defaults.ExamSweepGrace"/>.
/// Runs on a thread of its own that joins the input desktop, and does nothing else.
/// </summary>
internal static class ProgramCloser
{
    /// <summary>Returns the input desktop's handle the calling thread joined, for its owner to close once the thread has exited.</summary>
    public static nint CloseWindowsOf(IReadOnlySet<uint> processIds, ILogger log)
    {
        nint desktop = 0;
        try
        {
            DesktopAccess.AttachToInputDesktop(out desktop);
            var asked = 0;
            PInvoke.EnumWindows((hwnd, _) =>
            {
                try
                {
                    uint pid;
                    unsafe
                    {
                        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
                    }

                    if (processIds.Contains(pid) && PInvoke.IsWindowVisible(hwnd) && PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull)
                    {
                        PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, default, default);
                        asked++;
                    }
                }
                catch (Exception)
                {
                    // One window that cannot be asked is ended by the service after the grace.
                }

                return true;
            }, default);
            log.LogInformation("exam: asked {Windows} window(s) of {Processes} process(es) to close", asked, processIds.Count);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "exam: asking programs to close failed: {Message}", ex.Message);
        }

        return desktop;
    }
}
