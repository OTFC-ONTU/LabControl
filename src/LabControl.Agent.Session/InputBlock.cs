using System.ComponentModel;
using System.Runtime.InteropServices;
using LabControl.Shared;
using LabControl.Shared.Policy;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LabControl.Agent.Session;

/// <summary>
/// The student's keyboard and mouse, switched off under a lock (M6 portion 1, D-70 item 3):
/// a <c>WH_KEYBOARD_LL</c> and a <c>WH_MOUSE_LL</c> hook on a <b>dedicated thread that does
/// nothing but pump them</b>. Windows silently unhooks a low-level hook whose callback
/// exceeds <c>LowLevelHooksTimeout</c> (300 ms by default) — without telling the installer,
/// and the handle it returned stays non-null — so the callbacks here read one flag, test
/// one bit and return, and the hooks are <b>re-armed on a timer</b> while blocking (every
/// <see cref="Defaults.OverlayRepaintInterval"/>, D-70 item 8): a fresh pair is installed
/// first and the old pair unhooked second, so no event passes in between, and
/// <see cref="HooksInPlace"/> reports the outcome of the <i>most recent</i> install rather
/// than the handles. The hooks go up on <see cref="Block"/> and come down on
/// <see cref="Unblock"/>; between locks the helper's hook procedure is not in the student's
/// input path at all. Every physical event is swallowed while blocking is on; anything
/// flagged <c>LLKHF_INJECTED</c> / <c>LLMHF_INJECTED</c> passes, so the teacher's
/// <c>SendInput</c> remote control still works under the lock. Ctrl+Alt+Del is unblockable
/// by design and is not attempted.
/// <para>
/// <see cref="BlockInput"/> is the fallback only, when a hook cannot be installed: it blocks
/// the teacher's injected input too, and any process on the desktop can call it back off, so
/// the console is told in <see cref="Reason"/> (stable <see cref="OverlayReasons"/> codes)
/// that the lock is weaker than usual; the Win32 detail goes to the log.
/// </para>
/// </summary>
internal sealed class InputBlock : IDisposable
{
    // Kept in fields so the delegates the hooks call are never collected.
    private static readonly HOOKPROC KeyboardProc = OnKeyboard;
    private static readonly HOOKPROC MouseProc = OnMouse;
    private static volatile bool s_blocking;

    private static readonly uint WmInstall = PInvoke.WM_USER + 11;
    private static readonly uint WmUninstall = PInvoke.WM_USER + 12;

    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _applied = new(false);
    private uint _threadId;
    private nint _desktopHandle;
    private volatile string? _desktopName;

    // Hook-thread state: only ever touched on the hook thread.
    private HHOOK _keyboard;
    private HHOOK _mouse;
    private nuint _rearmTimer;

    // What the rest of the helper reads, written by whichever thread decided it.
    private volatile bool _hooksInstalled;
    private volatile bool _blockInputFallback;
    private volatile string _reason = string.Empty;
    private volatile string _hookFailure = string.Empty;
    private bool _disposed;

