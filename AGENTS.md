# LabControl — instructions for Codex

## Shared project context (read first)

LabControl is a self-hosted classroom-management system: one cross-platform teacher
console manages up to 30 Windows student PCs in its active lab on an isolated LAN.
The current implementation has one lab per console profile. Planned M5 (`D-53`) adds
bulk import of lab files and fast switching among saved rooms, with at most one active
lab and no background connections/video for inactive rooms. Rosters are mostly stable;
teachers change rooms between lessons. The current room has 14 PCs. The system must
remain usable by its teacher-owner without an IT department, so changes must be zero-maintenance and
self-explanatory.

Read [`CLAUDE.md`](CLAUDE.md) completely before changing the project. It is the
detailed project brief and records the current implementation status. Then read
the documents relevant to the task:

- `docs/ARCHITECTURE.md` and `docs/PROTOCOL.md` before changing `src/`;
- `docs/INSTALLER.md` before changing provisioning, installation, updates, users,
  Windows services, firewall, power settings, or the USB payload;
- `docs/ROADMAP.md` before adding scope or changing milestone status;
- `docs/DECISIONS.md` before making or revisiting a non-obvious design choice.

## Non-negotiable constraints

- The teacher console is replaceable and may run on macOS, Windows, or Linux. Do
  not bind the lab to one console machine.
- Never hard-code the number of PCs; the product is designed for up to 30.
- M5 lab files are for routine classroom access, distinct from full administrator
  backups. Preserve existing backup recovery; do not distribute CA private keys or
  enrollment codes as ordinary teacher access. Keep lab data and session lifecycles
  isolated, and release the old lab before activating another (`D-53`).
- M5 also accepts administrator `.lcbak` backups in the same bulk import and lab
  selector; preserve full authority per lab without repeated restoration on switching.
  Teacher-console installers package/register the desktop app and file types, with
  no agent services or student preparation. Verify scoped LAN permissions and native
  prerequisites separately from copying files; retain profiles/keys on upgrade and by
  default on uninstall (`D-54`, `docs/INSTALLER.md`).
