# LabControl — classroom fleet control for the ОНТФК computer lab

## What this is (read first)

LabControl is a self-hosted classroom-management system, currently implemented for
one lab per console profile. The first room has
**14 student PCs (Windows 10/11, x64, wired to a hub)** managed from **one teacher
machine (currently a MacBook Air M-series on Wi-Fi, same router; may become a Windows
PC later)**. It replaces walking from desk to desk to install software, power
machines on/off, and watch what students are doing.

Planned M5 (`D-53`) lets teachers bulk-import files for several labs and select the room
for each lesson. Only one lab is active per console; inactive labs keep saved metadata
without background connections or video. Room rosters are mostly stable, while teachers
and their devices move between rooms. Routine lab files are distinct from full
administrator backups and must not distribute the CA private key or enrollment codes.
Administrators can add those full `.lcbak` backups directly to the same lab list and
switch rooms identically, retaining administrator authority per lab. M5 also includes
simple offline teacher-console installers/packages: app files, launchers and file-type
registration, with no student service or system preparation (`D-54`, INSTALLER).
The console still hosts a network server: LAN permissions, native OS prerequisites,
document activation and key access after updates are explicit M5 acceptance work.

Owner: Viacheslav (teacher, Odesa Technical Vocational College). Solo project, built
with Claude Code and Codex. The teacher is also the only admin — there is no IT department
behind this, so **everything must be zero-maintenance and self-explanatory**.

## Why it exists

- Installing an IDE/JDK/Python on 14 machines by hand takes a whole afternoon.
- Turning the lab on before class and off after it means touching every PC.
- During labs the teacher needs to see all screens at once, take control of one to
  help, show his own screen to everyone, or lock screens during a test.
- Existing tools (Veyon, iTALC, commercial suites) either have no macOS master,
  need per-PC manual configuration, or pull in a second product to maintain.
  Decision (see docs/DECISIONS.md D-03): **build everything ourselves, no third-party
  runtime programs on the student PCs.**

## Hard requirements (do not silently relax these)

1. **Teacher console is cross-platform and replaceable**: macOS today, Windows tomorrow,
   Linux nice-to-have. The lab's identity is a private CA, not the console's certificate,
   so the teacher machine can die, be stolen or be swapped for a Windows PC and the lab
   keeps working after importing one encrypted backup file — **without touching a single
   student PC** (docs/ARCHITECTURE.md §3, D-13). Several teacher machines may hold a
   console permanently and **take turns** (MacBook one day, the Windows desk PC the next);
   one drives the lab at a time, two at once is tolerated but never shared (§3.7, D-21). Nothing may hard-code which computer
   runs the console, and nothing may hard-code 14 PCs: design for **up to 30** (D-17).
   M5 adds several saved labs with **one active lab per console** and fast switching
   (`D-53`); the 30-PC limit applies to each lab. Different teachers may independently
   use different rooms at the same time. Switching releases the old room first.
2. **Student agent is Windows-only** (10/11 x64). Runs as a Windows service, survives
   reboots, cannot be killed or uninstalled by the student.
3. **Installer is one-shot from a USB stick**: run once as local admin on each PC,
   asks at most one question (PC number) with default options, does *everything* else itself (service,
   firewall, Wake-on-LAN, power settings, `student` user, auto-logon, enrollment).
   M4 adds a checked-by-default account-creation checkbox: untick it for testing with
   an existing home-PC account, preserving accounts and sign-in settings. A standalone
   uninstaller works without the USB or console (`D-40`, `docs/INSTALLER.md`).
   The stick carries **no secret** — only the public CA certificate and single-use
   enrollment codes (D-14).
4. **`student` account**: standard (non-admin) local user, password `1`, auto-logon at
   boot, profile can be reset to a clean desktop on command. This is the default classroom
   configuration; installations opting out never reset or adopt a personal profile (D-40).
   Account-on Setup journals explicit UAC credential entry (`EnumerateAdministrators=0`)
   before hiding the original administrator tile, and restores the tile before that
   machine-wide policy. Account-off touches neither; UAC security policy is not bypassed
   to satisfy administrator-maintenance acceptance (D-52).
5. **Live screens**: mosaic of all 14 screens on the teacher's monitor, click a tile
   for full-size view with mouse/keyboard control.