    public InputBlock(ILogger log)
    {
        _log = log;
        _thread = new Thread(Run) { Name = "input-block", IsBackground = true };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>True while blocking and the most recent install put both hooks up — never inferred from a handle.</summary>
    public bool HooksInPlace => s_blocking && _hooksInstalled;

    /// <summary>Input is blocked one way or the other: the hooks, or <c>BlockInput</c>.</summary>
    public bool Blocking => s_blocking && (_hooksInstalled || _blockInputFallback);

    /// <summary><see cref="OverlayReasons"/> codes for <c>OverlayState.reason</c>, space-separated; empty when the hooks hold.</summary>
    public string Reason => _reason;

    /// <summary>The Win32 words behind <see cref="Reason"/>, for the log; empty when the hooks hold.</summary>
    public string Detail => _hookFailure;

    /// <summary>
    /// Starts swallowing physical input: installs the hooks on the hook thread and waits for
    /// the outcome; installs the fallback if a hook is missing. Safe to call again for a
    /// re-stated lock — the hooks are simply re-armed.
    /// </summary>
    public void Block()
    {
        if (_disposed)
        {
            return;
        }

        s_blocking = true;
        if (!Request(WmInstall))
        {
            _hooksInstalled = false;
            _hookFailure = "the hook thread did not answer";
        }

        if (_hooksInstalled)
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

        if (!_blockInputFallback && TryBlockInput(true))
        {
            _blockInputFallback = true;
        }

        _reason = _blockInputFallback
            ? OverlayReasons.Join([OverlayReasons.HookRefused, OverlayReasons.BlockInputFallback])
            : OverlayReasons.Join([OverlayReasons.HookRefused, OverlayReasons.InputOpen]);
        _log.LogWarning("input block: {Reason} ({Detail}); {Fallback}", _reason, _hookFailure,
            _blockInputFallback ? "BlockInput is in use instead, which also blocks the teacher's remote control" : "BlockInput failed too; the student's input is NOT blocked");
    }

    /// <summary>Lets input through again: the fallback is undone and the hooks come down; nothing of this stays in the input path.</summary>
    public void Unblock()
    {
        s_blocking = false;
        if (_blockInputFallback)
        {
            TryBlockInput(false);
            _blockInputFallback = false;
        }

        Request(WmUninstall);
        _reason = string.Empty;
    }

    /// <summary>Posts a request to the hook thread and waits for it to have been handled.</summary>
    private bool Request(uint message)
    {
        if (_threadId == 0 || !_thread.IsAlive)
        {
            return false;
        }

        _applied.Reset();
        if (!PInvoke.PostThreadMessage(_threadId, message, default, default))
        {
            _log.LogWarning("could not post to the hook thread: {Message}", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return false;
        }

        return _applied.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// The desktop the hook thread joined, or <c>null</c> if it could not join one. Low-level
    /// hooks only see input on that desktop: a block made while the lock followed the student to
    /// the Ctrl+Alt+Del screen stays there, and must not be reused for the next lock or broadcast
    /// on the ordinary desktop (PC-13, 2026-10-26).
    /// </summary>
    public string? DesktopName => _desktopName;

    // ------------------------------------------------------------------ the hook thread

    private void Run()
    {
        try
        {
            // Hooks are desktop-scoped, just like the overlay window. A fresh pair after
            // a desktop change must join that desktop before creating its message queue.
            _desktopName = DesktopAccess.AttachToInputDesktop(out _desktopHandle);
            _threadId = PInvoke.GetCurrentThreadId();
            // A thread has no message queue until it asks for one; PostThreadMessage to a
            // thread without a queue fails with ERROR_INVALID_THREAD_ID.
            PInvoke.PeekMessage(out _, HWND.Null, PInvoke.WM_USER, PInvoke.WM_USER, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
        }
        catch (Exception ex)
        {
            _hookFailure = ex.Message;
            _log.LogWarning(ex, "the hook thread could not start: {Message}", ex.Message);
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
                if (message.hwnd.IsNull)
                {
                    if (message.message == WmInstall)
                    {
                        Install();
                        _applied.Set();
                        continue;
                    }

                    if (message.message == WmUninstall)
                    {
                        Uninstall();
                        _applied.Set();
                        continue;
                    }

                    if (message.message == PInvoke.WM_TIMER)
                    {
                        if (s_blocking)
                        {
                            Install();
                        }

                        continue;
                    }
                }

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

    /// <summary>
    /// On the hook thread: a fresh pair of hooks, the old pair unhooked only afterwards, so
    /// no physical event slips through the swap. An old hook that Windows had already
    /// removed fails to unhook — that is the detection, and it is logged as such.
    /// </summary>
    private void Install()
    {
        var module = PInvoke.GetModuleHandle(default(PCWSTR));
        var failure = string.Empty;

        var keyboard = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, KeyboardProc, module, 0);
        if (keyboard.IsNull)
        {
            failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            _log.LogWarning("WH_KEYBOARD_LL could not be installed: {Message}", failure);
        }
        else
        {
            Replace(ref _keyboard, keyboard, "keyboard");
        }

        var mouse = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, MouseProc, module, 0);
        if (mouse.IsNull)
        {
            failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            _log.LogWarning("WH_MOUSE_LL could not be installed: {Message}", failure);
        }
        else
        {
            Replace(ref _mouse, mouse, "mouse");
        }

        _hooksInstalled = !keyboard.IsNull && !mouse.IsNull;
        _hookFailure = failure;

        if (_rearmTimer == 0)
        {
            _rearmTimer = PInvoke.SetTimer(HWND.Null, 0, (uint)Defaults.OverlayRepaintInterval.TotalMilliseconds, null);
            if (_rearmTimer == 0)
            {
                _log.LogWarning("the hook re-arm timer could not be started: {Message}", new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
        }
    }

    private void Replace(ref HHOOK slot, HHOOK fresh, string which)
    {
        var old = slot;
        slot = fresh;
        if (old.IsNull)
        {
            return;
        }

        if (!PInvoke.UnhookWindowsHookEx(old))
        {
            // Windows had already removed it — silently, after a callback that stalled — and
            // the student's input was passing until this re-arm.
            _log.LogWarning("the previous {Which} hook was no longer installed (Windows removed it: {Message}); re-armed", which, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }

    private void Uninstall()
    {
        _hooksInstalled = false;
        if (_rearmTimer != 0)
        {
            PInvoke.KillTimer(HWND.Null, _rearmTimer);
            _rearmTimer = 0;
        }

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
        else
        {
            DesktopAccess.CloseExitedThreadDesktop(_desktopHandle);
            _desktopHandle = 0;
        }

        _ready.Dispose();
        _applied.Dispose();
    }
}
