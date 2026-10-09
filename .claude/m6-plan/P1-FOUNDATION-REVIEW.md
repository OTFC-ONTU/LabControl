# M6 portion 1 — adversarial review of the foundation (commit `3aada91`, branch `m6-portion1`)

Read-only review, 2026-09-16. Nothing was built or run; every claim below is quoted from the
tree at `.claude/worktrees/m6`. Line numbers refer to that commit.

## 1. The seven prior defects

| # | Prior defect | Verdict | Evidence |
|---|---|---|---|
| 1 | NRE on `"lab_id":null`, `"policy":null`, `"displaced":null` | **absent for the three named fields — but see BLOCKER B1 for the nested nulls** | `PolicyJournal.cs:566` `if (document.LabId is null) { Unreadable(…"lab_id") }`, `:576` `document.Policy is null`, `:582` `document.Policy.Displaced is null`; `PolicyDocument.cs:24/28` declare `string? LabId`, `ClassroomPolicy? Policy`; `ClassroomPolicy.cs:119` `DisplacedSettings? Displaced`. `ReadDocument` wraps both the reader (`:540`) and `JsonStore.Parse` (`:552–562`) in `catch (Exception)`. Tested by `PolicyJournalTests.cs:137–162` (`nulls` flavour) and `:177–196` (`nullDisplaced`). |
| 2 | Stored `HardLimitUnix` never re-clamped on re-apply | absent | `PolicyJournal.cs:450` `session.HardLimitUnix = ClassroomPolicy.Clamp(session.HardLimitUnix, ceiling, nowUnix);` runs before `Guard(what, () => apply(session))` at `:452`, and the mutated object is what `keep()` stores and `Save` writes. Test `:254–270` covers thirty days out and the RTC-back-to-2001 case, asserting both the applied and the saved limit equal `now + 1 h`. |
| 3 | `MachineOpen: true` on a failed lift; overlay nulled before the lift | absent | `LiftLockLocked` `:363–376`: `Guard(LiftOverlay)` first; on failure `_machineOpen = false` (`:369`) and return with `_policy.Overlay` untouched; `_policy.Overlay = null` only at `:374` after success. Same shape in `Lift` `:477–496` (`forget()` only inside `if (result.Succeeded)`, `_machineOpen = false` at `:493`). Tests `:305–323`, `:325–339`. |
| 4 | Failed `RefineRestore` discarded `DisplacedSettings` | absent (but see MAJOR M2) | `:145–153`: success → `policy.Displaced = new DisplacedSettings()`, failure → `policy.Displaced = displaced; open = false;` and `Save` at `:201` writes it. Test `:274–303` asserts the saved `Displaced` still holds `block` and `10.0.0.1`. |
| 5 | Refusal echoed `Kind = LOCK` | absent | `AgentLink.cs:1145–1148` answers `new OverlayState { Kind = Overlay.Types.Kind.Unspecified, Problem = … }` directly on `outgoing`, never through `PublishOverlayState`, so `_latestOverlayState` is untouched. Inside the journal, every refusal goes through `OverlayStateLocked(problem)` (`:378–394`), which reports the lock actually held, never the request. Tests `:441–451`, `:474–486`, `:488–500`. |
| 6 | `RevokedConsoleReason` fail-open on `LinkedInstanceId is null` | absent | `OwnershipGate.cs:27–30` `if (linkedInstanceId is null \|\| boundInstanceId is null) return PolicyEvents.RefusedUnbound;`; `AgentLink.cs:1141` additionally refuses when `instanceId is null` even if the gate somehow passed. Test `:582–585`. |
| 7 | Teacher-facing English literals | absent in substance | `PolicyStrings.resx` (34 lines) and `OverlayStrings.resx` (13 lines) hold every teacher/student string; `PolicyEvents.Text` `:41–55` and `OverlayText.Text` `:256–270` resolve them. Every name used in code exists in the resx (checked by hand). Residual literals: `PolicyJournal.cs:617` `"never"` and the `yyyy-MM-dd HH:mm 'UTC'` stamp inside the `Reapplied` message; `Describe` `:526` falls back to `result.Outcome.ToString()` (`"Failed"`). Minor. |

`LeaveIfConsoleRevoked` (`AgentLink.cs:1191–1219`) is byte-for-byte unchanged: `git show 3aada91 --numstat` reports `93 0` for `AgentLink.cs` — additions only, no deletions.

## 2. New defects

### BLOCKER