6. **Power**: Wake-on-LAN, shutdown, reboot, log off — for one PC or all.
7. **Software & commands**: install packages and run scripts on all PCs in parallel,
   with per-PC result/log. Student PCs may have no internet — installers are
   distributed from the console over the LAN.
8. **Broadcast, lock & exam mode**: show the teacher's screen full-screen on every
   student PC; lock student screens/input with a message; and an exam mode built from
   four **independent switches** — countdown timer, allowed-programs whitelist, internet
   block, collect-work-at-the-end — that always restores the machine by itself if the
   console disappears (D-16).
9. Works on an isolated LAN with no server, no cloud, no domain, no internet.

## Tech stack (decided — see docs/DECISIONS.md)

- **.NET 10 (LTS, supported until Nov 2028)**, C# 14. Not .NET 8/9 — both reach
  end of support on 10 Nov 2026.
- **Avalonia UI 12.x** for the teacher console (macOS/Windows/Linux, Skia rendering).
  Pinned in `Directory.Packages.props`; see D-02.
- **gRPC (Grpc.AspNetCore / Grpc.Net.Client) over TLS** for all console↔agent traffic
  (control, events, video frames, file transfer). Protobuf contracts in `src/LabControl.Shared/Protos`.
- **Win32 via CsWin32 (`Microsoft.Windows.CsWin32`) source generator** for the agent;
  **Vortice.Windows** for DXGI Desktop Duplication. No SharpDX (unmaintained).
- **SkiaSharp** for JPEG encode/decode on both sides (Avalonia already ships it).
- Self-contained single-file publishes (`win-x64` for agent/installer;
  `osx-arm64`, `win-x64`, `linux-x64` for the console). No runtime install required.
- No PowerShell dependency in the installer: it is a C# exe with a
  `requireAdministrator` manifest and calls Win32/WMI/registry directly.

## Repository layout

```
LabControl/
├── AGENTS.md                  ← Codex instructions + shared agent-document contract
├── CLAUDE.md                  ← this file
├── README.md                  ← human overview + quick start
├── LabControl.sln             ← classic .sln, not .slnx (D-18)
├── Directory.Build.props      ← settings shared by every project
├── Directory.Packages.props   ← central package versions; projects reference names only
├── global.json                ← SDK band + the Microsoft.Testing.Platform opt-in
├── docs/
│   ├── ARCHITECTURE.md        ← components, processes, data flow, threat model
│   ├── PROTOCOL.md            ← gRPC services, message flows, discovery, video encoding
│   ├── INSTALLER.md           ← exactly what the USB installer does, step by step
│   ├── DECISIONS.md           ← ADR-style log of decisions and rejected alternatives
│   ├── ROADMAP.md             ← milestones M0…M7 with acceptance criteria
│   └── html/                  ← GENERATED mirror of every .md — never edit by hand
├── src/
│   ├── LabControl.Shared/     ← .proto files, generated stubs, shared models, constants
│   ├── LabControl.Console/    ← Avalonia app (teacher). Hosts the gRPC server.
│   ├── LabControl.Agent/      ← Windows service (SYSTEM). gRPC client. Privileged ops.
│   ├── LabControl.Agent.Session/ ← helper exe spawned into the interactive session:
│   │                              screen capture, input injection, lock/broadcast overlay
│   ├── LabControl.Setup/      ← the USB installer exe (Windows, elevated)
│   └── LabControl.FakeAgent/  ← cross-platform simulated agent for developing the
│                                 console on macOS without any Windows machine
├── tests/                     ← xUnit; protocol, state machines, installer steps (dry-run)
├── tools/                     ← build/publish scripts (build-usb.sh, publish-all.sh),
│                                 docs-build.sh + DocsBuild/ (Markdown → docs/html/)
├── packages/                  ← package catalog (*.yaml) + cached installers (git-ignored binaries)
└── scripts/                   ← library/ = seed of the console's script library (D-38); dev-install.* for the VM
```

Solution file: `LabControl.sln` at the root (create in M0).

## Development environment realities

- Primary dev machine is an **Apple Silicon Mac**. `dotnet publish -r win-x64` cross-compiles
  the agent fine, but **Win32 code cannot run or be debugged on the Mac**.
