# LabControl — classroom fleet control for the ОНТФК computer lab

## What this is (read first)

LabControl is a self-hosted classroom-management system for one computer lab:
**14 student PCs (Windows 10/11, x64, wired to a hub)** managed from **one teacher
machine (currently a MacBook Air M-series on Wi-Fi, same router; may become a Windows
PC later)**. It replaces walking from desk to desk to install software, power
machines on/off, and watch what students are doing.

Owner: Viacheslav (teacher, Odesa Technical Vocational College). Solo project, built
with Claude Code. The teacher is also the only admin — there is no IT department
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
2. **Student agent is Windows-only** (10/11 x64). Runs as a Windows service, survives
   reboots, cannot be killed or uninstalled by the student.
3. **Installer is one-shot from a USB stick**: run once as local admin on each PC,
   asks at most one question (PC number), does *everything* else itself (service,
   firewall, Wake-on-LAN, power settings, `student` user, auto-logon, enrollment).
   The stick carries **no secret** — only the public CA certificate and single-use
   enrollment codes (D-14).
4. **`student` account**: standard (non-admin) local user, password `1`, auto-logon at
   boot, profile can be reset to a clean desktop on command.
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
│   ├── ROADMAP.md             ← milestones M0…M6 with acceptance criteria
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
└── scripts/                   ← reusable PowerShell/cmd scripts pushed to PCs
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

- Read `docs/ARCHITECTURE.md` and `docs/PROTOCOL.md` before touching `src/`.
  Any change to a `.proto` file must be reflected in `docs/PROTOCOL.md` in the same commit.
- **Documentation is part of every change, not a follow-up.** The `.md` files are the
  single source of truth; `docs/html/` is a generated mirror. Whenever you change a
  `.md` file — or change behaviour that a `.md` file describes — update the Markdown
  **and** run `tools/docs-build.sh` before finishing the task. Never hand-edit anything
  under `docs/html/`. A change that leaves the docs stale, or the HTML out of sync with
  the Markdown, is not finished.
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
`D-32`); the teacher-facing script library is M4 and the console has only the development
*Run test script…* dialog until then. Jobs outlive a dropped link (`D-32` item 7). The VM
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
once, because a portion-3 agent refuses `self_update`. `Setup` is still a skeleton (M4).
**M3 is in progress, in three portions** (ROADMAP M3, *How it is being built*; `D-34`).
**Portion 1 is built (2026-09-07)**, all on the Mac: `Shared/Video/` (`JpegCodec`,
`VideoGeometry`, `ScreenImage`, `VideoPacer`, `VideoSettings`, `VideoUplink`), `VideoControl`
handling and `TryPushVideo` in `AgentLink`, `PushVideo` served by the console into
`Services/ScreenStore`, the `ScreenView` control on the tile and in `ScreenWindow`, and
`FakeAgent`'s `FakeScreen`. **Portion 2 is built (2026-09-07), not yet run on the VM** (`D-35`):
the producer loop is `Shared/Video/ScreenProducer` over an `IScreenSource` (the simulator's
`FakeScreen` is one; `FakeScreenStreamer` is gone), `session.exe` captures with
`DxgiScreenSource` (Vortice) or the `GdiScreenSource` fallback (`TileDiff` for its rectangles),
`DesktopAccess` makes it DPI aware and follows the input desktop, and `SessionSupervisor`
relays `VideoControl` down the pipe and `VideoFrame` up into the uplink, dropping a refused
frame with a keyframe request back. Portion 3 (input) is next.

The console's non-UI core lives in `src/LabControl.Console/Services` (`LabSession`,
`ConsoleBootstrap`, `LabKeyVault`) and `Server/`; the agent side shared by `FakeAgent` and
the real agent is `src/LabControl.Shared/Link/AgentLink.cs`, and the install-time routines
shared by the agent, the simulator and Setup are `src/LabControl.Shared/Setup/`. Tests:
`tests/LabControl.Shared.Tests` (pure logic) and `tests/LabControl.Console.Tests`
(in-process console + agents over real TLS/UDP, plus headless UI renders). Run them as
`dotnet test` or, if that reports zero tests, by executing the built test exe directly
(`tests/<project>/bin/Debug/net10.0/<project>`).
