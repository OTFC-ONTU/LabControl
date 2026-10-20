using System.ComponentModel;
using System.Runtime.InteropServices;
using LabControl.Shared;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.System.Power;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace LabControl.Agent.Session;

/// <summary>
/// Keeps the student's monitor on while the overlay shows something (D-84). Setup turns the
/// display off after 20 idle minutes, and under a lock or a broadcast nobody touches the PC —
/// the hooks even swallow what the student does — so without this the monitor went dark in
/// the middle of a demonstration. While held, a thread of its own declares the display
/// required (<c>SetThreadExecutionState</c>, continuous, re-stated every
/// <see cref="Defaults.DisplayKeepAliveInterval"/>), and on taking hold it wakes a monitor
/// that is already off with one injected mouse step there and back — injected input passes
/// the hooks, and an execution state alone does not turn a dark display on. The state belongs
/// to the thread, so it ends with the thread even if the helper dies. Nothing here throws out.
/// </summary>
internal sealed class DisplayKeeper : IDisposable
{
    private readonly ILogger _log;
    private readonly AutoResetEvent _changed = new(false);
    private readonly Thread _thread;
    private volatile bool _wanted;
    private volatile bool _stopping;
    private string? _lastProblem;

    public DisplayKeeper(ILogger log)
    {
        _log = log;
        _thread = new Thread(Run) { Name = "display-keeper", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Whether the overlay shows something now; cheap, called every repaint interval, acts only on a change.</summary>
    public void Want(bool on)
    {
        if (_wanted == on)
        {
            return;
        }

        _wanted = on;
        _changed.Set();
    }

    private void Run()
    {
        var held = false;
        while (!_stopping)
        {
            var wanted = _wanted;
            try
            {
                if (wanted)
                {
                    Hold(wake: !held);
                    held = true;
                }
                else if (held)
                {
                    Release();
                    held = false;
                }
            }
            catch (Exception ex)
            {
                Problem($"{ex.GetType().Name}: {ex.Message}");
            }

            _changed.WaitOne(Defaults.DisplayKeepAliveInterval);
        }

        if (held)
        {
            Release();
        }
    }

    private void Hold(bool wake)
    {
        var flags = EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_DISPLAY_REQUIRED | EXECUTION_STATE.ES_SYSTEM_REQUIRED;
        if (PInvoke.SetThreadExecutionState(flags) == 0)
        {
            Problem("SetThreadExecutionState refused the display requirement");
        }

        if (!wake)
        {
            return;
        }

        _log.LogInformation("keeping the display on while the overlay shows");
        Wake();
    }

    private unsafe void Wake()
    {
        // One mickey right and back: the pointer ends where it was, and the monitor wakes like
        // it does for any input. The thread must be on the input desktop for SendInput to land.
        DesktopAccess.AttachToInputDesktop();
        Span<INPUT> inputs = [Step(1), Step(-1)];
        var sent = PInvoke.SendInput(inputs, sizeof(INPUT));
        if (sent != inputs.Length)
        {
            Problem($"waking the display placed {sent} of {inputs.Length} events: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
    }

    private static INPUT Step(int dx)
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_MOUSE };
        input.Anonymous.mi.dx = dx;
        input.Anonymous.mi.dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE;
        return input;
    }

    private void Release()
    {
        try
        {
            PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
            _log.LogInformation("the display may turn off again by the power plan");
        }
        catch (Exception ex)
        {
            Problem($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Problem(string message)
    {
        if (message == _lastProblem)
        {
            return;
        }

        _lastProblem = message;
        _log.LogWarning("keeping the display on: {Message}", message);
    }

    public void Dispose()
    {
        _stopping = true;
        _changed.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _log.LogDebug("the display-keeper thread did not finish in time; its execution state ends with the process");
            return;
        }

        _changed.Dispose();
    }
}