- Therefore: the console is developed against `LabControl.FakeAgent` (N simulated PCs,
  synthetic frames, fake power state). The real agent is tested on a lab PC designated
  as the test box (call it `PC-00`) or in a Windows VM. A Windows-on-ARM VM on the Mac
  needs a `win-arm64` publish; the lab PCs need `win-x64` — publish both.
- Rider is the IDE of choice; keep the solution Rider-friendly (no VS-only project types).
- Keep everything buildable with plain `dotnet build` / `dotnet test` from the CLI.

## Conventions for Claude Code

- Read `AGENTS.md` as the companion instructions for Codex. `CLAUDE.md` and
  `AGENTS.md` are shared project documentation, not private notes for one agent.
  Whenever project facts, requirements, status, workflows, commands, repository
  layout or shared conventions change, inspect **both** files and update every
  applicable section in the same task — regardless of which agent is doing the work.
  Never leave the other agent with stale or contradictory guidance. Instructions that
  truly apply to only one agent may stay in one file when they are explicitly labelled
  as agent-specific.
- Read `docs/ARCHITECTURE.md` and `docs/PROTOCOL.md` before touching `src/`.
  Any change to a `.proto` file must be reflected in `docs/PROTOCOL.md` in the same commit.
- **Documentation is part of every change, not a follow-up.** The `.md` files are the
  single source of truth; `docs/html/` is a generated mirror. Whenever you change a
  `.md` file — or change behaviour that a `.md` file describes — update the Markdown
  **and** run `tools/docs-build.sh` before finishing the task. Never hand-edit anything
  under `docs/html/`. A change that leaves the docs stale, or the HTML out of sync with
  the Markdown — including either agent's applicable instructions — is not finished.
- Any non-obvious design choice goes into `docs/DECISIONS.md` as a new `D-NN` entry.
- Code, identifiers, commits, comments: English. UI strings of the console: English first,
  Ukrainian localization is a later milestone (resource files from day one, no hard-coded UI text).
- Agent must never crash the student's session: every Win32 call is wrapped, every
  failure is reported to the console as an event, never thrown out of the service loop.
- Never log the `student` password, the lab key or its passphrase. Never commit
  `lab-key.lck`, `lab.json`, certificates, or `packages/**/*.exe|msi` (see `.gitignore`).
- Prefer boring, well-supported libraries. Every NuGet dependency must be listed in
  `docs/DECISIONS.md` with a one-line reason.
- Don't invent features beyond `docs/ROADMAP.md`; propose them there first.
- Two things must never be constants in code: **the number of student PCs** and **which
  computer runs the console**. Both come from `lab.json` / the lab key.
- Never write the lab key, its passphrase, or a private key to a log, a temp file, or the
  USB payload.
- Ports, names, paths, and the `student` account details are constants in
  `LabControl.Shared/Defaults.cs` — one place, referenced everywhere.

The console and Windows Setup share `src/LabControl.Console/Assets/labcontrol.ico`,
regenerated by `tools/make-icon.py`. Setup embeds it in its executable and dialogs;
the installed uninstaller is a copy of that executable.

## Quick commands

```bash
dotnet build                                   # everything, Windows projects included
dotnet test                                    # unit tests (Microsoft.Testing.Platform)
dotnet run --project src/LabControl.Console    # console on the Mac
dotnet run --project src/LabControl.FakeAgent -- --count 14 --payload ~/usb   # 14 fake PCs from the console's USB payload
tools/publish-all.sh                           # self-contained binaries for all RIDs
tools/build-usb.sh /Volumes/USB               # USB installer payload (needs lab.json from the console)
tools/docs-build.sh                            # regenerate docs/html/ after ANY .md change
tools/make-icon.py                             # regenerate the console icon into src/LabControl.Console/Assets
```

## Current status

See `docs/ROADMAP.md` — it holds the milestone table, the per-milestone acceptance
criteria, the on-site verification checklist and the open questions for the owner.

**Planning update (2026-09-08, D-53):** new M5 is lab files, teacher access and fast
switching between rooms; it is not implemented. Previous M5 classroom control is now
M6, and previous M6 catalog/localization/polish is now M7. Existing single-lab backup
import and device alternation remain the current behavior until M5 is delivered.
`D-54` extends M5 with direct administrator-backup onboarding into that same selector
and lightweight desktop installers for teacher devices. Switching does not re-import
or unlock the CA; privileged operations still require unlocking the selected lab's key.

