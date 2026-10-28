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
/// The exam strip on the student's screen (M6 portion 4, D-75; owner decision 2026-10-23): a
/// narrow band at the top centre of the primary monitor with the countdown and the teacher's
/// line, built like the lock's <see cref="OverlayWindow"/> — a raw Win32 popup that is
/// layered, click-through, never activated, topmost and re-asserted topmost every
/// <see cref="Defaults.OverlayRepaintInterval"/>, on a thread of its own that joined the input
/// desktop first, painted by one <c>StretchDIBits</c> of the bitmap
/// <see cref="OverlayText.ComposeStrip"/> draws. It covers a strip of the screen and blocks
/// nothing: the student clicks through it. It is a class of its own, not a second
/// <see cref="OverlayWindow"/>, because that one's window procedure finds its one window in a
/// static. Nothing here throws out.
/// </summary>
internal sealed class ExamStripWindow : IDisposable
{
    private const string ClassName = "LabControlExamStrip";
    private const nuint RepaintTimerId = 1;
    private static readonly uint WmStop = PInvoke.WM_USER + 2;

    private static ExamStripWindow? s_current;
    private static readonly WNDPROC WindowProc = OnMessage;
    private static readonly HWND TopMost = new(-1);
    private static ushort s_classAtom;

    private readonly ILogger _log;
    private readonly Func<ExamStripContent> _content;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _shown = new(false);
    private readonly Lock _lock = new();
    private HWND _hwnd;
    private uint _threadId;
    private nint _desktopHandle;
    private OverlayBitmap? _picture;
    private string _drawn = string.Empty;
    private int _x, _y, _width, _height;
    private bool _disposed;

    /// <param name="content">What to show, read on every tick; the window recomposes only when the words change.</param>
    public ExamStripWindow(ILogger log, Func<ExamStripContent> content)
    {
        _log = log;
        _content = content;
        _thread = new Thread(Run) { Name = "exam-strip", IsBackground = true };
        _thread.Start();
        _shown.Wait(TimeSpan.FromSeconds(5));
    }

    public bool IsShowing => !_hwnd.IsNull && Problem is null;

    public string? DesktopName { get; private set; }

    public string? Problem { get; private set; }

    /// <summary>Asks the window to recompose now rather than at its next tick.</summary>
    public void Refresh()
    {
        if (!_hwnd.IsNull)
        {
            PInvoke.PostMessage(_hwnd, PInvoke.WM_TIMER, RepaintTimerId, default);
        }
    }

    private unsafe void Run()
    {
        try
        {
            var desktop = DesktopAccess.AttachToInputDesktop(out _desktopHandle);
            DesktopName = desktop;
            _threadId = PInvoke.GetCurrentThreadId();
            var module = PInvoke.GetModuleHandle(default(PCWSTR));

            if (s_classAtom == 0)
            {
                fixed (char* className = ClassName)
                {
                    var wc = new WNDCLASSEXW
                    {
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
                        if (error != 1410)
                        {
                            Fail($"RegisterClassEx failed: {new Win32Exception(error).Message}");
                            return;
                        }

                        s_classAtom = 1;
                    }
                }
            }

            Measure();
            s_current = this;
            fixed (char* className = ClassName)
            fixed (char* title = "LabControl exam")
            {
                _hwnd = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                    | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
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

            // Slightly translucent, so the strip reads as LabControl's and not as the student's window.
            PInvoke.SetLayeredWindowAttributes(_hwnd, default, 235, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
            Recompose(force: true);
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            PInvoke.SetWindowPos(_hwnd, TopMost, _x, _y, _width, _height, SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
            PInvoke.SetTimer(_hwnd, RepaintTimerId, (uint)Defaults.OverlayRepaintInterval.TotalMilliseconds, null);
            _log.LogInformation("exam strip up on desktop {Desktop}: {Width}x{Height} at ({X},{Y})", desktop, _width, _height, _x, _y);
            _shown.Set();

            while (PInvoke.GetMessage(out var message, HWND.Null, 0, 0))
            {
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
        }
        catch (Exception ex)
        {
            Fail($"the exam strip thread failed: {ex.Message}");
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
        _log.LogWarning("exam strip: {Problem}", problem);
    }

    /// <summary>The strip's place: top centre of the primary monitor, as wide as what it says needs.</summary>
    private void Measure()
    {
        var screenWidth = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN));
        var screenHeight = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN));
        ExamStripContent? content = null;
        try
        {
            content = _content();
        }
        catch (Exception)
        {
            // Sized for no content; Recompose reports the failure.
        }

        (_width, _height) = OverlayText.StripSize(screenWidth, screenHeight, content);
        _x = (screenWidth - _width) / 2;
        _y = 0;
    }

    private void Recompose(bool force)
    {
        ExamStripContent content;
        try
        {
            content = _content();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "reading the exam strip failed: {Message}", ex.Message);
            return;
        }

        var words = content.Message + "|" + OverlayText.StripRemainingText(content.RemainingSeconds, content.Language) + "|" + content.Notice;
        lock (_lock)
        {
            if (!force && words == _drawn && _picture is not null)
            {
                return;
            }

            _drawn = words;
        }

        OverlayBitmap picture;
        try
        {
            picture = OverlayText.ComposeStrip(content, _width, _height);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "composing the exam strip failed: {Message}", ex.Message);
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
                info.bmiHeader.biHeight = -picture.Height;
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
        var (x, y, width, height) = (_x, _y, _width, _height);
        Measure();
        var moved = (x, y, width, height) != (_x, _y, _width, _height);
        Recompose(force: moved);
        PInvoke.SetWindowPos(_hwnd, TopMost, _x, _y, _width, _height,
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | (moved ? 0 : SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE));
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

                if (message == WmStop)
                {
                    PInvoke.DestroyWindow(hwnd);
                    return (LRESULT)0;
                }

                if (message == PInvoke.WM_MOUSEACTIVATE)
                {
                    return (LRESULT)(nint)PInvoke.MA_NOACTIVATE;
                }

                if (message == PInvoke.WM_CLOSE || message == PInvoke.WM_SYSCOMMAND)
                {
                    // The exam decides when the strip goes, not Alt+F4 or a close request.
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
            window?._log.LogWarning(ex, "the exam strip window procedure failed on message {Message}: {Error}", message, ex.Message);
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

        if (_thread.Join(TimeSpan.FromSeconds(3)))
        {
            DesktopAccess.CloseExitedThreadDesktop(_desktopHandle);
            _desktopHandle = 0;

            // Only once the thread is gone: its finally still sets the event (review finding 10).
            _shown.Dispose();
        }
    }
}
