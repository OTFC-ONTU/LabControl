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
| **M2** | Windows agent: service, helper, power, scripts | **built; verified on the VM (2026-09-05…07); `PC-00` enrolled as `PC-10` and verified (2026-09-07); Wake-on-LAN deferred to M4** |
| **M3** | Screens: mosaic, full view, remote control | **all three portions built and verified on `PC-10` (2026-09-07): capture, control, text, Ctrl+Alt+Del, 14–18 fps scrolling with auto quality (`D-37`, build 0.1.4); the hour-long and 30-tile measurements remain for the close-out** | M2, `PC-00` |
| **M4** | Deployment: USB installer, files, self-update | **in progress — portion 1 built; portion 2: resumable file transport and per-PC batch logs built on the Mac (2026-09-07), handout UI and simulator delivery built; portion 3 account journal, Windows preparation, protected settings journal and machine registry/AC power-plan adapters built, executable pipeline pending (`D-38`, `D-41`…`D-49`)** | M3 |
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
  in `JobResult`, timeout and kill of the whole process tree. The script reaches the PC as
  a **file through `PullFile`**, hash-verified (`D-31`) — the same channel packages and the
  update bundle use in M4, built here in its minimal form (no resume yet). The console side
  in M2 is deliberately small: one development action that sends the built-in acceptance
  scripts; the teacher-facing **script library** is M4 (`D-31`).
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
  deployed by hand before the real installer exists in M4. With `-Student` it also creates
  the `student` account with auto-logon (INSTALLER.md step 8), so the `student` criterion
  below can be checked on `PC-00` before M4 (`D-29` item 5).