**M5 design recorded, implementation started (2026-09-08, `D-55`…`D-60`):** the profile
store and resumable migration (`profiles.json`, `labs/<lab_id>/`, `D-55`), the signed
`.lclab`/`.lcreq`/`.lcgrant` exchange with the role in the subject OU and `instance:`
revocation (`D-56`), `ActiveLabController` with release-before-acquire, the departure
report and results bound to the delivering instance (`D-57`), take-over on the agent's
clock and observed-only ownership (`D-58`), the C# per-user Windows installer, scripted
`.app`/`.dmg`, Linux tarball, single instance and scoped firewall rules (`D-59`), and
dormant imported enrollment codes (`D-60`). Eight portions (ROADMAP M5, *How it is being
built*); **all eight built and reviewed**, 1–3 on 2026-09-08 and portions 4–8
on 2026-09-09 (portion 1: profile store, resumable
migration, `lab.json` schema 2, `console.lock`; migration tried on a copy of the owner's
live data, the live directory migrates on the next M5 launch; portion 2:
`ActiveLabController`, the *My labs* chooser, *Disconnect*, the departure report, bulk
`.lcbak` import; real-Mac switch timing under investigation, `D-57` item 11; portion 3:
signed `.lclab`/`.lcreq`/`.lcgrant` exchange, teacher sessions without a vault, `instance:`
withdrawal with confirmed delivery, the *Teacher devices* panel, dormant imported codes;
753 tests, smoke-tested on two copies of the data directory; portion 4: results and progress
bound to the console instance the agent's TLS handshake validated, `jobs-inflight.json`
restoration with narrow re-send rules, 785 tests, verified with a real agent on the isolated
Windows clone — instance X's result waited for X while instance Y saw nothing; portion 5:
take-over decided on the agent's own clock (arrival order, the press bound to its own
beacon, honoured once), the four ownership states a console can prove — linked here,
observed elsewhere within `Defaults.OwnershipObservationLifetime`, unknown, offline — the
banner's *holds at least N*, and the additive informational `Welcome.console_access` that
never decides a refusal; the portion-5 security fixes of 2026-09-09 then made a withdrawn
instance's beacon refused where the beacon is judged — by the agent ahead of both the
take-over and the dial branch, and by the console — and made ownership follow the PC's own
`link.taken_over` departure report instead of any stream that ends, with the banner wording
expiring and the beacon's arrival stamped in the listener's receive loop; still not run on
the Windows VM with two consoles and skewed clocks; portion 6:
console documents on the command line, `--import-only`, a second launch forwarding its file
paths over a per-data-directory named pipe or private-directory Unix socket instead of
starting a second console, macOS file activation, 788 tests, checked by hand on the Mac —
the Windows pipe since exercised by portion 7's Windows drill and the Linux `SO_PEERCRED`
path still not run;
portion 7: teacher-console packaging (`D-59` items 1–4) — the single-file per-user Windows
installer with an untrusted `installed-files.txt`, a finishing temporary copy and an
uninstall that never elevates, the read-only LAN-access banner that only calls a port
reachable when a rule opens it for this console and nothing blocks it, the ad-hoc-signed
macOS `.app`/DMG, the per-user Linux tarball and `tools/package-*.sh` into
`artifacts/package/`, 936 tests after the merge with portions 4 and 6, packages built and
the Linux scripts round-tripped on the Mac; **run on Windows for the first time on
2026-09-09** (isolated Windows 11 ARM64 clone, guest `PC-27`, no interactive session, so
every unelevated step ran as `LOCAL SERVICE` and the elevated one as `SYSTEM`), which found
four defects, all fixed and merged (`a7fbe33`): an `app.manifest` double hyphen inside an
XML comment that made Windows refuse the activation context so the installer would not
start at all, a firewall profile constant of 6 that opened Public and rejected a real
private-and-domain rule (`NET_FW_PROFILE_TYPE2` is Domain 1, Private 2, Public 4, so 3),
a `group=` argument in the printed `netsh` line that `add rule` refuses outright, and an
`ArgumentList`-built self-delete command `cmd.exe` cannot parse, which left a 148 MB copy
in the temp directory. The install, the plan and dry run, the shortcut, the Installed-apps
entry, both file-type registrations, an idempotent repeat run, the lock refusal, document
forwarding into a running console over the Windows named pipe, the real firewall rules and
the banner, uninstall with its temporary copy, `--remove-data` and a DPAPI-sealed key
surviving replacement are now verified. Still unverified: Explorer's own double-click, a
real elevation prompt a teacher answers, `win-x64` (only ARM64 was built and run), the
installer under an ordinary interactive profile, the unsigned-download warning, the Linux
`SO_PEERCRED` path and a real Linux desktop menu);
portion 8: the acceptance drills and the six gaps an adversarial audit of every M5
acceptance criterion found (`D-68`) — the combined picker filter that makes a mixed
selection possible, batched authorization in one folder, the departure flow *Disconnect*,
the window close and quit all ask, the beacon-resume step-over that relinks thirty refused
PCs in 2.7 s instead of 30.3 s, and agent events bound to the console that delivered their
job while machine events stay unbound. Main stands at 984 tests (755 Shared + 229 Console),
run twice on the merged tree. **Still unverified:** the Windows and Linux runs of the
console packaging, portion 5 on Windows with two consoles and skewed clocks against a real
agent, the Avalonia quit hook by hand, and the milestone in the physical lab.
ARCHITECTURE §3.9/§4 and PROTOCOL (*Discovery beacon*, *Files exchanged offline*,
*M5 additions*) describe the design; the `Welcome.console_access` `.proto` change landed
with portion 5, and PROTOCOL was updated in that commit.