**B1 — `Start` throws `NullReferenceException` on a nested null inside `displaced`.**
`PolicyJournal.cs:143` `if (loaded?.Displaced is { IsEmpty: false } displaced)` evaluates
`DisplacedSettings.IsEmpty` (`ClassroomPolicy.cs:99`):
`ProfileDefaultOutbound.Count == 0 && Dns.Count == 0 && OwnedRules.Count == 0`.
System.Text.Json assigns JSON `null` into these non-nullable `Dictionary`/`List` properties
(nullable annotations are not enforced), so a hand-edited or half-written
`"displaced": { "dns": null }` makes `IsEmpty` dereference null **outside any `Guard`**,
inside `lock (_lock)`, and the exception leaves `Start` — the service-start path. The same
`IsEmpty` is used in `Stop` at `:217`. D-69 §(2) itself promises "any null member →
`policy.unreadable`". The machine has already been restored at that point (step 1 ran at
`:132`), so the PC is not left restricted, but the service loop throws, which CLAUDE.md
forbids. Fix: validate nested members in `ReadDocument` (or make `IsEmpty` null-tolerant and
normalise the lists), and add the test.

**B2 — a null string inside a session throws from every `OverlayState` read.**
Same STJ behaviour: `"overlay": { "session_id": null }` (or `set_by_instance`, `message`)
deserialises to a null `string`. `Reapply` survives it (`string.Format` tolerates null), the
session is kept in `_policy`, and `Save`'s `Copy` cleans the *file* — but the in-memory
`_policy.Overlay` still holds the null. `OverlayStateLocked` `:383–388` then does
`state.SessionId = overlay.SessionId` on a protobuf message whose generated setter is
`ProtoPreconditions.CheckNotNull(value)` (`obj/…/Labcontrol.cs:9442`) → `ArgumentNullException`
from `OverlayStateOf`, `ReassertOverlay`, `NoteEnforcement` and `SetOverlay`, none of which are
guarded. The re-send after `Welcome` and the helper-reconnect re-assert are exactly the calls
the service makes unattended. Fix: normalise nulls to `string.Empty` after parsing (one pass
over the loaded policy), test with `"session_id": null`.

### MAJOR

**M1 — `PolicyMutex.Lease.Dispose` on a foreign thread does not release the mutex; the comment
says it does.** `PolicyMutex.cs:54–64`: `ReleaseMutex()` throws `ApplicationException` when the
disposing thread is not the owner; the catch comment claims disposal "is treated as a release
by the kernel". It is not: a Win32 mutant stays owned by the thread until that thread exits
(then it is *abandoned*), and closing handles does not change ownership. The API shape
(`using var lease = PolicyMutex.TryAcquire(); await …;`) invites exactly this in an async
`OnStart`/`OnStop`: the lease is disposed on a different pool thread, the mutex stays held,
and the next pass — including `OnStop`'s restore and the watchdog task in another process —
waits `PolicyMutexWait` (30 s) and reports `policy.busy`, doing nothing. Fix: state the
thread-affinity contract on `TryAcquire`, or run the pass on a dedicated thread inside
`PolicyMutex` (`Run(Action)`), and fix the comment. Not exercised by
`PolicyMutexTests` (`:610–640`), which deliberately uses one thread per taker.

**M2 — `RefineRestore` succeeded + `Confirm` failed discards `DisplacedSettings`.**
`PolicyJournal.cs:145–147`: the originals are dropped as soon as `RefineRestore` returns
`Done`; `Confirm` at `:157` runs afterwards and only clears `open`. The spec says the
originals are kept "until a restore **confirms**" (CONSTRAINTS §0 step 3, D-69 §(3)). If the
refine's own read-back is what "Done" means, the interface contract (`IMachineState.cs:52`)
should say so; otherwise keep `displaced` when `Confirm` fails. Either way this needs a test.

**M3 — no test drives `DeliverOverlayAsync`.** Only the pure `OwnershipGate.Refusal` is tested
(`:571–598`). Nothing proves that on the link (a) a refused `Overlay` answers
`Kind = Unspecified` with `problem` set, (b) `LatestOverlayState` is untouched by a refusal,
(c) the handler receives the validated instance id, (d) a throwing handler is swallowed
(`AgentLink.cs:1157–1166`), (e) the latest state is re-sent after `Welcome` (`:811–814`). The
Console.Tests in-process rig exists for exactly this and is not touched by the commit.

**M4 — `PolicyMutex.TryAcquire` can throw.** `new Mutex(false, "Global\\…")` at `:22` throws
`UnauthorizedAccessException`/`WaitHandleCannotBeOpenedException` when the object exists with
a DACL the caller cannot open (an operator running `agent.exe --restore-policy` unelevated
while the SYSTEM service holds it). Not the service loop, but the method has no throw
contract and the two callers (start/stop) will not expect it. Wrap and return `null`, or
document.

### Minor / advisory

- `Start` re-applies sessions even when `RestoreDefaults`/`Confirm` failed (`:160–194` run
  regardless of `open`). Consistent with the spec's ordering, but re-applying an internet
  policy on top of an unconfirmed restore deserves an explicit decision line in D-69.
- Foreign-lab document (`:571–575`): its `Displaced` block is *not* refined and the file is
  overwritten by `Save`, so a static DNS list displaced before a re-enrolment is lost.
  Windows defaults (DHCP) are in place, so the PC is open; note it in D-69.