**How it is being built.** In four portions, each committed and then run on the Windows VM
before the next starts (owner's choice, 2026-09-05): **(1)** the service host, the real store
with DPAPI, provisioning from the USB payload, inventory, the side-by-side layout and
`dev-install.ps1`; **(2)** the session helper and its supervision; **(3)** power jobs,
Wake-on-LAN from the console, a minimal `PullFile` and `run_script` on top of it, plus a
development-only *Run test script* action in the console for the acceptance criteria;
**(4)** the minimal push-and-restart. Portion 3 stays about the agent: the script library
the teacher will actually use is M4, because both earlier portions found a Windows-only bug
the Mac could not show, and the agent must be proven on the VM before more console UI is
built on it (owner's decision, 2026-09-05, `D-31`).

**Progress.**

- *Portion 1 (built 2026-09-05).* `LabControl.Agent` is a real
  service host: `Microsoft.Extensions.Hosting.WindowsServices`, `LocalSystem`, a rolling
  7-day log under `ProgramData\LabControl\logs\`, and a loop that never exits — an
  unprovisioned PC waits and says so (`D-29`). The PC's private key goes through DPAPI at
  machine scope (`MachineKeyProtection`); the trust half of INSTALLER.md step 4 is one shared
  routine, `AgentProvisioning`, used by `agent.exe --install`, by `FakeAgent` and, in M4, by
  Setup. Inventory is real: hostname, Windows build from the registry, CPU, RAM, the system
  drive, uptime and the interactive user through `WTSQuerySessionInformation` (CsWin32).
  The agent checks the ACL on `ProgramData\LabControl\` at every start and reports a
  wrong one as an event; `AgentLink` gained `Report()` for exactly that, with events queued
  across a reconnect. `InstallLayout` is the `app\<version>` arithmetic, unit-tested on the
  Mac. `scripts/dev-install.ps1` lays the PC out as Setup will, provisions, registers the
  service with restart-on-failure, adds the firewall rule and the Defender exclusion.
  Jobs are answered with *not in this build* until portion 3.
- *Portion 1 on the VM (2026-09-05).* Windows 11 ARM64 in UTM on the MacBook, installed
  by `dev-install.cmd` from the shared folder. The service started, waited for the lab key
  with the right message, enrolled the moment the key was unlocked, then failed its first
  mutual-TLS handshake — SChannel refuses ephemeral keys, a Windows-only bug M1's tests on
  the Mac could not see (`D-29` item 6). Fixed, republished, and `PC-01` linked, stayed
  linked and reported its inventory (hostname, MAC, version, the interactive user). Two
  more Windows-only lessons landed in the script: PowerShell 5.1 needs a BOM, and the UTM
  shared drive needs its WebDAV size limit raised (`D-29` item 7, README).
- *Portion 2 (built 2026-09-05).* `LabControl.Agent.Session` is a real
  helper and the service supervises it (`D-30`). The service owns the named pipe, spawns
  `session.exe` into the console session with its own SYSTEM token re-homed to that session,
  and keeps it there: restarted within about a second after a crash, on logon and logoff,
  when the console session changes, when it goes silent, with a 30-second back-off and an
  event after five deaths in a minute. The helper says hello, then reports every 2 s which
  desktop has the input (`Default` / `Winlogon`) and the screen size — the proof that a
  process of ours lives on the student's desktop — and exits the moment the pipe closes.
  The service reads the session through WTS every 2 s and is woken early by the service
  control manager's session-change notifications (a `WindowsServiceLifetime` subclass);
  every change goes to the console as `SessionState`, now with `locked`, and the latest one
  is re-sent after every `Welcome`. The console shows *student (locked)* on the tile, an
  orange line when the helper is down, and logs logon / logoff / lock / unlock as events.
  `FakeAgent` publishes the same message so the simulator stays representative. Tests: the
  pipe framing over a real named pipe on the Mac, the session state through the console and
  across a reconnect, and the tile texts in the headless UI test. **To verify on the VM:**
  the `session.exe` process appears in Task Manager (as SYSTEM, in the user's session) and
  `session.helper_ready` shows in the events panel; killing it as administrator brings it
  back within 5 s with a `session.helper_exited` warning; Win+L marks the tile *locked* and
  unlocking clears it; sign out shows *nobody logged on* and a new helper appears on the
  logon screen; sign in shows the user again; `Get-Content session-<date>.log -Wait` shows
  the desktop switching between `Default` and `Winlogon`.
- *Portion 2 on the VM (2026-09-05).* All five checks passed on Windows 11 ARM64:
  `session.exe` runs as SYSTEM in the user's session and the console logs
  `session.helper_ready`; ended from Task Manager it was back in two seconds with
  `session.helper_exited` in between; Win+L produced `session.lock` and the unlock
  `session.unlock`; signing out produced `session.logoff` and a new helper on the logon
  screen (`session 2, desktop Winlogon`), signing in `session.logon` and a helper on
  `Default`; the helper's log shows every `Default` ↔ `Winlogon` change. Two things the run
  surfaced and fixed: at OS shutdown the Windows Event Log provider that
  `AddWindowsService` registers threw through the agent loop (now only Serilog is left),
  and the console did not persist the logged-on user on a session change (it does now).
  The shutdown fix was re-verified the same evening: with the agent linked, an OS shutdown
  ends the log with *stopping → unlinked → LabControl agent stopped* and nothing after it.

- *Portion 3 (built 2026-09-05).* Power jobs on the real agent:
  shutdown and reboot through `InitiateSystemShutdownEx` with the privilege enabled first,
  immediate and forced, answered 2 s before the call; log off through `WTSLogoffSession`
  (`D-32`). `PullFile` in its minimal form on both sides — the console offers files by their
  SHA-256 (`FileOffers`), serves 64 KiB chunks and checks the peer like `Link`; the agent
  pulls, hashes and refuses a mismatch (`AgentLink.PullFileAsync`). `run_script` on the
  agent with every parameter of `D-31`: PowerShell 5.1 or `cmd.exe`, as SYSTEM or as the
  student in the student's session (`UserProcessLauncher`: `WTSQueryUserToken`,
  `CreateProcessAsUser`, inherited pipes), output streamed line by line in the OEM code
  page, the whole tree killed after `timeout_s` of silence, the job directory removed
  afterwards (`ScriptRunner`, `ProcessRunner`). Jobs now outlive the link: a cable pulled
  mid-script no longer kills it, and the result is queued for the next `Welcome` — a latent
  M1 defect found on the way (`D-32` item 7). Wake-on-LAN from the console: *Wake* on the
  toolbar, three magic packets to every broadcast and to `last_ip`, *waking…* on the tile,
  `wake.woke` / `wake.failed` events. The development-only *Run test script…* dialog sends
  one of three built-in scripts (100 lines then `exit 3`; hang; who am I) with shell,
  run-as and timeout selectable. `FakeAgent` pulls the real file and pretends only the
  shell. Tests: the magic packet, the `run_script` arguments, `ProcessRunner` driven by
  `sh` (lines, exit code, kill at the inactivity timeout, cancellation), `PullFile` end to
  end with a hash mismatch and an unknown reference, the test-script action, a job that
  finishes while unlinked, and the wake bookkeeping. **To verify on the VM:** (1) *Shut
  down* — the tile goes offline within 20 s and the VM is off; (2) *Reboot* — the VM is back
  online by itself with the same agent id; (3) *Log off* — `session.logoff`, *nobody logged
  on* on the tile, a helper on the logon screen, and after signing in `session.logon`;
  (4) *Run test script* → *100 lines, exit 3* with PowerShell as SYSTEM: 100 lines in the
  Jobs panel and `exit 3`, row red; (5) the same with *cmd*; (6) *Hang* with a 10 s
  timeout: killed after 10 s, the result says so, no leftover `powershell.exe` in Task
  Manager; (7) *Who am I* as SYSTEM (`user: PC-01$`, session 0) and as the logged-on user
  (`user: student`, the user's session, directory under `C:\Users\Public\LabControl\jobs`,
  the Cyrillic line intact) with both shells; (8) *Who am I* as user while signed out — a
  clear failure, nothing runs; (9) *Hang* with a 120 s timeout, then the VM's network off
  at 20 s and on again at 50 s: the tile goes offline and back, the job is still *running*,
  and the *killed* result arrives at 120 s with no `job.timed_out` event; (10) `Wake` on the
  switched-off VM: `wake.sent` and *waking…*, then `wake.failed` after 90 s — UTM does not
  implement Wake-on-LAN, so the real wake is a `PC-00` check; (11) `C:\ProgramData\LabControl\jobs`
  is empty after every job.
- *Portion 3 on the VM (2026-09-06, driven from the console by Claude through the
  accessibility tree, the VM signed in as `admin`, no `student` account).* **Scripts:** *100
  lines, exit 3* with PowerShell and with `cmd` — 100 lines each, `exit 3`, row red; *Hang*
  with a 10 s timeout — killed after 10 s, result "Killed after 10 s without output (1 line
  received, 11 s in total)"; *Who am I* in all four combinations — `WIN-…$` /
  `NT AUTHORITY\SYSTEM`, session 0, `ProgramData\LabControl\jobs\<id>` as SYSTEM;
  `admin`, session 1/2, `C:\Users\Public\LabControl\jobs\<id>` as the user, both shells.
  Afterwards no `powershell.exe` / `cmd.exe` of the agent's in Task Manager (the only one was
  the administrator's own terminal) and both `jobs` directories empty; `agent` in session 0,
  `session` in the user's session. *Who am I* as user while signed out: "nobody is logged
  on to this PC right now", nothing ran. **Power:** *Log off* — `session.logoff`, a helper
  on the logon screen, `session.logon` after signing back in; *Reboot* — link lost 3 s after
  the click, back on its own 19 s later with the same agent id and serial; *Shut down* —
  link lost 3 s after the click, VM off. **Wake** on the switched-off VM: `wake.sent` to the
  limited broadcast, the Wi-Fi subnet and `last_ip`, then `wake.failed` after 88 s with the
  BIOS / Fast Startup / NIC hints — as expected, UTM has no Wake-on-LAN; the real check is
  `PC-00`. **The network-drop check (9) is not done:** `Disable-NetAdapter` on the guest's
  virtio NIC bugchecked Windows (Kernel-Power 41, EventLog 6008, WER 1001) 20 s into the
  120 s *Hang*; the VM rebooted, the agent re-pulled the script (its ledger is in memory,
  so a reboot re-runs an in-flight job — expected), and the *killed after 120 s* result
  arrived after the reconnect; the console kept both output lines. The cable pull must be
  done from outside the guest (UTM cannot unplug a running NIC; `pfctl` on the Mac or the
  real `PC-00`). **Findings** (all five fixed the same night, `0133e92`; the fixes await their own VM run, see below): (a) the *Who am I* Cyrillic line prints as
  `??????` in all four runs — the VM is `en-US`, so the OEM code page is 437, which has no
  Cyrillic; `D-32` item 5's OEM decoding is right for a Ukrainian PC but the output path
  should not depend on the system locale (candidate: set `[Console]::OutputEncoding` /
  `chcp 65001` in a tiny launcher and decode UTF-8); (b) the `cmd` *Who am I* prints an empty
  `session:` line (`%SESSIONNAME%` is unset for a service-started process); (c) every log
  off and log on produces a `session.helper_exited` / `session.helper_down` warning pair
  before `session.helper_ready`, because Windows kills the helper before the supervisor's
  own restart — an intended restart should not read as a crash; (d) `session.lock` is
  reported at the logon screen right after a log off (WTS flags the empty session as
  locked) — suppress when nobody is logged on; (e) the console log prints the full Kestrel
  connection-reset stack trace at `INF` on every link drop, and "Error reading message."
  without the PC's name. **Still to run on the VM after `0133e92`:** *Who am I* with both
  shells shows the Cyrillic line intact and the `cmd` variant a session number; a log off
  and a log on produce only `session.logoff` / `session.logon` and `session.helper_ready`,
  no `helper_exited` / `helper_down`; no `session.lock` at the logon screen; and check (9),
  the network drop, done from the Mac (`pfctl` blocking 192.168.64.2 for 30 s) rather than
  inside the guest. The two `BeaconTests` fail while a real console is running on the same
  Mac — the test agents hear its beacon on the shared UDP port — so run them with the
  console closed.
- *Portion 3, second pass (2026-09-06).* The five findings are fixed (`D-32` items 5 and
  12): both shells now print UTF-8 whatever the PC's locale, the cmd *Who am I* reports the
  session through `tasklist`, a helper ended by Windows at logon/logoff is a planned
  restart rather than `session.helper_exited` + `session.helper_down`, an empty session is
  never *locked*, and the console log no longer carries gRPC's stack for every dropped
  link. Still to run on the VM: *Who am I* in both shells (Cyrillic intact, a session number
  from cmd), a logoff/logon with no helper warnings and no `session.lock`, and item 9 with
  the network cut by a temporary Windows Firewall rule against the console's address
  instead of `Disable-NetAdapter`, which bugchecks the virtio guest.
- *Portion 3, third pass (2026-09-06, late).* The console was launched as a macOS `.app`
  wrapper so the checks could be driven end to end. PowerShell *Who am I* as the user: Cyrillic
  intact, session 1. cmd was still garbled — not the code page after all but **LF line
  endings** (the console writes them on the Mac; cmd's parser eats the start of the next
  lines), proved with the same batch in LF and CRLF form on the VM; `ScriptText.ForCmd` now
  writes CRLF. Logon was clean; logoff still showed `helper_exited`/`helper_down` because
  Windows ends the helper a moment before the logoff notification, so the supervisor now
  holds an unexplained exit for 3 s (`HelperExitGrace`). No `session.lock` on the logon
  screen. Re-run, same night: cmd *Who am I* as the user and as SYSTEM prints the Cyrillic
  line intact and the session from `tasklist` (2 and 0); a logoff now yields exactly
  `session.logoff` and `session.helper_ready` on the logon screen, a logon `session.logon`
  and `session.helper_ready`; and item 9 passed with the cut made by a Windows Firewall
  rule against the console's address: *Hang* started at 00:01:40, the link was lost at
  00:02:18 and back at 00:02:38, and *Killed after 120 s* arrived at 00:03:41 over the new
  link with no `job.timed_out`. Portion 3 is complete on the VM; Wake-on-LAN waits for
  `PC-00`.
- *Before the first `PC-00` visit (2026-09-07).* `dev-install.ps1 -Student` creates the
  `student` account with auto-logon so the criterion "`student` cannot stop the service, kill
  `session.exe` or read `ProgramData`" no longer waits for Setup.exe (`D-29` item 5). Not yet
  run anywhere: first on the VM (`STUDENT=1` in `dev-install.cmd`, from a snapshot), then on
  `PC-00`. **To check:** after a reboot the PC logs on
  as `student` by itself; as `student`, *services.msc* refuses to stop `LabControl`, Task
  Manager refuses to end `session.exe`, `C:\ProgramData\LabControl` is *access denied*, and
  the tile reads *student*; `dev-install.ps1 -Uninstall -RemoveStudent` removes the
  auto-logon and the account again.
- *`-Student` on the VM (2026-09-07, driven by Claude through the screen).* `install-p4s.cmd`
  with `STUDENT=1` over the portion-3 install: the account was created at the first try (no
  password-policy retry), put in `Users` only, and the auto-logon secret stored. After a
  reboot the VM signed in as `student` by itself and the tile read *student*. As `student`:
  *services.msc* shows *LabControl Agent* running with Start / Stop / Pause / Restart greyed
  out; Task Manager's *End task* on `session.exe` answers *Access denied*; Explorer refuses
  `C:\ProgramData\LabControl` with *You don't currently have permission*. *Who am I* as the
  logged-on user printed `user: student`, session 1, the Cyrillic line intact. Not run:
  `-Uninstall -RemoveStudent` (it also removes the agent; the VM stays installed for
  portion 4). One thing to know about UTM: after `Restart-Computer` from inside the guest the
  VM sat on the UEFI *Start boot option* splash until it was powered off and started again —
  the console's *Reboot* job in portion 3 did not show this, so it is not a LabControl issue.
- *Portion 4 (built 2026-09-07).* The minimal push-and-restart (`D-33`). The console gained
  the development action *Push agent build…*: a folder with the published `agent.exe` and
  `session.exe` (side by side, or `publish-all.sh`'s `artifacts/<rid>/` layout) and the
  version number; the dialog hashes the files and shows the version directory name the PC
  will get, `<number>+<8 hex of agent.exe's hash>`. `LabSession.PushAgentBuild` offers the
  two files and an `UpdateManifest` through `PullFile` and sends a `self_update` job with
  `version`, `ref`, `sha256` (`SelfUpdateRequest`). On the PC `AgentUpdater` pulls the
  manifest, checks it (`UpdateBundle`), pulls each file into
  `ProgramData\LabControl\update\<version>\` with progress, runs the new
  `agent.exe --version` as a preflight, moves the directory into `app\<version>\`, writes
  `app\previous` / `app\current`, repoints the service (`ServiceControl`, `ChangeServiceConfig`)
  and spawns its own `agent.exe --restart-service`; it never sends a result — the new
  version answers the re-sent job (*Running X now (was Y)*) and prunes version directories
  older than `previous`. `Hello.agent_version` is now the directory name when installed, so
  the tile shows the build. `AgentLink` forgets a job cancelled by a stop so the re-sent copy
  runs. `FakeAgent` plays the whole exchange with real pulls. Tests: `SelfUpdateTests`
  (Shared), `PushBuildTests` (Console). Not yet run on Windows. **To check on the VM:**
  first install a portion-4 build once by hand (`dev-install.cmd`) — the agent on the PC
  must already understand `self_update`, and the one from portion 3 answers *does not run
  SelfUpdate jobs yet* (seen on the first attempt, 2026-09-07 01:44; this is the one
  bootstrap the push cannot do for itself). Then publish a build (`tools/publish-all.sh`),
  push it to the installed `PC-01` from the console;
  the Jobs panel shows *pulled agent.exe*, *pulled session.exe*, the preflight line and
  *restarting*; the PC goes offline and is back within a minute with the tile reading
  `agent 0.1.0+…`; the job ends *Running 0.1.0+… now (was 0.1.0)*; on the VM
  `app\` holds both directories, `app\current` and `app\previous` name them,
  `sc qc LabControl` points into the new one, `ProgramData\LabControl\update\` is empty,
  and the agent log shows the `restart` lines. Then push the same build again (a quick
  *Running … now*), push a third build (the first version directory is pruned), and push a
  `win-x64` build to see the preflight — on the ARM VM it will pass under emulation, so the
  wrong-architecture refusal is a `PC-00` check with a `win-arm64` build.
- *Portion 4 on the VM (2026-09-07 01:58–02:03, driven from the console by Claude).* After
  the one-time hand install of a portion-4 build (`dev-install.cmd`, see above), three
  pushes to `PC-01`, all from the *Push agent build…* dialog: **(1)** `0.1.0+e5437f74` from
  `artifacts/win-arm64` — *pulled agent.exe (83.0 MB)*, *pulled session.exe (80.8 MB)*,
  *agent.exe 0.1.0 runs on this PC*, *installed into app\0.1.0+e5437f74; service repointed,
  restarting it now*; the link dropped 1.4 s after the push and the new version was linked
  0.9 s later; the re-sent job ended *Running 0.1.0+e5437f74 now (was 0.1.0; app\previous
  still names it)*; on the VM `app\` held `0.1.0` and `0.1.0+e5437f74`, `current` /
  `previous` named them, `sc qc` pointed into the new directory, `update\` was empty and the
  agent log had *asking for a restart* / *restart: stopping* / *starting* / *The service was
  restarted*. **(2)** the same build again — *Running … now* in 0 s, nothing pulled.
  **(3)** a `0.1.1` build (`dotnet publish -p:Version=0.1.1`, because deterministic builds
  of unchanged sources hash identically) — the same four lines, back in 0.9 s as
  `0.1.1+72fb359a`, *Removed older version directory: 0.1.0*; afterwards `app\` held
  exactly `0.1.0+e5437f74` and `0.1.1+72fb359a`, `agent.exe` and `session.exe` ran from
  the new one. The planned helper restarts around each push produced only
  `session.helper_ready`, no warnings. Not done: the `win-x64` push (passes under emulation
  on this VM, so it proves nothing here); the wrong-architecture preflight refusal waits
  for `PC-00`. Portion 4, and with it the M2 build, is verified on the VM; the `PC-00`
  checks (Wake-on-LAN, `win-x64`, antivirus, real capture) remain for the lab.
- *First `PC-00` visit (2026-09-07, the lab).* The stick held `dev-install.ps1`, an
  `install.cmd` that asks the PC number (the agent refuses 0, so the test box enrolled as
  **`PC-10`**; number 1 stays the VM), the `win-x64` portion-4 build and the payload whose
  codes were live. `install.cmd` with `STUDENT=1` ran as the local administrator, the PC
  found the console by its beacon (no pinned address), enrolled (`enroll.issued`) and linked
  in the same second; inventory: `DESKTOP-44I93Q7`, 1920×1080, `agent 0.1.0`. After the
  reboot the PC signed in as `student` by itself and the tile read *student*. The first
  Wake-on-LAN from the console ended in `wake.failed` after 90 s: the magic packet went to
  the broadcast and the PC's own address, but the PC's BIOS/NIC Wake-on-LAN settings were
  not touched by the dev install (INSTALLER.md step 7 is Setup.exe's job) — to enable by
  hand and retry. Later the same visit: the three `student` refusals (services.msc, Task
  Manager on `session.exe`, `ProgramData`) held, and *Shutdown* / *Reboot* from the console
  worked (`shutdown now`, `reboot now` in the agent log, the PC back and linked ~45 s
  later). **The first `win-x64` push found a bug.** `0.1.0+d82bee07` from
  `artifacts/win-x64`: both files pulled in 5 s over Wi-Fi, preflight passed, service
  repointed, restarted — and the PC stayed offline for twelve minutes with the service
  *Running* and the layout right. Cause: `dev-install.ps1`'s inbound firewall rule named
  `app\0.1.0\agent.exe`, so Windows Firewall dropped the console's beacon for the new
  directory; the VM never showed it because its console address was pinned. Fixed by a
  rule **by port** (UDP 47801) in `dev-install.ps1` and INSTALLER.md step 6 (`D-33` item
  8); on `PC-10` a `fixfw.cmd` from the stick replaced the rule and the tile was back in
  seconds, as `0.1.0+d82bee07`. The second push, `0.1.1+e8f19850` from a
  `-p:Version=0.1.1` `win-x64` build, went through with the port rule in place: pulled in
  4 s, link dropped at 13:56:15 and back as the new version 2.2 s later, `app\` left with
  exactly `0.1.0+d82bee07` and `0.1.1+e8f19850` (the plain `0.1.0` pruned). The push is
  verified on real x64 hardware. **Wake-on-LAN is deferred to M4** (owner's decision,
  2026-09-07): it is not critical for the lessons, its Windows half is Setup.exe's step 7
  anyway and the BIOS half is a hand visit either way, so the real wake is tested when
  Setup.exe exists — and if it then does not work, the fix reaches the PCs through the
  push. Still to check on `PC-10`: antivirus (`D-10`), real capture (M3).

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
  keyframe every 5 s or on request, per-agent bandwidth cap (default 8 Mbit/s; raised to 24 Mbit/s after the `PC-10` measurement, `D-36`).
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

**How it is being built.** In three portions, on the pattern M2 set — each committed, then
run before the next starts: **(1)** everything the Mac can prove — the `PushVideo` channel,
`VideoControl` on the link, the thumbnail and full-mode frame formats, the console's
per-PC pictures, the mosaic tile with a live thumbnail, the single-PC window, and `FakeAgent`
drawing synthetic desktops with honest dirty rectangles; **(2)** real capture in `session.exe`
— DXGI Desktop Duplication with the GDI fallback, the pipe relay through the service, the
thumbnail and full producers on the same `VideoUplink` the simulator uses — run on the VM
and measured on `PC-00`; **(3)** input — `Input` → `SendInput` in the helper, `ctrl_alt_del`
through `SendSAS` from the service, the control toggle in the window, and the graceful
reasons on the tile (no session, locked, duplication failed). The split follows the M2
lesson: the console half is finished against the simulator first, so the VM runs only have
to answer Windows questions (`D-34`).

**Progress.**

- *Portion 1 (built 2026-09-07).* The wire is real end to end: `LabSession` sends a
  thumbnail `VideoControl` to every PC after `Welcome`, `AgentLink` remembers the control,
  announces it to the producer and owns a `VideoUplink` per link session — one
  `PushVideo` call per activation, a one-deep latest-wins queue, a `video.unsupported`
  warning against an older console (`Shared/Video/`, `D-34`). The console's
  `ScreenStore` holds two `ScreenImage`s per PC (thumbnail, full), applies whole frames
  and dirty-rectangle deltas, asks for a keyframe when a delta has nothing to land on, and
  scales full keyframes into the thumbnail while the full view is open. The tile draws the
  thumbnail through the `ScreenView` control with the number, the logged-on user and the
  status in a strip under it, dims it when the PC is offline or silent for 10 s, and says
  *no picture yet* / *picture stalled*; a double-click (or *Open screen* in the menu) opens
  the `ScreenWindow`, which switches the PC to full mode and back on close and shows
  resolution, fps and kbit/s under the picture; the status bar counts the screens' total
  kbit/s. `FakeAgent` draws a desktop per PC (1080p or 1366×768, every third one idle):
  wallpaper with the PC number, a clock, a Notepad window where the student types, a
  drifting box — each change with its own rectangle, so the full-mode deltas are honest.
  Tests: geometry, codec, `ScreenImage`, pacer and settings in `Shared.Tests`; in
  `Console.Tests` the control after `Welcome`, thumbnails and deltas over a real `PushVideo`,
  the keyframe request, the identity check, a `FakeMachine` streaming both modes by itself,
  and the main window rendered with seven live thumbnails plus the single-PC window.
- *Portion 2 (built 2026-09-07, not yet run on the VM).* Real capture in `session.exe`
  (`D-35`). The producer loop moved out of the simulator into
  `Shared/Video/ScreenProducer` behind an `IScreenSource`, so the helper and `FakeAgent`
  run the same code: thumbnails on change, keyframes and deltas in full mode, the pacer,
  a refused frame's rectangles carried into the next, a failing source reported once and
  reopened every 2 s, a stop that closes the source. The helper has two sources —
  `DxgiScreenSource` (Vortice, primary output, a staging texture as the persistent picture,
  Windows' dirty and move rectangles, re-duplication after `ACCESS_LOST` on the desktop
  that has the input) and `GdiScreenSource` (`BitBlt` into alternating DIB sections,
  `TileDiff` for the rectangles) — chosen by `ScreenSourceFactory` on every open, with a
  `capture.fallback` event when GDI is used. `session.exe` declares itself per-monitor DPI
  aware and attaches its capture thread to the input desktop (`DesktopAccess`). The
  service relays: `VideoControl` down the pipe (and to every new helper), `VideoFrame` up
  into the uplink, and a refused frame dropped with a `request_keyframe` back to the
  helper. Tests (`ScreenProducerTests`): the producer against a scripted screen in both
  modes, the refused-frame union, the failure/recovery events, stop and mode change,
  `TileDiff`, and `JpegCodec.EncodeScaled`. **To verify on the VM** (expected: GDI, the
  basic display adapter has no duplication): `capture.fallback` once, the thumbnail on the
  tile moving when the desktop changes, the single-PC window with deltas, `session.exe`
  CPU in Task Manager; lock/unlock and a UAC prompt must not stop the picture for more
  than a couple of seconds. **On `PC-00`** (DXGI): `capture` events absent, ≤ 5 % CPU in
  thumbnail mode, ≥ 15 fps in the window while scrolling. Then portion 3 (input).
  *First push to `PC-10` (2026-09-07 15:17):* refused twice before the build was seen —
  once by the preflight (the dialog said 0.1.2, the binary still said 0.1.0; the version is
  now 0.1.2 in `Directory.Build.props`), once by *Access to the path 'agent.exe' is denied*
  while moving the staged version into `app\` — the antivirus scanning the new executable;
  the move is retried now (`D-33` item 9). Once installed, every new `session.exe` hung after
  `Hello` and was killed as silent 12 s later, for as long as the console wanted video — a
  pipe-flush deadlock between the service and the helper (`D-35` item 8, fixed the same
  hour). With the console restarted the first real DXGI picture of `PC-10` reached the
  mosaic (15:24). *Verified on `PC-10` with build `0.1.2+f1caf778` (2026-09-07 15:37–15:45):*
  DXGI (no `capture.*` event), the picture on the tile, and the picture back by itself
  after Win+L / unlock and after a reboot — the helper reconnects into a console that
  already wants video without the old hang. Still to measure on `PC-10`: CPU of
  `session.exe` in thumbnail mode, fps in the single-PC window while scrolling, memory
  after an hour; and the GDI fallback on the VM. Owner's word: "все работает".
- *Portion 3 (built 2026-09-07, not yet run on a PC).* Input (`D-36`). The single-PC
  window has a *Control* toggle, *Ctrl+Alt+Del* and *Win* buttons and a status line that
  says why control is unavailable; `Console/Services/InputMapper` + `KeyMap` turn Avalonia
  events into `Input` messages — text as text, command keys and shortcuts by physical key,
  ⌘ as Ctrl on the Mac, wheel fractions carried over, everything released when control
  ends or the window loses focus; `Shared/Control/InputQueue` holds the latest mouse move
  for the window's 16 ms flush. `LabSession.SendInput` queues the message on the link;
  `AgentLink.InputReceived` hands it to the host. The service (`SessionSupervisor`) relays
  it down the pipe and answers `CTRL_ALT_DEL` itself (`SecureAttention`: the
  `SoftwareSASGeneration` policy, then `SendSAS`); the helper's `InputInjector` queues it
  for an input thread that joins the input desktop and calls `SendInput`, reporting
  `input.<reason>` / `input.recovered`. The tile and the window show the PC's own reason
  for a missing picture — *no user session*, *session helper not running*, *cannot capture:
  …* from the `capture.*` events — before the console's *picture stalled*. `FakeAgent`
  draws the teacher's pointer, a ring per click and the typed text on its desktop, and
  answers Ctrl+Alt+Del with an `input.sas` event. Tests: the vocabulary and the queue in
  `Shared.Tests`; the mapper, input over a real link into a `FakeMachine`, Ctrl+Alt+Del,
  the tile's reasons and an old agent ignoring input in `Console.Tests`. Build version
  0.1.3. **To verify on `PC-10`**: open the window, switch *Control* on, move and click in
  Notepad, type a sentence with Ukrainian text, ⌘C / ⌘V, arrows and Backspace, scroll a
  web page with the trackpad, drag a window; *Ctrl+Alt+Del* must bring the secure screen
  up (first use writes the policy) and the picture must follow it; Win+L from the toolbar
  key plus a click on the lock screen; then the acceptance numbers: ≥ 15 fps while
  scrolling, latency comfortable enough to type. A helper from before portion 3 says
  nothing when input arrives, so the agent must be pushed first.
  *First run on `PC-10` with build `0.1.3+4dfdc6f1` (2026-09-07 16:05–16:25):* the push
  went through first time, the mouse drives the PC, Ctrl+Alt+Del brings the secure screen
  up (the policy written by the agent on first use), and the single-PC window holds
  **15–16 fps** while the PC is driven remotely — on the acceptance line, not above it.
  Text did not arrive at all: the window handled every `KeyDown`, and macOS produces
  `TextInput` only for unhandled ones (`D-36` item 10, fixed the same hour, console-side
  only). Fixed on the way: tile tooltips floated above the single-PC window and other
  applications on macOS (served only while the console window is active now).
  **Scrolling a page in Edge: 3–4 fps** — the 8 Mbit/s full-mode cap, not the capture:
  a scroll dirties nearly the whole 1080p screen and each frame is a 250–350 KB JPEG, and
  1 MB/s is three or four of those. The cap is 24 Mbit/s now (`D-36` item 11), a
  console-only change since the control carries it. To measure again on `PC-10`: fps
  while scrolling Edge with the new cap; if it is still under 15 the next lever is the
  quality (q75 → q60 halves the bytes) and after that H.264 (`D-11`, M6 — these are
  the numbers it asked for). Still to check: Ukrainian text, ⌘C/⌘V, CPU and memory of
  `session.exe` over an hour.
  *Second run (2026-09-07 16:45):* text types, and scrolling Edge under the 24 Mbit/s cap
  is **10–17 fps, about 13 on average** — the acceptance line for scrolling is reached on
  Wi-Fi. Owner's request, done the same evening: a **quality selector** in the single-PC
  window — *Auto / High / Medium / Low* — with *Auto* letting the PC step the JPEG quality
  down while the cap makes it wait and back up when it does not (`D-37`); the status line
  shows the quality each frame used. Build **0.1.4** carries the producer side; the
  console's control says 0 for auto, which a 0.1.3 agent reads as q75, so the console can
  be updated first. To check on `PC-10` with 0.1.4: fps while scrolling Edge in *Auto* and
  the *q* the status line settles at; that *Low* is readable; that the mosaic thumbnail
  keeps moving through the choice.
  *Third run, build `0.1.4+977f1b9b` (2026-09-07 17:00):* *Auto* settles at **q40–50**
  while scrolling Edge and the window holds **14–18 fps** — the scrolling criterion is
  met with the quality lever, on Wi-Fi; *Low* is readable. Portion 3 is verified on
  `PC-10`. Left for the M3 close-out on `PC-00`: Ukrainian text and ⌘C/⌘V (typing works,
  not yet tried in Ukrainian), CPU of `session.exe` in thumbnail mode, memory after an
  hour, the 30-tile mosaic (29 fake + 1 real).

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
- **Optional student account and standalone uninstall (planned, D-40).** On the PC-number
  screen, *Create student account and enable automatic sign-in* is checked on a fresh
  install. Unticking it installs the agent for testing with an existing Windows session,
  without changing accounts, passwords, password policy, automatic sign-in or profile
  defaults. Repair preserves the saved choice. Provide `Uninstall.exe` on the PC and a
  Windows Installed apps entry, usable without the USB stick or console; retain
  `Setup.exe --uninstall` as an equivalent entry point. Track installer-owned changes
  so uninstall preserves personal accounts/files and restores settings safely.
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
- **The script library** (`D-31`): scripts live *inside the console*, not as files on the
  teacher's disk. A *Scripts* view with a list on the left (name, one-line description) and
  the script itself on the right — name, description, PowerShell / cmd, run as SYSTEM / in
  the student session, timeout, and the text in a monospaced code editor. *Run on selected
  PCs* and *Save*; unsaved text can be run once. Stored in `scripts.json` next to
  `lab.json` (with a `schema_version`, `D-20`), so the library is in the backup and moves
  with the lab; the repository's `scripts/` directory is the **seed** imported on first run.
  Results go to the existing jobs panel. Owner-requested extension (2026-09-07, D-39):
  syntax highlighting and local validation, a quick runner in the Lab view, and built-in
  open/close scripts for Word, PyCharm, IntelliJ IDEA, Visual Studio and VS Code,
  including explicit UAC launch variants. No parameters or schedule.

**Acceptance criteria**

- A factory-fresh PC goes from "Windows desktop" to "green tile in the console" with
  **one run of `Setup.exe` and one answer (the PC number)**, and reboots into auto-logon
  as `student`.
- Running `Setup.exe` again on the same PC changes nothing and reports every step as
  "already done".
- `Setup.exe --uninstall` leaves no service, no firewall rule and no LabControl
  directories; `--rekey` re-issues trust material in about 30 s while leaving the
  `student` account, the installed software and the settings untouched.
- **Home-PC round trip (D-40).** Untick account creation, install, reboot, log into the
  existing account, verify capture/control and a user-session script, repair and update,
  then uninstall without the USB or console. No student account is created at any stage;
  existing accounts, profiles and sign-in settings stay intact. The removal restores
  installer-owned system changes without overwriting later user changes. Repeat uninstall
  after a partial install and with a pre-existing account named `student`; it is never
  adopted or deleted. Verify the default checked classroom path separately.
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
- A new script typed into the *Scripts* view, saved and run on all PCs shows its output per
  PC; after restoring the backup on another teacher machine the same script is there.

**How it is being built.** In four portions, on the M2/M3 pattern — each committed, then
run before the next starts (`D-38`): **(1)** the script library, all on the Mac — the
*Scripts* tab, `scripts.json` beside `lab.json`, the seed imported from the repository's
`scripts/library/` on the first run, *Run on selected PCs* over the `run_script` the VM
already proved, the library inside the backup; **(2)** the file channel grown up —
`PullFile` with resume after a reconnect, `PushFile`, `send_file` into `Materials` with
*open after delivery* and *Send files…* on the toolbar, the parallel fan-out with the
per-PC log bundle — console and simulator on the Mac, the agent on the VM; **(3)**
`Setup.exe` as `docs/INSTALLER.md` specifies it, `tools/build-usb.sh` and the console's
*Build USB installer*, optional account creation and standalone uninstall (`D-40`) —
on the VM, then the fresh-PC criterion on `PC-00` and the home-PC round trip; **(4)** the
full `self_update` — the signed manifest, probation, the rollback driven from outside the
agent, the fleet view — on the VM with the deliberately broken release. The order puts the
console-only work first (nothing new to prove on Windows) and the two Windows-heavy
portions last, each with its own VM day.

**Progress.**

- *Portion 2 handout dispatch and simulator delivery built (2026-09-07, D-44).*
  *Send files…* selects multiple local files, with optional document opening, and creates
  one batch containing every file/PC job. Names are validated for Windows before dispatch;
  offline PCs remain pending. FakeAgent really downloads into its own `Materials`, checks
  SHA-256 and replaces only after completion; corrupt downloads preserve the old file.
  Opening is simulated, limited to document/image formats; executables are never launched.
  The dialog states that Windows delivery is not implemented yet. The next step is the
  D-40 managed-account ownership prerequisite in Setup, then Windows delivery/opening under
  that identity; real fleet acceptance remains pending. TLS, validation and headless UI tests
  cover this Mac slice. Keep source files unchanged and available until delivery completes.
- *Portion 3 account ownership foundation built (2026-09-07, D-45).*
  The shared `installation.json` journal preserves the selected account mode and records
  creation intent and the created SID. Pure account/removal plans refuse existing or
  replaced accounts and ambiguous interrupted installs. Missing legacy history requires
  an explicit mode choice; no profile is inferred from a name. Tests cover repair, opt-out,
  collisions, crash recovery, confirmed removal, corrupt/future records and storage failure.
  This pulls forward the D-40 prerequisite identified above; Setup.exe remains a skeleton.
  The Windows create-new/SID bridge and lock/private storage component are now built
  in D-46 below. Next: the protected settings journal, account activation/group
  verification, setup pipeline and Windows handout delivery.
  No real Windows account, sign-in setting or profile was changed or verified in this slice.
- *Portion 3 Windows account preparation component built (2026-09-07, D-46).*
  `AccountSetupScope` establishes private fresh storage, refuses untrusted existing
  journals/reparse points and holds the exclusive setup file lock. The shared coordinator
  records intent before local SAM create-new and saves the verified SID afterwards;
  new users remain disabled. Opt-out makes no account API calls; collisions and failed
  creation/read-back/storage never cause adoption or deletion. Mac tests cover ordering,
  repair, lookup/storage failures and concurrent account replacement. The component
  compiles for Windows; Setup's executable still does not invoke it. No real user was
  created or modified. Validation: 331 solution tests passed on macOS; Setup published
  self-contained for win-x64 and win-arm64. Next: protected prior-settings/sign-in storage, group verification
  and activation, then the executable pipeline and Windows handout delivery.
  VM checks pending: fresh disabled creation and SID match, repeat without password
  changes, opt-out, existing-name collision, password-policy refusal, simultaneous
  setup scopes, permissive/redirected storage refusal, and failure after SAM creation.
- *Portion 3 protected settings journal core built (2026-09-07, D-47).*
  `SetupSettingsJournal` saves original/applied values and operation phases before native
  changes, verifies read-back, preserves user edits and leaves ambiguous interrupted
  applies for review. `AccountSetupScope` provides the private lock and machine-scope
  DPAPI binding to this installation. Main and temporary files contain only ciphertext.
  Mac tests cover absence, pre-existing settings, repair, conflicts, interrupted writes
  and restoration, storage failures and invalid/missing/future/mismatched history.
  Validation: all 354 solution tests passed on macOS (23 new journal cases).
  No executable command invokes this component or changes Windows settings yet.
  Next: native settings/sign-in adapters, group verification and activation, executable
  install/repair/removal pipeline, then Windows handout delivery. DPAPI verification and
  the home-PC round trip still require a Windows VM.
- *Portion 3 first machine registry adapters built (2026-09-07, D-48).*
  Fast Startup and SoftwareSASGeneration now have a fixed Windows registry adapter and
  typed journal bridge, exposed by `AccountSetupScope`. Existing SAS value 3 is retained;
  non-DWORD originals are refused unchanged. Reread before mutation and read-back detect
  observed concurrent changes; the guard is not a transaction with other administrators.
  Mac tests exercise original DWORD/absence restoration, already-correct values,
  user changes, failed reads and malformed snapshots. No CLI invokes these policies;
  Windows runtime verification remains pending. Validation: `dotnet build` passed
  (two existing AVLN3001 window warnings), all 369 solution tests passed on macOS
  (15 new cases), and Setup published self-contained for win-x64 and win-arm64.
  Next: remaining settings/sign-in
  adapters, group verification/activation and the executable install/repair/removal flow.
- *Portion 3 AC power-plan adapters built (2026-09-07, D-49).*
  Sleep/display/disk AC timeouts now journal the active scheme identity and original
  seconds, with guarded Windows writes and activation before completion. Repair/removal
  preserves changed schemes and later edits; interrupted restore retries activation when
  the original index has already been written. Battery settings remain untouched.
  Mac tests exercise native failures, scheme races, activation recovery and journal
  round trips. Validation: build passed with the two existing Avalonia warnings; all
  392 solution tests passed (23 new power cases); self-contained Setup publishes passed
  for win-x64 and win-arm64. One existing file-resume TLS test failed on the first full
  run and passed on the complete rerun. Windows runtime checks and executable integration
  remain pending.
  Next: remaining native settings/sign-in adapters, account group verification/activation,
  then the executable install/repair/removal flow and Windows handout delivery.
- *Portion 2 started (2026-09-07, D-41).* `PullFile` now resumes at the last fully
  written chunk after a link reconnect, with a bounded inactivity timeout and a full-file
  SHA-256 check. Fixed terminal metadata for empty files and exact 64 KiB multiples.
  Loopback TLS tests cover repeated reconnects and cancellation; suffix and offset checks
  exercise the server.
- *Portion 2 upload transport built (2026-09-07, D-42).* `PushFile` and the additive
  `GetUploadStatus` RPC resume uploads at console-confirmed offsets, with per-PC grants,
  expected size/SHA-256 checks and recovery when the final acknowledgement is lost.
  Loopback TLS tests exercise reconnects and boundary files; rejection tests cover peer
  isolation and corrupt chunks. This is the shared agent/console transport API;
  Windows `send_file`/Materials, Windows verification and
  fleet-scale acceptance are still pending.
- *Portion 2 batch logs built (2026-09-07, D-43).* Every group action saves a
  schema-versioned report under `logs/batches/<batch-id>.json`, with the complete roster
  and per-PC results/output, at creation and on terminal results. In *Jobs*, select a row
  and use *Export batch logs…* for a ZIP containing every PC in that batch. Export captures
  current output consistently, includes unfinished PCs explicitly, and leaves the chosen
  destination intact on failure. Disk errors raise an event without stopping delivery.
  These are the results already received over `Link`, not uploaded agent diagnostic files.
  Tests cover concurrent results for 30 PCs, mixed outcomes over loopback TLS, late results,
  Unicode, unfinished exports, schema refusal and storage failures; the Jobs view is
  rendered by the headless UI suite. Real fleet transfer acceptance remains pending.
- *Script editor extension (2026-09-07, D-39).* Added line numbers, syntax coloring,
  local PowerShell diagnostics and parse-error blocking on run; cmd checks are explicitly
  limited. Quick runs now sit above the lab mosaic. Added open/UAC-open/graceful-close
  for Word, PyCharm, IntelliJ IDEA, Visual Studio and VS Code, plus `find-python`.
  Existing libraries use *Add missing built-in scripts*. Windows verification remains:
  installed-app discovery, GUI lifetime after the script exits, save prompts, UAC
  approval/cancellation and Python visibility under the student account.
- *Portion 1 (built 2026-09-07).* The script library (`D-31` items 4–5, `D-38`). The
  *Scripts* tab sits between *Lab* and *Jobs*: the library on the left (name, one line,
  shell · run-as · timeout), the selected script on the right — name, description, shell,
  run-as, the inactivity timeout and the text in a plain monospaced editor — with *New
  script*, *Delete*, *Save*, *Revert* and *Run on N selected PC(s)*, which sends the
  editor's text as typed, saved or not, to the PCs selected in the lab view and says so
  in the status line; unsaved edits are kept per script while the teacher looks at
  another one (a dot in the list). `Shared/Persistence/ScriptsDocument` is `scripts.json`
  (`schema_version`, `D-20`), `Shared/Jobs/ScriptSeed` turns a seed file into a record
  (shell from the extension, description from the first comment line, `run-as:` and
  `timeout:` header comments), `Console/Services/ScriptLibrary` loads it, imports the
  seed once and saves on every change, and `LabSession.RunScript` offers the text through
  `PullFile` and sends the `run_script` jobs exactly as the development dialog did. The
  seed is `scripts/library/*.ps1|*.cmd`, embedded into the console at build time, so a
  published console carries it; originally four scripts (`pc-info`, `list-installed`,
  `close-browsers`, `clear-temp`). The backup carries the library (`BackupPayload.scripts`;
  an older backup restores without one and the seed fills in). The jobs panel names the
  script in the *Job* column. The development-only *Run test script…* dialog is gone
  (`D-31` item 3); the built-in test scripts stay in `Shared/Jobs/TestScripts` for the
  simulator and the tests. Tests: seed parsing, the document, the backup with and without a
  library (`Shared.Tests/ScriptLibraryTests`); the seed imported once and never again,
  validation on save, the library restored from a backup on another console, a library
  script run over a real link (`Console.Tests`), and the tab rendered in `UiTests` with a
  new script run unsaved and then saved. **To run on the VM with the next agent push:**
  `pc-info` and `list-installed` as SYSTEM, `close-browsers` in the student session with
  Edge open, `clear-temp` — each with its output in the jobs panel; a script edited and run
  unsaved; the library present after importing the backup on the Windows desk PC.

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