**M0 is done (2026-09-04). M1 is done (2026-09-05)**: the trust model, beacon discovery,
mutual TLS, enrolment, the `Link` stream with jobs and renewal, take-over between teacher
machines, the sealed backup, `FakeAgent` and the console UI — see the M1 *Progress* and
*Live run* paragraphs in `docs/ROADMAP.md` and `D-24`…`D-28`.

**M2 is in progress, built in four portions** (ROADMAP M2, *How it is being built*), each
run on the owner's Windows VM before the next. Portion 1 is verified on the VM: `LabControl.Agent`
is a real service host with a DPAPI-protected store, `agent.exe --install` provisioning from
the USB payload, real inventory (CsWin32 + registry), the `app\<version>` layout and
`scripts/dev-install.ps1` (`D-29`). Portion 2 is verified on the VM too: `Agent.Session` is a
real helper supervised by the service over the named pipe (`SessionSupervisor`,
`SessionLauncher`, `PipeFraming`; `D-30`), and `SessionState` carries `locked` and
`helper_alive` to the tile. Portion 3 is verified on the VM as well (2026-09-05…07): power
jobs (`PowerControl`), Wake-on-LAN from the console (`Shared/Power/WakeOnLan`, tracked in
`LabSession`; only `wake.sent`/`wake.failed` on the VM, the real test waits for `PC-00`),
the minimal `PullFile` (`FileOffers` on the console, `AgentLink.PullFileAsync` on the agent)
and `run_script` on top of it (`ScriptRunner`, `UserProcessLauncher`,
`Shared/Jobs/ProcessRunner`, the per-shell byte rules in `Shared/Jobs/ScriptText`; `D-31`,
`D-32`); the teacher-facing script library is M4 portion 1 (built, see below) and replaced
the development *Run test script…* dialog. Jobs outlive a dropped link (`D-32` item 7). The VM
runs taught three Windows-only lessons worth remembering: cmd.exe needs CRLF line endings
and both shells are forced to UTF-8 (`D-32` item 5), a helper ended by Windows at logoff is
a planned restart held for `HelperExitGrace` (`D-32` item 12), and `Disable-NetAdapter`
bugchecks the virtio guest — cut the network with a firewall rule instead. **Portion 4 is
built (2026-09-07)**: the minimal push-and-restart (`D-33`) — *Push
agent build…* in the console, `SelfUpdateRequest` / `AgentBuild` / `UpdateBundle` in Shared,
`AgentUpdater` + `ServiceControl` (`agent.exe --restart-service`) in the agent; the new
version answers the re-sent job and `Hello.agent_version` is the `app\<version>` name.
**Portion 4 is verified on the VM too (2026-09-07)**: three pushes — a new build, the same
build again, a `0.1.1` build that pruned the oldest directory — all ended with the new
version answering the re-sent job; the first portion-4 agent has to be installed by hand
once, because a portion-3 agent refuses `self_update`. The M4 Setup executable is being integrated; Windows acceptance is pending.
**M3 is in progress, in three portions** (ROADMAP M3, *How it is being built*; `D-34`).
**Portion 1 is built (2026-09-07)**, all on the Mac: `Shared/Video/` (`JpegCodec`,
`VideoGeometry`, `ScreenImage`, `VideoPacer`, `VideoSettings`, `VideoUplink`), `VideoControl`
handling and `TryPushVideo` in `AgentLink`, `PushVideo` served by the console into
`Services/ScreenStore`, the `ScreenView` control on the tile and in `ScreenWindow`, and
`FakeAgent`'s `FakeScreen`. **Portion 2 is built and verified on `PC-10` (2026-09-07: DXGI picture, back after lock and reboot)** (`D-35`):
the producer loop is `Shared/Video/ScreenProducer` over an `IScreenSource` (the simulator's
`FakeScreen` is one; `FakeScreenStreamer` is gone), `session.exe` captures with
`DxgiScreenSource` (Vortice) or the `GdiScreenSource` fallback (`TileDiff` for its rectangles),
`DesktopAccess` makes it DPI aware and follows the input desktop, and `SessionSupervisor`
relays `VideoControl` down the pipe and `VideoFrame` up into the uplink, dropping a refused
frame with a keyframe request back. Two `PC-10` lessons: the antivirus holds a freshly run
`agent.exe` (`D-33` item 9, retry) and a pipe flush deadlocks two read loops (`D-35` item 8,
never await a write from a read loop). **Portion 3 is built (2026-09-07), not yet run on a
PC** (`D-36`): `Input` on the link (`Shared/Control/InputMessages`, `InputQueue`;
`AgentLink.InputReceived`), the console's `InputMapper` + `KeyMap` (text as text, shortcuts
by physical key, ⌘ as Ctrl) behind the *Control* toggle in `ScreenWindow`, the service
relaying input down the pipe and raising Ctrl+Alt+Del itself (`SecureAttention`:
`SoftwareSASGeneration` + `SendSAS`), the helper's `InputInjector` (`SendInput` on a thread
attached to the input desktop), the PC's own reasons on the tile (`capture.*`, no session,
helper down), and `FakeScreen` drawing the teacher's input. **Run on `PC-10` the same day**:
mouse, text, Ctrl+Alt+Del work; scrolling Edge reached 10–17 fps once the full-mode cap
went to 24 Mbit/s (`D-36` items 10–11). The single-PC window then got a quality selector
with an *Auto* mode the producer drives from the pacer's waits (`D-37`,
`VideoFrame.quality`). Version 0.1.4, verified on `PC-10`: *Auto* at q40–50, 14–18 fps
while scrolling. M3's close-out measurements (an hour of streaming, the 30-tile mosaic,
Ukrainian text) remain separate M3 close-out work; M4 implementation has started.
**M4 portion 3 planning update (D-40):** optional student creation (checked by default),
existing-account testing and standalone uninstall with ownership-aware restoration are
planned baseline; Setup executable integration is now in progress. **The shared account journal is built (D-45):**
`InstallationDocument` / `InstallationState` persist the selected mode, installation id,
pending creation and created SID in `installation.json`. Repair preserves the choice;
missing legacy history, account collisions and interrupted creation never imply ownership.
The owner confirmed on 2026-09-08 that M4 covers clean installation and owned repair only;
unowned legacy dev installations are refused, and legacy migration is outside M4.
Managed-profile access and confirmed removal require the recorded SID to match. Mac tests
cover these rules and storage failures. **Windows account preparation is built (D-46):**
`AccountSetupScope` checks private ownership storage and holds an exclusive file lock;
`StudentAccountProvisioning` connects the journal to `WindowsStudentAccountSystem`
(create-new local SAM account, disabled, with immediate SID/creation-marker read-back).
The component is connected to the executable Setup pipeline, with explicit activation,
Users membership, native sign-in/settings adapters and standalone removal (D-52). Native
account lifecycle verification is still required.
**The protected settings journal core is built (D-47):** `SetupSettingsJournal` records
original/applied values before mutation, confirms native read-back, preserves user edits
and refuses ambiguous interrupted applies. `AccountSetupScope` binds its encrypted
`setup-settings.json` to this installation using machine-scope DPAPI; temporary files
contain only ciphertext. Mac tests cover recovery and storage failures; Windows DPAPI
still needs Windows verification. **The first machine registry adapters are built
(D-48):** Fast Startup and SoftwareSASGeneration use typed DWORD snapshots and the
protected journal through `AccountSetupScope`. Existing SAS value 3 is retained;
unsupported native types and observed concurrent changes are refused. Executable integration is built; native execution remains to be verified.
**AC power-plan adapters are built (D-49):** sleep/display/disk timeouts use the protected
journal with the active scheme GUID and original seconds. Native activation is confirmed
before completion; interrupted restoration retries activation even after the original
index is written. A later scheme/value change is preserved as a conflict. The executable pipeline invokes them; Windows
execution remains to be verified.
**Windows Update active hours are built (D-50):** one protected enable/start/end tuple
requests 07–20 and restores the original values only if no member was edited later.
Native writes guard the complete tuple; partial failures remain conflicts. The fixed
policy leaf may be created but is never removed as a tree. Mac tests cover the journal
bridge; executable integration is built. Windows execution and effective restart behavior remain
to be verified.
**LSA sign-in secret storage is built (D-51):** `StudentSignInSecret` skips all native
access when account mode is off and otherwise requires the recorded student SID.
`WindowsStudentSignInSecretStore` reads/writes the fixed LSA secret with expected-value
checks and clears owned buffers. Original absent/empty/UTF-16 values use the protected
journal. Fresh setup separately journals the original nullable REG_SZ `AutoLogonSID`,
applying it before the Winlogon/LSA tuple and restoring it afterward; older tuple-only
history preserves the unknown SID baseline with an explicit warning (D-52).
The executable uses the combined Winlogon/LSA setting (D-52), with activation/group
configuration; Windows execution and the full sign-in round trip remain to be verified.
**M4 is in progress, in four portions** (ROADMAP M4, *How it is being built*; `D-38`).
**Portion 1 is built (2026-09-07)**, all on the Mac: the *Scripts* tab (`ScriptsViewModel`,
`ScriptsView`), `Shared/Persistence/ScriptsDocument` (`scripts.json` beside `lab.json`, in
the backup), `Shared/Jobs/ScriptSeed` and `Console/Services/ScriptLibrary` (the seed from
`scripts/library/` embedded in the console, imported once), `LabSession.RunScript`. Not yet
run against a real agent: the seed scripts wait for Windows verification.
The owner-requested editor extension (`D-39`) adds AvaloniaEdit, local PowerShell parse
diagnostics, a quick runner above the lab mosaic, and 16 additional built-ins: open,
UAC-open and graceful close for five apps, plus Python discovery. Existing libraries
can add missing built-ins explicitly without replacing edits. UAC still requires admin
credentials on a standard student account; no SYSTEM desktop launch was introduced.

