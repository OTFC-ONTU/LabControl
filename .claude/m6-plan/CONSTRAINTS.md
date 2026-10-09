# M6 plan — settled constraints (recovered 2026-09-16)

The first M6 plan (2026-09-09) and its adversarial review were lost with the session
scratchpad. This file records everything that was settled, so the regenerated plan starts
from the corrected state. Everything here is decided; do not re-open it.

## Owner decisions (2026-09-09, binding)

1. **Lock hard limit = 1 hour** (`Defaults.LockHardLimit`). Rationale: a lock is "look at
   me" for minutes, not a lesson mode; a short ceiling means fast self-recovery.
2. **Exam start sweeps already-open programs** not on the whitelist (closes them). The exam
   dialog states this plainly above *Start* so the teacher can warn the room.
3. **A `teacher`-access console (D-56, no lab key) may do everything in M6**: lock,
   broadcast, exam, internet control and profile reset. `self_update` and `rekey` stay the
   only role-gated jobs. Becomes an explicit line in D-56's terms.
4. **Any console of the same lab may end a lock/exam/policy it can see**, including one
   that took the PC over. `set_by_instance` on `OverlayState`/`ExamState`/`InternetState`;
   the console shows whose it is. (Orchestrator note: no confirmation dialog was asked
   for; add one only if the owner asks.)

## Milestone shape

Six portions, in this order (2 and 3 could swap; 4 needs 3; 5 needs 4; 6 last):

1. **Policy journal + Lock** — `policy.json` on the agent, restore-first/re-apply-second,
   agent-clamped hard limits, the Win32 overlay in the helper, `Lock`/`Unlock` in the console.
2. **Broadcast** — console `IScreenSource` (CoreGraphics on macOS, GDI on Windows),
   one encode fanned out to N latest-wins subscribers over a new server-streaming
   `PullBroadcast` RPC the agent dials. `ConsoleMessage.broadcast = 10` becomes `reserved`
   (in the message body, NOT inside the `oneof` — protoc rejects that).
3. **Internet control** — profile `DefaultOutboundAction = Block` + owned allow rules
   (group `Defaults.InternetFirewallRuleGroup`), loopback resolver with one accumulating
   address rule, exact DNS restore, `InternetState` gains `enforcing`/`problem`/`source`.
4. **Exam mode** — four independent switches; whitelist enforced in the **service** (not
   the helper — D-30 restarts the helper at logon/logoff), judging session+token before
   name; opening sweep (decision 2); suspended for power jobs.
5. **Collect work + profile reset** — new additive `RequestUpload` RPC (the agent asks for
   the D-42 grant it needs, since size/hash are unknown until the zip exists);
   `DeleteProfile` (not `Win32_UserProfile.Delete` — Setup already uses `DeleteProfile`;
   amend ARCHITECTURE §6); light reset = bounded wipe of Desktop/Documents/Downloads;
   account-off installations (D-40) refuse profile reset explicitly, tested.
6. **Departure, drills, docs** — `DepartureReport` extended; `ProtocolVersion` → 2; D-56
   amendment; the drills; ROADMAP/ARCHITECTURE/PROTOCOL close-out.

Decision numbers: `D-61`…`D-67` are unused in DECISIONS.md (it jumps D-60 → D-68); do not
reuse them. M6 takes **D-69 … D-79**:

| | |
|---|---|
| D-69 | One policy journal on the agent: restore unconditionally and document-independently first, the document only decides what is re-applied; named mutex; OnStop restore; watchdog task |
| D-70 | Overlay = raw Win32 window in the helper composed with SkiaSharp; a lock has its own 1 h hard limit; hooks on a dedicated thread |
| D-71 | Broadcast is a server-streaming `PullBroadcast` the agent dials, not frames on `Link` |
| D-72 | Console capture: CoreGraphics on macOS, GDI on Windows (DXGI later, with numbers); one encode shared by every PC; downscale inside the screen source |
| D-73 | Internet enforcement sets the profile default outbound action and always allows `agent.exe`; the exemption is the last rule removed and the first added; `FirewallEnabled` read per profile |
| D-74 | The whitelist resolver keeps one accumulating allow rule; a policy that cannot be lifted is never applied |
| D-75 | The whitelist runs in the service, judges by session and token before name, sweeps once at start (`System.Management`) |
| D-76 | Exam presets (in `BackupPayload`/`LabBackup.UpgradeNested`, excluded from `.lclab`), and how the exam's internet switch hands back to a standalone policy; exam-internet limit = `min(ExamHardLimit, exam's own)` |
| D-77 | `RequestUpload`; collected work belongs to the delivering instance |
| D-78 | Profile reset uses `DeleteProfile`; light reset is a bounded wipe of three known folders |
| D-79 | M6 in six portions, and the departure contract for lock, broadcast, exam and internet |

## §0 — the policy journal (corrected, binding)

`C:\ProgramData\LabControl\policy.json`, schema 1, at most one of each: overlay session,
exam session, standalone internet policy, exam's internet policy; plus the **original
machine state each displaced** (per-profile `DefaultOutboundAction`, per-interface DNS with
DHCP/static origin, owned firewall rules). Each session: `since`, `until` (0 = until lifted),
clamped `hard_limit`, `set_by_instance`. Displaced settings marked applied only after native
read-back (the D-47 discipline).

**Service start, `agent.exe --restore-policy`, and `OnStop` — all under the named kernel
mutex `Global\LabControl.Policy`** (service and watchdog task both start at boot; JsonStore
protects bytes, not decisions):

0. Read `FirewallEnabled` per profile; a disabled profile means "cannot enforce", never
   "enforcing".
1. **Restore unconditionally, BEFORE the document is opened or read** (the reader is a
   delegate so a disk error takes the same path as a corrupt file). Every value written is
   the Windows default, so no journal is needed to know it is right. Order: every profile's
   default outbound → Allow first (a stale allow is harmless, a stale block is a brick); then
   delete the owned rule group with the `agent.exe` exemption rule LAST; DNS on any loopback
   interface → DHCP; stop the resolver; delete the watchdog task. Overlay needs no restore.
2. Only now read `policy.json`. Unreadable / newer schema / foreign lab / **any null
   field (`lab_id`, `policy`, `displaced`) — must not throw** → `policy.unreadable`,
   nothing re-applied, never a reason to refuse to start.
3. Refine the restore from the document (a static DNS list back exactly; a profile that
   was genuinely block-all before). **A failed refine keeps `DisplacedSettings`** until a
   restore confirms — never discard the teacher's original values on failure.
4. Confirm by read-back; failures → `policy.restore_failed`, machine left as open as
   possible; `MachineOpen` is **false** if anything failed (never true on a failed lift).
5. Only now the clock: re-apply each session iff `now < until` (or `until == 0`) AND
   `now < hard_limit`, **re-clamping the stored `hard_limit` against the agent's ceiling
   on every re-apply** (a hand-edited or clock-moved-back document must not yield a
   far-future limit). A lift that fails keeps the session so the next pass retries.
6. Save the document, whatever happened.

The brief unrestricted window in 1–5 is deliberate ("comes back unrestricted before
anything is re-applied").

**Hard limits** (agent's, never the console's; clamp to `min(proposed, now + ceiling)`,
proposed ≤ 0 → ceiling; report what is held): exam 4 h, standalone internet 8 h, exam's
internet `min(ExamHardLimit, exam's own)`, **lock 1 h**, broadcast 30 min (and dies with
its stream).

**OnStop is also a restore**: `sc failure` recovery fires on abnormal exit only; a graceful
`sc stop` otherwise leaves a whitelist in force for hours. Document is NOT cleared on stop.

