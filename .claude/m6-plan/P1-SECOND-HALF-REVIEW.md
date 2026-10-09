# M6 portion 1 — adversarial review of the second half (commit `49726a6`, branch `m6-portion1`)

Read-only review, 2026-09-16. Nothing was built or run. `src/LabControl.Shared/Policy/` was read at
`49726a6` (`git show`), everything else from the working tree at `.claude/worktrees/m6`. Line numbers
refer to those sources. The Windows helper and service were read as the machine would run them; none
of it has been executed anywhere yet.

## 1. BLOCKER

**B1 — Remote control under a lock cannot work as built; D-70 item 3 and the VM drill claim it does.**
`InputBlock.cs:195–223` correctly pass `LLKHF_INJECTED`/`LLMHF_INJECTED` events, but
`OverlayWindow.cs:138–156` creates an *opaque* `WS_POPUP` window covering the whole virtual screen
with no `WS_EX_TRANSPARENT`/`WS_EX_LAYERED`, then calls `SetForegroundWindow` (`:156`) and the first
`SetWindowPos` (`:155`) without `SWP_NOACTIVATE`. Every injected mouse event lands on the overlay
(hit-testing stops at the topmost opaque window) and every injected keystroke goes to the overlay's
focus, where `OnMessage` ignores it. Passing the hook is necessary, not sufficient. D-70 item 3 ("the
teacher's `SendInput` remote control works under a lock"), `FakeScreen.cs:206` ("remote control works
under a lock on a real PC too") and the ROADMAP drill line "the teacher's input must reach the desktop
under the lock" are all false against this code. Two honest fixes: (a) make the window click-through
and non-activating — `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE`, alpha 255 via
`SetLayeredWindowAttributes`, no `SetForegroundWindow` — so the student's physical input is still
swallowed by the hooks while injected input reaches the app underneath (D-70 rejected "a layered
window", but that rejection was about painting, and this is the documented way to get hit-test
transparency on a top-level window); or (b) drop the claim from D-70/ROADMAP/FakeScreen and remove
the drill line. Either way the teacher sees the lock picture in the video (DXGI captures the composed
desktop), so control under a lock is blind; (b) may be the right product answer.

## 2. MAJOR

**M1 — A silently removed hook is reported as `input_blocked = true`.** Windows 7+ removes a
low-level hook *without telling the installer* after one callback exceeds `LowLevelHooksTimeout`
(documented: "the hook is silently removed without being called"). `InputBlock.HooksInPlace`
(`:52`) tests only that `_keyboard`/`_mouse` are non-null — handles that stay non-null after the
removal — so `OverlayController.State()` (`:142`) keeps saying `InputBlocked = true` while every key
passes. The hooks are also installed at the first lock and never uninstalled between locks
(`Unblock` `:97–107` keeps them), so the helper's hook proc sits in the student's input path for the
rest of the session, including through video encoding, GC pauses and any debugger stop — each a
chance to lose the hook before the *next* lock. Fix: install on `Block`, uninstall on `Unblock`, and
while blocking re-install (unhook + hook) every few seconds on the hook thread; a failing
`UnhookWindowsHookEx` is the detection.

**M2 — `until_unix` is an absolute console-clock time judged on the PC's clock; the lab has no time
source.** `LabSession.cs:1772` sets `UntilUnix = console now + duration`; the journal refuses it as
`RefusedAlreadyEnded` when `until < agent now` and otherwise judges it on the PC's clock. These PCs
have no domain, no internet and (CLAUDE.md, hard requirement 9) no NTP, so a PC ten minutes ahead
refuses every "for 5 minutes" lock and a PC behind holds it up to the hour. `AgentLink.ClockSkew`
(`:988`, from `Welcome.server_time_unix`) already measures the offset on the agent: either the
enforcer subtracts it before judging `until_unix`, or the console sends a duration and the agent
computes `until` on its own clock (the proto is still new in this portion). The hard limit is fine —
it is clamped against the agent's own `now`.

**M3 — `PolicyEnforcer.Start` evaluates the journal outside every guard; a throw takes the whole
agent offline.** `PolicyEnforcer.cs:105` `Publish(Journal.OverlayStateOf(...))` — the argument is
evaluated before `Publish`'s `try`, and `:106` `Rearm` → `Journal.NextDeadline` is inside a
`catch (ObjectDisposedException)` only. `AgentService.cs:58` calls `policy.Start()` *before*
`behaviour.Start()` (`:60`) and `supervisor.Start()` (`:64`), inside the catch-all at `:100` that
logs and waits forever. So any exception the journal can still raise after a corrupt `policy.json`
(the foundation review's B2: `"session_id": null` → `ProtoPreconditions.CheckNotNull` in
`OverlayStateLocked`) means the link never dials and the helper never starts, until the service is
restarted — a corrupt policy file becomes an offline PC. The same unguarded evaluation is in
`NoteEnforcement` (`:137`, `:145`), which the pipe read loop calls (`SessionSupervisor.cs:749`),
and in `Program.cs:222` (`journal.Start` in `--restore-policy`, no try; also `PolicyMutex.TryAcquire`
at `:213` can throw per foundation M4). Wrap all four; add a test that a corrupt document still
links.

**M4 — `OverlayController.State()` shares the lock `Apply` holds while creating the window and the
hooks.** `Apply` (`:80`) holds `_lock` through `Show` → `new InputBlock` (`_ready.Wait(5 s)`,
`InputBlock.cs:48`) and `new OverlayWindow` (`_shown.Wait(5 s)`, `OverlayWindow.cs:64`). `State()`
(`:117`) takes the same lock and is called from the helper's status loop (`Program.cs:186`) and
from the pipe read loop on every `Ping` (`Program.cs:202`), so both stall for up to ten seconds —
past `Defaults.HelperSilenceTimeout` (10 s), after which the supervisor kills the helper
(`SessionSupervisor.cs:376`), which the re-assert then restarts, and so on. Keep the reported
fields in volatile snapshot fields written by `Apply` and read by `State()` without the lock, or use
a separate lock for the snapshot.

**M5 — Teacher-facing free text from the agent side is hard-coded English.** `InputBlock.cs:82,
86, 90`, `OverlayController.cs:127, 132`, `SessionSupervisor.cs:724, 748`, `WindowsMachineState.cs`
(none) all produce `OverlayState.reason` strings the tile shows verbatim in `Tile.LockedByWeak`
(`MachineTileViewModel.cs:110`). CLAUDE.md ("UI text is never hard-coded") and CONSTRAINTS
("every teacher-facing string in resources; stable event codes stay constants") point the same
way as the M3 pattern: a stable reason code on the wire (`hooks_missing`, `blockinput`,
`no_helper`, `window_down`) mapped by a console resource, as `CaptureReasonText` does at
`MachineTileViewModel.cs:254`.

## 3. Minor / advisory

- `SessionSupervisor.cs:740–746`: the stale-lock re-lift (`held is null && reported.Kind == Lock`)
  does not compare session ids. Sequence *Unlock S1, Lock S2* with a helper status for S1 in flight
  can send the extra `Unlock` after `Lock S2`, so the helper drops S2; the next status (≤ 2 s) shows
  no lock while S2 is held and the 3 s re-assert brings it back. Compare `reported.SessionId` with
  the last lifted id before lifting again, or skip the re-lift when a session is being applied.
- `RelayOverlayState` `:732–737` publishes `NoteEnforcement(false, "not showing the lock yet")` for
  every helper status that predates the lock's arrival — a one-status flicker of "input not fully
  blocked" on the tile after each lock. Harmless; suppress for the first `ReassertInterval`.
- `OverlayWindow.cs:155–156`: the initial `SetWindowPos` lacks `SWP_NOACTIVATE`; `Tick` (`:307`)
  has it. If B1 is fixed by (a), remove `SetForegroundWindow` too. `WM_DISPLAYCHANGE` is handled by
  the 1 s tick (`:300–304`) — fine; the DPI story is the process-wide per-monitor-V2 declaration
  (`DesktopAccess.DeclareDpiAware`) so `GetSystemMetrics` returns physical pixels — fine.
- `OverlayWindow.cs:161`: `GetMessage` returning -1 is truthy and loops; harmless with `HWND.Null`.
- `InputBlock.cs:197–205`: every physical key-up is swallowed too, so a modifier held at `Block()`
  stays down in the OS until the student taps it after the unlock. Send injected key-ups for
  Shift/Ctrl/Alt/Win on `Block()`, or track and pass the release of keys already down.
- `InputBlock.Dispose` `:236`: `PostThreadMessage(WM_QUIT)` can fail with `ERROR_INVALID_THREAD_ID`
  if the hook thread has not yet reached `GetMessage` (no queue yet); the 2 s join then times out.
  Tiny window; call `PeekMessage` once in `Run` before `_ready.Set()`.
- `BlockInput` fallback (`InputBlock.cs:83`) is called on the controller thread and undone from
  `Unblock`/`Dispose` on whichever thread — allowed; the OS also unblocks when the calling thread
  or process ends, so a controller-thread crash (process ends) is covered. Verified.
- `WindowsMachineState.CurrentOverlay` (`:34, :42, :69, :82`) is written and never read outside the
  class; the re-assert goes through `PolicyEnforcer.ReassertOverlay` → journal. Dead code; remove or
  use it.
- `OverlayController.Show` `:180–181` recreates the window on every 3 s re-assert while creation
  keeps failing (`IsShowing: false`), each attempt waiting up to 5 s on the controller thread. Rate
  the retry or keep the failed window and only retry on a new session id.
- `PolicyEnforcer.Apply` `:196`: when `Guarded` runs the pass but the pass *throws*, nothing is
  published and the console waits for a state that never comes (the tile keeps the old state; no
  `problem`). Publish a `RefusedBusy`-shaped problem from the catch in `Guarded` too.
- `AgentConnection.ApplyOverlayState` `:135–136`: `InputBlocked`/`Reason` changes do not count as
  "changed", so `LockText`'s weakness only refreshes on the next unrelated `MachinesChanged`.
- `LockTests.cs:51` comment: the mute PC *does* answer now (`AgentLink.cs:1152–1163` sends
  `NotInThisBuild`), so its tile carries a `LockProblem`; the assertions still hold, the comment is
  stale.
- `LabSession.Lock` `:1778` logs the lock message into the console events — not a secret, fine;
  `MaxLength = 500` in the dialog caps the picture (`LockScreensDialog.cs:29`) — good.
- Docs: ROADMAP's portion-1 line says "not yet run on Windows" and the drill is explicit; CLAUDE.md
  and AGENTS.md carry matching (not identical, not contradictory) status paragraphs — both must lose
  the remote-control claim with B1.

## 4. Verified clean

- **Thread/desktop discipline in the helper**: `OverlayWindow.Run` (`:99`) calls
  `DesktopAccess.AttachToInputDesktop()` → `SetThreadDesktop` as its first act, before
  `RegisterClassEx`/`CreateWindowEx`; the message loop (`:161`) is on that same STA thread; the
  `WNDPROC` is a `static readonly` delegate (`:37`); `WM_NCCREATE`/`WM_CREATE` fall to
  `DefWindowProc` because `_hwnd` is not yet set; `WM_CLOSE`/`WM_SYSCOMMAND` are eaten; `Dispose`
  posts `WmStop` → `DestroyWindow` on the window thread → `WM_DESTROY` → `PostQuitMessage`.
- **`BITMAPINFO`**: `biSize = sizeof(BITMAPINFOHEADER)` (40), `biHeight = -height` for the
  top-down Skia bitmap, 32 bpp, `BI_RGB`, `DIB_RGB_COLORS`; the source is `SKColorType.Bgra8888`
  premul (`JpegCodec.PixelFormat`), row stride `width*4` = DWORD aligned. Correct.
- **Hooks**: installed and uninstalled on the thread that pumps them (`InputBlock.Run` `:117/:124`,
  `Uninstall` `:155`); the procs read one volatile flag and one bit and return; `LLKHF_INJECTED`
  (0x10) via the CsWin32 enum, `LLMHF_INJECTED` (0x01, the correct mouse value — the prompt's 0x10
  applies to the keyboard struct only); `HOOKPROC` delegates are `static readonly`; nothing touches
  Ctrl+Alt+Del; Ctrl+Shift+Esc, Win+L and everything else physical are swallowed with the rest.
- **Service ordering**: `policy.Start()` (`AgentService.cs:58`) precedes `behaviour.Start()` and
  `supervisor.Start()`; `Journal.Start` runs inside `Guarded` under the kernel mutex; the lease is
  acquired and disposed on the same thread (synchronous `Guarded`), so the foundation's M1
  thread-affinity trap is avoided; `DisposeAsync` is reached on `sc stop` because `ExecuteAsync`
  unwinds through the `await using`s on cancellation, and the enforcer is declared after the
  supervisor so its stop pass runs before the helper is ended; the timer is disposed; overlapping
  ticks are serialized by the mutex (or the journal's own lock when the mutex is unavailable).
- **Re-assert on helper connect** (`SessionSupervisor.cs:664`) runs after `helper.Connected = true`
  and `helper.Server = server` (`:645–653`), so `SendToHelper` sees the helper; the pipe buffers the
  `Overlay` until the helper's read loop, whose `OverlayController` exists before the loop
  (`Session/Program.cs:171`). The Unlock-vs-re-assert race is benign: both passes take the mutex,
  `Journal.ReassertOverlay` re-reads `_policy.Overlay` under the lock, and `Helper.SendAsync`'s
  semaphore keeps pipe order.
- **D-30 item 1**: `SessionLauncher.cs` is not in the commit; the helper still runs with the
  service's token on `winsta0\default`.
- **Console**: the badge reads `connection.OverlayState`/`OverlayLocked` only
  (`MachineTileViewModel.RefreshLock` `:232`), which only `LabSession.ApplyOverlayState` writes from
  `AgentMessage.overlay_state`; the request never touches it. `set_by_instance` is compared with
  `Instance.InstanceId` (ordinal-ignore-case) and the agent's value is the validated certificate's
  id (`AgentLink.cs:991`), which `LockTests.cs:68` proves equal. A refusal keeps the last screen
  state and sets `OverlayProblem`; the next problem-free state clears it. Fresh `session_id` per
  `Lock` call; no PC count anywhere; `LabCloseStep` (`LabSession.cs:70–82`) has no lock step; every
  console string is in `Strings.resx`.
- **FakeAgent**: the real `PolicyEnforcer` over the PC's own `policy.json` with the wall clock and a
  per-PC mutex name; `FakeScreen.Advance` draws `OverlayText.Compose` over the whole desktop so the
  mosaic shows the lock; `OpenDesktop` applies a lock set before video started.

## 5. Tests

- The withdrawn-console test (`LockTests.cs:225–228`) reaches into `AgentLink._revocations` by
  name and merges the entry directly. It does prove the production gate: `DeliverOverlayAsync`
  consults that very set through `OwnershipGate.Refusal` and answers on the wire, and the tile shows
  the problem with no lock. It does not prove that a wire-merged `Revocation` lands in the same set
  (M5 code, untouched) and it is brittle to a rename; acceptable with a comment naming the field
  contract.
- Missing against CONSTRAINTS portion 1: (1) a lock survives the console's departure — lock, then
  `Disconnect`/switch, PC still locked, console returns and sees `set_by_instance` = itself;
  (2) a *second* console of the same lab ends a lock and the first console's tile names it
  (`NameOfInstance` / `Lock.ByUnknown` never exercised); (3) a corrupt/half-written `policy.json`
  and the PC still links (M3 above); (4) the busy-mutex refusal *on the wire* (`RefusedBusy` —
  `PolicyEnforcerTests:186` only checks `Start()` returns null); (5) the console's "skipped"
  path for an unlinked/outdated PC; (6) the helper re-assert and `--restore-policy` are Windows-only
  and go to the drill.
- No tautologies found; `LockTests.cs:235` is a real negative assertion.

## 6. The three things most likely to fail on the Windows VM (look here first)

1. **Remote control under the lock** — the drill's "move the mouse and type — the teacher's input
   must reach the desktop" will not happen (B1). Decide (a) or (b) before the drill, not after.
2. **Hook loss under load** — with video streaming at 24 Mbit/s and the hooks living forever in
   the helper, one 300 ms stall removes a hook silently and the tile keeps saying "blocked" (M1).
   In the drill, lock while streaming *full* mode and scroll a browser for a minute, then type.
3. **Helper liveness during lock/unlock** — `State()` blocking behind window/hook creation on a
   slow VM can cross the 10 s silence timeout and cycle the helper (M4); watch for
   `session.exe has been silent` followed by a re-assert loop in the service log.