**M4 portion 2 has started (2026-09-07, D-41):** `PullFile` resumes a running download
at completed chunk boundaries across reconnects, keeping the whole-file hash and a
30-second inactivity budget. Empty files and exact 64 KiB multiples finish correctly.
Loopback TLS tests cover repeated disconnects and cancellation. **The upload transport is
also built (D-42):** `FileUploads` grants a specific PC an expected size/hash and staging
stream; `AgentLink.PushFileAsync` resumes through the additive `GetUploadStatus` RPC.
Tests cover repeated reconnects, lost completion acknowledgements, corruption and peer
isolation. Grant callers own staging and cleanup; no upload UI or collection job yet.
**Per-PC batch logs are built (D-43):** `JobBatchLogs` saves the roster and results under
`logs/batches/`; *Jobs → Export batch logs…* exports a consistent ZIP snapshot with a
separate log for each PC, including unfinished jobs. This uses existing `Link` output;
no diagnostic-upload job was added. Tests cover 30 concurrent PC results, mixed outcomes,
late results and disk errors. **Handout dispatch and simulator delivery are built (D-44):**
*Send files…* queues a file/PC job matrix in one batch; FakeAgent downloads into its own
`Materials`, verifies the hash and replaces a same-named file only on success. Opening is
simulated and only document/image formats are eligible. Windows delivery now resolves the D-40 managed SID and performs final file operations
and document opening under its token (D-52). Delivery, replacement and PDF opening under
the standard student token passed on the VM; fleet-scale and Word verification remain.