**Watchdog**: while (and only while) a firewall/DNS policy is applied, a scheduled task
`LabControl Policy Restore` runs `agent.exe --restore-policy` as SYSTEM at startup and once
at `hard_limit + 5 min`; same mutex, same restore; deleted after a clean one. The
`UpdateRecovery.Arm` pattern. Does not survive binary deletion — README documents the two
`netsh` lines rather than implying coverage. **`self_update` is refused while a policy is
applied** (the `agent.exe` allow rule is pinned to `app\<version>\`).

**Ownership gate** for `Overlay`, `ExamMode`, `InternetPolicy`: checked BEFORE applying,
on the same read loop as `RevocationState`; **fails closed when `LinkedInstanceId is null`**
(unlike `LeaveIfConsoleRevoked`, whose behaviour must not change). A refusal answers
`OverlayState.problem` on the wire **without echoing a `Kind` that is not showing** and
without overwriting the enforcer's true latest state. When `LeaveIfConsoleRevoked` drops the
link, an applied policy keeps its own hard limit — never silently orphaned, never silently
lifted.

## Portion 1 specifics (corrected)

- Overlay: `RegisterClassExW`/`CreateWindowExW(WS_EX_TOPMOST|WS_EX_TOOLWINDOW, WS_POPUP)`
  on its own thread after `SetThreadDesktop` on the input desktop (D-35 item 3); painted
  by one `StretchDIBits` of a BGRA bitmap `OverlayText.Compose` (SkiaSharp) produces.
- **Hooks on a dedicated thread that does nothing but the hook**: `WH_KEYBOARD_LL` +
  `WH_MOUSE_LL` swallow Alt+Tab, Alt+Esc, Ctrl+Esc, LWin/RWin, Alt+F4 and every mouse event
  — but pass `LLKHF_INJECTED`/`LLMHF_INJECTED` so the teacher's remote control works
  under a lock. Windows silently drops a hook that exceeds `LowLevelHooksTimeout` (300 ms),
  so JPEG decode / blit must never share that thread. `BlockInput` is the fallback only,
  reported in `OverlayState.reason`. Ctrl+Alt+Del is unblockable and the UI says so.
- Service re-asserts the current `Overlay` to a freshly connected helper (D-30 restarts it
  at logon/logoff), as it re-sends `VideoControl`; `OverlayState.reason` names the gap.
- **A lock does NOT end on lab departure** (it has its own limit). `LabCloseStep` gains
  only a `Broadcast` step (portion 2); `LabSwitchTests.cs:231` asserts the step sequence
  and must be updated then. The enum's numeric values are not persisted anywhere
  (only logged by name and used by a test hook), so renumbering is safe.
- Proto (outside the frozen subset): `Overlay` gains `session_id = 3`, `until_unix = 4`,
  `hard_limit_unix = 5`; new `OverlayState { kind=1, session_id=2, since_unix=3,
  until_unix=4, hard_limit_unix=5, input_blocked=6, reason=7, set_by_instance=8,
  problem=9 }`; `AgentMessage.overlay_state = 12`; `HelperMessage.overlay_state = 5`.
  Frozen subset (`Hello`, `Heartbeat`, `Job{self_update}`, `JobResult`) untouched.
- Every teacher-facing string in resources (`OverlayStrings.resx` in Shared for the
  student screen; `Strings.resx` in the console); stable event codes stay constants.

## Portion 2 specifics (corrected)

- `AgentConnection._outgoing` is unbounded → frames must not go on `Link`. `PullBroadcast`
  mirrors `PushVideo`: own HTTP/2 stream, gRPC flow control per PC, dies with the session.
- `VideoSettings` has no width; only THUMBNAIL scales → **downscale inside the console's
  screen source** (`BroadcastWidth = 1280`), `ScreenProducer` itself unchanged.
- Aggregate pacer: `min(BroadcastBitsPerSecond, BroadcastTotalBitsPerSecond / subscribers)`,
  start 24 Mbit/s total (D-36 item 11). D-37 auto quality driven by the aggregate waits.
- macOS: `CGDisplayCreateImage` via `DllImport` (no NuGet), `CGPreflightScreenCaptureAccess`
  / `CGRequestScreenCaptureAccess`, first-run explanation, restart after the tick, ad-hoc
  signing re-prompts after every rebuild (drill against one built `.app`), `dotnet run`
  attaches the grant to the terminal.
- Windows: GDI (`BitBlt` + `TileDiff`) via `DllImport`; console stays single `net10.0`.
  Screen sources live in `Shared/Video/` (it has `AllowUnsafeBlocks`).
- "Readable from the back": define it as *14 pt editor text legible at 5 m on a 1366×768
  panel at 1280 wide* before the drill.

## Portion 3 specifics (corrected)

- Windows Firewall: block beats allow → a whitelist must set the profile default outbound
  action; the program-scoped `agent.exe` allow rule is what keeps every PC online under
  *blocked* (LocalSubnet alone is not enough: MacBook on Wi-Fi vs wired PCs).
- Restore order: default outbound → Allow first, exemption rule removed last.
- Presets/policies persisted in the console go into `BackupPayload` + `UpgradeNested`.

## Portion 4 specifics (corrected)

- Whitelist in the service; never-touch set hard-coded (session, shell, OS, LabControl);
  the rename hole (a renamed exe passes) stated in one sentence in the UI.
- Exam's internet policy hard limit = `min(ExamHardLimit, exam's own)`.
- A standalone policy set for the lesson survives an exam that starts and ends inside it.

## Portion 6 specifics

- Fail-safe drill needs an explicitly short hard limit plus a separate clamp test — the
  ROADMAP must say so. Drill 4 split into `sc stop` / kill / power-loss.
- New drill: corrupt/half-written `policy.json` → PC still restores.

## Known pre-existing test failure (not M6's)

`LabSwitchTests.Twenty_rounds_of_switching_leak_neither_sessions_nor_threads_nor_handles`
fails on clean `main` (bdc5b6f) under load: `:333` fixed 15 s for 30 PCs to link, `:371`
`ninetieth < 2000` ms (measured 2034). Out of M6 scope; flagged to the owner.