- The journal re-applies an `OverlaySession` whatever its `Kind` (`:189`); a document edited
  to `"kind": "broadcast_start"` reaches `ApplyOverlay`. Reject anything but `Lock` in
  portion 1.
- `NoteEnforcement` state (`_inputBlocked`, `_overlayReason`) is never reset by
  `ApplyLockLocked`/`LiftLockLocked`; after a helper restart the next lock reports the
  previous helper's `input_blocked = true` until the new helper speaks.
- `DeliverOverlayAsync` with no `OverlayReceived` subscriber (`:1151–1155`) answers nothing on
  the wire; the console waits forever. Answer a `NotInThisBuild` problem.
- `OverlayText.Compose` (`:88–130`): a size of 100000×100000 asks Skia for 40 GB and rethrows
  (`catch { bitmap.Dispose(); throw; }`) — the "never throws on odd input" summary only covers
  ≤ 0. Clamp to a sane maximum (e.g. 16384). A 2000-character message wraps to ~50 lines at
  1366×768 and pushes the countdown below the screen (the bottom band stays put). The console
  should cap the message; the composer should stop drawing at `bottom`.
- `OverlayText.CountdownText` `:65` `TimeSpan.FromSeconds(long ~9e18)` overflows; `Remaining`
  `:76` guards only `≥ long.MaxValue - 1`. Unreachable after the journal's clamp, but the
  guard should be `TimeSpan.MaxValue.TotalSeconds`.
- `Runs` allocates a fresh `SKTypeface` from `MatchCharacter` per non-default character on
  every repaint (once a second), never disposed. Fine on Windows (Segoe UI has Cyrillic),
  wasteful elsewhere.
- `IMachineState.ApplyInternet(session, displaced)` will be called twice in `Start` (standalone
  then exam-internet, `:164` and `:181`) with the same `policy.Displaced`; portion 3 must record
  originals only when the block is still empty, or the second call overwrites the teacher's
  values with the first policy's. Put that sentence on the interface now.
- `Stop` (`:211–225`) does not lift the overlay (by design: "the helper has already been
  restarted"); the Windows `OnStop` must actually end the helper before or with the restore.
- CLAUDE.md/AGENTS.md status paragraphs not updated for M6 (WIP commit; must land with the
  portion).

## 3. Verified clean

- **Restore-before-read ordering**: `RestoreDefaults` at `:132` precedes `ReadDocument` at
  `:135`; tests assert `Calls[0] == RestoreDefaults`, `Calls[1] == "read"` for a throwing
  reader, garbage and nulls — a restore conditional on the document would fail them.
- **Clock cases**: `until == 0` with passed hard limit → `IsStillValid` false → dropped;
  `until` past/limit future and the reverse → dropped (test `:241–251`); clock earlier than
  `SinceUnix` is irrelevant (only `until`/`hard_limit` are judged) and re-clamped.
- **Confirm throws after a good restore**: `Guard` turns it into `Failed`, `open = false`,
  sessions still re-applied, nothing escapes (test `:340–365`).
- **`Stop`**: under `_lock`, restores + refines + confirms, no `Save`, `_stopped` refuses
  applies and ticks; next `Start` re-applies (test `:400–422`). The *named* mutex is the
  caller's job by contract (`:27–28`).
- **`Tick`/`NextDeadline`**: `nowUnix < until` so the deadline second itself lifts; `Min`
  over all sessions; deadlines are bounded by the clamped hard limit so `long.MaxValue`
  never reaches the timer. Failed lift keeps the session and retries next tick.
- **`Clamp`**: overflow guarded at `ClassroomPolicy.cs:146`; `proposed ≤ 0` → ceiling;
  zero/negative ceiling → `now` (session ends at once). Tests `:13–33`.
- **Gate placement**: `HandleAsync` `:1062` runs on the read loop (`:867`) that also merges
  `Revocation` (`:1041–1049`), before the handler; `outgoing` is unbounded so `WriteAsync`
  cannot stall the loop.
- **Proto**: new numbers only (`Overlay` 3–5, `OverlayState` new, `AgentMessage` 12,
  `HelperMessage` 5); no `reserved` added; frozen subset untouched; `ProtocolTests` proves an
  old two-field `Overlay` still parses and re-encodes to the same 7 bytes.
- **Secrets / constants**: no PC count, no console machine, no message body in logs
  (`AgentLink.cs:1143` logs kind and reason only).
- **Resources**: SDK-style `LabControl.Shared` with `RootNamespace=LabControl.Shared` embeds
  `Policy/*.resx` as `LabControl.Shared.Policy.PolicyStrings`/`OverlayStrings`, matching the
  `ResourceManager` names; tests asserting `"another-lab"` in the message would fail if the
  fallback-to-name path were taken.