The console's non-UI core lives in `src/LabControl.Console/Services` (`LabSession`,
`ConsoleBootstrap`, `LabKeyVault`) and `Server/`; the agent side shared by `FakeAgent` and
the real agent is `src/LabControl.Shared/Link/AgentLink.cs`, and the install-time routines
shared by the agent, the simulator and Setup are `src/LabControl.Shared/Setup/`. Tests:
`tests/LabControl.Shared.Tests` (pure logic) and `tests/LabControl.Console.Tests`
(in-process console + agents over real TLS/UDP, plus headless UI renders). Run them as
`dotnet test` or, if that reports zero tests, by executing the built test exe directly
(`tests/<project>/bin/Debug/net10.0/<project>`).

**M4 integration (2026-09-08, D-52) is in progress:** signed manifests, external
probation/rollback, USB building, Windows handouts and an executable Setup/removal/rekey
flow are implemented and undergoing native acceptance and independent review. Native settings include ordered
Winlogon/LSA/countdown, standard-account activation, hostname/firewall, Defender/hibernation,
NIC settings and selected session policies. ROADMAP records completed native checks and
the remaining removal, administrator-access and physical-lab acceptance. Preserve the private
journals and binary-directory identity marker, and never infer legacy ownership.

M4 integration also includes a journaled non-domain password-policy fallback, a bounded
add-only Default-profile document seed, a 30-second cancellable reboot prompt and durable
uninstall retry metadata. Native verification remains required; account-off skips these
account/profile operations. See INSTALLER and D-52 for exact limits.