- The M5 design is recorded in `D-55`…`D-60` (2026-09-08), extended by `D-68`
  (2026-09-09): profile store and resumable migration, signed `.lclab`/`.lcreq`/`.lcgrant`
  files with the role in the subject OU and `instance:` revocation, one
  `ActiveLabController` with release-before-acquire and result ownership, take-over on
  the agent's clock, per-user console packaging with single-instance forwarding, dormant
  imported enrollment codes. Follow those entries and ROADMAP M5 *How it is being built*
  (eight portions; **all eight built and reviewed**, 1–3 on 2026-09-08 — portion 1: profile
  store, resumable migration, `lab.json` schema 2, `console.lock`; portion 2:
  `ActiveLabController`, the *My labs* chooser, *Disconnect*, the departure report and bulk
  `.lcbak` import; portion 3: the signed `.lclab`/`.lcreq`/`.lcgrant` exchange, teacher
  sessions without a vault, `instance:` withdrawal with confirmed delivery, the *Teacher
  devices* panel and dormant imported codes, 753 tests — and portion 4 on 2026-09-09:
  results and progress bound to the console instance the agent's TLS handshake validated,
  `jobs-inflight.json` restoration with narrow re-send rules, 785 tests, verified with a
  real agent on the isolated Windows clone — and portion 5 on 2026-09-09: take-over decided
  on the agent's own clock (arrival order, the press bound to its own beacon, honoured
  once), the four ownership states a console can prove — linked here, observed elsewhere
  within `Defaults.OwnershipObservationLifetime`, unknown, offline — the banner's *holds at
  least N*, and the additive informational `Welcome.console_access` that never decides a
  refusal, followed the same day by the portion-5 security fixes — a withdrawn instance's
  beacon refused where the beacon is judged (the agent ahead of both the take-over and the
  dial branch, the console in the same way), ownership attributed only on the PC's own
  `link.taken_over` departure report, an expiring banner and the arrival stamped in the
  listener's receive loop — still not run on the Windows VM with two consoles and
  skewed clocks — and portion 6 on 2026-09-09: console documents
  on the command line, `--import-only`, a second launch forwarding its file paths over a
  per-data-directory named pipe or private-directory Unix socket instead of starting a
  second console, macOS file activation, 788 tests, checked by hand on the Mac, with the
  Windows pipe since exercised by portion 7's Windows drill and the Linux `SO_PEERCRED` path
  still not run — and portion 7
  on 2026-09-09: teacher-console packaging (`D-59` items 1–4) — the single-file per-user
  Windows installer with an untrusted `installed-files.txt`, a finishing temporary copy and
  an uninstall that never elevates, the read-only LAN-access banner that calls a port
  reachable only when a rule opens it for this console and nothing blocks it, the
  ad-hoc-signed macOS `.app`/DMG, the per-user Linux tarball and `tools/package-*.sh` into
  `artifacts/package/`, 936 tests after the merge with portions 4 and 6, packages built and
  the Linux scripts round-tripped on the Mac, then **run on Windows for the first time on
  2026-09-09** (isolated Windows 11 ARM64 clone, guest `PC-27`, no interactive session, so
  every unelevated step ran as `LOCAL SERVICE` and the elevated one as `SYSTEM`), which
  found four defects, all fixed and merged (`a7fbe33`): an `app.manifest` double hyphen
  inside an XML comment that made Windows refuse the activation context so the installer
  would not start at all, a firewall profile constant of 6 that opened Public and rejected a
  real private-and-domain rule (`NET_FW_PROFILE_TYPE2` is Domain 1, Private 2, Public 4, so
  3), a `group=` argument in the printed `netsh` line that `add rule` refuses outright, and
  an `ArgumentList`-built self-delete command `cmd.exe` cannot parse, which left a 148 MB
  copy in the temp directory — the install, the plan and dry run, the shortcut, the
  Installed-apps entry, both file-type registrations, an idempotent repeat run, the lock
  refusal, document forwarding into a running console over the Windows named pipe, the real
  firewall rules and the banner, uninstall with its temporary copy, `--remove-data` and a
  DPAPI-sealed key surviving replacement are verified, while Explorer's own double-click, a
  real elevation prompt a teacher answers, `win-x64` (only ARM64 was built and run), the
  installer under an ordinary interactive profile, the unsigned-download warning, the Linux
  `SO_PEERCRED` path and a real Linux desktop menu are not — and portion 8
  on 2026-09-09: the acceptance drills and the six gaps an adversarial audit of every M5
  acceptance criterion found (`D-68`) — the combined picker filter that makes a mixed
  selection possible, batched authorization written into one folder, the departure flow
  *Disconnect*, the window close and quit all ask, the beacon-resume step-over that relinks
  thirty refused PCs in 2.7 s instead of 30.3 s, and agent events bound to the console that
  delivered their job while machine events stay unbound; main stands at 984 tests, 755
  Shared + 229 Console, run twice on the merged tree). Still unverified: the rest of the
  console packaging on Windows (Explorer's double-click, a real elevation prompt,
  `win-x64`, an ordinary interactive profile, the unsigned-download warning) and all of it
  on Linux, portion 5 on Windows with two consoles and skewed clocks against a real agent,
  the Avalonia quit hook by hand, and the milestone in the physical lab. The `Welcome.console_access`
  `.proto` change landed with portion 5 and `docs/PROTOCOL.md` was updated in that commit.
