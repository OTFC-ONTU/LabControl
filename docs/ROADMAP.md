# Roadmap

Milestones **M0 … M6**. Each one is independently demonstrable: at the end of a
milestone there is something the teacher (or the developer on the Mac) can actually run
and look at, not just code that compiles. Milestones are ordered by *risk first, value
second* — the genuinely uncertain parts (the trust model, the Windows service + session
helper, screen capture) are pulled forward so that a dead end is discovered early, while
the lab is still being used the old way.

Do not add features that are not listed here. Propose them in this file first, then
implement.

## Status

| Milestone | Title | State | Blocked by |
|---|---|---|---|
| **M0** | Skeleton and toolchain | **done 2026-09-04** | — |
| **M1** | Lab identity, link and presence | **done 2026-09-05** | M0 |
| **M2** | Windows agent: service, helper, power, scripts | not started | M1, Windows VM |
| **M3** | Screens: mosaic, full view, remote control | not started | M2, `PC-00` |
| **M4** | Deployment: USB installer, files, self-update | not started | M3 |
| **M5** | Classroom control: broadcast, lock, exam mode | not started | M4 |
| **M6** | Software catalog, localization, polish | not started | M5 |

Update the **State** column (`not started` / `in progress` / `done <date>`) in the same
change that finishes the work, and regenerate the HTML mirror (see
[Documentation workflow](#documentation-workflow)).

> **Why seven milestones and not six.** The original plan had software installation in
> M4. The owner has confirmed the catalog starts **empty** and his lab's PCs are already
> provisioned, so package management moved behind the lesson-time features that are used
> every week. What matters for packages is not a prefilled catalog but a comfortable way
> to *add* an installer — that is the M6 acceptance criterion.

## Definition of done — applies to every milestone

A milestone is done only when **all** of these hold:

1. `dotnet build` and `dotnet test` are green from the CLI on macOS, with no warnings
   that were introduced by the milestone.
2. `tools/publish-all.sh` produces self-contained binaries for every RID the milestone
   touches (`osx-arm64`, `win-x64`, `linux-x64` for the console; `win-x64` and
   `win-arm64` for the agent side).
3. The acceptance criteria below are demonstrated **live**, not argued from code.
4. Nothing assumes 14 PCs, and nothing assumes which computer the console runs on
   (`D-13`, `D-17`).
5. Every `.proto` change is reflected in `docs/PROTOCOL.md`; every non-obvious choice is
   a new `D-NN` in `docs/DECISIONS.md`; every new NuGet package is in the dependency
   table with a one-line reason.
6. All `docs/*.md` touched by the milestone are regenerated into `docs/html/`.
7. Nothing secret is committed: no lab key, no certificates, no `student` password in
   logs, no package binaries.
8. The agent never throws out of its service loop: every new Win32 / process / file call
   added in the milestone is wrapped and reports failure as an event.

---

## M0 — Skeleton and toolchain

**Goal.** A repository that builds, tests, publishes and documents itself. No product
behaviour yet. This milestone exists so that every later one starts from a known-good
baseline instead of fighting the toolchain.

**Deliverables**

- `LabControl.sln` at the root with the six projects from `CLAUDE.md` plus a test project
  per testable component.
- `Directory.Build.props`: `net10.0`, `LangVersion 14`, nullable enabled, warnings as
  errors, deterministic builds, one version stamp.
- Windows-only projects (`Agent`, `Agent.Session`, `Setup`) target `net10.0-windows` and
  are compiled by an ordinary `dotnet build` on the Mac via `EnableWindowsTargeting`
  (`D-18`) — they cannot run there, but they can never rot unnoticed either.
- `src/LabControl.Shared/Defaults.cs` — ports, pipe name, paths, account name, timeouts,
  file names, **maximum lab size**. Every later milestone references these constants; no
  literals elsewhere.
- `src/LabControl.Shared/Protos/labcontrol.proto` — service and message skeletons from
  `docs/PROTOCOL.md`, compiling to C# stubs on both sides.
- Avalonia console that opens an empty main window with the localization seam in place
  (`Strings.resx`, English only, **no hard-coded UI text**).
- `LabControl.FakeAgent` that starts, parses `--count`, and logs a heartbeat.
- `tools/publish-all.sh`; `tools/docs-build.sh` + `tools/DocsBuild/` (already written).
- `Directory.Packages.props` (central package versions) and `global.json` (SDK band plus
  the Microsoft.Testing.Platform opt-in that .NET 10 requires) — see `D-18`.
- Real tests, not placeholders: `Defaults` invariants and a protobuf round trip that
  proves the generated contract is usable on both sides.

**Acceptance criteria**

- `dotnet build` and `dotnet test` succeed on the Mac with zero warnings.
- The same `dotnet build` also compiles the three `net10.0-windows` projects on the Mac.
- `dotnet run --project src/LabControl.Console` opens a window on macOS.
- `dotnet run --project src/LabControl.FakeAgent -- --count 30` starts and logs 30
  simulated machines; `--count 31` is refused with a message naming `D-17`.
- `tools/docs-build.sh` regenerates `docs/html/`, and the result opens by double-click
  with no internet connection.
- `tools/publish-all.sh` writes runnable single-file binaries for `osx-arm64`,
  `win-x64`, `win-arm64`, `linux-x64`.

**Not in scope.** Any networking, any Win32 call, any UI beyond an empty window.

**Rough size.** Small. One sitting.

---

## M1 — Lab identity, link and presence

**Goal.** A lab exists, machines enrol themselves into it, and the console shows them
live — **and the whole lab can be moved to a different teacher computer without touching
a single PC**. Proven entirely on the Mac against `FakeAgent`; no Windows machine needed.

This is the milestone that implements `docs/ARCHITECTURE.md` §3 and decisions
`D-13`/`D-14`/`D-21`. It is deliberately front-loaded: getting the trust model wrong is the one
mistake that would later require a walk to every PC to fix.

**Deliverables**

- **Lab key**: ECDSA P-256 CA created by a first-run wizard; private key encrypted with
  AES-256-GCM under a master key stored as one wrapping per **named key holder**
  (PBKDF2-SHA256 passphrase, ≥ 600 000 iterations) plus one printable recovery code. Add
  and remove holders from the console. All from the .NET BCL, no new dependency.
- **First-run wizard** that will not finish until a backup has been exported and the
  recovery code acknowledged, resumes at the missing step on the next launch if it was
  closed early, and re-checks the backup's age on every launch.
- **Console instance**: leaf certificate minted from the lab key, named, with the private
  key protected by the OS keystore (macOS Keychain / Windows DPAPI / libsecret with an
  encrypted-file fallback).
- **Migration**: *Import lab key* (file + passphrase, or recovery code) → mint a new
  instance → restore the machine list and layout → beacon. Plus instance revocation and
  the revocation list pushed to agents on connect.
- **Alternating teacher machines** (`D-21`, ARCHITECTURE §3.7): the machine list self-heals
  from agents that connect with a valid certificate; `instances[]` in `lab.json`; the
  *other teacher machine* banner with the PCs it holds; *Take over the lab* via the
  beacon `take` field; revocation entries signed by the lab key and merged as a set from
  `RevocationState`; revoke moved behind a confirmation in Settings.
- Console gRPC server on `47800/tcp` with **mutual TLS** validated against the lab CA on
  both sides, plus revocation checking.
- UDP beacon on `47801` in the v2 format (instance public key + CA endorsement +
  signature), verifiable offline; rate-limited dialling and beacon-ignoring-while-
  connected on the agent side.
- `EnrollmentService`: single-use codes, CSR in, certificate out, code burned, machine
  recorded, duplicate/burned-code attempts reported as events; refused with a retry-later
  message while the lab key is locked (`D-24`). Writing a new USB payload voids the unused
  codes of earlier sticks, and *Remove from lab* revokes the PC's certificate (`D-28`).
- **Certificate renewal over the link** (`D-25`, ARCHITECTURE §3.8): `AgentService.Renew`,
  the agent asking from 60 days out, the console's *certificates need renewing* banner,
  console instance re-mint at startup when its own leaf is close to expiry.
- **The PC number is the identity** (`D-25`): a reinstalled PC replaces its old record and
  raises an event; `--rekey` keeps the `agent_id`.
- Agent-side link library shared by `FakeAgent` and the real agent: enrolment, reconnect
  with exponential backoff, offline job queue.
- `Job` plumbing end to end: console creates a job → delivered on `Link` → agent reports
  `JobProgress` / `JobResult` → jobs panel shows per-PC rows and persists to `logs/`.
  Idempotency by job id, including "a re-sent job returns the cached result".
- `FakeAgent`: N simulated machines with persistent per-machine state and failure
  injection (never connects, connects late, dies mid-job, replies with an error, presents
  a burned code, presents a revoked certificate).
- Console lab view: tiles in a grid, drag to rearrange, layout in `lab.json`, per-tile
  status (online / offline / last seen / logged-on user / agent version), inventory.
- **`schema_version` in every persisted file** (`lab.json`, `instance.json`,
  `enrollment.json`, `agent.json`, catalog YAML, backup archive) with a forward-only
  migration chain that is empty today, and a refusal — with the version named — to open a
  file written by a newer build (`D-20`). Cheap now, impossible to retrofit honestly.
- **The frozen protocol subset declared and enforced** (`D-19`): `Hello`, `Heartbeat`,
  `Job{self_update}` and `JobResult` fixed at wire version 1. The console accepts an agent
  whose `protocol_version` is below its minimum instead of refusing it, marks the tile
  *outdated*, and greys out what that agent cannot do. A test asserts the frozen messages
  round-trip between a v1 and a current serializer.

**Progress.** Everything in the deliverables list is built and covered by tests, on the
Mac, against `FakeAgent`:

- the trust core (`D-24`, `D-25`): lab key and wrappings, issuance, chain validation,
  renewal, the v2 beacon, signed revocation entries, single-use codes, the self-healing
  machine list, the job queue and ledger, the schema-versioned store, the OS keystore;
- the transport: beacon broadcaster and listener over real UDP, the `AgentLink` library
  shared by `FakeAgent` and the real agent, Kestrel with mutual TLS validated per call
  through `LabTrust`, `Enroll`/`Link`/`Renew`;
- `FakeAgent` with persistent per-PC state, installation from the console's USB payload
  and failure injection (`D-27`);
- the console: first-run wizard and import, lab view with draggable tiles, jobs, events,
  settings, the banners, the 15-minute unlock window and the sealed backup (`D-26`), which
  also carries the enrollment codes (`D-28`);
- the frozen-subset round trip and the backup tests.

The in-process test rig (`tests/LabControl.Console.Tests`) exercises the acceptance list
below with real TLS and real beacons: 30 agents enrolling, locked-key refusal, burned
codes, queued and online-only jobs, the cached result after a reconnect, reinstall by
number, renewal, revocation carried between consoles, a forged entry, an outdated agent,
console restart, discovery by beacon, take-over between two consoles, and a forged beacon.
The headless UI tests render the wizard and the main window and save PNGs.

**Live run (2026-09-05).** The owner ran the acceptance list by hand on the Mac with two
console profiles and 30 fake PCs. Everything held, and watching it surfaced what tests had
not: the first-run wizard could be bypassed by closing it (now it resumes on the next
launch), a saved layout let new tiles land on top of old ones, *Remove from lab* left a
valid certificate behind, enrollment codes piled up and were tied to the console that wrote
the stick (`D-28`), and the jobs and events lists painted their state text black on the
dark theme. All fixed in the same sitting. Not demonstrated live: a renewal refused while
the key is locked (enrolment and the first renewal happen in the same second against
`FakeAgent`, so the refusal is covered by `LinkTests` only) and the `schema_version`
refusal (covered by `PersistenceTests`).

**Acceptance criteria**

- 30 fake agents enrol from a fresh lab and appear online within 5 s of the console
  starting.
- **Migration test — the headline criterion.** Export the backup, delete the console's
  data directory, start the console on a *different* profile (simulating another
  computer), import the lab key with the passphrase, name the new instance. All 30 agents
  reconnect **with no change on the agent side at all**. Then repeat the import using the
  **recovery code** instead of the passphrase.
- Add a second key holder, then unlock the lab key with **that** holder's passphrase;
  remove the first holder and confirm their passphrase no longer opens anything.
- Revoke the old instance; an agent presented with the old leaf certificate refuses it
  and says why.
- **Renewal test.** A fake agent whose certificate has 30 days left asks to renew; with
  the lab key locked it is refused and keeps working; after the key is unlocked it gets a
  new certificate for the same agent id and number, reconnects with it, and the console
  shows the new serial. A console instance minted with 30 days left is re-minted at
  startup after the passphrase.
- **Reinstall test.** A fake agent enrols as PC-07, then enrols again with a new agent id
  as PC-07: the lab view still shows one PC-07, the old agent id is gone, and the event
  log names both.
- **Alternation test.** Two console profiles (A and B) for the same lab, both with the lab
  key imported. Run A with 30 fake agents; add a PC on A; close A; start B. All 30 agents
  are online on B within 15 s, the PC added on A is present on B, and nobody was asked for
  a passphrase. Close B, start A: same result in reverse.
- **Two live consoles.** Start B while A is running and holds all 30 agents. Nothing on
  the agents changes. B shows the banner naming A and listing the 30 PCs it holds; A shows
  the banner naming B with 0 PCs. Press *Take over* on B: every agent re-homes to B within
  15 s, A's tiles go to *held by B*, and a lock job issued on A while B held a PC is never
  delivered. No agent ever holds two `Link` streams.
- Revoke an agent on B, close B, start A: the first agent that connects to A brings the
  revocation with it and A refuses the revoked agent thereafter. A forged revocation entry
  (unsigned or signed by the wrong key) offered by a fake agent is ignored.
- Kill the console and restart it — every agent reconnects within 15 s.
- Change the console's bound address — every agent follows the new beacon within 15 s.
- A forged beacon (valid JSON, wrong CA endorsement) is rejected without a connection
  attempt; a beacon flood does not cause more than one dial attempt every 2 s.
- A burned enrollment code is refused and surfaces in the console as an event.
- A job sent to an offline agent is queued and runs on reconnect; a `shutdown` job for an
  offline agent is `not_delivered` immediately and is not delivered even if the agent
  connects a minute later (`deliver: online_only`, `D-25`).
- A fake agent that enrols while the lab key is locked is refused, keeps retrying, and
  enrols on its own once the teacher unlocks the key.
- A `lab.json` with a `schema_version` one higher than the build understands is refused
  with a message naming the version needed, and the console starts anyway rather than
  crashing; a backup exported by this build re-imports into it unchanged.
- A fake agent reporting `protocol_version: 1` against a console whose minimum is higher
  still connects, still appears in the lab view marked *outdated*, and can still be sent a
  `self_update` job.
- Unit tests cover: key wrapping/unwrapping by both passphrase and recovery code,
  certificate issuance and chain validation, certificate renewal for the proved identity
  only, beacon signing and verification, enrollment code lifecycle, machine replacement by
  number, job idempotency, `online_only` never queued, inactivity timeouts, reconnect
  backoff, schema migration and refusal, and the frozen-subset round-trip.

**Not in scope.** Real Windows anything, video, files, packages.

**Rough size.** Large — the backbone every later milestone plugs into.

---

## M2 — Windows agent: service, helper, power, scripts

**Goal.** A real Windows machine appears in the console, can be powered on, shut down,
rebooted, logged off, and can run a script with output streamed back.

**Where it is tested.** Both, as the owner confirmed: a **Windows VM on the MacBook** for
the fast edit-run-revert cycle and for anything destructive, and **`PC-00` in the lab**
for what a VM cannot honestly answer — Wake-on-LAN through a real NIC and BIOS, real
capture performance, real antivirus behaviour. Note the VM on Apple Silicon is
`win-arm64`, so both RIDs must stay publishable at all times.

**Deliverables**

- `LabControl.Agent` as a Windows service (`Microsoft.Extensions.Hosting.WindowsServices`),
  `LocalSystem`, auto-start, failure recovery, rolling 7-day log.
- Certificate enrolment on the real PC: keypair generated on the machine, private key
  DPAPI-protected at machine scope, ACLs verified.
- `LabControl.Agent.Session`: spawned into the active interactive session with a
  duplicated SYSTEM token (`WTSQueryUserToken` / `CreateProcessAsUser`), named-pipe
  protocol with the service, automatic restart on logon / logoff / crash. In M2 it does
  nothing visible — it proves the session plumbing and reports session state.
- Power jobs: `shutdown`, `reboot`, `logoff` via `InitiateSystemShutdownEx` /
  `ExitWindowsEx` with `SE_SHUTDOWN_NAME`.
- Wake-on-LAN from the console: magic packet broadcast plus directed to `last_ip`;
  "woke / did not wake within 90 s" reported per PC.
- `run_script`: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File` or `cmd /c`,
  as SYSTEM or in the student session, stdout/stderr streamed as `JobProgress`, exit code
  in `JobResult`, timeout and kill.
- Real inventory: hostname, Windows build, CPU / RAM / disk, uptime, logged-on user.
- The **side-by-side version layout** from the first install: binaries under
  `C:\Program Files\LabControl\app\<version>\`, `app\current` naming the running
  version, the service's binary path pointing inside it (`D-19`). No update machinery yet
  — just the layout, because it is what M4 needs to already be true on every PC.
- A **minimal push-and-restart** for the development loop: the console sends a new agent
  build to one machine, the agent unpacks it into a new version directory, repoints the
  service and restarts. Hash-verified; no signature, no probation, no rollback — those are
  M4. This exists because iterating on the agent from M2 onwards is unworkable if every
  build means walking to `PC-00` with a USB stick.
- A development-only install script (`scripts/dev-install.ps1`) so the agent can be
  deployed by hand before the real installer exists in M4.

**Acceptance criteria**

- The test machine enrols itself on first boot and appears online within 30 s, with
  correct inventory.
- Shut down `PC-00` from the console; wake it with Wake-on-LAN from the MacBook over
  Wi-Fi; it comes back by itself. If WoL fails, the cause is identified — BIOS, Fast
  Startup, or a NIC property — and recorded in `D-10`.
- Reboot and log off work and are reflected in the tile status.
- A script printing 100 lines and exiting with code 3 shows all 100 lines and `exit 3`;
  a script that hangs is killed at the timeout and reported.
- `student` cannot stop, pause or delete the service, cannot kill `session.exe`, and
  cannot read `C:\ProgramData\LabControl\`.
- Killing `session.exe` as an administrator brings it back within 5 s; logging off and on
  re-spawns it into the new session.
- Pulling the network cable for 2 minutes and plugging it back restores the link with no
  service restart.
- Five consecutive reboots leave the service healthy every time.
- Pushing a rebuilt agent from the console replaces the running version on `PC-00` in
  under a minute and the machine is back online by itself, with both version directories
  present afterwards.
- Defender does not quarantine the agent after the exclusion is in place; if a
  third-party antivirus is present on `PC-00`, its behaviour is recorded in `D-10`.

**Not in scope.** Screen capture, input, overlay, installer, packages.

**Rough size.** Large. Highest technical risk in the project.

---

## M3 — Screens: mosaic, full view, remote control

**Goal.** The teacher sees all screens at once and can take over one of them. The feature
the project exists for.

**Deliverables**

- Session helper capture: DXGI Desktop Duplication (Vortice) with a GDI `BitBlt`
  fallback; dirty-rectangle tracking.
- Thumbnail mode: whole screen downscaled to ≤ 320 px wide, JPEG q50 (SkiaSharp), ≤ 2 fps,
  sent only when the screen changed.
- Full mode: native resolution, 64×64 tile grid, only dirty tiles re-encoded at q75,
  keyframe every 5 s or on request, per-agent bandwidth cap (default 8 Mbit/s).
- `PushVideo` as a separate streaming RPC, so a video stall never delays a `shutdown`.
- Console: per-agent persistent bitmap, tile blitting, scalable mosaic (1–30 PCs, tiles
  shrink and the grid scrolls), double-click → full-size view with an input-control
  toggle.
- Input injection: `Input` → `SendInput` in the helper, normalized coordinates,
  `ctrl_alt_del` via `SendSAS` from the service.
- Graceful degradation: a PC that cannot capture (no session, locked, duplication
  failure) shows a clear reason on its tile instead of a frozen frame.

**Acceptance criteria**

- A mosaic of 30 tiles (29 fake + `PC-00` real) is smooth, the console stays responsive,
  and the MacBook's fan does not spin up.
- Thumbnail capture costs **≤ 5 %** of one core on the student PC, measured in Task
  Manager while the student is watching a video.
- Full view of `PC-00` at 1920×1080: **≥ 15 fps** while scrolling a web page, with input
  latency low enough to comfortably drive the mouse and type — verified by opening
  Notepad and typing a sentence from the Mac, including Ukrainian text.
- After one hour of streaming the helper's memory is stable and no GPU handle leak is
  visible.
- Switching a PC between thumbnail and full mode repeatedly leaves both working.
- Total bandwidth for 30 thumbnails stays inside the measured LAN headroom (`D-10`).

**Not in scope.** Broadcast to students, lock, exam mode, H.264.

**Rough size.** Large.

---

## M4 — Deployment: USB installer, file transfer, self-update

**Goal.** The lab can actually be rolled out: walk to each PC once with a USB stick, and
never walk again — including when the teacher machine is replaced.

**Deliverables**

- `LabControl.Setup` exactly as specified in `docs/INSTALLER.md`: `ISetupStep` pipeline,
  `--dry-run`, `--number`, `--uninstall`, `--rekey`, idempotent re-runs, `setup.log`,
  green summary, reboot prompt, Defender exclusion, third-party-antivirus detection.
- `tools/build-usb.sh` and the console's *Build USB installer* action, writing the
  **secret-free** payload of `docs/INSTALLER.md`: public CA certificate + a batch of
  single-use enrollment codes.
- File transfer both directions: `PullFile` and `PushFile`, hash-verified, resuming after
  a reconnect. Its first consumer is `install_package` — the reason the channel exists.
- **Send files to students** (`D-23`): `send_file` lands in `Materials` on the `student`
  desktop, optional *open after delivery* through the helper, one job per file, a batch
  entry in the jobs panel expanding to per-file, per-PC rows; *Send files…* on the toolbar.
- `self_update`, the full version (`D-19`, `docs/ARCHITECTURE.md` §7.2): a bundle whose
  manifest (version, per-file SHA-256, minimum installed version) is **signed with the lab
  key** and verified by the agent against its pinned `ca.crt`; unpack into a new version
  directory; repoint the service and restart; **probation** of 10 minutes before the
  version is accepted; **rollback driven from outside the agent** — the service recovery
  action `agent.exe --rollback` plus a scheduled task at the end of the window. Fleet-wide
  rollout with per-PC state (`stable` / `on_probation` / `rolled_back`) in the lab view.
- Updating the console itself, documented rather than engineered: replace the
  self-contained publish, plus the one-time macOS quarantine removal (`D-15`), in the
  README.
- Job fan-out across the whole lab in parallel, with a per-PC log bundle in `logs/`.

**Acceptance criteria**

- A factory-fresh PC goes from "Windows desktop" to "green tile in the console" with
  **one run of `Setup.exe` and one answer (the PC number)**, and reboots into auto-logon
  as `student`.
- Running `Setup.exe` again on the same PC changes nothing and reports every step as
  "already done".
- `Setup.exe --uninstall` leaves no service, no firewall rule and no LabControl
  directories; `--rekey` re-issues trust material in about 30 s while leaving the
  `student` account, the installed software and the settings untouched.
- All 14 PCs of the first lab are enrolled and visible.
- **Migration, for real this time.** Move the console to another computer (or another OS
  user profile standing in for one) using only the backup and the passphrase. All 14 real
  PCs reconnect **without anyone entering the classroom**.
- A stolen-stick drill: enrol a machine that should not exist, confirm it appears as
  unexpected in the console, and remove it in one click.
- Pushing a 500 MB file to all 14 PCs in parallel completes, is hash-verified, and
  survives one PC losing its link mid-transfer.
- *Send files…* with a 20 MB `.docx` and a `.pdf` to all 14 PCs: both appear in
  `Materials` on every `student` desktop within a minute, the `.docx` opens on every
  screen when *open after delivery* is ticked, a second send of the same file replaces the
  first without a duplicate, and a `.exe` sent this way lands as a file and is not run.
  Immediately afterwards an `install_package` job on the same PCs still works unchanged.
- `self_update` upgrades every agent, and they reconnect on the new version.
- **The deliberately broken release.** Push a bundle whose agent exits on start to all 14
  PCs. Every one of them is back on the previous version and online **without anyone
  entering the classroom**, and the console shows them as `rolled_back` with the version
  that failed. Repeat with a bundle whose agent starts but never reaches the console —
  same outcome, via the probation deadline instead of the crash counter.
- A bundle with a valid hash but a manifest signed by the wrong key is refused, and the
  refusal appears in the console as an event.
- An agent still running the version from before the protocol changed connects, is shown
  as outdated, and is brought current with one *Update* click.

**Not in scope.** Broadcast, lock, exam mode, the package catalog.

**Rough size.** Large.

---

## M5 — Classroom control: broadcast, lock, exam mode

**Goal.** The features used in every lesson.

**Deliverables**

- **Broadcast**: the console captures its own screen (macOS: ScreenCaptureKit /
  CoreGraphics via P/Invoke, with a first-run explanation of the Screen Recording
  permission; Windows: DXGI) and streams it to the selected agents; the helper renders a
  topmost full-screen overlay with input blocked.
- **Lock**: full-screen overlay with a teacher-supplied message, low-level keyboard hook,
  `BlockInput`. Ctrl+Alt+Del stays unblockable by design — stated in the UI, not hidden.
- **Exam mode** as four independent switches (`D-16`, `docs/ARCHITECTURE.md` §6.1), saved
  as named presets:
  - *Timer* — absolute deadline, countdown on the student screen, auto-end, extend and
    end-early from the console.
  - *Allowed programs* — whitelist enforced by the helper via process-start events,
    matched by **executable name only** (no process-tree following), with a hard-coded
    never-touch set (session, shell and OS processes, LabControl itself).
  - *Internet* — the internet policy of `D-22` (*blocked* or a *whitelist* preset) for the
    exam's lifetime, overriding any standalone policy and handing back to it at the end.
  - *Collect work* — one folder, chosen while setting up the exam, zipped and uploaded
    from every PC at the end, filed under `<exam>/PC-07/`.
- **Internet control as a standalone action** (`D-22`, `docs/ARCHITECTURE.md` §6.2):
  *open* / *whitelist* / *blocked* per PC or lab-wide, with a duration (*this lesson*, *N
  minutes*, *until I lift it*) and an 8 h hard limit; hostname presets with wildcards; the
  loopback resolver that turns allowed names into time-limited firewall allow rules; the
  *recently refused names* list in the console; a tile badge and `InternetState` reporting.
- Fail-safe machinery for all of the above: state persisted on the agent, an absolute
  hard limit, and "restore first, then re-apply" on service start.
- **Profile reset**: full (log off `student`, delete the profile via `Win32_UserProfile`,
  reboot into a pristine auto-logon) and light (wipe Desktop / Documents / Downloads).

**Acceptance criteria**

- Broadcasting the Mac's screen to all 14 PCs is readable from the back of the room, and
  students cannot escape the overlay with Alt+Tab, Win, or by clicking.
- Lock with a message works on every PC and unlocks cleanly, including on a PC that was
  rebooted while locked.
- Each exam switch works **alone**: a timer-only session leaves the internet and the
  programs untouched; an internet-block-only session shows no countdown.
- **Internet control drill.** *Blocked* on all 14 PCs: no site loads, the console keeps
  every PC online, `install_package` and *Send files* still work. *Whitelist* with
  `*.jetbrains.com` and `docs.oracle.com`: those load in a browser (including a browser with
  DNS-over-HTTPS switched on), everything else shows a blocked page within 2 s, and the
  refused names appear in the console. *Open* restores the original DNS servers exactly.
  Kill the agent service under a whitelist: on restart the PC has its internet back before
  anything is re-applied; pull the PC's power under a whitelist: same result after boot.
- A standalone *blocked* policy set for the lesson survives an exam that starts and ends
  inside it, and is still in force after the exam.
- **Fail-safe drill, mandatory.** Start an exam with the internet blocked and the
  whitelist on, then close the console's laptop and leave it closed. Every PC restores
  itself at the hard limit. Repeat by killing the agent service mid-exam: on restart the
  PC comes back unrestricted before anything is re-applied.
- Reboot a PC mid-exam: it comes back still in the exam, with the correct remaining time.
- The whitelist never kills a system process, and a PC in whitelist mode still shuts down
  cleanly on command.
- Collect-work gathers files from all 14 PCs into per-PC folders, and a PC that was off
  is reported as missing rather than silently skipped.
- Profile reset returns a deliberately messed-up `student` desktop to a clean state in
  one command, and the PC comes back online by itself.

**Rough size.** Large. The program whitelist, the firewall switch and the loopback
resolver are the risky parts.

---

## M6 — Software catalog, localization, polish

**Goal.** Everything that makes the system pleasant to own for one person with no IT
department. The catalog ships **empty** — the deliverable is the ease of filling it.

**Deliverables**

- **Add package wizard**: point the console at any local `.exe` / `.msi`; it suggests
  silent arguments for known installer families (NSIS, Inno, InstallShield, MSI),
  computes the hash, proposes a `detect` rule, writes the YAML, and offers a **test
  install on one PC** before the fleet.
- `install_package` job: push over the LAN, run silently, verify `detect`, report per PC,
  "Install on all missing".
- Ukrainian localization of the console UI from the resource files created in M0.
- Robustness pass: reconnect storms, many simultaneous full-view requests, disk-full,
  clock skew, a PC powered off for a week, a lab key backup older than the machine list.
- Documented and *rehearsed* recovery: "the teacher machine died and the lab must run
  tomorrow" performed end to end from the backup.
- `README.md` rewritten as a teacher-facing operating manual: what to click before a
  lesson, what to do when a PC does not appear, how to move the console to a new
  computer.
- Optional, and only if M3's measurements demand it: H.264 via Media Foundation behind
  the existing `VideoFrame` envelope. Decide with numbers, not taste.

**Acceptance criteria**

- The owner adds a package of his own choosing **without reading the source code**, using
  only the wizard, and installs it on one PC and then on the rest.
- A deliberately broken package (wrong silent arguments) reports a clear failure on every
  PC without hanging the job or the console.
- Installing one real program on all 14 PCs in parallel, with the PCs having **no
  internet**, succeeds with `detect` verified; the wall-clock time is measured and written
  into this file next to the manual afternoon it replaces.
- Every UI string comes from the resource files; switching to Ukrainian changes all of
  them.
- The console runs a full 90-minute lesson without a restart, a leak, or a stuck job.
- The recovery procedure is executed once for real, from the backup, and it works.

**Rough size.** Medium.

---

## Cross-cutting work, tracked continuously

Not milestones; done inside whichever milestone touches them.

- **Security.** Re-read `docs/ARCHITECTURE.md` §3 and §9 whenever a new surface is added.
  Never log the `student` password, the lab passphrase or a private key.
- **Portability.** Two constants may never appear in code: the number of PCs, and the
  identity of the machine running the console.
- **Failure reporting.** Every failure the teacher could act on becomes a visible event
  with a plain-language cause, not a stack trace in a log file.
- **Tests.** Crypto, protocol serialization, state machines (reconnect, job lifecycle,
  session supervision, exam-mode restore) and installer `Check()` logic are unit-tested.
  Video and Win32 are verified by hand on `PC-00` — do not fake confidence with mocks
  there.
- **Documentation.** See below.

## Documentation workflow

`.md` files are the source of truth. `docs/html/` is a **generated mirror** — never edit
it by hand. After changing any `.md`:

```bash
tools/docs-build.sh
```

Committing a `.md` change without the regenerated HTML is incomplete work (Definition of
done, item 6).

## On-site verification checklist (fills `D-10`)

Do this on the first visit to the lab; several milestones depend on the answers.

- [ ] Router model; is the Wi-Fi on the **same subnet and broadcast domain** as the wired
      hub? Is AP isolation / guest mode off?
- [ ] Do UDP broadcasts from the MacBook reach the wired PCs? (Test before M1 ships.)
- [ ] Is it a switch or a real hub? Bandwidth headroom for 14 (later 30) video streams.
- [ ] DHCP lease behaviour; do PCs keep their IPs?
- [ ] Per PC: BIOS/UEFI "Wake on LAN" / "Power on by PCI-E" enabled.
- [ ] NIC models across the PCs (Realtek/Intel differences change the WoL settings).
- [ ] Windows edition and build (Home vs Pro changes some policy options).
- [ ] Local administrator account name and password, per PC or common.
- [ ] Antivirus in use — Defender only, or a third-party product that Setup cannot
      configure?
- [ ] Do the PCs have internet at all, and is it permanent or occasional?
- [ ] Screen resolutions (drives the mosaic tile sizing).
- [ ] Which folder should be offered as the default when setting up *collect work*?

Record the answers as an update to `D-10` in `docs/DECISIONS.md`.

## Answered — settled requirements

Kept here so the reasoning is not lost.

| Question | Answer | Where it landed |
|---|---|---|
| Is the teacher machine fixed? | No. Several teacher machines take turns (MacBook some days, the Windows desk PC on others); one drives the lab at a time; the software must also move to other labs; needs a security scheme for replacement | `D-13`, `D-21`, ARCHITECTURE §3, M1 |
| Code signing? | None available; accept warnings and add an antivirus exclusion | `D-15`, INSTALLER step 6a |
| Which packages on day one? | None. The catalog ships empty; what matters is a comfortable way to add installers later | `D-16` context, M6 |
| Do installers need downloading? | No. The lab's software is already installed; testing will use an arbitrary program later | M6 |
| Exam mode? | Yes, and every restriction is an independent switch | `D-16`, ARCHITECTURE §6.1, M5 |
| Lab size? | Design and load-test for up to 30 PCs | `D-17` |
| Where to test the real agent? | Both a Windows VM (fast, destructive tests) and `PC-00` in the lab (WoL, capture, performance) | M2 |
| Can someone else open the lab key? | Yes — multiple named key holders, each with their own passphrase | `D-13`, ARCHITECTURE §3.2, M1 |
| Two consoles at once? | Not a mode, but it must not break: two live consoles split the room, never share a PC, and either can *Take over*. A shared room is out of scope | `D-21`, ARCHITECTURE §3.7 |
| What does *collect work* take? | One dedicated folder, chosen when the exam is set up — not the whole desktop | `D-16`, M5 |
| How precise is the whitelist? | Executable names only; no following of child processes | `D-16`, M5 |
| Internet control outside exams? | Yes: *open* / *whitelist* (hostnames, wildcards) / *blocked*, standalone with a duration and a hard limit, and the same thing as the exam's internet switch | `D-22`, ARCHITECTURE §6.2, M5 |
| Sending files to students? | Yes — but the file channel exists first for installing software without a USB walk, and that flow stays untouched; handouts land in `Materials` on the student desktop | `D-23`, ARCHITECTURE §6, M4 |
| How does LabControl update itself? | Side-by-side version directories, a bundle signed by the lab key, 10-minute probation and a rollback driven from outside the agent; the console is updated by replacing its binary | `D-19`, ARCHITECTURE §7, M2 + M4 |
| How do old agents and new consoles coexist? | A frozen protocol subset (`Hello`, `Heartbeat`, `Job{self_update}`, `JobResult`); an old agent is never refused, only marked outdated | `D-19`, PROTOCOL *Versioning*, M1 |

## Open questions for the owner

None outstanding. Every question raised so far is answered and recorded above; the
remaining unknowns are physical facts about the room, in the on-site checklist.

## Out of scope for v1

Multi-lab management from one console, two consoles *sharing* one lab at the same time
(alternating teacher machines are in scope, `D-21`), cloud relay, mobile console, Linux/macOS student agents, grading or LMS integration,
student-initiated help requests, session recording.