Validation during M4 integration: full Mac solution build succeeded (two existing Avalonia
constructor warnings), all 673 tests passed without skips, and all targets from tools/publish-all.sh were published
for the supported console and Windows Agent/Session/Setup outputs. Later changes still require
focused verification; these results do not replace Windows or real-lab acceptance.

Initial Windows ARM64 evidence: 17 read-only probes and 8 actual machine-setting
apply/restore round trips passed on an isolated VM clone (exact original-byte comparison).
Hibernation was already off; no physical NIC was present. Subsequent opt-out install,
repair, reboot, full USB/network probation, external crash/deadline rollback, rekey with a
fresh-lab TLS Hello and standalone removal passed on that clone. Account/profile/sign-in
fingerprints were preserved. A fresh default-on GUI installation and automatic student
sign-in after reboot also passed. Student-session scripting, remote pointer input,
hash-verified editable handouts and wrong-key update refusal passed. Executable delivery
with Open=true produced zero launches under an observer with a verified positive control.
Managed-account retain/delete paths and fixed ReadOnly cleanup passed on isolated clones;
partial-install cleanup and account-off repair/reboot preserved the new retained-user
baseline. A fresh fixed-installer cycle verified actual student autologon, owned-account
removal with a loaded-profile refusal and ordinary retry, exact restoration of all three
account/profile/sign-in fingerprints including AutoLogonSID, and complete owned-file
cleanup without fixture patches. The final d27bc649 network update passed its full
10-minute probation, recovery finalization and retained-user baseline checks. The
account-off user-session loop still needs an actual Windows sign-in. Replacement and PDF
opening under the student token passed; the repeated deadline recovery also verified correct
console terminal status after the race fix; ROADMAP records the evidence and
physical-lab limits.

The owner specifically requires Setup to finish console-push and Wake-on-LAN prerequisites.
Setup now selects a physical Ethernet MAC, repairs a previously recorded generic MAC,
reports NIC/driver inventory and applies supported power controls in dependency order.
The native magic-packet-only WMI schema is verified; actual NIC writes and shutdown wake
still require the physical hardware. Do not treat local installation success as a verified
console connection or a successful wake test.
Installer antivirus/network advisories now reach the console via a validated fixed-code
snapshot, refresh after repair and remain visible while the PC is offline. Physical wake
is informational until tested; unsupported configuration and antivirus issues need attention.
