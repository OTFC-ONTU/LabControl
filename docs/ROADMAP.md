# Roadmap

Milestones **M0 … M7**. Each one is independently demonstrable: at the end of a
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
| **M4** | Deployment: USB installer, files, self-update | **in progress — `1.0.0` is the physical-lab release candidate (owner decision 2026-09-08); script/file flows, USB Setup and signed self-update implemented; isolated Windows installation, delivery and recovery checks passed. Removal, administrator-access and physical-lab acceptance remain (`D-38`, `D-41`…`D-52`; verification ledger below)** | M3 |
| **M5** | Lab files, teacher access and fast switching between rooms | **in progress — design recorded 2026-09-08 (`D-53`…`D-60`), extended by `D-68`; all eight portions built and reviewed (1–3 on 2026-09-08 — profile store and migration; `ActiveLabController`, the *My labs* chooser, *Disconnect*, bulk `.lcbak` import; signed `.lclab`/`.lcreq`/`.lcgrant` exchange, teacher sessions without a vault, `instance:` withdrawal with confirmed delivery, dormant imported codes — and 4–8 on 2026-09-09: results bound to the delivering console instance and in-flight job restoration; the take-over decided on the agent's own clock, the four ownership states this console can prove and the informational `Welcome.console_access`, followed by the portion-5 security fixes that refuse a withdrawn instance's beacon where the beacon is judged and attribute ownership only on the PC's own departure report; command-line documents, single-instance forwarding and macOS file activation; the per-user Windows console installer, the LAN-access banner and the macOS and Linux packages, run on Windows for the first time on 2026-09-09 and fixed for the four defects that run found — the `app.manifest` double hyphen that stopped the installer starting at all, the firewall profile constant that opened Public and rejected a real private-and-domain rule, the `group=` argument that made the printed `netsh` line create nothing, and the argument-list quoting that left a 148 MB copy in the temp directory; and the acceptance drills with the gaps an adversarial audit of every acceptance criterion found — a combined picker filter, batched authorization, the departure flow every leaving route uses, the beacon-resume step-over that relinks thirty refused PCs in 2.7 s instead of 30.3 s, and events bound to the console that delivered their job), migration tried on a copy of the live data, portion 3 smoke-tested on two copies, portion 4 also proved with a real agent on the isolated Windows VM clone, portions 6–8 checked on the owner's Mac, and portion 7 also run on the isolated Windows 11 ARM64 clone (guest `PC-27`) on 2026-09-09 — the installer, its plan and dry run, the per-user install by a non-administrator, the shortcut, the Installed-apps entry, both file-type registrations, an idempotent repeat run, the lock refusal, document forwarding into a running console over the Windows named pipe with spaces and Ukrainian in the path, the real Windows Firewall rules and the banner going from missing to allowed, uninstall including the temporary copy's self-deletion, all three `--remove-data` answers and a DPAPI-sealed key surviving replacement; main stands at 984 tests (755 Shared + 229 Console, 13 macOS bundle tests skipping unless the package has been built). **Still unverified:** on Windows, Explorer's own double-click, a real elevation prompt a teacher answers, `win-x64` (only ARM64 was built and run), the installer under an ordinary interactive profile rather than a service account, and the unsigned-download warning — the clone has no interactive session, so every unelevated step ran as `LOCAL SERVICE` and the elevated one as `SYSTEM`; on Linux, the `SO_PEERCRED` path and a real desktop menu; portion 5 on Windows with two consoles and skewed clocks against a real agent, the Avalonia quit hook by hand, and the milestone in the physical lab; real-Mac switch timing under investigation** | M4 |
| **M6** | Classroom control: broadcast, lock, exam mode | not started | M5 |
| **M7** | Software catalog, localization, polish | not started | M6 |

Update the **State** column (`not started` / `in progress` / `done <date>`) in the same
change that finishes the work, and regenerate the HTML mirror (see
[Documentation workflow](#documentation-workflow)).

> **Why eight milestones.** M5 was inserted on 2026-09-08 at the owner's request:
> teachers carry several labs on one device and select one for each lesson (`D-53`).
> Former M5 (classroom control) is now M6; former M6 (catalog/polish) is now M7.
> The original plan had software installation in
> M4. The owner has confirmed the catalog starts **empty** and his lab's PCs are already
> provisioned, so package management moved behind the lesson-time features that are used
> every week. What matters for packages is not a prefilled catalog but a comfortable way
> to *add* an installer — that is the M7 acceptance criterion.

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
  shared drive needs its WebDAV size limit raised (`D-29` item 7, `docs/DEVELOPMENT.md`).
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
  will get, `<number>+<8 hex of the executable bundle digest>` (D-52 extends the original agent-only identity). `LabSession.PushAgentBuild` offers the
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
  after an hour; and the GDI fallback on the VM. Owner's word: "everything works".
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
  quality (q75 → q60 halves the bytes) and after that H.264 (`D-11`, M7 — these are
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

**Installation scope (owner decision 2026-09-08).** Clean installation and repair of
installer-owned installations only. Unowned legacy dev installs are refused; migration
from them is outside M4. Clean means no earlier LabControl installation, not an empty
Windows user profile: the account-off path continues to preserve existing personal accounts.

**Deliverables**

- `LabControl.Setup` exactly as specified in `docs/INSTALLER.md`: `ISetupStep` pipeline,
  `--dry-run`, `--number`, `--uninstall`, `--rekey`, idempotent re-runs, `setup.log`,
  green summary, reboot prompt, Defender exclusion, third-party-antivirus detection.
- **Optional student account and standalone uninstall (D-40).** On the PC-number
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
  a reconnect. Preserve the general private-staging channel for the planned M7
  `install_package` consumer; package execution moved with the catalog, as recorded above.
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
  Private-staging transfers must remain available after handouts. The originally listed
  `install_package` regression belongs to M7, where that job is introduced; no current
  Windows package handler is claimed by M4.
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

**Integration in progress (2026-09-08, D-52).** The owner requested completion of all M4,
with parallel implementation and independent criticism. New code now covers signed update
manifests, a durable external rollback trial, the USB builder and console action, Windows
managed handouts, ordered sign-in and activation, hostname/firewall/Defender/hibernation/NIC
adapters, session policies and executable setup/removal/rekey integration. The two existing
UDP test failures are addressed by a separate in-process test port.

**Verification ledger (isolated fixtures; milestone still in progress).**

The owner designated version `1.0.0` as the clean physical-lab release candidate on
2026-09-08. This does not close M4 before the remaining rows pass. The later M5 portion-5
`Welcome.console_access` addition is informational and preserves the frozen v1 update
subset, so this baseline is updated in place rather than reinstalled.
The clean `f1b7137` source snapshot with only the `1.0.0` version change passed all
702 tests (591 Shared, 111 Console); its `win-x64` Agent/Session/Setup publish formed
the initial physical-lab media. The first two physical runs stopped at the Defender step
despite successful independent exclusion read-back. `D-61` adds conservative repair and
WMI value compatibility. That rebuild reached Hibernation, where the physical PC proved
Windows had successfully removed the file while normalizing enable/type to absent and size
to zero. `D-62` handles and journals that bounded result. Exact USB/local hash comparison
proved that rebuild was present when a rerun still stopped there; the remaining file-type
sentinel check is now ignored only when no hibernation file exists, and Setup prints its
version/build identity. The hash-verified rerun still failed during state reading, before
journal comparison. `D-63` now bypasses the native capabilities query only after a
fixed-path probe proves the file absent and logs fixed safe stage codes. Latest tests pass
600 Shared + 111 Console. Build `96756f3f` isolated a strict path/native disagreement;
the successful native "no active file" result now overrides a residual/protected path
object. A second PC also reproduced the generic Defender failure on its first run; `D-64`
tests the delayed-read-back inference with a bounded ten-second convergence window and
fixed safe codes. Build `09f13b86` (hash-identical on the USB stick) still stopped at
Hibernation; elevated read-back showed *Hibernate available*, `HibernateEnabled` absent and
`HibernateEnabledDefault=1`, i.e. the factory OS-default state that the adapter rejected.
`D-65` accepts the absent value as enabled and restores it as absent. Tests pass 602 Shared
+ 111 Console. The combined rebuilt Setup requires another physical rerun before acceptance.

| Area | Verified evidence | Still required |
| --- | --- | --- |
| Mac build and tests | `dotnet test`: **673/673, no skips**. All nine targets from `tools/publish-all.sh` published; two existing Avalonia constructor warnings. | Recheck only after further source changes. |
| Windows settings | ARM64 clone: **17 read-only probes**, **8 actual apply/restore round trips**, exact original-byte comparison (Fast Startup, SAS, three AC timeouts, active hours, two firewall rules). | Hibernation was already off; actual physical NIC writes and shutdown wake are not proved by this VM. |
| Fresh classroom install | Actual initial dialog: checked account default, one PC number; install and reboot into standard `student`. Five template checks proved actual file editability, copy/edit/rename/delete and read-only Default originals, with original hashes restored. | Corrected administrator sign-in/UAC accessibility and the physical fresh-PC run. |
| Account-off and removal | Install, repair, reboot, full USB probation, rekey, standalone uninstall and restored hostname; account/profile/sign-in fingerprints preserved. Rekey reached a fresh lab; a second rekey changed console port while retaining identity and resumed actual thumbnails. Further native removal retained the student in one clone and deleted the owned student after a loaded-profile refusal/ordinary retry in another. Both exercised the fixed ReadOnly cleanup; an existing-student collision preserved a new baseline exactly. An earlier partial install was removed with all three retained-user fingerprints unchanged; a second partial stopped at the foreign firewall rule and preserved it before an explicitly recorded fixture cleanup/repair. | Complete the home-PC capture/control/script loop in account-off mode (Windows currently has no signed-in user). |
| Files and session | Real TLS loopback: **14 × 500 MiB**, independent destination hashes, one interrupted/resumed peer. Windows: 20-MiB DOCX and PDF delivered without duplicates and editable by the standard student. Network user-session script and pointer input passed. Modified handouts were replaced without duplicates; PDF opened in Edge under the recorded student token. An actual executable delivery with Open=true passed a process observer with a verified positive control and zero launches. | Physical 14-PC LAN throughput and Word opening. The VM's PDF association is Edge; no Word opening is claimed. |
| Signed updates | Full 10-minute USB and network success probation, including the final `d27bc649` build: stable state, finalized recovery, exact SCM path and all three retained-user fingerprints verified. Actual crashing service recovered after the fourth SCM failure; actual alive-but-unlinked service recovered by the unchanged 12-minute task. Wrong-key manifest refused without switching, with visible `update.refused` event. Repeated deadline recovery also verified durable finalization and correct console failed-version status. | Complete the physical fleet drills. |
| Administrator maintenance | A native UAC test exposed missing credential fields when the sole administrator was hidden. Setup now journals and verifies explicit credential entry before hiding; repair applied the policy without changing agent version. | Corrected UI and manual sign-in remain unverified because the Mac screen is locked. The clone's original `EnableLUA=0` was restored after a separate temporary test condition; Setup does not change that security policy. |
| Physical lab | Installer reports supported NIC changes, selected wired MAC and persistent readiness advisories. On 2026-09-08 both the previously used and clean PCs reached the Defender step; an independent elevated `Add-MpPreference` returned success and exact read-back. `D-61` repaired that recovery path. A second PC reproduced the first-run failure; D-64 adds bounded provider convergence and safe stage codes. The next runs reached Hibernation and read-only evidence showed the file absent, enable/type absent and size zero. Exact hash/size comparison proved the first D-62 rebuild was on USB. D-63 added safe diagnostics; build `96756f3f` identified a residual/protected path object disagreeing with the native inactive state, which is now resolved in favor of the successful native result. Build `09f13b86` then exposed the real cause: a factory-fresh PC with hibernation enabled and no `HibernateEnabled` value (`D-65`); the PowerShell "file absent" probes were unreliable. The rebuilt Setup installed PC-04 and PC-10 completely; both reconnect after reboot with automatic student sign-in. Wake-on-LAN failed on both, also from a wired neighbour: the in-box Realtek driver exposes no WoL settings, so all `Network …` steps were skipped. That advisory is now informational (`D-66`). A third PC stopped at Defender with `DefenderProviderRejected` on its first run and installed completely on Repair; Setup now prints the provider status and retries the additive call three times (`D-64` addendum). **Owner's call (2026-09-08, end of day): the physical baseline is established.** Five PCs (PC-04, PC-08, PC-09, PC-10, PC-11) are installed from the `1.0.0` USB media with the D-65 Setup, all enrolled in lab `444-2`, all linked to the console with agent `1.0.0+a3c9f3ac`, automatic `student` sign-in and a live session helper; two of them still report their pre-rename `DESKTOP-…` hostname until their next reboot. The remaining M4 physical checks continue in the background of M5 rather than blocking it. | Remaining nine PCs from the same media. Vendor Realtek driver + USB repair, then the shutdown-and-wake test (Wake-on-LAN is known impossible with the in-box driver). Rerun the rebuilt Setup on both PCs, then all 14 PCs, backup-only console migration, fresh-PC setup, real LAN transfers and shutdown Wake-on-LAN. Manual native read-back is diagnostic evidence, not completed Setup acceptance, and a virtual NIC is not hardware acceptance. |

The network tests found and corrected native Task Scheduler XML encoding (UTF-16LE),
exact deadline rounding and missing-task cleanup. The first no-link recovery restored the
service but exposed a console-state race; the terminal reporter now retries after the
recovery journal lock clears. The repeat showed both durable recovery finalization and
the console's correct `rolled_back` failed-version state. A final review also corrected template restoration during removal: with no pending
template entries, it no longer queries a not-yet-created or already-deleted student.
Five regression cases cover both retry paths and preserve SID checks for actual work.
A disposable console's normal idle key lock also delayed
a fresh enrollment; the fixture now exposes explicit unlock, and the generated USB
instructions put console/key preparation before the first PC installation.

Native removal exposed two further issues: ISO-derived ReadOnly installer copies stopped
final cleanup, and Windows populated `AutoLogonSID` during actual student autologon.
The cleanup now handles ReadOnly only inside verified owned paths. Fresh sign-in setup
journals the original SID value separately; earlier tuple-only test journals cannot
reconstruct that original and explicitly report the limitation. ReadOnly cleanup passed in both clones after a recorded test-only replacement of the
private retry worker; remaining installed ReadOnly files were deleted by production code.
A subsequent fresh GUI install using the fixed installer passed actual student autologon,
explicit removal, loaded-profile refusal, logoff and ordinary retry. All three fresh
baseline fingerprints (accounts, profiles and Winlogon/LSA) matched exactly, including
AutoLogonSID restoration. Service, installation/data roots, uninstall registration and
the owned profile were absent; this fresh cycle needed no worker replacement or manual
attribute changes.
After the final reboot, the original hostname was restored and the temporary cleanup
worker was absent.

Remaining before completion: finish the pending Windows observations above and record the
physical-lab criteria. Neither macOS compilation nor isolated-VM success substitutes for
those checks. Clean installation remains the scope; unowned legacy-install migration is
not part of M4.

**Earlier implementation history.** The dated entries below record each slice at the time
it was built. Their then-pending integration steps are superseded by the verification
ledger above; they are not a second current checklist.

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
- *Portion 3 Windows Update active-hours adapter built (2026-09-08, D-50).*
  A single protected enable/start/end tuple requests 07–20 without disabling updates.
  Repair/removal preserves the entire policy after a later member edit. Native writes
  guard all three values; partial failures remain conflicts. Missing policy leaves can
  be created, but restoration never deletes a registry tree. Mac tests cover journal
  round trips, conflicts and interrupted writes. Windows execution and effective restart
  behavior need VM verification; no CLI invokes the component yet.
  Validation: build passed with zero warnings, all 20 new tests passed; the complete
  suite passed 410/412. The two existing BeaconTests failed discovery while a running
  console advertised on the shared UDP port (the diagnostic heard that console, not
  the test consoles). Setup self-contained publishes passed for win-x64 and win-arm64.
  Next: remaining native settings/sign-in adapters, account group verification/activation,
  then the executable install/repair/removal flow and Windows handout delivery.
- *Portion 3 LSA sign-in secret adapter built (2026-09-08, D-51).*
  The protected journal now bridges the fixed LSA autologon secret. Account mode off
  skips all native access; mode on requires recorded SID ownership. Original absent,
  empty and UTF-16 values are preserved; later edits and uncertain applies conflict.
  Native code rechecks expected data, verifies writes and clears owned secret buffers.
  No CLI invokes the component. Windows LSA/DPAPI execution and actual sign-in remain
  pending; next are ordered Winlogon setup/restoration, account group verification and
  activation, remaining machine adapters and executable integration.
  Validation: all 18 new tests passed; full suite 428/430, with the same two existing
  UDP BeaconTests failing alongside the running console. Final build had zero warnings
  (the first compile also reported the two known Avalonia window warnings).
  Self-contained Setup publishes passed for win-x64 and win-arm64.
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

## M5 — Lab files, teacher access and fast switching between rooms

**Goal.** A teacher imports the files for all their rooms in one operation, then selects
the room for the current lesson. The roster of each room is mostly stable; the dynamic
part is which teacher/device uses which room. One console has **at most one active lab**.
Changing rooms must not require a restart, repeatedly restoring a backup, visiting
student PCs or waiting for unreachable PCs. The same workflow supports administrators
adding their `.lcbak` backups as lab profiles. Simple teacher-console installation is
also part of this milestone (`D-54`). This is planned work, not an existing capability.

**Teacher workflow**

1. Install the teacher console using the simple desktop installer/package. The
   administrator creates each lab once and exports its lab file. Teachers and
   administrators use the same *Add labs…* with multiple file selection (or drop files
   into the app): teacher lab files, administrator `.lcbak` backups, or a mixed batch.
   Any first-time device authorization is batched during this setup, not repeated at
   the beginning of lessons. Backups are unlocked during import with their passphrase
   or recovery code, then added with an explicit *Administrator* access label. No
   conversion to a teacher file is required. The UI separates *Add an existing lab*
   from *Create a lab*.
2. Imported labs remain in a local *My labs* list: room/name, expected PC count, access
   status and last-used time. Importing does not activate a lab. No live availability
   claim is made for an inactive lab; the list uses saved metadata only.
3. Select a room to open its mosaic. The active lab is always named prominently beside
   the controls. The same selector changes rooms during the day; routine switching
   requires no passphrase and no re-import. Launch opens the chooser with the last-used
   lab highlighted, without silently acquiring a room.
4. *Disconnect* releases the current room and returns to the list. Removing a lab from
   this device removes its local profile/access only; it never uninstalls agents,
   deletes the classroom or revokes other teachers.

**Deliverables**

- **Lab files distinct from administrator backups.** A versioned, authenticated lab
  file carries `lab_id`, a human-readable room/name, public trust, a roster/layout
  snapshot and the material needed for the authorized teacher-device onboarding flow.
  Ordinary distribution must not carry the CA private key, recovery material or student
  enrollment codes. Any access secret is protected in transit and moved into the local
  designed store on import. Each device obtains its own revocable identity; importing
  the same room on several devices must not clone a shared private identity. The exact
  extension and authorization exchange are now specified in `D-56` (`.lclab`, `.lcreq`,
  `.lcgrant`; PROTOCOL *Files exchanged offline*); the acceptance requirement is one
  batched onboarding workflow, fully offline capable. Existing `.lcbak` archives remain full administrator backups and are also
  accepted directly by *Add labs…*: importing one adds a switchable administrator
  profile to the same list. Their recovery purpose and format remain compatible; there
  is no separate application, recovery-only restriction or repeated restore when
  selecting that room. Preserve the existing device identity on re-import; mint a new
  one only when adding this lab to a new device. Switching uses the local identity and
  does not unlock the CA; enrollment, signing and other privileged actions still do.
  A public room description alone is not authorization: specify a batched offline
  request/approval exchange using device-generated keys, or an equally explicit secure
  provisioning flow, before implementing teacher files. Show *Needs authorization* until
  it completes. Existing valid access enables later switching without administrator
  presence; expired/revoked access needs a separate renewal/recovery flow.
- **Simple teacher-console installers/packages (`D-54`).** Supply offline-capable,
  self-contained distribution for the supported teacher platforms: a Windows installer
  with Start-menu entry, optional desktop shortcut and Installed apps removal; a macOS
  `.app` in a `.dmg` with Applications installation; a Linux desktop package/install
  flow with launcher and removal instructions. The packaging tools are chosen in
  `D-59`. Copy/register the console and its required runtime/native assets; register
  file-opening support for teacher lab files and `.lcbak` backups. Opening files routes
  them into the existing console's import flow and does not start another active lab.
  No student Agent/Session service, background daemon, scheduled task, startup control
  session, student account, autologon or student-machine policy tuning is installed.
  The embedded server runs only with the interactive console and selected lab. Use
  per-user installation where supported; OS network/privacy permissions are explained
  at the point of use, without disabling firewall/antivirus or granting broad exclusions.
  Updates/repair preserve lab profiles and credentials; removal deletes app-owned
  binaries/registrations and retains user lab data by default. Data/key deletion is a
  separate explicit choice and never deletes external backups or student installations.
  Packaging must include a stable application identity/path, macOS bundle metadata and
  actual file-activation handling in the app, not only OS registrations. Windows LAN
  access needs inbound TCP `ConsolePort` and UDP `BeaconPort`; provide a scoped network
  setup/diagnostic flow, requesting elevation only if an OS rule change requires it.
  Installation alone does not imply firewall permission. On Linux specify tested distro
  versions and native-library prerequisites; self-contained .NET does not remove those.
  Offline prerequisites must be present or supplied locally. Document/test the current
  unsigned distribution path and its SmartScreen/Gatekeeper prompts (`D-15`); do not
  promise warning-free installation or silently require purchased signing credentials.
- **Bulk import and safe refresh.** Match by `lab_id` and pinned authority, not file name,
  display name, IP or PC number. Re-import updates the existing room without duplicates,
  replacing its device identity or overwriting local job history. Older snapshots cannot
  roll back revocations or erase newly learned PCs. Same-named rooms with different ids
  remain distinguishable; the same id with different trust is rejected. Corrupt,
  unauthorized or unsupported-version files leave existing profiles intact and produce
  a per-file result without preventing other valid files from importing. Persist each
  accepted import atomically; no requirement for all files to succeed as one transaction.
  A valid backup for an existing teacher profile upgrades that same profile to
  administrator access after explicit import/unlock, without duplicate rooms or loss of
  its local identity/history. A later teacher-file import never silently downgrades an
  administrator profile; access level is per lab, not global across the application.
  Preserving device identity means keeping its stable id and history; an explicitly
  authorized certificate refresh may still be required when its access changes.
- **Profiles isolated by lab.** Trust, credentials, machine records, layout, grants,
  jobs, logs, scripts and catalog state are scoped by `lab_id`; PC-01 in two rooms cannot
  collide. Keep the known roster for offline display and Wake-on-LAN, while validated
  agent connections still refresh inventory and discover legitimate roster changes.
  No timetable, automatic room reassignment or continuous roster synchronization is
  needed. Migrate the current single-lab installation to one profile without changing
  its lab or device identity, requiring student re-enrollment or losing local data.
- **One active session with bounded switching.** Stop old discovery advertisements,
  agent links, screen streams, input and background connection attempts before activating
  the next lab. Dispose old frame buffers and session workers; inactive labs retain
  metadata on disk, not live sessions. Reject inactive-lab peers at the active server.
  Serialize rapid selections; stale callbacks, open PC windows and queued commands must
  never act on the next room. Multiple windows/another launch against the same app
  profile cannot bypass the one-active-lab rule. A failed activation leaves an explicit
  disconnected/retry state, not two partially active labs.
- **Safe departure and results.** Ordinary idle switching is immediate. Surface any
  running scripts, transfers or update probation before leaving; explain which can be
  stopped safely and which must complete, with an explicit wait/cancel/leave choice only
  where valid. Never abort an update unsafely or keep its lab secretly connected. Bind
  operations and cached results to their originating lab and console; preserve outcomes
  for later reconnect, and never send an old teacher's output to the next teacher.
  Pending interactive commands must not unexpectedly replay on a later lesson. M6 must
  extend this departure contract to lock, broadcast and exam policies with their existing
  independent expiry/restoration guarantees.
- **Teacher handover and access.** Keep the existing one-controller-per-PC invariant.
  Switching away releases the room for another teacher; selecting an occupied room
  shows a truthful status and an explicit *Take over* action. Repair the audit gaps:
  handover must tolerate allowed clock skew, offline PCs must not be reported as known
  to be held elsewhere, and every authorized device must be identifiable and revocable
  across certificate renewal. Separate classroom access from CA ownership; an ordinary
  teacher cannot enroll PCs, issue identities or sign agent updates. Define renewal and
  revocation propagation without an always-on server, including an honest pending status
  for unreachable agents. Enrollment remains administrator-owned; copied backups must
  not silently resurrect spent/voided codes as globally usable enrollment authority.
  Adding a backup for switching must not automatically enable its pending enrollment
  codes. Keep their history for recovery, but separate ordinary room import from explicit
  administrator enrollment recovery. Strict global single-use cannot be promised across
  disconnected independent CA owners: define one issuing authority per enrollment batch
  or equivalent coordination, and test that path without claiming old offline copies
  are remotely erased. This does not limit the administrator's ordinary room control.

**Implementation reality and sequencing**

The reviewed build publishes executable directories (`tools/publish-all.sh`), not desktop
installers or a macOS app bundle. `Program` currently accepts development options, not
document-open activation; `App` starts one `LabSession` immediately and bootstrap assumes
a local `LabKeyVault`. M5 therefore needs application lifecycle/import refactoring and
a teacher profile that can operate without a CA key. File registration alone cannot
implement any of these flows. Existing `.lcbak` import writes into one data directory and
mints a new instance; reuse its cryptography, not those destructive replacement semantics
for an existing profile. Preserve the original profile if migration is interrupted.

Treat installation as three separately verified outcomes: app files installed, desktop
launch/file opening works, and LAN connection works. The app is an ordinary user process
hosting Kestrel, not a service, but still needs network permission. macOS packaging needs
bundle identity/metadata; updates may affect OS keychain/privacy access. Test key access
after upgrade/reinstall on the same OS account, including Linux's file fallback. Retained
files alone do not make OS-bound keys portable to another user/device: use backup or
device authorization there. Check denied permissions and existing file associations;
do not overwrite a user's chosen default handler by force.

Before the switcher is treated as complete, prove isolated sessions and result ownership,
then the offline access/renewal contract, then desktop integration. Maintain administrator
access to older agents for migration; document capability negotiation for any new teacher
authorization requirements, without weakening them or breaking the frozen update path.
The 2/15-second goals below apply after completed import and valid permissions/access,
with graceful idle departure. They exclude initial authorization, cold OS prompts and
noninterruptible jobs. Abrupt laptop loss/network failure has a separately measured
reconnect bound (the current heartbeat timeout alone is 20 seconds), not a false 15-second
guarantee. Test the isolated-subnet/UDP discovery assumptions rather than promising
cross-VLAN discovery, Internet access or recovery from arbitrary firewall policy.

**Acceptance criteria**

- Install and launch from the teacher package on clean supported Windows, macOS and
  Linux environments from the documented OS/native-prerequisite matrix, with no
  separately installed .NET and no internet. Verify launcher
  and file-opening registration; open multiple lab files/backups while the console is
  running and confirm a single import flow with no second active room. No student
  service, account or preparation settings appear. Exercise upgrade, repair/reinstall
  and removal: existing profiles/keys survive by default, and reinstall restores their
  use. Verify required LAN permissions with a real agent; screen/privacy permission
  acceptance for broadcast remains M6. Successful copying alone is not connectivity proof.
  Test denied LAN access and its guided recovery, unsigned-package launch prompts, paths
  containing spaces/non-ASCII characters, file activation on first and subsequent launch,
  and retained key access under the same OS account after app replacement.
- Bulk-import three independent labs, each with up to 30 simulated PCs, in one workflow.
  Restart the console: all three remain listed, none is active until selected, and the
  original input files/USB are no longer needed. Complete initial authorization offline.
- Import several `.lcbak` backups together, then a mixed batch of backups and teacher
  files. An administrator switches A → B → A using the same selector, one active lab
  and the same performance targets as a teacher, with no repeated restore or unlock
  for routine control. A wrong backup password affects only that file. Re-import and
  teacher-to-administrator upgrade preserve identity/history and produce no duplicate;
  teacher access to another lab remains limited. Administrative actions still require
  unlocking that lab's key, and every inactive lab's CA key is locked on departure.
- On the development Mac, switching an idle lab shows the destination's saved mosaic
  and responsive controls within **2 seconds**. On a healthy supported LAN, all already
  running reachable agents of that lab connect within **15 seconds**. Unreachable PCs
  remain visibly offline and never extend the switch indefinitely. These are acceptance
  targets to measure, not claims about current performance; ongoing noninterruptible
  work is explicitly reported separately.
- Switch A → B → C → A at least 20 times with 30 agents per lab, also making rapid
  repeated selections and switching during capture/control. Observe no old-lab beacons,
  links or video after departure, no input delivered to the wrong room and no growing
  accumulation of frame buffers/workers. Inactive-lab traffic is rejected even if both
  rooms share a subnet. Up to 30 is the limit **per active lab**, not across saved profiles.
- Two teacher devices alternate between two labs; different teachers may use different
  labs at the same time, while each device controls only one. Demonstrate Mac → Windows
  → Mac handover without student reinstallation or daily secret entry; verify discovery
  on the real lab LAN as well as with FakeAgent. Test takeover with ±30 s clock skew and
  distinguish offline, unknown and positively observed ownership states.
- The administrator can withdraw one ordinary device's access without affecting other
  teachers or labs. Re-importing its old lab file cannot bypass withdrawal after agents
  have learned it; renewal cannot restore revoked access. Explain the propagation limit
  for disconnected agents. Show that a teacher file cannot mint a fresh privileged
  identity, enroll a PC or sign an update.
- Duplicate, old, corrupt and same-name/different-id imports, expired access and a crash
  during import/switch preserve other profiles and local history. A legacy `.lcbak`
  restore remains supported for the administrator, without silently downgrading trust
  or revocation state. Reusing an enrollment code through a second stale admin copy is
  refused or requires an explicit safe recovery flow, never silently issues twice.
- Switch during a script, a resumed transfer and update probation; confirm the departure
  choices, preserved result ownership and eventual visible outcome. Queued commands
  cannot leak into another lab or execute unexpectedly on a later lesson.

**How it is being built.** In eight portions, on the M2/M3/M4 pattern — each committed,
then run before the next starts; the design is `D-55`…`D-60` (recorded 2026-09-08 from
the reviewed design, before code): **(1)** the profile store and migration — `profiles.json`,
`labs/<lab_id>/`, `LabStore` per profile, the copy-rename-commit-delete migration with
resume at every step, `lab.json` schema 2 (`D-55`) — all on the Mac; **(2)**
`ActiveLabController`, the chooser, *Disconnect*, bulk `.lcbak` import into profiles and
the nullable vault (`D-57`) — on the Mac, with a `TestRig` of two labs on one port;
**(3)** lab files, the `.lcreq`/`.lcgrant` exchange and teacher mode — envelopes,
`IssueTeacherDevice`, `LabName.Access`, `instance:` revocation, the teacher branches of
`LabSession`, *Teacher devices* and *Export lab file…* in Settings, dormant codes (`D-56`,
`D-60`) — on the Mac; **(4)** the departure report and result ownership — the agent's
ledger bound to the delivering instance, the journal columns, the dialog (`D-57`) — **on
the Windows VM**, because the real agent's ledger is what must keep the result; **(5)**
take-over on the agent's clock and the ownership states, `Welcome.console_access`
(`D-58`) — **on the VM** with two consoles and skewed clocks; **(6)** document activation,
the single instance and the command line (`D-59`) — tests on the Mac, manual on macOS;
**(7)** packaging and the firewall flow — `LabControl.ConsoleSetup`, `tools/package-*.sh`,
`NetworkReadiness`, README/INSTALLER (`D-59`) — **on the VM** for Windows, manual on macOS
and Linux; **(8)** the acceptance drills and the documentation close-out — 20 × A → B →
C → A with 30 agents each, the leak check, two devices alternating — in the lab and on the
VM. `FakeAgent` gains `--lab <payload>` per group so three labs of 30 run from one process.
The order proves isolated sessions and result ownership first, then the offline access
contract, then desktop integration (`D-54` item 6).

**Progress.**

- *Design recorded 2026-09-08* (`D-55`…`D-60`; ARCHITECTURE §3.9/§4, PROTOCOL *Files
  exchanged offline* and *M5 additions*). The `.proto` addition of portion 5 —
  `Welcome.console_access` — was made with that portion and PROTOCOL was updated in the
  same commit.
- *Portion 1 (built and reviewed 2026-09-08, on the Mac).* The profile store and the
  migration (`D-55`): `ProfilesDocument` (`profiles.json`, schema 1), `ProfileStore`,
  `ProfileMigration`, `ConsoleLock` (`console.lock` opened with `FileShare.None`, one
  process per data directory — a second launch is told so and quits; not enforced where
  `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` is set), and `ConsoleBootstrap` per profile:
  `OpenExisting(labId)`, `CreateLab` into `labs/<id>/`, `ImportBackup` into a new profile
  that refuses a lab already saved on this device until portion 3's upgrade semantics.
  `lab.json` is schema 2 with an identity 1 → 2 step; `LabBackup.Open` upgrades the
  nested lab/scripts/enrollment documents by their own `schema_version` and refuses a
  newer one by name. The Serilog console log moved from `logs/` to the data root
  (`console-<date>.log`); old files are moved once. The migration is state-driven and
  resumable: copy → rename (5 retries for an antivirus hold) → commit (`profiles.json`)
  → delete the originals in the order `lab.json`, `enrollment.json`, `scripts.json`,
  `packages/`, `logs/`, then `instance.json`, then `lab-key.lck` (the two sentinels
  last). Before any delete every root document is compared byte for byte with its copy
  and `packages/`/`logs/` by file list and size; an uncommitted copy that differs is
  discarded and made again, a committed one that differs leaves the root files moved,
  complete, to `<data>/migration-conflict-<timestamp>/` — never deleted. Symlinks and
  junctions are skipped and logged, free space is checked before the copy, and a root
  with `instance.json` but no `lab-key.lck` is refused, not committed. Reversal to a
  pre-M5 build is copying `labs/<id>/*` back to the root; that build drops the schema-2
  fields it does not know on its next save (`docs/DEVELOPMENT.md`, *Downgrading*). Tests: 702 (591
  Shared + 111 Console), including a crash and resume after every step with
  byte-identical originals, the conflict directory, a symlink loop, a crash mid-delete
  and a keystore protector that throws on `Remove`. *Trial on the owner's Mac* against
  a copy of the live `~/.labcontrol` (`--data`): migrated in under a second, the same
  instance id served lab "444-2", the `PC-01` VM agent linked with its existing
  certificate; a second launch ran no migration step and linked again. The live
  directory itself is not migrated yet — it will be on the owner's next launch of an
  M5 build.
- *Portion 2 (built and reviewed 2026-09-08, on the Mac).* One active session, the chooser
  and bulk import (`D-57` items 1–3 and 5–11): `ActiveLabController` holds the only
  `LabSession`; activation is serialised by a `SemaphoreSlim(1,1)`, a generation counter
  and a latest-request collapse (a burst A → B → C activates C once), moves through
  `Activating` / `Active` / `Deactivating` / `Failed` / `Idle`, runs off the UI thread and
  is cancelled when the controller is disposed (also while it waits on a prompt).
  `LabSession.CloseAsync` releases in the fixed order `LabCloseStep` names — cancel →
  beacons → every link closed with *the console left this lab* → listener → server stop
  and dispose (`ConsoleServer.DisposeAsync` disposes the host in a `finally`, so the port
  is always freed) → housekeeping join → `SaveLab` → `Screens` → `Vault` (the CA locked)
  → `Instance` → `Disposed` — each step isolated, so a step that throws does not stop the
  rest, and the tail (save, screens, vault, instance) sits in a `finally`. `SwitchTimings`
  logs the departure, open, build and start phases of every switch. The cached mosaic:
  `SessionBuilt` shows the main window with the roster tiles from `lab.json` before the
  server starts (*Connecting…* in the lab chip, toolbar disabled) and the window is
  enabled on `Active`. `DescribeDeparture` → `DepartureReport` behind the *Leave anyway* /
  *Stay* / *Wait for N jobs* dialog; `MainViewModel.Detach` unsubscribes and closes every
  screen window. The *My labs* chooser (`LabChooserViewModel`, `LabChooserWindow`): one
  row per saved lab, the last-used one highlighted and never opened by itself, *Open*,
  *Add labs…* (`.lcbak` only until portion 3 — `LabImports.Register` is the extension
  point for `.lclab`/`.lcreq`/`.lcgrant`), *Create a lab…*, *Remove from this device*
  with a second confirmation that names the last exported backup before a lab key is
  deleted, *Retry* after a failed activation, *Quit*; files dropped on the chooser or on
  the main window import the same way; one result per file, a wrong passphrase fails only
  that file, and an import is atomic per file (directory, keystore item and in-memory
  index undone on failure). Closing the main window returns to the chooser; only the
  chooser's *Quit* ends the process; *Disconnect* shows *Leaving…* in the chooser before
  the release runs. A profile record without an access label reads as *Teacher*.
  `FakeAgent --lab <payload>` groups run several labs from one process, with one default
  `--count`. `SecretProtectorTiming` warns when a keystore call takes longer than 250 ms.
  Real agents refuse the other lab's console certificate themselves (their own `LabTrust`
  check) before presenting theirs; the console-side `link.refused` — *belongs to lab
  "A", which is not the active lab on this console* — is for clients without that check,
  and the test proves both. Measured in-process (30 agents per lab, real TLS/UDP, one
  port): departure 12–30 ms, server up 156–965 ms, all 30 PCs linked in 1.0–2.6 s, 0
  lab-A beacons in the 5 s after departure; 20 × A → B → C → A with bounded threads and
  no leaked session. Two limits: under sustained refusal load a departure can spend
  Kestrel's 2 s stop budget, and a PC refused for a long stretch returns within the 30 s
  reconnect cap rather than the 15 s target — a `BeaconGate` follow-up that portion 5 did
  not make and that portion 8 then made (`D-68` item 4).
  Tests: 728 (603 Shared + 125 Console; the Console run twice, `LabSwitchTests` a
  non-parallel collection). *On the owner's Mac* (Debug build, ad-hoc signed) the server
  was up 4–6 s after *Open* and after a re-open; the Keychain prompt per rebuilt ad-hoc
  binary is the prime suspect (the cdhash changes with every Debug build; the tests use
  the file protector), the open/build/start split and the 250 ms warning now instrument
  it, and it is expected to disappear with the signed packaged app (portion 7) — to be
  re-measured. *Trial against a copy of the live data*: the chooser appeared with the lab
  row (administrator, 14 PCs), nothing listening until *Open*; the owner had exercised
  *Open* → *Disconnect* → *Open* on the copy earlier.
- *Portion 3 (built, security-reviewed and fixed 2026-09-08, on the Mac).* Lab files,
  offline device authorization, teacher mode and dormant codes (`D-56`, `D-60`). The
  signed envelope (`SignedEnvelopeDocument`: `kind`, `lab_id`, `lab_name` in the clear,
  base64 `payload`, P1363 `signature` over domain || payload bytes; domains
  `labcontrol/lab-file/v1\0`, `labcontrol/device-request/v1\0`,
  `labcontrol/device-grant/v1\0`) — a wrong kind for the extension, a tampered byte, a
  signature made under another domain or a newer `schema_version` is refused and changes
  nothing. `.lclab` (`LabFilePayload`: authority DER + fingerprint, `snapshot_version`,
  roster, layout, revocations, optional scripts) with a test proving it carries no key,
  wrapping, recovery material, enrollment code or private material; the pinned authority
  is compared byte for byte on re-import. Merge as implemented (`LabFile`): an older
  snapshot contributes only its revocations ("older snapshot; nothing rolled back"), a PC
  this console has seen linked is only filled in, never rewritten, a serial revoked
  locally blocks re-adding, the layout comes only from a newer snapshot, scripts go only
  into an empty library, the access level is never downgraded, the same `lab_id` under a
  different authority is refused. Request/grant (`DeviceAuthorization`): the pending key
  lives under the keystore reference `instance-<id>-pending`
  (`AccessDocument.PendingKeyReference`), the CSR self-signature is verified, P-256 only,
  the name is bounded (`Defaults.MaxInstanceNameLength` = 64); `Approve` refuses a
  request naming an existing id unless that record is a teacher device — never this
  machine, an administrator or an unknown record (`LabRegistry.RecordAuthorization` is
  the registry guard; the review found the administrator's own id could otherwise be
  hijacked) — and refuses a renewal that presents the already-certified key
  (`InstanceRecord.PublicKeyFingerprint`); grant import checks the instance id, key
  equality, the chain, `OU=LabControl Teacher` and the endorsement, and the new key is
  protected and saved before the old items are forgotten. Role: `LabName.Access` from
  exactly one single-valued OU (`LabControl Console` → `Administrator`,
  `LabControl Teacher` → `Teacher`, anything else → `Unknown`); the SAN is unchanged, so
  pre-M5 agents link to teacher consoles; agents refuse `self_update`/`rekey` with the
  event `job.refused_by_role` and skip renewal unless the validated peer
  (`ConsoleChannel.PeerName`, `AgentLink.LinkedConsoleAccess`) is `Administrator`.
  Teacher sessions have a null `LabSession.Vault` (`LabSession.Access` = `Teacher`):
  `Enroll`/`Renew` answer `Closed` naming the administrator; push, revoke, USB payload,
  backup and holders refuse. Withdrawal (`LabSession.TryWithdrawDevice`): the
  `instance:<id>` pseudo-serial plus every serial in `InstanceRecord.CertificateSerials`
  are revoked; agents answer every `Revocation` with `RevocationState`,
  `MachineRecord.RevocationSerialsSeen` feeds `DeliveryOf` (delivered / pending /
  *cannot hold*); an M5 agent leaves a console whose serial or instance id becomes
  revoked (`console.revoked`). Correction to the design: pre-M5 agents do **not** store
  `instance:` entries inertly — their serial normalisation breaks the signature, they
  drop the entry and are re-pushed on every link; the console shows them as *cannot
  hold* (`RevocationDelivery.CannotHold`, the sibling-serial signal) until the agent is
  updated. Settings: the merged *Teacher devices* panel (replacing *Other teacher
  machines*) is the device book with access and dates, *Authorize requests…*, *Withdraw
  access…*, *Export lab file…* (`ConsoleBootstrap.TryExportLabFile`); Enrollment gains
  *Use codes from the imported backup*. `LabImports` registers `.lclab`, `.lcgrant` and
  `.lcreq` beside `.lcbak`; a backup imported onto a teacher profile upgrades it
  (`ConsoleBootstrap.UpgradeFromBackup`) keeping `instance.json` and history, its codes
  dormant (`enrollment.json` schema 2: `DormantSinceImportUnix`, `IssuedByInstanceId`,
  `Batches[]`; `EnrollmentOutcome.DormantCode`; `Supersede` voids dormant codes too).
  `lab.json` counts snapshots (`imported_snapshot_version`, `exported_snapshot_version`).
  Chooser states for a teacher profile: *Needs authorization*, *Request pending*,
  *Access expires <date>* (with *— request a renewal* under 60 days), *Expired*,
  *Withdrawn*. Tests: 753 (622 Shared + 131 Console). *Manual smoke on two copies of the
  data directory*: export → import → request → approve → grant import → "Access expires
  08.09.2027", a teacher session opened with a teacher leaf and no vault; re-importing
  the old file reported "older snapshot; nothing rolled back". Not run on Windows at the
  time; portion 4's VM session then confirmed the role refusal against a real agent (below),
  while the rest of these `AgentLink` changes still reach the lab with the portion-4/5 push.
- *Portion 4 (built, reviewed and fixed 2026-09-09, on the Mac; proved on the VM the same
  day).* Result ownership and in-flight jobs (`D-57` item 4 and the new items 12–13). The
  review found three blockers, all fixed. Job rows carry `lab_id` and `instance_id`
  (`JobRecord`, `JobLogSnapshot`, `JobJournal`, `JobBatchLogs`). The agent's `JobLedger`
  binds a job to the instance id in the SAN URI of the console leaf the TLS handshake
  validated (`ConsoleChannel.PeerName`), never to `Welcome.instance_id` — a claim: a link
  whose validated peer has no console identity is refused, and a disagreeing `Welcome`
  raises `console.instance_mismatch` with the certificate winning and being used for
  `LinkedInstanceId`, the beacon gate, the stored `LastInstanceId` and the next
  `Hello.previous_instance_id`. Results and progress lines go only to the delivering
  instance; another console's copy of the job id is refused (`job.other_instance`), not
  answered from the cache and not run again. The wait is bounded and stated honestly: at
  most `Defaults.MaxPendingJobResults` (500) finished results per PC wait for a console
  that is not linked, the oldest dropped with a `job.result_dropped` event; the ledger's
  own 500 entries are a separate cache; nothing survives an agent restart; progress
  produced while the deliverer is away is dropped rather than buffered; and nobody is told
  if that console never comes back. Restoration: `logs/jobs-inflight.json`
  (`InFlightJobsDocument`, schema 1) carries delivered-and-unanswered rows across a switch;
  `InFlightJobPolicy` with `LabSession.RefuseRestore` re-sends only a `run_script` whose
  text is still in `scripts.json` (re-offered through the new session's `FileOffers`), only
  while the row is younger than the smaller of the job's timeout and
  `Defaults.InFlightJobsMaxAge` (one hour), and only to a PC whose `Hello.boot_time_unix`
  predates the delivery — a rebooted PC has lost its ledger and would run the job a second
  time. Never restored: `shutdown`, `reboot`, `logoff`, `reset_profile`, `self_update`,
  `rekey`, `send_file`, `install_package`, `collect_files`; every other saved row appears
  as a closed row in `TimedOut` reading *Outcome unknown — the console left this lab*, with
  a `job.outcome_unknown` event, so a late result can still replace it. A restored row is
  marked `RestoredFromDisk` and excluded from the next snapshot, so it cannot outlive two
  sessions, and an unreadable, newer-schema or foreign-lab file means nothing is owed and
  never blocks activation. The in-flight save is coalesced like `lab.json`'s;
  `JsonStore.Save` claims the familiar `<document>.tmp` when it is free and a unique name
  when it is taken, and a saver whose rival has already moved its temporary away now falls
  back instead of rethrowing. A restored batch merges into the batch document already on
  disk instead of overwriting it, so the PCs that finished first keep their results, and
  restored rows appear in the jobs panel. Tests: 785 (637 Shared + 148 Console). *On the
  Windows VM (2026-09-09, the isolated clone "M4 isolated native tests 2026-09-08" only,
  host-only network; the classroom VM and the owner's live lab untouched):* a real agent
  `1.0.0` (`win-arm64`, hash-verified in the guest) ran a 110-second SYSTEM script
  delivered by fixture console instance X; X was stopped 21 s in; the agent linked to
  instance Y of the same lab, the script finished under Y, and Y never saw the job (its job
  list stayed empty and the agent logged that the result waits for the console that sent
  it); X came back with the same instance id and received the result with its output and
  exit code. The same session confirmed portion 3's role refusal natively: a fixture-minted
  teacher leaf was refused for `self_update` with `job.refused_by_role` before any manifest
  pull, and the installed app directories were unchanged. Two honest caveats: progress
  produced while the deliverer was away reached nobody (as designed), and the drill's
  ad-hoc script was not a library script, so the console first closed the restored row as
  outcome unknown and the agent's pending-result flush then delivered the true result and
  replaced it — a library script would have been re-offered and re-sent instead. Hand-over
  took 15–20 s. Not done on the VM: the network push of this build from an administrator
  console, and `run_as: user` (the clone has no interactive user). Portion 5 is next.
- *Portion 5 (built and reviewed 2026-09-09, on the Mac).* Take-over on the agent's own
  clock, the ownership states a console can prove and `Welcome.console_access` (`D-58`).
  **The take-over no longer compares two machines' clocks.** While an agent is linked and a
  verified beacon arrives from a *different* instance of its lab, `BeaconGate` ignores a
  beacon with no `take`; ignores one that arrived before the current link was established,
  ordered purely by the agent's own clock (`Linked(instance, endpoint, at)` against the
  arrival time handed to `Consider`); accepts the press only when its timestamp lies within
  `Defaults.TakeOverWindow` + `Defaults.BeaconMaxSkew` of the beacon's *own* `ts`, which
  `Beacon.TryVerify` has already checked against that same clock; and spends the
  honour-once `(instance, take)` token only when all of that passes, so a beacon that was
  already in flight cannot consume the press. A press made just before the PC linked
  elsewhere still moves it, as long as it is inside the window. The console side stopped
  comparing clocks too: whether a departure belongs to a take-over is decided by when this
  console *saw* the taker's signed beacon (`OtherConsole.TookOverSeenAt`), not by the
  timestamp the taker wrote. **Ownership is four states the console can prove**
  (`LabSession.Ownership` → `MachineOwnership`/`OwnershipKind`): `LinkedHere`;
  `ObservedElsewhere` only when another console has a fresh sighting *and* the machine
  record names that instance within the new `Defaults.OwnershipObservationLifetime`
  (fifteen minutes) — `MachineRecord.LastInstanceId`/`LastInstanceObservedUnix`,
  `LabRegistry.IsObservedWith` — so an observation read back from `lab.json` after a
  restart cannot become today's claim; `Unknown` when another console is beaconing but
  nothing is known about this PC; `Offline` with a last-seen time otherwise. The
  observation is written in exactly two places: `LabRegistry.RecordHello` puts the PC on
  this console's own line, superseding any earlier claim, and `LabSession.NoteDeparture`
  records the taker when a PC leaves during another machine's signed take-over
  (`RecordObservedElsewhere`). The tile (`TileStatus.HeldElsewhere` / `NotSeen`) and the
  banner count only observed PCs and say *holds at least N*; a PC nothing is known about
  reads *offline* or *not seen since*, never *held by*. One design detail worth recording:
  the second positive source the design imagined — a `Hello` naming its previous
  instance — cannot produce an observed-elsewhere state, because the same `Hello` puts the
  PC on our own line, so it is recorded as the event `link.arrived_from` (`NoteArrival`)
  instead. **`Welcome` gained the additive, informational `console_access = 6`** (field 6
  was free and not reserved; `Welcome` is not in the frozen update subset, so older agents
  ignore it and older consoles do not send it). The console fills it from the subject OU of
  its own leaf (`ConsoleInstance.Access`) and the agent keeps it in
  `AgentLink.AnnouncedConsoleAccess`, apart from `LinkedConsoleAccess`; every refusal still
  reads the validated certificate, never this field, and a test makes a teacher console
  announce administrator access and still get `rekey` refused. PROTOCOL was updated in the
  same commit. Tests: the branch added skew cases in both directions, honour-once, the
  window bound, a take that arrived before the link and a press made just before linking,
  plus ownership tests for a skewed take-over both ways, offline versus unknown, the named
  holder and the banner count; **after the merge main stands at 960 (744 Shared + 216
  Console)**, 13 of them macOS bundle tests that skip unless `tools/package-mac.sh` has
  been run. Open items: this
  portion has **not been run on the Windows VM**, and the design calls for two consoles
  with skewed clocks against a real agent; and a PC refused across many switches still
  returns on the reconnect ceiling rather than the 15 s target, because a verified beacon
  for the agent's own lab does not yet shorten its dial backoff — that was portion 8's and
  is built (`D-68` item 4). Two parts of this portion were also superseded the same day by
  the portion-5 security fixes below: withdrawal is now judged on the beacon itself, and a
  PC is credited to a taker only on its own departure report, never on a stream that ended
  while the press was live.
- *Portion 6 (built, reviewed and fixed 2026-09-09, on the Mac).* Command-line documents,
  the single instance and macOS file activation (`D-59` item 5); the review's blocker and
  should-fix items were all fixed before the merge. `ConsoleOptions.TryParse` takes
  positional documents — the four extensions in `Defaults.ConsoleDocumentExtensions`
  (`.lclab`, `.lcbak`, `.lcgrant`, `.lcreq`) — and `--import-only`, the forwarder mode of a
  file-type registration. A second launch on the same data directory does not start a
  second console: `console.lock` (`D-55`) decides who serves, the lock holder listens on
  `Services/SingleInstance.cs`, and a launch that cannot take the lock sends its paths
  there and exits (0 when they were taken; 1 with `--import-only` when nothing answered).
  The endpoint is a Windows named pipe `labcontrol-console-<hash>` (`CurrentUserOnly`) or a
  Unix socket `console-<hash>.sock` (0600) inside this user's `labcontrol-<uid>` directory
  (mode 0700, under `$XDG_RUNTIME_DIR` or the temp directory), the hash being the first 16
  hex digits of `sha256(<data directory>)`, so two `--data` directories never collide. The
  wire is one UTF-8 JSON line carrying only file paths, at most
  `Defaults.SingleInstanceMaxOpenPaths` (64) — the cap is checked by the forwarder before it
  connects and again by the server, which also drops duplicates within a batch; every
  forwarded path is validated exactly like a command-line path and additionally required to
  be absolute. On macOS a LaunchServices open arrives through Avalonia's
  `IActivatableLifetime`/`FileActivatedEventArgs`; files that arrive before the chooser
  exists are queued, and the first activation is filtered against the process's own
  arguments (`ConsoleOptions.WithoutArgumentEcho`), which AppKit echoes back as opened
  files. All three routes end in `App.OnFilesArrived` and run through the same `LabImports`
  batch as *Add labs…*; none of them activates a lab. What the review changed: a forwarded
  batch used to be acknowledged and then dropped while the console was shutting down — the
  launcher exited 0 and the teacher's file was never imported — so the endpoint is now
  disposed first in `StopAsync` and answers *the console is shutting down* while stopping,
  and the launcher starts its own console instead; the socket had been placed directly in a
  possibly world-writable temp directory under a predictable name, where another local user
  could pre-create the path and receive the teacher's document paths, so the private
  directory is verified (a real directory, not a symbolic link, mode 0700) by the server
  before it binds and by every client before it connects, and accepted connections are
  checked with `LOCAL_PEERCRED` (macOS) or `SO_PEERCRED` (Linux), falling back to that
  directory as the guard when the kernel answers neither; a path longer than the platform's
  `sun_path` limit threw an unhandled exception that could leave the console running with
  no window at all, and is now reported as an unusable endpoint that never costs the console
  its startup; document validation now requires an ordinary file within
  `Defaults.ConsoleDocumentMaxBytes` (8 MiB), because a FIFO or a huge file read on the UI
  thread froze the console permanently; imports from the drop target, *Add labs…* and
  forwarded launches are serialised — one pump in `App`, a counted busy flag for the
  chooser's nested file flows — because two concurrent imports could stack two passphrase
  prompts over the same profile store; and `Main` returns
  the lifetime's exit code, so a refused launch exits non-zero. Tests: 788 (625 Shared + 163
  Console), 16 of them for the single instance — hostile input (over-long lines, malformed
  and non-UTF-8 JSON, null and blank entries, 200 and 5000 paths), a silent client that must
  not block the accept loop, eight concurrent launches, the socket directory's mode with a
  planted symlink, the shutdown window, batch serialisation, the argv echo filter, and FIFO,
  symlink-to-FIFO, oversize, empty and directory documents; plus a real child process that
  forwards and exits 0 (and 1 when nothing serves), and a genuine stale `AF_UNIX` node
  recovered. *Manual check on the owner's Mac, on a copy of the data directory*: the socket
  appeared as 0600 inside a 0700 directory; a second launch forwarded a lab file and exited
  0 while the running console logged the hand-over and showed the import result; a bare
  second launch delivered an empty batch and brought the window forward; and after a
  `kill -9` the next launch exited immediately instead of hanging, while a restart removed
  the stale socket and served again. Open items: the Windows named-pipe path and the Linux
  `SO_PEERCRED` path are compile- and logic-checked only, with no Windows or Linux run (they
  belong to portion 7's matrix); the shutdown-window refusal has an automated test but could
  not be reproduced in the real GUI, because there is no way to ask a running console to
  quit from the shell; and after a refusal the forwarding launch may briefly report that a
  console is already running while the departing one still holds the lock for up to ten
  seconds — the file is surfaced to the teacher rather than lost.
- *Portion 7 (built, reviewed and fixed 2026-09-09, on the Mac).* Desktop packaging and the
  LAN-access banner (`D-59` items 1–4; INSTALLER, *Teacher console installation*). One file
  per platform, no administrator, no runtime download, and nothing that installs a service,
  touches an account or adds an antivirus exclusion. **Windows**:
  `src/LabControl.ConsoleSetup/` is a single-file, self-contained `asInvoker` executable
  (`win-x64` for the teacher machines, `win-arm64` for the VM) carrying the published
  console as an embedded zip. It installs into
  `%LOCALAPPDATA%\Programs\LabControl\Console\`, writes the Start-menu `.lnk`
  (`IShellLinkW` via CsWin32), the optional desktop shortcut, the HKCU Installed-apps entry
  and the `LabControl.LabFile` (`.lclab`) / `LabControl.Backup` (`.lcbak`) ProgIds with a
  quoted `"%1"`, adds itself to `OpenWithProgids` always and takes the default only where
  Windows holds no `UserChoice`, and changes no firewall rule at install time. The whole run
  is data (`ConsoleInstallPlan`), so `--dry-run` prints exactly the steps the same run would
  execute and every step is idempotent; a build without the payload refuses to install and
  names `tools/package-windows.sh`. `--uninstall` removes what `installed-files.txt` lists
  plus its keys and shortcuts, and its firewall rules only when that run is already
  elevated, and keeps the labs; `--remove-data` is a
  separate switch that makes the teacher type REMOVE and refuses with redirected input;
  `--firewall` is the one elevated step. Everything is appended to
  `%LOCALAPPDATA%\LabControl\console-setup.log`, and the installer never opens the data
  directory beyond testing `console.lock`. **The banner**: `NetworkReadiness` over
  `WindowsConsoleFirewall` reads the firewall read-only when a lab activates and, when
  inbound TCP 47800 or UDP 47801 is not open on the Private and Domain profiles, raises a
  non-blocking banner whose *Allow…* runs `--firewall` elevated and then re-reads the rules;
  a refusal, a missing helper or still-absent rules turn it into the two exact `netsh` lines
  with *Check again*. **macOS**: `tools/package-mac.sh` builds `LabControl.app` with both
  document types over the exported UTIs `org.ontfk.labcontrol.lab`/`.backup`, signs it ad
  hoc, wraps it in a DMG with an Applications symlink and prints the expected `spctl`
  refusal instead of hiding it (`D-15`). **Linux**: `tools/package-linux.sh` builds a
  tarball with per-user `install.sh`/`uninstall.sh` into `~/.local/opt/labcontrol/console/`,
  the `.desktop` entry, MIME XML, icons and the Ubuntu 22.04/24.04 prerequisite list.
  `tools/package-all.sh` runs the three into `artifacts/package/`; `tools/publish-all.sh` is
  unchanged. What the review changed: `installed-files.txt` is untrusted input, so a line
  that is rooted, names a drive, climbs out with `..` or lands outside the install directory
  is refused and logged rather than deleted, and the same guard resolves every payload
  entry; no usable manifest falls back to the two program files instead of reporting a clean
  removal; `--finish-removal` accepts only this layout's own install directory, and the
  temporary copy is told the uninstaller's process id, waits for it, retries, deletes itself
  and is swept by later runs; a registry key pointing at another installation is preserved
  and reported instead of throwing the uninstall away half-finished; the lock check also
  asks whether a console process is running, which a console started with `--data` elsewhere
  would not reveal; **firewall coverage became honest** — a rule scoped to another program
  or to a Windows service, cut down to one address range or to one interface type opens
  nothing for this console, and one matching block rule (judged generously) vetoes every
  allow rule; the banner believes the rules rather than the helper's exit code, and neither
  fire-and-forget task can fault; only a step that provably wrote nothing may claim nothing
  changed; uninstall never asks for an administrator — unless that run is already elevated
  the rules stay and the `netsh … delete rule` lines are printed, and a rule name shared
  with another group is preserved for review, because Windows deletes by name; a rule
  Windows will not describe is skipped rather than turning a healthy machine into *Failed*;
  a root Windows will not name refuses the run instead of writing blind; a payload-less
  build may still print its plan; `--remove-data` removes the installer log too; the copied
  uninstaller loses its `Zone.Identifier`; `install.sh` rewrites `Exec` and `TryExec` to the
  absolute launcher path, because `~/.local/bin` joins the PATH only at the next login and
  an unresolvable `TryExec` hides the entry; and the Windows setup manifest carries the
  build's own version, generated into `obj/` instead of a frozen `1.0.0.0`. Tests: 888 at
  the portion's own head (711 Shared + 177 Console) and **936 after merging with portions 4
  and 6** (726 Shared + 210 Console), 13 of them macOS bundle tests that skip unless
  `tools/package-mac.sh` has been run. *On the owner's Mac*: the full build; all three
  packages produced, the installer assembly really carrying its ~59 MB payload; the DMG
  built, its `Info.plist` keys and ad-hoc signature verified, the Gatekeeper refusal
  observed as documented, and the bundle launched once against a copy of a data directory;
  the Linux tarball installed and uninstalled into a throwaway `HOME`, with the generated
  `.desktop` entry inspected (absolute `Exec`/`TryExec`, an executable launcher, nothing
  left behind). ***On Windows, for the first time, 2026-09-09*** — the isolated UTM clone
  *M4 isolated native tests 2026-09-08*: Windows 11 Pro 10.0.26200 ARM64, guest `PC-27`,
  one host-only NIC, `LabControl-Console-0.1.4-win-arm64-Setup.exe` hash-compared on host
  and guest before every run, the clone stopped and left in its pre-drill state afterwards.
  The clone has no interactive session and takes no password, so every unelevated run was
  made as `LOCAL SERVICE` — a non-administrator with a real profile — through a scheduled
  task, and the one elevated step ran as `SYSTEM`. **Four defects, all fixed and merged
  (`a7fbe33`):** (1) `app.manifest` carried a double hyphen inside an XML comment, which
  XML forbids, so Windows refused to build the activation context and the installer would
  not start at all (*"side-by-side configuration is incorrect"*, an invalid-manifest-XML
  entry in the event log) — nothing in the console installer had ever run on Windows before
  that day, and the failure is an OS refusal, not a compile error; (2)
  `ConsoleFirewallRuleSpec.Profiles` was 6, but `NET_FW_PROFILE_TYPE2` numbers the profiles
  Domain 1, Private 2, Public 4, so the installer opened the **Public** profile it says it
  never requests, left the domain closed, and — the same constant deciding coverage —
  rejected a genuine private-and-domain rule as missing; at 3 the rule reads back as
  *Domain, Private* and the banner reports the port allowed; (3) the printed `netsh` line
  carried `group="…"`, which `add rule` has no argument for and refuses outright, so the
  fallback created nothing — removed, and the accepted price is that a hand-made rule has
  no group, so uninstall leaves it for review; (4) the uninstaller's temporary copy never
  deleted itself, because the command line was built with `ArgumentList`, whose C-runtime
  `\"` escaping `cmd.exe` rejects, leaving a 148 MB executable in the temp directory — one
  argument string now, and no copies remain. **Verified with the rebuilt installer**: that
  it starts at all; the whole ordered plan and a dry run that changes nothing; a clean
  per-user install by a non-administrator into the per-user programs directory; the
  Start-menu shortcut, and that it launches the console; the Installed-apps entry with its
  name, version, publisher and uninstall command; both file-type registrations and their
  open commands; a byte-identical machine on a repeat run; the refusal to install while a
  console holds its lock; document forwarding through the registered command into a running
  console's import flow for both file types, including a path with spaces and Ukrainian
  characters — which exercises portion 6's Windows named pipe; the real Windows Firewall
  rules and their profiles, the banner moving from missing to allowed, a rule belonging to
  another program correctly not counting as coverage, and uninstall removing only its own
  rule while leaving a foreign one for review; the unelevated uninstall's firewall policy;
  the temporary-copy hand-over including its self-deletion; all three answers to
  `--remove-data`, including its refusal with redirected input and its insistence on the
  exact word; and a DPAPI-sealed lab key still opening after the application was replaced.
  **Still unverified**: Explorer's own double-click (the registration, the forwarding and
  the import are proven, the shell's resolution step is not — it needs a desktop session);
  a real elevation consent prompt answered yes or no by a teacher (no interactive session,
  so Windows cannot show one); anything on `win-x64` (only the ARM64 package was built and
  run); the installer under an ordinary interactive user profile rather than a service
  account; the unsigned-download warning; the Linux `SO_PEERCRED` path of the single
  instance (portion 6); and a real GNOME or KDE application menu. Those stay in portion 7's
  manual matrix and must not be reported as passing until they are run. **Rough edges for a
  later pass**, none of them wrong behaviour: the dry run prints its plan header twice; a
  repeat install reports steps as *written* where the code's comments promise *already*, so
  the machine is idempotent but the wording is not; and uninstall leaves the now-empty
  parent programs directory behind.
- *Portion 8 (built and reviewed 2026-09-09, on the Mac).* The acceptance drills, and
  the six gaps an adversarial audit of every M5 acceptance criterion found (`D-68`).
  **The file picker could not take a mixed selection**: `LabImports` registered one
  filter per extension and both the macOS panel and the Windows common dialog apply
  exactly one filter at a time, so *Add labs…* opened with only backups selectable;
  `LabImports.Filters` now puts one combined filter over every extension in
  `Defaults.ConsoleDocumentExtensions` first and keeps the per-type filters after it.
  **Authorization was not batched**: one request per lab meant a teacher with three
  rooms made three selections and three save dialogs, so
  `DeviceAccess.NeedingAuthorization`/`WriteRequests` write a request for every lab that
  needs authorization or renewal into one chosen folder, deduplicating the file name
  when two rooms share a display name, writing nothing for a lab that needs nothing and
  failing one lab alone exactly like one file of an import batch; the chooser's
  *Authorize all (N)…* (`AuthorizeAllCommand`, `PendingAuthorizations`) is that one
  gesture and the single-row action stays. **Three drills the criteria name were
  added**: three independent labs of 30 PCs each imported and authorized in one
  workflow, with the source files deleted before a restart to prove they are no longer
  needed (`AcceptanceDrillTests`); switching while a screen streams and a PC is
  controlled, asserting that no frame, no released buffer and no keystroke reaches the
  wrong room; and the departure flow itself, which leaves at once when nothing runs,
  stays when asked, and waits and then leaves when asked (both in `LabSwitchTests`).
  **Quitting the application bypassed the departure report entirely**: the report, the
  wait and the choices moved into `Services/DepartureFlow.cs` (`IDeparturePrompt`,
  `MayLeaveAsync`, `MayQuitAsync`, `DepartureChoice`), which *Disconnect*, the main
  window's close and `App.QuitAsync` now all ask, with a guard so a second quit cannot
  open a second report and a catch so a dialog that cannot be shown never traps the
  teacher in an application that will not quit. **A PC refused across many switches came
  back only on the 30-second reconnect ceiling** instead of the 15-second target:
  `BeaconGate` now records when a verified beacon of the agent's own lab last arrived
  from each endpoint, and a serving gap of at least the new `Defaults.BeaconResumeGap`
  (two beacon intervals) re-arms a one-shot permission to step over the wait a failed
  dial imposed. The exponential escalation is deliberately not reset, and a console
  beaconing without a pause produces no gap and so earns at most one extra dial.
  Measured in the sixty-switch drill on the same machine: **30.3 s to relink thirty PCs
  without it, 2.7 s with it**; what the step-over is worth on its own is asserted
  deterministically in `BeaconGateTests`. **Agent events were not bound to the
  delivering console** the way results and progress are: `AgentLink.Report` takes a
  `forInstance` and `ReportForJob` reads the deliverer out of the ledger, so an event
  about a console's work reaches that console and waits for it, while machine events
  stay unbound on purpose — a dead helper or a broken permission must reach whoever is
  in the room — and the capacity line about dropped results (`job.result_dropped`) stays
  unbound for the same reason. Four audit claims were checked and all four hold, two of
  them previously untested and now covered: re-import preserves this device's identity,
  the same room on two devices does not clone one private identity, a backup re-imported
  over an existing administrator profile does not duplicate the room, and the same lab
  id arriving with different trust is refused for `.lcbak` as it already was for
  `.lclab`. Honest limit: the Avalonia shutdown hook itself is two lines calling that
  flow and is not covered by a headless test, because the harness never gives the
  application a classic desktop lifetime; it needs a manual quit check.
- *Portion 5 security fixes (built 2026-09-09 on the Mac, merged into main the same
  day).* An adversarial review of the built portion reproduced two blockers; both are
  fixed, with the hardening around them (`D-58`, amended). **A withdrawn console could
  still seize the room, repeatedly**: the take-over decision rested on the certificate
  authority's endorsement alone, so the instance-level withdrawal was enforced only at
  the TLS handshake — which happens after the agent has already dropped its link — and
  each press carried a fresh `take` value, defeating the honour-once rule. `BeaconGate`
  now takes the agent's live `RevocationSet` (the same object `AgentLink` merges into)
  through a required three-argument constructor, so no call site can skip it, and
  refuses a revoked instance's beacon immediately after signature verification, ahead of
  both the take-over and the dial branch. The console mirrors it: a withdrawn instance's
  beacon is dropped, `ForgetOtherConsole` removes the machine from the other-console
  list the moment access is withdrawn, and it can never be named as a holder. **A PC
  that merely died during the take-over window was credited to the taker**: any linked
  PC whose stream ended inside the window was attributed, so a student shutdown, a Wi-Fi
  blip or a crash all counted as evidence — exactly the presumption the ownership work
  exists to delete. Attribution is now the agent's own positive report: on a take-over
  the agent writes the fixed-code `link.taken_over` departure notice naming the taker
  (`Shared/Link/DepartureNotice.cs`, an ordinary `Event`, no `.proto` change),
  half-closes the request stream rather than resetting it so the notice is delivered in
  order, and cuts the link anyway after `Defaults.DepartureNoticeGrace` (two seconds);
  `LabSession.NoteDeparture` attributes only on that report, and only to an instance
  this console has itself heard beaconing and has not withdrawn. Also fixed: the *took
  over the lab at …* wording never expired and now shows only while the press this
  console saw is younger than the observations it produced, and is cleared outright when
  this console takes over; the *arrived after the link* condition could not fire in
  production because the arrival clock was read inside the handler, so `BeaconListener`
  stamps each datagram once in its receive loop before the fan-out and
  `AgentLink.OfferBeacon` carries that stamp; the claim that a machine minutes out of
  step could still move the room was false — past `Defaults.BeaconMaxSkew` such a
  console is not merely unable to take over, it is invisible to the whole lab — so the
  comment is corrected and a beacon rejected for a stale timestamp now raises the
  rate-limited `console.clock_skew` warning saying the two consoles cannot see each
  other until a clock is set; honoured take tokens are pruned once they can never be
  honoured again; the freshness check clamps the future side to the beacon skew, so a
  lab file written while the clock was ahead cannot count as fresh; the two fields
  behind an ownership claim are read as one pair under one lock; and a PC being woken
  says so instead of reading as held elsewhere. Tests: after both merges **main stands
  at 984 (755 Shared + 229 Console)**, 13 of them macOS bundle tests that skip unless
  `tools/package-mac.sh` has been run, and the full suite was run twice on the merged
  tree with no failures. Still open: this portion has not been run on the Windows VM,
  where two consoles with skewed clocks must move a real agent.

**Not in scope.** Simultaneous control of several labs by one console, a shared live view
between teachers, an always-on server/cloud, automatic timetable scheduling, moving PCs
between labs on each lesson, grading or an exam-content platform.

**Rough size.** Large. Desktop packaging, bulk import and the chooser are the visible
parts; isolating session lifecycle, access and recovery is the main work. Build
profiles/import first, switching second, then access/handover hardening, desktop
packaging and the multi-device acceptance drills.

---

## M6 — Classroom control: broadcast, lock, exam mode

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
- Extend M5's lab-departure flow to active broadcast, lock and exam policies: explain
  what ends or continues under its bounded lifetime, preserve work collection ownership,
  and never leave an inactive lab's streams or control session running.
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

### Settled before implementation (owner decisions 2026-09-09, corrected plan 2026-09-16)

The first M6 plan and its adversarial review were settled with the owner on 2026-09-09 and
the corrected constraints recovered on 2026-09-16. Everything below is decided and binding
for the implementation; do not re-open it without the owner. Where it is more specific than
the deliverables above — the whitelist enforced in the **service** rather than the helper,
profile reset through `DeleteProfile` rather than `Win32_UserProfile.Delete` — this
subsection wins, and ARCHITECTURE §6 is amended when portion 5 lands. None of it is
implemented yet.

#### Owner decisions (2026-09-09, binding)

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

#### Milestone shape

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

Decision numbers: M6 reserves **D-69 … D-79** (`D-61`…`D-68` are taken by the M4
physical-lab fixes, localization and the M5 audit); do not reuse them:

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

#### §0 — the policy journal (corrected, binding)

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

#### Portion 1 specifics (corrected)

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

#### Portion 2 specifics (corrected)

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

#### Portion 3 specifics (corrected)

- Windows Firewall: block beats allow → a whitelist must set the profile default outbound
  action; the program-scoped `agent.exe` allow rule is what keeps every PC online under
  *blocked* (LocalSubnet alone is not enough: MacBook on Wi-Fi vs wired PCs).
- Restore order: default outbound → Allow first, exemption rule removed last.
- Presets/policies persisted in the console go into `BackupPayload` + `UpgradeNested`.

#### Portion 4 specifics (corrected)

- Whitelist in the service; never-touch set hard-coded (session, shell, OS, LabControl);
  the rename hole (a renamed exe passes) stated in one sentence in the UI.
- Exam's internet policy hard limit = `min(ExamHardLimit, exam's own)`.
- A standalone policy set for the lesson survives an exam that starts and ends inside it.

#### Portion 6 specifics

- Fail-safe drill needs an explicitly short hard limit plus a separate clamp test — the
  ROADMAP must say so. Drill 4 split into `sc stop` / kill / power-loss.
- New drill: corrupt/half-written `policy.json` → PC still restores.

#### Known pre-existing test failure (not M6's)

`LabSwitchTests.Twenty_rounds_of_switching_leak_neither_sessions_nor_threads_nor_handles`
fails on clean `main` (bdc5b6f) under load: `:333` fixed 15 s for 30 PCs to link, `:371`
`ninetieth < 2000` ms (measured 2034). Out of M6 scope; flagged to the owner.

**Rough size.** Large. The program whitelist, the firewall switch and the loopback
resolver are the risky parts.

---

## M7 — Software catalog, localization, polish

**Goal.** Everything that makes the system pleasant to own for one person with no IT
department. The catalog ships **empty** — the deliverable is the ease of filling it.

**Deliverables**

- **Add package wizard**: point the console at any local `.exe` / `.msi`; it suggests
  silent arguments for known installer families (NSIS, Inno, InstallShield, MSI),
  computes the hash, proposes a `detect` rule, writes the YAML, and offers a **test
  install on one PC** before the fleet.
- `install_package` job: push over the LAN, run silently, verify `detect`, report per PC,
  "Install on all missing".
- Complete Ukrainian, English and Russian localization from the resource seam created in
  M0. This covers the teacher console, Windows Setup/removal, stock student-facing text
  and generated USB instructions; protocol identifiers, diagnostic codes and developer
  logs remain stable English (`D-67`).
- A console language selector offers `Українська`, `English` and `Русский`, persists per
  user and can override the OS language. Before a preference exists, use a supported OS
  display language and otherwise fall back to English. Setup follows the supported Windows
  display language, and generated USB instructions follow the console's selected language.
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
- Every product-facing stock string comes from the resource files. Switching among
  Ukrainian, English and Russian changes the entire console and survives a restart; each
  language is checked for missing keys, clipped controls and broken formatting.
- Setup/removal is exercised in all three languages, and the USB builder emits each of the
  three instruction variants with English fallback for any unsupported culture.
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

- [x] Router model; is the Wi-Fi on the **same subnet and broadcast domain** as the wired
      hub? Is AP isolation / guest mode off? — Yes: MacBook `192.168.0.168/24` on Wi-Fi,
      PCs `192.168.0.x` on the wire, one broadcast domain (2026-09-08).
- [x] Do UDP broadcasts from the MacBook reach the wired PCs? — Yes: the console's beacon
      is found by every installed PC and they enrol and link within seconds (2026-09-08).
- [ ] Is it a switch or a real hub? Bandwidth headroom for 14 (later 30) video streams.
- [ ] DHCP lease behaviour; do PCs keep their IPs?
- [ ] Per PC: BIOS/UEFI "Wake on LAN" / "Power on by PCI-E" enabled. — Not yet checked;
      pointless until the NIC driver below is replaced.
- [x] NIC models across the PCs (Realtek/Intel differences change the WoL settings). —
      All seen so far: Realtek PCIe GbE (RTL8168) on ASUS boards with the Microsoft
      in-box driver 9.1.410.2015, which exposes no Wake-on-LAN settings at all (`D-66`).
      The vendor driver is required before any wake test.
- [ ] Windows edition and build (Home vs Pro changes some policy options).
- [ ] Local administrator account name and password, per PC or common.
- [x] Antivirus in use — Defender only, or a third-party product that Setup cannot
      configure? — Defender only on the five installed PCs; its provider needed a retry
      on one of them (`D-64` addendum).
- [ ] Do the PCs have internet at all, and is it permanent or occasional?
- [ ] Screen resolutions (drives the mosaic tile sizing).
- [ ] Which folder should be offered as the default when setting up *collect work*?

Record the answers as an update to `D-10` in `docs/DECISIONS.md`.

## Answered — settled requirements

Kept here so the reasoning is not lost.

| Question | Answer | Where it landed |
|---|---|---|
| Is the teacher machine fixed? | No. Several teacher machines take turns (MacBook some days, the Windows desk PC on others); one drives the lab at a time; the software must also move to other labs; needs a security scheme for replacement | `D-13`, `D-21`, ARCHITECTURE §3, M1 |
| Can a teacher use different rooms during the day? | Yes. Bulk-import lab files once, then choose one active lab; room rosters are mostly stable, teacher/device allocation changes | `D-53`, ARCHITECTURE §3.9, M5 |
| Can administrators switch using backups? | Yes. Add `.lcbak` files directly, including mixed batches, to the same lab list; preserve administrator authority with key unlock only for privileged actions | `D-54`, M5 |
| How is the teacher console installed? | Simple offline desktop packages/installers for Windows, macOS and Linux: app files, launchers and lab/backup file registration; no student service or preparation | `D-54`, INSTALLER teacher-console section, M5 |
| Code signing? | None available; accept warnings and add an antivirus exclusion | `D-15`, INSTALLER step 6a |
| Which packages on day one? | None. The catalog ships empty; what matters is a comfortable way to add installers later | `D-16` context, M7 |
| Do installers need downloading? | No. The lab's software is already installed; testing will use an arbitrary program later | M7 |
| Exam mode? | Yes, and every restriction is an independent switch | `D-16`, ARCHITECTURE §6.1, M6 |
| Lab size? | Design and load-test for up to 30 PCs | `D-17` |
| Where to test the real agent? | Both a Windows VM (fast, destructive tests) and `PC-00` in the lab (WoL, capture, performance) | M2 |
| Can someone else open the lab key? | Yes — multiple named key holders, each with their own passphrase | `D-13`, ARCHITECTURE §3.2, M1 |
| Two consoles at once? | Not a mode, but it must not break: two live consoles split the room, never share a PC, and either can *Take over*. A shared room is out of scope | `D-21`, ARCHITECTURE §3.7 |
| What does *collect work* take? | One dedicated folder, chosen when the exam is set up — not the whole desktop | `D-16`, M6 |
| How precise is the whitelist? | Executable names only; no following of child processes | `D-16`, M6 |
| Internet control outside exams? | Yes: *open* / *whitelist* (hostnames, wildcards) / *blocked*, standalone with a duration and a hard limit, and the same thing as the exam's internet switch | `D-22`, ARCHITECTURE §6.2, M6 |
| Sending files to students? | Yes — but the file channel exists first for installing software without a USB walk, and that flow stays untouched; handouts land in `Materials` on the student desktop | `D-23`, ARCHITECTURE §6, M4 |
| How does LabControl update itself? | Side-by-side version directories, a bundle signed by the lab key, 10-minute probation and a rollback driven from outside the agent; the console is updated by replacing its binary | `D-19`, ARCHITECTURE §7, M2 + M4 |
| How do old agents and new consoles coexist? | A frozen protocol subset (`Hello`, `Heartbeat`, `Job{self_update}`, `JobResult`); an old agent is never refused, only marked outdated | `D-19`, PROTOCOL *Versioning*, M1 |

## Open questions for the owner

None outstanding. Every question raised so far is answered and recorded above; the
remaining unknowns are physical facts about the room, in the on-site checklist.

## Out of scope for v1

Simultaneous multi-lab control from one console (saved labs and sequential switching
are M5), two consoles *sharing* one lab at the same time
(alternating teacher machines are in scope, `D-21`), cloud relay, mobile console, Linux/macOS student agents, grading or LMS integration,
student-initiated help requests, session recording.
