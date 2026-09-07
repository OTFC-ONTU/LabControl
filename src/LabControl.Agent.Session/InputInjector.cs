using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using LabControl.Shared;
using LabControl.Shared.Control;
using LabControl.Shared.Protocol;
using LabControl.Shared.Video;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace LabControl.Agent.Session;

/// <summary>
/// The teacher's mouse and keyboard on the student's desktop (PROTOCOL "Input", D-36): every
/// <c>Input</c> the service relays down the pipe becomes one <c>SendInput</c> call on a
/// thread of its own. The thread is attached to whichever desktop has the input before each
/// call — a SYSTEM process may join the secure desktop, so the teacher can drive a UAC prompt
/// or the lock screen — and a queue between the pipe and the thread keeps the read loop
/// from ever waiting on Windows. Nothing here throws out: a refused injection is reported
/// once per reason as an event and the next message is tried anyway.
/// </summary>
internal sealed class InputInjector : IDisposable
{
    private readonly ILogger _log;
    private readonly ScreenProducer.Reporter _report;
    private readonly BlockingCollection<Input> _queue = new(Defaults.InputQueueLength);
    private readonly Thread _thread;
    private string? _lastProblem;
    private string? _desktop;
    private int _dropped;

    public InputInjector(ILogger log, ScreenProducer.Reporter report)
    {
        _log = log;
        _report = report;
        _thread = new Thread(Run) { Name = "input", IsBackground = true };
        _thread.Start();
    }