- **M6 portion 1 is built (2026-09-16, `D-69`, `D-70`) and verified in an isolated
  Windows 11 ARM64 VM (2026-10-17, `D-81`).** The policy journal (`Shared/Policy/`, `policy.json`) restores the machine
  first and re-applies second, under the named mutex `Global\LabControl.Policy`, with the
  hard limit re-clamped on every pass; a lock has its own 1 h ceiling
  (`Defaults.LockHardLimit`) clamped on the agent's clock and does **not** end on lab
  departure. One platform-neutral `PolicyEnforcer` is driven by the Windows service
  (`WindowsMachineState`, the overlay relayed by `SessionSupervisor` and re-asserted to
  every fresh helper), by the simulator (`FakeMachineState`, the lock drawn on the fake
  desktop) and by `agent.exe --restore-policy`. The helper shows the lock with a raw
  topmost Win32 window painted from the `OverlayText.Compose` bitmap — layered,
  click-through and never activated, purely visual (`D-70` item 7) — and low-level
  keyboard/mouse hooks on a dedicated thread that pass injected input, installed on lock
  and re-armed every second (`D-70` item 8); `BlockInput` is the fallback only. The
  limits on the wire are **durations** stamped onto the PC's clock at arrival (`D-70`
  item 6); `OverlayState.reason` is stable `OverlayReasons` codes the console puts into
  words; nothing in `PolicyEnforcer` throws and the service dials the link regardless
  (`D-70` item 9). The console has `LabSession.Lock`/`Unlock`, the *Lock screens*
  dialog (ceiling and Ctrl+Alt+Del stated), *Lock all…*/*Unlock all* and a tile badge fed
  by the PC's `OverlayState`, never by the request. Tests: 796 Shared + 241 Console after
  the second-half review's fixes (795 + 236 after the foundation review's, 782 + 232 when
  built, 778 + 228 before the portion; one load-sensitive beacon test failed under the
  full run and passed alone). `dotnet publish -r win-x64` and `-r win-arm64` of the helper
  and the agent succeed. Native acceptance found and fixed desktop following: the window
  and hooks are recreated on fresh threads when the input desktop changes, preserving
  absolute deadlines; the raw window thread must not use STA. The VM verified physical
  input refusal, injected input, Ctrl+Alt+Del, clock skew, offline expiry, service restart,
  sign-out/reboot/autologon and corrupt-policy recovery. Physical Windows x64 and fleet
  acceptance remain open; see ROADMAP M6. When touching
  `Overlay`/`OverlayState`, the ownership gate in `AgentLink` is judged before the
  enforcer sees the message and fails closed; keep `LeaveIfConsoleRevoked` as it is, keep
  the limits as durations, and keep every teacher-facing reason a code.
- **M6 portion 2 is built (2026-10-17, `D-71`, `D-72`), review-fixed (`D-71` items 9–11:
  picture reset on the helper's pipe read loop, `broadcast.stalled` after
  `Defaults.BroadcastStallTimeout`, a cut connection is `LinkEnded`; items 12–14 from a
  second review: a disposed `ScreenProducer` starts nothing, a keyframe requested while one
  is in flight is still sent, capture problems shown from `Capture.*` resources) and partly
  verified in the ARM64 VM — keyboard refusal, service restart, reboot and the Mac's real
  display passed; physical mouse refusal, the readability check and the lab remain
  (ROADMAP M6).** The
  console's screen (CoreGraphics on macOS after the *Screen Recording* permission, GDI on
  Windows, none on Linux) is scaled to 1280 wide inside `ScaledScreenSource`, encoded once
  by `BroadcastHub` and served over the server-streaming `PullBroadcast` with a latest-wins
  slot per PC; `ConsoleMessage.broadcast = 10` is reserved and frames never go on `Link`.
  On the PC, `BroadcastReceiver` (owned by `PolicyEnforcer`, never in `policy.json`) takes
  the screen on the first frame, clamps 30 min on its own clock and ends with its stream;
  a lock stays on top and `OverlayState` is composed lock-first. The helper shows it in
  `OverlayWindow`'s broadcast mode with the lock's hooks. Keep frames off `Link`, keep the
  broadcast out of the journal, and never await a pipe write from a read loop.
- **Lab key recovery comes next (owner decisions 2026-10-20, `D-82`), before M6 portion 4.**
  Remember the key on this computer (a device holder, secret in the OS store, on by default), and
  *Move this lab to a new key* from an administrator console: a new lab, every linked PC moved by a
  SYSTEM `run_script` carrying its own enrolment code that schedules the installed
  `C:\Program Files\LabControl\Setup.exe --rekey`. Never run `Uninstall.exe --rekey`: that file name
  makes the executable prepend `--uninstall`. Portions 1–2 built 2026-10-20; the move passed its
  VM drill on an owned installation (agent id and number kept, enrolled into the new lab by itself).
- **M6 portion 3 is built (2026-10-17, `D-73`, `D-74`) and passed its VM drill on Windows 11 ARM64 (2026-10-20); physical lab pending.**
  Internet control: every firewall profile's default outbound action to *Block* after the
  `LabControl Internet` group's `agent.exe` and local-subnet rules (Windows refuses loopback addresses in a rule), other programs'
  outbound allow rules switched off by fingerprint (*Core Networking* kept), and for a whitelist
  the platform-neutral `Shared/Internet/LoopbackResolver` on `127.0.0.1:53`/`[::1]:53` with every
  interface's DNS pointed at it in both families; allowed answers' addresses join one allow rule
  before the answer leaves. `IMachineState.ApplyInternet` takes a `recordDisplaced` callback (the
  originals are saved before anything changes), `LiftInternet` the displaced block. The watchdog
  `--restore-policy` does nothing while the service runs and otherwise restores without
  re-applying (`D-73` item 7); `self_update` is refused while a policy is held. Console:
  *Internet…*, `InternetDialog`, `InternetPresetLibrary` (`internet-presets.json`, in the backup
  and the lab file), refused names with *Allow* (`amend_hosts`), a tile badge from `InternetState`
  alone. Keep the limits as durations, keep reasons as codes, never record a resolver-pointed DNS
  list as an original, and never re-apply a whitelist from a short-lived process. The 14-PC
  physical-lab drill passed on 2026-10-20 after the lab fixes (`D-73` item 12). A lock or a
  broadcast holds the student's display on through the helper's `DisplayKeeper` (`D-84`, not yet
  run on Windows); the exam strip reuses it.
- **M6 portion 4 is built (2026-10-23, `D-75`, `D-76`), review-fixed and passed its ARM64 VM drill the same day.**
  Exam mode: `ExamMode`/`ExamState` with durations, the journal's `SetExam` (internet switch first as
  its own `ExamInternet` session that hands a standalone policy back, the exam last), a "Time is up"
  lock when the timer — not the hard limit — runs out, the allowed-programs whitelist in the
  **service** (`WindowsExamControl` over the pure `Shared/Exam/ProgramWatch`: session and token before
  name, never-touch names only under the Windows directories, a polite opening sweep with
  `WM_CLOSE` through the helper and a 10 s grace, at-once ending later, power jobs pause it, the
  teacher's scripts exempt), the strip in the helper (`ExamStripWindow`, click-through, hidden under
  a lock or broadcast), presets in `exam-presets.json` (backup only, never the lab file), late PCs
  joining an exam *for all* from the console's memory. Keep the whitelist out of the helper, keep
  the internet switch before the exam, and never count a process as closed that this call did not
  end. The ARM64 VM drill (2026-10-23, `D-75`, *VM drill*) fixed five defects — protected processes
  never judged, `remaining_seconds` aged on a re-send after `Welcome` and on *Extend*, the strip
  widening for its text, a new exam forgetting the last one's closed programs, more quiet helpers.
  Tests: 972 Shared + 289 Console. Next: the physical lab (alongside portion 5).
- **M6 portion 5 is built (2026-10-23, `D-77`, `D-78`), review-fixed and passed its ARM64 VM drill the same day.**
  Collect work and profile reset: the additive `RequestUpload` RPC (the agent asks for its D-42 grant
  once its archive exists), `WorkArchive` / `CollectRunner` packing under the student's token with links
  left out, `WorkCollections` granting only for a running job of that PC delivered by this instance and
  unpacking into `<destination>/<name date>/PC-07/` with every name checked and a `report.txt`; exams
  collect each PC when it reports the end, PCs off are missing and never retried; `ProfileResetRunner`
  refuses during an exam and on account-off (light = three folders emptied under the token by the
  iterative `ProfileWipe`; full = log off, `DeleteProfile` by SID, reboot). Keep student-file reads under
  the token and never follow links as SYSTEM. The VM drill fixed an offline *End exam* never reporting PCs
  missing and a light reset removing Windows' hidden system junctions. Tests: 1003 Shared + 301 Console.
  Next: portion 6.
- **M6 portion 6 is built (2026-10-26, `D-79`), on the Mac; its VM drills are next.** The departure
  contract: `DepartureReport` names the broadcast that ends, the locks and standalone internet
  policies that stay, the exams that continue (each with its latest self-lift) and the exam work the
  session would have collected; *Unlock, open the internet and leave* (owner decision 2026-10-26)
  ends locks and standalone policies, waits up to `Defaults.DeparturePolicyEndBudget` for the PCs'
  word and never ends an exam; exam work is collected by hand on return, never remembered.
  `Defaults.ProtocolVersion` is 2 and `ClassroomControlProtocolVersion` 2: a lock, broadcast,
  internet policy or exam is *set* only on a version-2 agent, every *end* goes to any non-outdated
  one, `MinimumProtocolVersion` stays 1 — push the current build to the physical lab (it reports 1)
  before setting anything there. The `D-56` amendment (a teacher console may do all of M6). Drills
  use `LabSession.LockLimit`/`InternetLimit`/`ExamLimit` via `TestLabHost --hard-limit-seconds`;
  procedures in ROADMAP M6, *The portion-6 drills*. Tests: 1003 Shared + 308 Console.
- **M6 is done (owner decision 2026-10-26)**: accepted in the physical lab on all 14 PCs, the
  findings fixed and retested on `1.0.0+cc39651`; open check: teacher access in the lab (no second
  teacher device). Next by the roadmap: M7; its last item is a UI/UX redesign of every screen
  (owner decision 2026-10-26), which also carries the failed-action feedback.
- **M7 portion 1 is built (2026-10-26, `D-86`), on the Mac**: the console in Ukrainian, English and
  Russian — `Shared/Localization/ProductLanguages` (stored choice, else a supported OS language,
  else English), `preferences.json` at the data root, `ConsoleLanguage` applied in `Program.Main`,
  the selector in *Settings* and the chooser with *Restart now* through the quit path, `*.uk.resx`
  and `*.ru.resx` beside every product `.resx`. A new key goes into all three files
  (`ResourceTranslationTests`); both test projects pin English (the owner's Mac reports `ru-UA`).
  Owner decisions: language applies on restart, the student sees the console's language
  (portion 2 carries it to the PC), "lab" = лабораторія / лаборатория. Seven portions in ROADMAP
  M7. Tests: 1032 Shared + 316 Console. Next: the owner reads the translations; portion 2.
- **M7 portion 2 is built (2026-10-26, `D-87`), on the Mac; not yet run on Windows**: the console's
  language reaches the PC. `Welcome.language` sets `Shared/Localization/PcLanguage` (kept in
  `agent.json`), in which the PC writes events, job results and refusals (`PolicyStrings`, the new
  `PcStrings` via `PcText.Get`; the helper learns it from `ServiceMessage.language`). Each lock and
  exam carries its own `language` (kept in `policy.json`, passed down as `ExamStrip.language`): the
  student's words are in the setting console's language and `OverlayText` never reads the process
  culture. Setup/removal follow the Windows display language; the step trace stays English.
  A test that changes `PcLanguage` runs in the language collection and restores English.
  Tests: 1050 Shared + 317 Console. Next: the VM run in all three languages; portion 3.
- **M6 in the physical lab (2026-10-26, build `1.0.0+68d0ae5`)** passed sections 0–6 of the checklist.
  Fixed the same day, not yet on a PC: the whitelist allows what an allowed program starts (`D-75`
  item 12), ends asked while a PC was off are sent on its return with the same session (`D-79` item 8),
  the *Internet* dialog names PCs whose exam controls the internet (item 9), no Ctrl+Alt+Del line on
  the student's lock screen. Open: mouse/Win under a broadcast on one PC; teacher access not run;
  failed-action feedback is M7. Tests: 1006 Shared + 309 Console. Then: a re-push replaces a
  rolled-back version's leftover directory; the probation window is 30 minutes (`D-83` addenda).
  An input block is reused only on the desktop its thread joined (`D-81` addendum, PC-13).
- The Windows student agent must survive reboots and must not let a failed Win32
  operation escape the service loop.
- Physical lab status (2026-09-08 evening): five PCs installed and linked from the
  `1.0.0` media; the owner declared the baseline established and moved development on
  to M5. Remaining PCs, the vendor Realtek driver and the wake test continue alongside
  (ROADMAP *Physical lab* row and on-site checklist). Never claim Wake-on-LAN works here.
- `1.0.0` is the current physical-lab release candidate (owner decision 2026-09-08).
  Do not call it released until the remaining M4 physical acceptance checks pass; the
  later additive M5 portion-5 `Welcome.console_access` field preserves the frozen
  update subset and does not require reinstalling this agent.
- The first physical runs exposed a Defender WMI/completion-journal failure (`D-61`).
  Repair may preserve only the explicitly marked additive exclusion as pre-existing;
  it must not claim ownership or clear unrelated history. After a successful provider
  mutation, allow only the bounded stable read-back convergence window and fixed safe
  stage codes from `D-64`. A nonzero `Add` status is retried three times, three seconds
  apart, and printed as `status 0x…`; the numeric status is the only value a diagnostic
  may carry (`D-64` addendum).
- The next physical step showed Windows normalizing successful hibernation-off to absent
  enable/type values and size zero (`D-62`). Record an accepted exact resolved post-state
  only after read-back; interrupted results remain unowned. With no hibernation file,
  native/registry type fields are undefined metadata and do not reject the disabled state.
  An absent fixed-path file also bypasses the native capabilities query, while an existing
  path still requires it for exact restoration; a successful native "no active file"
  answer overrides a residual/protected path object (`D-63`). Setup logs version/build
  identity and fixed value-free diagnostic codes for field verification. An absent
  `HibernateEnabled` value is the OS default (`HibernateEnabledDefault`), accepted as
  enabled next to an active file and restored as absent on removal (`D-65`); never treat
  a PowerShell `Get-Item`/`Test-Path` miss on `hiberfil.sys` as proof the file is absent.
  Native rerun is pending.
- Student PCs may have no internet. Do not introduce a cloud or per-PC runtime
  dependency.
- Never log or persist secrets outside their designed stores. In particular, do
  not expose the lab-key passphrase, private keys, enrollment material, or the
  `student` password.
- Ports, names, paths, and student-account details belong in
  `src/LabControl.Shared/Defaults.cs`, not in repeated literals.
- Keep the solution buildable with plain `dotnet build` and `dotnet test` on the
  Apple Silicon development machine. Windows-only behavior needs a Windows VM or
  a lab PC for runtime verification.
- Planned M4 Setup must support opting out of student-account creation (default on)
  and standalone uninstall. Preserve personal accounts and profiles; repair retains the
  selected mode and removal uses recorded ownership (`docs/INSTALLER.md`, D-40).
- M4 supports clean installation and repair of owned installs only; unowned legacy dev
  installations are refused. Legacy migration is outside M4 (owner decision 2026-09-08).
- The account journal, Windows preparation component (`D-45`, `D-46`), protected
  settings journal core (`D-47`), two machine DWORD adapters (`D-48`) and AC power-plan
  adapters with activation confirmation (`D-49`) and the Windows Update active-hours
  tuple adapter (`D-50`) and ownership-gated LSA sign-in secret adapter (`D-51`) are built,
  and Setup executable integration is in progress (`D-52`); Windows acceptance is pending. `AccountSetupScope` owns the private storage/lock; new accounts
  remain disabled until final successful setup activation. Binary-directory ownership also
  requires its matching installation-id marker and protected descendant ACLs. Never infer ownership for legacy dev installs from the account name.
- Before hiding the original administrator, account-on Setup must journal and verify
  CredUI `EnumerateAdministrators=0`; restore the tile before restoring that policy.
  Account-off touches neither setting. Do not change UAC security policy to bypass
  native administrator-maintenance acceptance (`D-52`).
- Do not invent features outside `docs/ROADMAP.md`; propose the scope there first.
- M6 portion 1 has passed ARM64 VM acceptance; portion 2 (broadcast) is built and has
  passed a partial VM drill; portions 3–6 remain. Its owner decisions
  (2026-09-09) and corrected six-portion plan are settled in ROADMAP M6, *Settled before implementation*; follow them and use the
  reserved `D-69`…`D-79`.
- The README is the public project page with one line of status per milestone; put long
  working notes in `docs/DEVELOPMENT.md`. Never commit local scratch (plans, logs, dev lab
  data, USB payloads, lab documents); `.gitignore` covers them.

## Shared documentation contract for Codex and Claude Code

`AGENTS.md` and `CLAUDE.md` are companion agent documents. Neither belongs only
to the agent named by the file: together they are shared project documentation.

- When project facts, requirements, status, workflows, commands, repository
  layout, or shared conventions change, inspect **both** files and update every
  applicable section in the same task.
- Never update only the current agent's file while leaving the other agent with
  stale or contradictory guidance. Agent-specific instructions may stay in one
  file only when they are explicitly labeled as agent-specific.
- Documentation is part of the implementation. If behavior described by any
  Markdown file changes, update that Markdown in the same task.
- Markdown is the source of truth. After changing any `.md` file, run
  `tools/docs-build.sh`; never edit `docs/html/` by hand.
- Any `.proto` change requires a matching `docs/PROTOCOL.md` update. Any
  non-obvious design decision requires a new `D-NN` entry in
  `docs/DECISIONS.md`.

A task that leaves either agent's instructions, the relevant project Markdown,
or the generated HTML stale is not finished.

## Engineering conventions

- Code, identifiers, comments, and commit messages are English.
- UI strings are resources, not hard-coded text. English is the neutral source and
  fallback locale. M7 must ship Ukrainian, English and Russian for every ordinary
  product-facing surface, with an explicit persistent console language choice (`D-67`).
  Every new or changed key goes into the neutral `.resx` and its `.uk.resx` / `.ru.resx`
  siblings in the same change; `ResourceTranslationTests` enforces it (`D-86`).
  Stable protocol identifiers, diagnostic codes and developer logs remain English.
- Prefer simple, well-supported libraries. Record every new NuGet dependency and
  its reason in `docs/DECISIONS.md`.
- Preserve protocol compatibility rules documented in `docs/PROTOCOL.md`,
  especially the frozen subset and the job lifecycle across reconnects.
- Develop the console against `LabControl.FakeAgent` on macOS. Do not claim a
  Windows-only behavior is verified until it has run in the VM or on a lab PC.
- Preserve unrelated work in a dirty worktree.

## Repository map

```text
LabControl/
├── AGENTS.md                      Codex instructions and shared agent contract
├── CLAUDE.md                      detailed project brief and Claude Code rules
├── README.md                      public project page: features, screenshots, quick start
├── LICENSE                        MIT
├── .github/workflows/ci.yml       CI: build + tests on Linux, native build on Windows
├── docs/                          architecture, protocol, installer, ADRs, roadmap
│   ├── DEVELOPMENT.md             command lines, FakeAgent, Windows VM workflow, status notes
│   ├── images/                    README screenshots from the headless UI renders
│   └── html/                      generated Markdown mirror; never hand-edit
├── src/LabControl.Shared/         contracts, shared models, constants, core logic
├── src/LabControl.Console/        cross-platform Avalonia teacher console
├── src/LabControl.Agent/          Windows service and privileged operations
├── src/LabControl.Agent.Session/  interactive-session capture/input helper
├── src/LabControl.Setup/          one-shot Windows USB installer
├── src/LabControl.FakeAgent/      cross-platform simulated student PCs
├── tests/                         shared and console test projects
├── tools/                         documentation and publish tooling
├── packages/                      package catalog and ignored installer cache
└── scripts/                       library/ = seed of the console's script library (D-38); dev-install.* for the VM
```

The console and Windows Setup share `src/LabControl.Console/Assets/labcontrol.ico`,
regenerated by `tools/make-icon.py`. Setup embeds it in its executable and dialogs;
the installed uninstaller is a copy of that executable.

## Common commands

```bash
dotnet build
dotnet test
dotnet run --project src/LabControl.Console
dotnet run --project src/LabControl.FakeAgent -- --count 14 --payload ~/usb
tools/publish-all.sh
tools/docs-build.sh
tools/make-icon.py
```

Use `docs/ROADMAP.md` and the `Current status` section of `CLAUDE.md` for the
latest milestone state; do not duplicate that volatile detail here unless Codex
needs a specific additional instruction.

M4 integration: password fallback is allowed only for a classified create-new policy
rejection on a non-domain PC, through the protected tuple journal. Profile templates add
only bounded documents to the resolved Default profile; never import arbitrary hives or
overwrite existing profiles. Keep removal retry metadata until cleanup can finish.
Fresh sign-in setup journals `AutoLogonSID` separately before enabling autologon and
restores it after the main tuple. Never infer its original value for older tuple-only
history; preserve it with the explicit warning. Account-off skips both settings.

The owner explicitly requires Setup to finish the Windows prerequisites for console-pushed
updates and Wake-on-LAN. Select the wake MAC from positively identified physical Ethernet,
never an arbitrary active virtual adapter. Apply dependent NIC power controls in order and
restore in reverse dependency order. Report unsupported capabilities; a local settings
read-back is not proof of network reachability or physical wake from shutdown.
Keep installer advisories bounded to the fixed readiness-code schema; translate them in
the console, retain valid cached warnings offline and never display arbitrary report JSON.
`network.wol_unverified` and `network.configuration_warning` are informational (tooltip,
no amber, logged only when the snapshot changes, `D-66`); do not promote them back to
attention without an owner decision.
