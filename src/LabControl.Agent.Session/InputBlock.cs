using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LabControl.Agent.Session;

/// <summary>
/// The student's keyboard and mouse, switched off under a lock (M6 portion 1, D-70 item 3):
/// a <c>WH_KEYBOARD_LL</c> and a <c>WH_MOUSE_LL</c> hook on a <b>dedicated thread that does
/// nothing but pump them</b>. Windows silently unhooks a low-level hook whose callback
/// exceeds <c>LowLevelHooksTimeout</c> (300 ms by default), and a JPEG decode or a blit on
/// the same thread would do exactly that — so the callbacks here read one flag, test one
/// bit and return. Every physical event is swallowed while blocking is on; anything flagged
/// <c>LLKHF_INJECTED</c> / <c>LLMHF_INJECTED</c> passes, so the teacher's <c>SendInput</c>
/// remote control still works under the lock. Ctrl+Alt+Del is unblockable by design and
/// is not attempted.
/// <para>
/// <see cref="BlockInput"/> is the fallback only, when a hook cannot be installed: it blocks
/// the teacher's injected input too, and any process on the desktop can call it back off, so
/// the console is told in <see cref="Reason"/> that the lock is weaker than usual.
/// </para>
/// </summary>
internal sealed class InputBlock : IDisposable
{
    // Kept in fields so the delegates the hooks call are never collected.
    private static readonly HOOKPROC KeyboardProc = OnKeyboard;
    private static readonly HOOKPROC MouseProc = OnMouse;
    private static volatile bool s_blocking;

    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;
    private HHOOK _keyboard;
    private HHOOK _mouse;
    private bool _blockInputFallback;
    private string _reason = string.Empty;
    private bool _disposed;

    public InputBlock(ILogger log)
    {
        _log = log;
        _thread = new Thread(Run) { Name = "input-block", IsBackground = true };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>True while every physical keyboard and mouse event is swallowed by the hooks (not the fallback).</summary>
    public bool HooksInPlace => !_keyboard.IsNull && !_mouse.IsNull && s_blocking;

    /// <summary>Input is blocked one way or the other: the hooks, or <c>BlockInput</c>.</summary>
    public bool Blocking => s_blocking && (HooksInPlace || _blockInputFallback);

    /// <summary>Why the block is weaker than the hooks would make it, for <c>OverlayState.reason</c>; empty when the hooks hold.</summary>
    public string Reason => _reason;

    /// <summary>Starts swallowing physical input; installs the fallback if a hook is missing.</summary>
    public void Block()
    {
        if (_disposed)
        {
            return;
        }

        s_blocking = true;
        if (!_keyboard.IsNull && !_mouse.IsNull)
        {
            if (_blockInputFallback)
            {
                // Both hooks arrived after all (a retry); the fallback is no longer needed.
                TryBlockInput(false);
                _blockInputFallback = false;
            }

            _reason = string.Empty;
            return;
        }

        var missing = _keyboard.IsNull && _mouse.IsNull ? "the keyboard and mouse hooks" : _keyboard.IsNull ? "the keyboard hook" : "the mouse hook";
        if (TryBlockInput(true))
        {
            _blockInputFallback = true;
            _reason = $"{missing} could not be installed ({_hookFailure}); BlockInput is in use instead, which also blocks the teacher's remote control";
        }
        else
        {
            _reason = $"{missing} could not be installed ({_hookFailure}) and BlockInput failed too; the student's input is NOT blocked";
        }

        _log.LogWarning("input block: {Reason}", _reason);
    }

    /// <summary>Lets input through again; the hooks stay installed for the next lock.</summary>
    public void Unblock()
    {
        s_blocking = false;
        if (_blockInputFallback)
        {
            TryBlockInput(false);
            _blockInputFallback = false;
        }

        _reason = string.Empty;
    }

    private string _hookFailure = string.Empty;

    private void Run()
    {
        try
        {
            _threadId = PInvoke.GetCurrentThreadId();
            var module = PInvoke.GetModuleHandle(default(PCWSTR));
            _keyboard = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, KeyboardProc, module, 0);
            if (_keyboard.IsNull)
            {
                _hookFailure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                _log.LogWarning("WH_KEYBOARD_LL could not be installed: {Message}", _hookFailure);
            }

            _mouse = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, MouseProc, module, 0);
            if (_mouse.IsNull)
            {
                _hookFailure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                _log.LogWarning("WH_MOUSE_LL could not be installed: {Message}", _hookFailure);
            }
        }
        catch (Exception ex)
        {
            _hookFailure = ex.Message;
            _log.LogWarning(ex, "installing the input hooks failed: {Message}", ex.Message);
        }
        finally
        {
            _ready.Set();
        }

        // The hooks live only while this thread pumps messages; nothing else runs here.
        try
        {
            while (PInvoke.GetMessage(out var message, HWND.Null, 0, 0))
            {
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "the hook thread's message loop ended: {Message}", ex.Message);
        }

        Uninstall();
    }

    private void Uninstall()
    {
        if (!_keyboard.IsNull)
        {
            PInvoke.UnhookWindowsHookEx(_keyboard);
            _keyboard = HHOOK.Null;
        }

        if (!_mouse.IsNull)
        {
            PInvoke.UnhookWindowsHookEx(_mouse);
            _mouse = HHOOK.Null;
        }
    }

    private bool TryBlockInput(bool block)
    {
        try
        {
            if (PInvoke.BlockInput(block))
            {
                return true;
            }

            _log.LogWarning("BlockInput({Block}) failed: {Message}", block, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "BlockInput({Block}) threw: {Message}", block, ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ the callbacks

    /// <summary>One flag, one bit, return — never more (D-70 item 3).</summary>
    private static unsafe LRESULT OnKeyboard(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0 && s_blocking)
        {
            var info = (KBDLLHOOKSTRUCT*)lParam.Value;
            if ((info->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) == 0)
            {
                // Physical: swallowed, whatever the key — Alt+Tab, Alt+Esc, Ctrl+Esc, Win, Alt+F4
                // and every ordinary key alike. Ctrl+Alt+Del never reaches a hook.
                return (LRESULT)1;
            }
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    private static unsafe LRESULT OnMouse(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0 && s_blocking)
        {
            var info = (MSLLHOOKSTRUCT*)lParam.Value;
            if ((info->flags & PInvoke.LLMHF_INJECTED) == 0)
            {
                return (LRESULT)1;
            }
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unblock();
        if (_threadId != 0)
        {
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
        }

        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _log.LogDebug("the hook thread did not finish in time; it is a background thread and dies with the process");
        }

        _ready.Dispose();
    }
}