    /// <summary>From the pipe's read loop: queued, never waited for; a full queue drops the newest.</summary>
    public void Offer(Input input)
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            if (!_queue.TryAdd(input))
            {
                if (Interlocked.Increment(ref _dropped) == 1)
                {
                    _log.LogWarning("the input queue is full ({Length} messages); dropping input until it drains", Defaults.InputQueueLength);
                }
            }
            else
            {
                Interlocked.Exchange(ref _dropped, 0);
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
            foreach (var input in _queue.GetConsumingEnumerable())
            {
                try
                {
                    Inject(input);
                }
                catch (Exception ex)
                {
                    Problem("failed", $"{input.Kind}: {ex.Message}");
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Inject(Input input)
    {
        if (!AttachToInputDesktop())
        {
            return;
        }

        switch (input.Kind)
        {
            case Input.Types.Kind.MouseMove:
                Send([Mouse(input.X, input.Y, MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE)]);
                break;

            case Input.Types.Kind.MouseButton:
                Send([Mouse(input.X, input.Y, MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE), Button(input)]);
                break;

            case Input.Types.Kind.MouseWheel:
                Send([Mouse(input.X, input.Y, MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE), Wheel(input)]);
                break;

            case Input.Types.Kind.Key:
                Send([Key(input.Vk, input.Scan, input.Pressed)]);
                break;

            case Input.Types.Kind.Text:
                SendText(input.Text);
                break;

            case Input.Types.Kind.CtrlAltDel:
                // The service does this one (SendSAS); it never reaches the pipe.
                _log.LogDebug("ctrl_alt_del reached the helper; the service should have handled it");
                break;

            default:
                _log.LogDebug("input {Kind} is not something this build injects", input.Kind);
                break;
        }
    }

    /// <summary>
    /// The thread joins the input desktop (Default, Winlogon) the way the capture thread does
    /// (D-35 item 3); a failure — no session, a desktop we may not join — is reported once.
    /// </summary>
    private bool AttachToInputDesktop()
    {
        try
        {
            var name = DesktopAccess.AttachToInputDesktop();
            if (name != _desktop)
            {
                _log.LogInformation("injecting input on desktop {Desktop}", name);
                _desktop = name;
            }

            Recovered();
            return true;
        }
        catch (Win32Exception ex)
        {
            Problem("no_desktop", ex.Message);
            return false;
        }
    }

    private static INPUT Mouse(double x, double y, MOUSE_EVENT_FLAGS flags)
    {
        // Absolute coordinates run 0..65535 across the primary display, which is what the
        // helper captures (D-35 item 9), so the normalised picture position maps straight on.
        var input = new INPUT { type = INPUT_TYPE.INPUT_MOUSE };
        input.Anonymous.mi.dx = (int)Math.Round(InputMessages.Clamp(x) * 65535);
        input.Anonymous.mi.dy = (int)Math.Round(InputMessages.Clamp(y) * 65535);
        input.Anonymous.mi.dwFlags = flags | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE;
        return input;
    }

    private static INPUT Button(Input input)
    {
        var (flags, data) = (input.Button, input.Pressed) switch
        {
            (InputMessages.LeftButton, true) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, 0u),
            (InputMessages.LeftButton, false) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP, 0u),
            (InputMessages.RightButton, true) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN, 0u),
            (InputMessages.RightButton, false) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP, 0u),
            (InputMessages.MiddleButton, true) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEDOWN, 0u),
            (InputMessages.MiddleButton, false) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEUP, 0u),
            (InputMessages.XButton1, true) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_XDOWN, PInvoke.XBUTTON1),
            (InputMessages.XButton1, false) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_XUP, PInvoke.XBUTTON1),
            (InputMessages.XButton2, true) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_XDOWN, PInvoke.XBUTTON2),
            (InputMessages.XButton2, false) => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_XUP, PInvoke.XBUTTON2),
            _ => ((MOUSE_EVENT_FLAGS)0, 0u),
        };

        var result = new INPUT { type = INPUT_TYPE.INPUT_MOUSE };
        result.Anonymous.mi.dwFlags = flags;
        result.Anonymous.mi.mouseData = data;
        return result;
    }

    private static INPUT Wheel(Input input)
    {
        var result = new INPUT { type = INPUT_TYPE.INPUT_MOUSE };
        result.Anonymous.mi.dwFlags = input.Button == InputMessages.HorizontalWheel ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_HWHEEL : MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL;
        result.Anonymous.mi.mouseData = unchecked((uint)input.Delta);
        return result;
    }

    private static INPUT Key(int vk, int scan, bool pressed)
    {
        var result = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        result.Anonymous.ki.wVk = (VIRTUAL_KEY)(ushort)vk;
        result.Anonymous.ki.wScan = (ushort)(scan & 0xFF);
        var flags = pressed ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        if (scan > 0xFF)
        {
            // The console sends the 0xE0 prefix with the scan code; Windows wants it as a flag.
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        }

        result.Anonymous.ki.dwFlags = flags;
        return result;
    }

    /// <summary>
    /// Characters as Unicode, one press and release per UTF-16 unit; a surrogate pair is two
    /// units and Windows reassembles it. Layout-independent: the PC types what the teacher
    /// typed, in whatever alphabet.
    /// </summary>
    private void SendText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var inputs = new INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            var down = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
            down.Anonymous.ki.wScan = text[i];
            down.Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE;
            var up = down;
            up.Anonymous.ki.dwFlags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
            inputs[2 * i] = down;
            inputs[2 * i + 1] = up;
        }

        Send(inputs);
    }

    private unsafe void Send(ReadOnlySpan<INPUT> inputs)
    {
        if (inputs.Length == 0)
        {
            return;
        }

        var sent = PInvoke.SendInput(inputs, sizeof(INPUT));
        if (sent == inputs.Length)
        {
            Recovered();
            return;
        }

        // Zero with ERROR_ACCESS_DENIED is UIPI or a blocked desktop; anything else is odd
        // enough to name.
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        Problem("blocked", $"SendInput placed {sent} of {inputs.Length} events: {error.Message}");
    }

    private void Problem(string reason, string message)
    {
        var text = $"[{reason}] {message}";
        if (text == _lastProblem)
        {
            return;
        }

        _lastProblem = text;
        _log.LogWarning("input not injected ({Reason}): {Message}", reason, message);
        _report(Event.Types.Severity.Warning, $"input.{reason}", $"Input from the console is not reaching the desktop: {message}");
    }

    private void Recovered()
    {
        if (_lastProblem is null)
        {
            return;
        }

        _lastProblem = null;
        _log.LogInformation("input reaches the desktop again");
        _report(Event.Types.Severity.Info, "input.recovered", "Input from the console reaches the desktop again.");
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _log.LogDebug("the input thread did not finish in time; it is a background thread and dies with the process");
        }

        _queue.Dispose();
    }
}
