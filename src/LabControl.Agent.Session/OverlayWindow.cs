using System.ComponentModel;
using System.Runtime.InteropServices;
using LabControl.Shared;
using LabControl.Shared.Policy;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LabControl.Agent.Session;

/// <summary>
/// The lock picture on the student's screen (M6 portion 1, D-70 item 2): a raw Win32
/// popup — <c>RegisterClassExW</c> + <c>CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
/// WS_POPUP)</c> — covering the whole virtual desktop, on a thread of its own that joins the
/// input desktop first (<c>SetThreadDesktop</c>, D-35 item 3) and then runs its own message
/// loop. It is painted by one <c>StretchDIBits</c> of the BGRA bitmap
/// <see cref="OverlayText.Compose"/> produces, recomposed on a timer every
/// <see cref="Defaults.OverlayRepaintInterval"/> when the countdown text changes, and
/// re-asserted topmost on the same timer — Windows lets other topmost windows above it.
/// Composing and painting happen here, never on the hook thread (D-70 item 3).
/// <para>
/// Nothing here throws out: a window that cannot be created is reported through
/// <see cref="Problem"/> and the controller says so in <c>OverlayState.reason</c>.
/// </para>
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "LabControlOverlay";
    private const nuint RepaintTimerId = 1;
    private static readonly uint WmUpdate = PInvoke.WM_USER + 1;
    private static readonly uint WmStop = PInvoke.WM_USER + 2;

    // The one window this process shows; the static WNDPROC finds it here.
    private static OverlayWindow? s_current;
    private static readonly WNDPROC WindowProc = OnMessage;
    private static readonly HWND TopMost = new(-1); // HWND_TOPMOST
    private static ushort s_classAtom;

    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _shown = new(false);
    private readonly Lock _lock = new();
    private HWND _hwnd;
    private uint _threadId;
    private OverlayContent _content;
    private long _untilUnix;
    private long _hardLimitUnix;
    private string _countdown = string.Empty;
    private OverlayBitmap? _picture;
    private int _x, _y, _width, _height;
    private bool _disposed;

    public OverlayWindow(ILogger log, string message, string pcName, long untilUnix, long hardLimitUnix)
    {
        _log = log;
        _content = new OverlayContent(message, pcName, null);
        _untilUnix = untilUnix;
        _hardLimitUnix = hardLimitUnix;
        _thread = new Thread(Run) { Name = "overlay", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _shown.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>The window is up on the input desktop.</summary>
    public bool IsShowing => !_hwnd.IsNull && Problem is null;

    /// <summary>Why the window is not up, or <c>null</c>.</summary>
    public string? Problem { get; private set; }

    /// <summary>A re-stated lock: new text or limits; the window repaints on its next tick.</summary>
    public void Update(string message, long untilUnix, long hardLimitUnix)
    {
        lock (_lock)
        {
            _content = _content with { Message = message };
            _untilUnix = untilUnix;
            _hardLimitUnix = hardLimitUnix;
            _countdown = string.Empty;
        }

        if (!_hwnd.IsNull)
        {
            PInvoke.PostMessage(_hwnd, WmUpdate, default, default);
        }
    }

    // ------------------------------------------------------------------ the window thread

    private unsafe void Run()
    {
        try
        {
            // Windows are created on the desktop the thread belongs to: join the input desktop
            // first (the student's Default, or Winlogon at the lock screen), as the capture
            // and input threads do (D-35 item 3).
            var desktop = DesktopAccess.AttachToInputDesktop();
            _threadId = PInvoke.GetCurrentThreadId();
            var module = PInvoke.GetModuleHandle(default(PCWSTR));

            if (s_classAtom == 0)
            {
                fixed (char* className = ClassName)
                {
                    var wc = new WNDCLASSEXW
                    {
                        // sizeof() is refused for a struct that holds a delegate; the marshaller's size is the native one.
                        cbSize = ClassSize(),
                        style = WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW,
                        lpfnWndProc = WindowProc,
                        hInstance = module,
                        lpszClassName = className,
                        hbrBackground = HBRUSH.Null,
                    };
                    s_classAtom = PInvoke.RegisterClassEx(wc);
                    if (s_classAtom == 0)
                    {
                        var error = Marshal.GetLastWin32Error();
                        // ERROR_CLASS_ALREADY_EXISTS: a previous window in this process registered it.
                        if (error != 1410)
                        {
                            Fail($"RegisterClassEx failed: {new Win32Exception(error).Message}");
                            return;
                        }

                        s_classAtom = 1;
                    }
                }
            }

            MeasureVirtualScreen();
            s_current = this;
            fixed (char* className = ClassName)
            fixed (char* title = "LabControl")
            {
                _hwnd = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                    (PCWSTR)className,
                    (PCWSTR)title,
                    WINDOW_STYLE.WS_POPUP,
                    _x, _y, _width, _height,
                    HWND.Null, HMENU.Null, (HINSTANCE)module, null);
            }

            if (_hwnd.IsNull)
            {
                Fail($"CreateWindowEx failed on desktop {desktop}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                return;
            }

            Recompose(force: true);
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOW);
            PInvoke.SetWindowPos(_hwnd, TopMost, _x, _y, _width, _height, SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            PInvoke.SetForegroundWindow(_hwnd);
            PInvoke.SetTimer(_hwnd, RepaintTimerId, (uint)Defaults.OverlayRepaintInterval.TotalMilliseconds, null);
            _log.LogInformation("overlay window up on desktop {Desktop}: {Width}x{Height} at ({X},{Y})", desktop, _width, _height, _x, _y);
            _shown.Set();

            while (PInvoke.GetMessage(out var message, HWND.Null, 0, 0))
            {
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
        }
        catch (Exception ex)
        {
            Fail($"the overlay thread failed: {ex.Message}");
        }
        finally
        {
            if (!_hwnd.IsNull)
            {
                PInvoke.KillTimer(_hwnd, RepaintTimerId);
                PInvoke.DestroyWindow(_hwnd);
                _hwnd = HWND.Null;
            }

            if (ReferenceEquals(s_current, this))
            {
                s_current = null;
            }

            lock (_lock)
            {
                _picture?.Dispose();
                _picture = null;
            }

            _shown.Set();
        }
    }

    /// <summary>The native size of <c>WNDCLASSEXW</c>: 80 bytes on x64 and ARM64, 48 on x86.</summary>
    private static uint ClassSize()
    {
        try
        {
            return (uint)Marshal.SizeOf<WNDCLASSEXW>();
        }
        catch (ArgumentException)
        {
            return IntPtr.Size == 8 ? 80u : 48u;
        }
    }

    private void Fail(string problem)
    {
        Problem = problem;
        _log.LogWarning("overlay: {Problem}", problem);
    }

    private void MeasureVirtualScreen()
    {
        _x = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        _y = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        _width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        _height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        if (_width <= 0 || _height <= 0)
        {
            _x = 0;
            _y = 0;
            _width = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN));
            _height = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN));
        }
    }

    /// <summary>On the window thread: a new picture when the countdown text changed (or on demand), then a repaint.</summary>
    private void Recompose(bool force)
    {
        OverlayContent content;
        string countdown;
        lock (_lock)
        {
            var remaining = OverlayText.Remaining(_untilUnix, _hardLimitUnix, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            countdown = OverlayText.CountdownText(remaining);
            if (!force && countdown == _countdown && _picture is not null)
            {
                return;
            }

            content = _content with { RemainingSeconds = remaining };
            _countdown = countdown;
        }

        OverlayBitmap picture;
        try
        {
            picture = OverlayText.Compose(content, _width, _height);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "composing the overlay failed: {Message}", ex.Message);
            return;
        }

        lock (_lock)
        {
            _picture?.Dispose();
            _picture = picture;
        }

        PInvoke.InvalidateRect(_hwnd, (RECT?)null, false);
    }

    private unsafe void Paint()
    {
        var hdc = PInvoke.BeginPaint(_hwnd, out var ps);
        try
        {
            lock (_lock)
            {
                if (_picture is not { } picture)
                {
                    return;
                }

                var info = new BITMAPINFO();
                info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
                info.bmiHeader.biWidth = picture.Width;
                info.bmiHeader.biHeight = -picture.Height; // top-down, as composed
                info.bmiHeader.biPlanes = 1;
                info.bmiHeader.biBitCount = 32;
                info.bmiHeader.biCompression = (uint)BI_COMPRESSION.BI_RGB;
                fixed (byte* bits = picture.Pixels)
                {
                    PInvoke.StretchDIBits(hdc, 0, 0, _width, _height, 0, 0, picture.Width, picture.Height, bits, &info, DIB_USAGE.DIB_RGB_COLORS, ROP_CODE.SRCCOPY);
                }
            }
        }
        finally
        {
            PInvoke.EndPaint(_hwnd, ps);
        }
    }

    private void Tick()
    {
        // The virtual desktop may have changed size (a monitor plugged in): follow it.
        var (x, y, width, height) = (_x, _y, _width, _height);
        MeasureVirtualScreen();
        var resized = (x, y, width, height) != (_x, _y, _width, _height);
        Recompose(force: resized);

        // Other topmost windows may have been raised above this one since the last tick.
        PInvoke.SetWindowPos(_hwnd, TopMost, _x, _y, _width, _height,
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    private static LRESULT OnMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        var window = s_current;
        try
        {
            if (window is not null && hwnd == window._hwnd)
            {
                if (message == PInvoke.WM_PAINT)
                {
                    window.Paint();
                    return (LRESULT)0;
                }

                if (message == PInvoke.WM_ERASEBKGND)
                {
                    return (LRESULT)1;
                }

                if (message == PInvoke.WM_TIMER)
                {
                    window.Tick();
                    return (LRESULT)0;
                }

                if (message == WmUpdate)
                {
                    window.Recompose(force: true);
                    return (LRESULT)0;
                }

                if (message == WmStop)
                {
                    PInvoke.DestroyWindow(hwnd);
                    return (LRESULT)0;
                }

                if (message == PInvoke.WM_CLOSE)
                {
                    // Alt+F4 or a close request from elsewhere: the lock decides, not the window.
                    return (LRESULT)0;
                }

                if (message == PInvoke.WM_SYSCOMMAND)
                {
                    // No system menu, no move/size, no screen saver from a lock window.
                    return (LRESULT)0;
                }

                if (message == PInvoke.WM_DESTROY)
                {
                    PInvoke.PostQuitMessage(0);
                    return (LRESULT)0;
                }
            }
        }
        catch (Exception ex)
        {
            window?._log.LogWarning(ex, "the overlay window procedure failed on message {Message}: {Error}", message, ex.Message);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_hwnd.IsNull)
        {
            PInvoke.PostMessage(_hwnd, WmStop, default, default);
        }
        else if (_threadId != 0)
        {
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
        }

        if (!_thread.Join(TimeSpan.FromSeconds(3)))
        {
            _log.LogDebug("the overlay thread did not finish in time; it is a background thread and dies with the process");
        }

        _shown.Dispose();
    }
}
