# Architecture

## 1. Physical picture

```
                 Wi-Fi                        wired (hub/switch)
  MacBook  ─────────────┐  ┌────────┐  ┌──────────────────────────────┐
  (Console)             ├──┤ Router ├──┤ Hub ── PC-01 … PC-14 (Windows)│
  or a Windows PC ──────┘  └────────┘  └──────────────────────────────┘
```

The first lab has **14** PCs, but nothing may assume that number: the software is meant
to move to other labs, so it is designed and load-tested for **up to 30** student PCs
and one console. The count comes from `lab.json`, never from a constant.

Assumptions to verify on site before M1 (put results in DECISIONS.md D-10):
- Wi-Fi and the wired hub are the **same IP subnet / same L2 broadcast domain**
  (no "AP isolation" / guest network on the router). Required for UDP discovery
  broadcasts and for Wake-on-LAN magic packets sent from the Mac.
- PC BIOS/UEFI has "Wake on LAN" / "Power on by PCI-E" enabled. The installer configures
  the Windows side, but BIOS is a one-time manual check per PC.
- Router hands out DHCP; PCs may change IP → the console identifies PCs by
  **agent ID + MAC**, never by IP.

## 2. Components

| Component | Runs on | Runs as | Purpose |
|---|---|---|---|
| **Console** (`LabControl.Console`) | teacher machine (macOS/Windows/Linux) | the teacher | Avalonia UI + embedded gRPC server (Kestrel). Holds the lab key, issues certificates, and is the source of truth for `lab.json`, the package catalog and the scripts. Replaceable — see §3.6. |
| **Agent** (`LabControl.Agent`) | each student PC | Windows service, `LocalSystem` | Outbound gRPC client to the console. Privileged operations: power, run scripts, install packages, file transfer, profile reset, self-update. Supervises the Session helper. |
| **Session helper** (`LabControl.Agent.Session`) | each student PC | spawned by the Agent into the active interactive session (the service's own SYSTEM token duplicated, its session id rewritten to the console session, `CreateProcessAsUser` on `winsta0\default` — D-30) | Screen capture (DXGI Desktop Duplication, GDI fallback — D-35), input injection (`SendInput`), full-screen overlay window for lock/broadcast. Per-monitor DPI aware, follows the input desktop (lock screen, UAC). Talks to the Agent over a local named pipe. Restarted automatically on logon/logoff/crash. |
| **Setup** (`LabControl.Setup`) | each student PC, once | elevated (admin) | The USB installer. See INSTALLER.md. |
| **FakeAgent** (`LabControl.FakeAgent`) | dev machine | user | Simulates N agents (synthetic screens, power state, fake command results) so the console can be built on macOS. |

Why two processes on the PC: a session-0 service cannot see the interactive desktop
(no screen, no input). The classic pattern is service + per-session helper. Keeping the
helper as SYSTEM (not as `student`) lets it capture the UAC secure desktop and the
lock screen, and prevents the student from killing it from Task Manager.

The helper runs whenever there is a console session at all — at the logon screen, at the
lock screen, with a student at the desk — and is restarted into the current session on
logon and logoff, within a few seconds after a crash, and slowly (with an event to the
console) when it keeps dying. The service owns the named pipe and is the only party the
helper will talk to; the helper reports which desktop has the input (`Default` versus
`Winlogon`) and the screen size, and exits as soon as the pipe closes. Supervision details
and the reasons behind them are `D-30`; the wire format is in PROTOCOL.md.

## 3. Trust model and connection

The teacher machine is **replaceable**: it may die, be stolen, or be swapped for a
Windows PC, and the same software must be usable in another college's lab. Therefore
the identity of *the lab* is deliberately separated from the identity of *the computer
that happens to be running the console today*. Nothing in the design may require
walking to the student PCs again after a change of teacher machine.

### 3.1 Three separate identities

| Identity | Created | Lives | Proves |
|---|---|---|---|
| **Lab key** — a private certificate authority (ECDSA P-256) | once, on the console's first run | `lab-key.lck`, encrypted; backed up wherever the teacher wants | "this is lab *X*" |
| **Console instance** — leaf certificate + key signed by the lab key | when a teacher machine is set up or migrated | that machine only | "I am a legitimate console of lab *X*" |
| **Agent** — keypair + certificate signed by the lab key | at enrollment, on the PC itself | that PC only | "I am PC-07 of lab *X*" |

The lab key is the only thing that must survive; everything else can be reissued from it.

### 3.2 How the lab key is protected

A random 256-bit master key encrypts the CA private key (AES-256-GCM). The master key
itself is never stored directly — only as a set of **wrappings**, any one of which opens
it:

- one per **key holder**: a named person and their passphrase (PBKDF2-HMAC-SHA256,
  ≥ 600 000 iterations, per-holder salt);
- one **recovery code** — 128 random bits shown once as printable groups, meant for a
  wallet, a safe, or a home drawer.

More than one holder is supported deliberately: the owner asked for a colleague to be
able to open the lab if he is ill on an exam day. Holders are added and removed from the
console (adding one needs an existing holder's passphrase; removing one drops that
holder's wrapping and does not require the removed person's cooperation — note that a
removed holder who kept an *old copy* of the file can still open that old copy, which is
why revocation of what the key has signed, not re-wrapping, is the answer to a departed
colleague). Every
holder is equal — there is no "owner" wrapping that outranks the others — and the list of
holder *names* is stored in the clear so you can see who can open the lab without opening
it yourself.

So a forgotten passphrase is recoverable, a lost recovery sheet is survivable, an ill
teacher does not stop an exam, and the file alone is useless to whoever finds it. The lab key is **not** kept on the USB
stick — the USB carries no secret at all (§3.4).

Daily use does not ask for the passphrase: it is needed only when the lab key itself is
unlocked — first run, minting a console instance, migrating, revoking, **enrolling PCs**
and **renewing their certificates** (§3.8, `D-24`, `D-25`). The last two are the ones
that happen on a schedule rather than in a crisis: the console says when it needs the key
and refuses politely until it has it, and the agents simply try again. Once unlocked, the
key stays in memory for 15 minutes after it was last used, or until the teacher presses
*Lock*; closing the console always forgets it (`D-26`). The console
instance's own private key is protected at rest by the operating system (macOS Keychain,
Windows DPAPI, Linux libsecret with an encrypted-file fallback).

### 3.3 What a student PC knows

Only: `lab_id`, the **public** CA certificate (pinned), its own keypair and certificate,
its number and MAC. No lab secret, no passphrase, no shared symmetric key, nothing about
any other PC. A student who fully compromises a PC — including pulling its disk — can
impersonate *that one PC* and nothing else, and the console can revoke it.

### 3.4 Connection

- **Agents dial out** to the console's gRPC server on `47800/tcp`. Rationale unchanged:
  the console moves, the PCs do not, and outbound connections need no inbound firewall
  rule. The one inbound thing a PC receives is the discovery beacon (UDP 47801, below and PROTOCOL.md),
  and its firewall rule is by port, not by program path — a per-program rule stops
  matching after a self-update moves `agent.exe` (`D-33` item 8).
- **Mutual TLS.** The agent validates the console's leaf certificate against the pinned
  CA; the console validates the agent's certificate against the same CA and checks it
  against the revocation list. Neither side trusts a public CA, an IP address or a
  hostname — only the lab key.
- **Discovery beacon**, UDP `47801`, every 2 s. It is verifiable offline without any
  shared secret: it carries the console instance's public key plus the CA's signature
  over that public key (the *endorsement*), and is itself signed by the instance key.
  An agent checks endorsement → signature → timestamp, then connects. Agents ignore
  beacons while connected, and rate-limit connection attempts, so a flood of forged
  beacons costs a few TCP handshakes and nothing else.
- **Enrolment and renewal need the lab key** (`D-24`, `D-25`): a console whose lab key
  is locked answers `Enroll` and `Renew` with a plain refusal, not an error; the agent
  keeps its current state and asks again with the same backoff it uses for reconnects.
  So a freshly installed PC waits, enrolled by nobody, until the teacher opens *Enrol PCs*
  and types the passphrase once — and 14 PCs then enrol within a few seconds of each
  other.
- **Fallback** for networks that drop broadcasts: `console_host` pinned in `agent.json`.
  The console also sends a loopback copy of every beacon, for a `FakeAgent` on its own
  computer (`D-27`).
- **One long-lived bidirectional stream per agent** (`AgentLink`) carries commands and
  events; separate streaming RPCs carry video and files so a large transfer never
  delays a "shutdown". See PROTOCOL.md.

### 3.5 Enrollment — the USB stick carries no secret

The USB payload contains the **public** CA certificate and a batch of **single-use
enrollment codes** generated by the console. At install the agent generates its own
keypair on the PC; at first connection it presents an unused code and a certificate
signing request; the console issues the agent certificate, burns the code, and records
the machine in `lab.json`.

A USB stick that a student finds therefore allows, at worst, enrolling a bogus PC —
which appears in the console as an unexpected machine and is removed with one click.
It does not decrypt traffic, does not impersonate the console, and does not expose the
lab key. (Contrast with the earlier design, where the stick carried the lab secret and
was itself a credential.)

### 3.6 Replacing the teacher machine

1. On the new machine (macOS, Windows or Linux): **Import lab key** — the backup file
   plus the passphrase, or the recovery code.
2. Give the instance a name (`MacBook-2026`, `Lab PC`), so logs say which console did
   what.
3. The console mints itself a new leaf certificate from the lab key, restores the
   machine list, package catalog, room layout and outstanding enrollment codes from the
   same backup, and starts beaconing. PCs installed from a stick the old machine wrote,
   but not yet enrolled, enrol here (`D-28`).
4. Every agent sees the new beacon, validates it against the CA it has pinned since
   installation, and connects. **No student PC is touched.**
5. Optionally revoke the old instance — only if it is lost or stolen (§3.7.3). If the old
   machine is simply the other teacher machine, leave it: both keep working, one at a
   time (§3.7). The revocation list is pushed to every agent when it connects, so a
   stolen laptop stops working as soon as the lab has seen the new console.

### 3.7 Several teacher machines, one at a time

Replacement is the disaster case. The everyday case is **alternation**: the owner drives
the lab from a MacBook on some days, a colleague drives it from the Windows PC at the
teacher's desk on others, and both machines keep their console installed permanently.
This is the baseline the design is built for (`D-21`). Nothing here needs a second
installation on any student PC.

What the machines share and what each keeps to itself:

| | Where it lives | How it reaches the other machine |
|---|---|---|
| Lab key, key holders, recovery code | `lab-key.lck` on every teacher machine | imported once from the backup (§3.6 steps 1–3); holders added later travel with the next backup |
| Console instance (name, leaf certificate, key) | that machine only | never — each machine mints its own, and that is the point |
| Machine list (`machines[]`) | `lab.json` on each machine | **self-healing**: an agent that connects with a valid lab-issued certificate and is not in this console's list is added from its `Hello` (number, MAC, serial); a console never has to be told about a PC twice. **The PC number is the identity** (`D-25`): a PC that arrives with a number another record holds is that PC reinstalled, and the old record is replaced, with an event saying so. The only way out of the list is *Remove from lab*, which revokes the PC's certificate first (`D-28`) — otherwise the PC would heal itself straight back |
| Room layout | `lab.json` on each machine | default layout is derived from PC numbers, so an unseen list still looks right; a hand-arranged layout travels only with a backup |
| Revocation list | `lab.json` on each machine **and** every agent | merged as a set (§3.7.3); a console learns from the first agent that connects what the other console revoked |
| Package catalog, cached installers | that machine only | export / import a backup, or copy the `packages/` directory; the console shows *catalog last changed on <instance>* so a stale copy is visible |
| Script library (`scripts.json`) | that machine only | inside the backup (`D-38`); seeded from the console's built-in scripts on a first run that has no backup |
| Logs, job results | that machine only | never |

The rule that follows from the table: **the lab's truth is the lab key plus what the agents
know; a console's `lab.json` is a cache**. That is what makes alternation cheap — the
second machine catches up by watching the agents connect, not by being told.

#### 3.7.1 Handover

Closing the console on machine A is the whole handover. Agents see the stream end and
immediately return to listening for beacons; machine B's beacon is endorsed by the same
CA, so within about 15 s every PC is on B. B shows the machines as they arrive; a PC that
was added on A last week appears on B by itself (self-healing list, above). Nothing needs
to be clicked on either side, and nobody needs the passphrase.

The console records every instance it sees beaconing, and every instance an agent reports
having been connected to, in `lab.json` as `instances[]` (id, name, first/last seen,
certificate serial). Settings lists them as *This machine* and *Other teacher machines*.

#### 3.7.2 Two consoles on the network at once — tolerated, not supported

Two teachers driving the same lab at the same time is not a use case (owner's decision,
`D-21`), and the owner expects it to essentially never happen. It is still not allowed to
break anything, because a MacBook left open in the staff room *is* a second live console.
The behaviour is defined and dull:

- Agents stay with the console they are connected to and ignore other beacons while
  linked (§3.4). Two live consoles therefore hold **disjoint** sets of PCs — whichever
  each PC happened to connect to first. No PC ever takes commands from two consoles.
- Each console sees the other's beacon and shows a **persistent, informational** banner:
  *"Lab PC (instance `…`) is also running this lab and holds 6 of 14 PCs."* The count is
  known because agents report their previous console in `Hello`, and the banner lists the
  missing PCs by number. This banner has **no revoke button**; revocation is a security
  action for a stolen machine, not a way to win an argument about who is teaching.
- The banner offers **Take over the lab**. For the next 30 s the console adds a `take`
  timestamp to its beacon. An agent linked to *another* instance that receives an endorsed
  beacon with a `take` newer than its current connection disconnects and dials the taker.
  The other console watches its PCs leave and shows *"MacBook-2026 took over the lab at
  10:32."* No arbitration, no locking, no shared state between consoles: the last person
  to press the button has the room, and every PC always answers to exactly one console.
  The `take` field only works for endorsed beacons, so it is no more forgeable than the
  beacon itself.
- Anything a console does to a PC it does not hold — lock, broadcast, exam mode — simply
  is not delivered; the tile shows *held by Lab PC* and the job stays pending. It is
  delivered if the PC later arrives, which is why *Take over* exists.

What is **not** built: a shared view of the room, two consoles both controlling one PC,
merging job queues, or any console-to-console channel. That is the "genuinely simultaneous
consoles" mode of §10, and it stays out of scope.

#### 3.7.3 Revocation is for theft only

Revoking an instance requires the passphrase (§3.2) and lives in *Settings → Other teacher
machines → Revoke…*, behind a confirmation that names the instance and states that it
will stop working until someone imports the backup on it again. It is the right answer to
a stolen laptop and to nothing else; the migration steps in §3.6 call it optional for that
reason. Revocation entries are signed by the lab key and carry a timestamp, so every
console and every agent keeps the **union** of all entries it has ever seen; a revocation
made on the Windows PC reaches the MacBook through the first agent that connects to it,
and a console cannot be tricked into accepting an entry the lab key did not sign.

Failure cases, from cheapest to worst:

| Situation | Cost |
|---|---|
| Teacher machine dies or is replaced; backup intact | ~10 minutes on the new machine |
| Teacher machine stolen | as above, plus revoke the stolen instance (§3.7.3) and power-cycle the lab so every agent picks up the revocation |
| Passphrase forgotten | unlock with the recovery code, then set a new passphrase |
| Recovery code lost, passphrase known | reprint a new recovery code from the console |
| Lab key file lost entirely | re-run `Setup.exe --rekey` on every PC (~30 s each; keeps the `student` account, the software and the settings) |

Because the last row is the only genuinely painful one, the console's first-run wizard
refuses to finish until the backup has actually been exported somewhere and the
recovery code has been acknowledged, and on every launch it checks that `lab-key.lck`
still matches the fingerprint recorded at the last export — a holder added or a recovery
code reprinted on *either* teacher machine changes the file, and a date alone cannot see
that (`D-25`). Closing the wizard early does not get around this: the lab is already on
disk, so the next launch asks for the passphrase and reopens the wizard at the step that
is missing — a fresh recovery code if the first was never acknowledged (the old one is
voided), otherwise the backup — and the main window stays shut until it is done.

### 3.8 Certificate lifetimes and renewal

| Certificate | Lifetime | Renewed how |
|---|---|---|
| lab authority | 20 years | not renewed; when it ends, the lab is re-keyed (`Setup.exe --rekey`, ~30 s per PC) |
| console instance | 1 year | re-minted from the lab key on that machine — the console asks for the passphrase at startup once fewer than 60 days remain |
| agent | 5 years | **over the existing link** (`AgentService.Renew`, `D-25`): once fewer than 60 days remain the agent sends a fresh CSR each time it connects; the console issues a new certificate for exactly the identity the agent proved with its current one, needs the lab key to sign, and until then answers with a refusal the agent shrugs off. Nobody visits the PC |

The console shows one banner — *"N certificates need renewing — unlock the lab key"* — for
both cases, so the yearly and five-yearly chores are the same gesture as enrolment. A
leaf never outlives the authority that signed it.

## 4. Data on the console

`~/.labcontrol/` (macOS/Linux) or `%APPDATA%\LabControl\` (Windows):

```
lab-key.lck       the lab certificate authority, encrypted (§3.2). The one file that
                  must survive. Never leaves this directory unencrypted.
instance.json     this console instance: name, leaf certificate, key reference
                  (OS keystore), issued/expires, fingerprint of lab-key.lck at last backup
lab.json          lab_id, room layout, revocation list (signed entries, §3.7.3),
                  instances[] (every teacher machine this lab has seen, §3.7.1), machines[]:
                  {id, number, name, mac, last_ip, last_seen, agent_version, cert, notes}
enrollment.json   outstanding single-use enrollment codes and which ones were burned
scripts.json      the script library (D-31, D-38): name, description, shell, run-as,
                  timeout and text per script; seeded from the console's built-in
                  scripts/library/ on the first run, inside the backup
packages/         package catalog: <name>.yaml + cached installer binaries
logs/             per-day console log + per-command result bundles
```

Every one of these files — and the backup archive — carries a `schema_version` as its
first field, and the console refuses to open a file written by a **newer** version of
itself rather than silently dropping the fields it does not understand (`D-20`). This is
what keeps §3.6 working a year from now, when the backup being restored was written by an
older build than the console restoring it.

Created on first launch by the setup wizard. **Backup** = `lab-key.lck` + `lab.json` +
`enrollment.json` + the catalog, exported as one file (`<lab> <date>.lcbak`): the key document as it is —
already encrypted under its holders and the recovery code — and the rest sealed with
AES-256-GCM under the same master key, so whoever can open the lab key can open the backup
and nobody else can read even the machine list (`D-26`). It is what makes §3.6 a
ten-minute operation instead of a walk around the room. The console nags until a backup exists and
warns when `lab-key.lck` no longer matches the fingerprint taken at the last export or
when a USB stick was written since (`D-28`).

## 5. Data on a student PC

```
C:\Program Files\LabControl\   app\<version>\  agent.exe, session.exe — one directory per
                                               installed version, kept side by side (D-19);
                                               a pushed build is named 0.1.0+1a2b3c4d — the
                                               number plus 8 hex digits of its hash (D-33)
                               app\current     names the version the service runs
                               app\previous    names the version to roll back to
                               setup.exe       kept for repair
C:\ProgramData\LabControl\     agent.json  {schema_version, agent_id, number, lab_id,
                                            ca_cert (public), mac, console_host?}
                               agent.key   the PC's own private key, DPAPI-protected (machine scope)
                               agent.crt   the PC's certificate, signed by the lab CA
                               update\     staging area for an incoming version bundle,
                                           emptied once the new version proves itself (§7)
                               logs\       rolling agent log (7 days)
                               cache\      downloaded package installers (cleaned after install)
                               jobs\<id>\  a run_script's file while it runs as SYSTEM; removed
                                           with the result (D-32)
C:\Users\Public\LabControl\    jobs\<id>\  the same for a script that runs as the student: it
                                           must be able to read it, and nobody weaker than the
                                           student can write there (D-32)
```
ACL: `SYSTEM` + `Administrators` full control, `Users` read/execute on Program Files,
**no access** for `Users` to `ProgramData\LabControl`.

Versions live in numbered subdirectories rather than in `LabControl\` itself because a
running service cannot overwrite its own `.exe`, and because a bad update must be
reversible without anyone entering the room (`D-19`). The Defender exclusion is set on the
`C:\Program Files\LabControl\` **parent**, so a version directory created later is
covered without a second visit.

## 6. Feature → mechanism map

| Feature | Mechanism |
|---|---|
| Screen mosaic | Session helper captures via DXGI Desktop Duplication (GDI `BitBlt` fallback, `D-35`), downscales to 320 px wide, JPEG q50, ≤ 2 fps per PC and only when the screen changed → frames up the named pipe to the service, which relays them as one small `PushVideo` stream per PC, asked for by the console right after `Welcome` and relayed down the same pipe. The console keeps one persistent picture per PC (`ScreenStore`) and the tile draws it (PROTOCOL "Video", `D-34`). |
| Full view + control | Double-click a tile: the console sends `VideoControl{full}` to that one PC and gets native-resolution dirty-rectangle JPEG deltas (bounding box per frame, keyframe every 5 s or on request) up to 20 fps under a 24 Mbit/s cap; closing the window goes back to the thumbnail. With *Control* on, the window's mouse and keyboard become `Input` messages — text as Unicode, shortcuts and command keys by physical position — relayed by the service down the helper's pipe to `SendInput` on the input desktop; Ctrl+Alt+Del is `SendSAS` from the service (PROTOCOL "Input", `D-36`). Optional H.264 via Media Foundation later (ROADMAP M6). |
| Wake-on-LAN | Console sends magic packet (UDP broadcast `:9`, plus directed to `last_ip`). MAC comes from enrollment. Installer enables WoL on the NIC and disables Fast Startup/hibernation (they break WoL on Windows). |
| Shutdown / reboot / logoff | Agent: `InitiateSystemShutdownEx` with `SE_SHUTDOWN_NAME`, immediate and forced (the result leaves 2 s before the call, `D-32`); log off with `WTSLogoffSession` on the interactive session — `ExitWindowsEx` would only log off session 0. |
| Run script | Agent pulls the script through `PullFile` (`D-31`) and runs `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File …` or `cmd.exe /d /c …` as SYSTEM (default) or **as the student in the student's session** (`as: user`: the user's token from `WTSQueryUserToken`, `CreateProcessAsUser` with pipes the service reads — not through the helper, which is SYSTEM). stdout/stderr streamed line by line, exit code in the result, the whole process tree killed after `timeout_s` of silence (`D-32`). |
| Install package | Catalog entry = `{name, version, installer file, silent args, detect: {path|registry|command}}`. Console pushes the installer over the file stream (LAN, no internet needed), agent runs it silently, verifies `detect`, reports result. `winget`/`choco` are *not* used (winget does not work under SYSTEM). **This is the reason the file channel exists** — installing an IDE on every PC without walking a USB stick around the room — and nothing built on the channel later may change how it behaves (`D-23`). |
| Send files to students | The same `PullFile` channel with a different landing: the file goes to `Materials` on the `student` desktop (path in `Defaults.cs`), hash-verified, optionally opened at once in the student session by the helper. One job per file, so a batch of three handouts to 14 PCs is 42 rows in the jobs panel with a result each. Installers never land here and handouts are never executed (`D-23`). |
| Broadcast teacher screen | Console captures its own screen (macOS: CoreGraphics `CGDisplayCreateImage`/ScreenCaptureKit via P/Invoke — needs Screen Recording permission; Windows: DXGI) → JPEG stream → helper shows a topmost full-screen window, input blocked. |
| Lock screen | Helper shows a full-screen topmost window with a message; low-level keyboard hook swallows Alt+Tab/Win; `BlockInput`. Ctrl+Alt+Del cannot be blocked by design — accepted. |
| Exam mode | A composable set of independent switches, not a single mode (see §6.1). |
| — timer | Countdown rendered by the helper; the deadline is absolute (console clock, skew-corrected at `Hello`) so it keeps running if the link drops, and the mode ends by itself. |
| — app whitelist | Helper subscribes to process-start events (WMI `Win32_ProcessStartTrace`, polling fallback) and terminates anything outside the allowed list. Matching is by **executable path/name only** — a process started by an allowed program is judged on its own name, not on its parent (owner's choice: the simpler rule). Hard-coded never-touch set: session, shell and OS processes, and LabControl itself. |
| Internet control | Three modes per PC — *open*, *whitelist* (named sites only), *blocked* — as a standalone action on one PC or the whole lab, and as the exam mode's internet switch. Enforcement is always Windows Firewall: outbound blocked in a LabControl rule group, exempting the lab's own subnet (so the console, the file channel and the printer keep working) and, in whitelist mode, the addresses of allowed sites. Names become addresses through a small resolver the agent runs on the loopback interface while a whitelist is active: it forwards allowed names upstream, records the answers as allow rules, and refuses everything else; encrypted-DNS bypasses fail on the firewall anyway. Fail-safe exactly like exam mode: the policy carries an expiry and a hard limit, is removed on service start before anything is re-applied, and a crashed agent can never leave a PC offline (§6.2, `D-22`). |
| — collect work | At the end (or on demand) the agent zips **one folder chosen when the exam is set up** and uploads it through `PushFile`; the console files it under `<exam>/PC-07/`. The folder is a dedicated work directory, not the whole `student` desktop, so what is collected is predictable and small. |
| Package management | The console's *Add package* wizard takes any local `.exe`/`.msi`, suggests silent arguments for known installer families, computes the hash, writes the YAML, and offers a test install on one PC before the fleet. The catalog ships **empty** — it is filled by the teacher. |
| Profile reset | Agent logs `student` off, deletes the profile (`Win32_UserProfile.Delete`), reboots; auto-logon recreates a pristine profile. Optional "light reset" = wipe Desktop/Documents/Downloads only. |
| Self-update | Console pushes a bundle signed by the lab key; the agent unpacks it into a new version directory, repoints the service and restarts, and rolls itself back if the new version cannot reach the console (§7, `D-19`). |
| Inventory | Agent reports hostname, Windows build, CPU/RAM/disk, installed catalog packages, uptime, current logged-on user. |

### 6.1 Exam mode is a set of switches

Different kinds of written work need different restrictions, so "exam mode" is not one
button. The teacher composes it from four independent switches and saves the
combination as a named preset (*Written test*, *Practical with the IDE*, *Open-book*):

| Switch | Off | On |
|---|---|---|
| Timer | mode ends when the teacher ends it | absolute deadline, countdown on the student screen, auto-end |
| Allowed programs | anything runs | only the listed executables; others are closed as they start (matched by name, not by which program launched them) |
| Internet | untouched | *blocked* or *whitelist* (§6.2) for the length of the exam; automatically restored |
| Collect work at the end | nothing collected | one folder, picked while setting up the exam, is uploaded from every PC |

Every switch is independently reversible, and the whole mode has a hard maximum
duration after which the agent restores the machine on its own — a lesson must never
end with a PC left restricted because the teacher's laptop was closed.

### 6.2 Internet control: open, whitelist, blocked

The owner wants the internet controllable during ordinary lessons, not only in exams:
"no internet for this lab", or "only the JetBrains site and the Oracle docs". So the
internet switch is its own action (`D-22`), applied to a selection of PCs or the whole
lab from the toolbar and shown as a badge on the tile, and the exam mode's internet
switch is the same mechanism with the exam's lifetime.

| Mode | What the student PC can reach |
|---|---|
| Open | everything (the machine as it was) |
| Whitelist | the lab's own subnet, plus the listed sites: hostnames with optional wildcard (`*.jetbrains.com`, `docs.oracle.com`). Hostnames only — with HTTPS a URL path is invisible to the machine, so a rule cannot say "this page but not that one" |
| Blocked | the lab's own subnet only |

Lists are saved as named presets (*Java docs*, *Nothing but the college site*) and
reused in exam presets. A standalone policy always has a duration — *this lesson*
(default 90 min), *N minutes*, or *until I lift it* — and, like an exam, an absolute hard
limit (`Defaults.InternetPolicyHardLimit`, 8 h) past which the agent restores the
machine on its own. If an exam starts while a standalone policy is in force, the exam's
policy wins; when the exam ends, the standalone one is re-applied if it is still within
its limits, so a "no internet today" is not silently cancelled by a test.

How the whitelist is enforced, because this is the part that can go wrong:

1. The agent installs the outbound block rules and the subnet exemption (same as
   *Blocked*), plus a rule allowing UDP/TCP 53 to the loopback interface only.
2. It starts a resolver on `127.0.0.1:53` and points the NIC's DNS at it, remembering the
   previous DNS servers in the persisted policy state so they can be put back.
3. For an allowed name the resolver forwards to the previous upstream, returns the real
   answer, and adds every address in it (following CNAMEs) to the allow rule set with an
   expiry a little longer than the record's TTL. For any other name it answers `REFUSED`.
4. A browser that ignores the system resolver (DNS-over-HTTPS) still resolves only through
   addresses the firewall lets it reach — which, for an unlisted site, it cannot. The
   result is a blocked page, not a bypass.
5. Restore = remove the rule group, stop the resolver, put the NIC's DNS back. This runs on
   policy end, at the hard limit, and **first thing on every service start**.

Known limits, accepted: a site behind a large CDN may need its CDN hostnames listed too
(the console shows the resolver's refused names for the last minutes, so the missing one
is a click away); a phone hotspot bypasses everything, exactly as it did before
LabControl existed; captive-portal or proxy networks (`D-10` on-site checklist) may need
the proxy address exempted.

## 7. Updating LabControl itself

New versions arrive constantly while the software is being built, and keep arriving after
that. Updating must therefore be an ordinary operation, not an event — and above all it
must never be able to strand a PC, because the only repair for a stranded PC is a walk to
its desk. The full reasoning is `D-19`; this is the shape of it.

### 7.1 The rule that makes updates safe

**A small part of the protocol is frozen forever.** `Hello`, `Heartbeat`,
`Job{kind: self_update}` and `JobResult` keep their v1 wire meaning for the life of the
product; everything else may change freely (`docs/PROTOCOL.md`, *Versioning*). An agent of
any version can therefore always connect to a console of any version, be recognised, and
be told to update itself — even when it understands nothing else the console says. As long
as that holds, a bad release is a remote fix, never a walk around the room.

The console consequently never *refuses* an agent for being old. It marks the tile
"outdated", disables the features that agent cannot do, and offers *Update*.

### 7.2 Updating an agent

1. The console builds a **bundle**: `agent.exe`, `session.exe` and a manifest
   (`version`, per-file SHA-256, minimum installed version). The manifest is signed with
   the **lab key** — the CA whose public certificate every agent pinned at install — so
   the bundle is verifiable on its own, independently of the TLS session that carried it.
2. `Job{kind: self_update}` → the agent downloads the bundle through `PullFile` into
   `ProgramData\LabControl\update\`, verifies the signature and every hash, and refuses
   the job on any mismatch.
3. The agent unpacks it into `Program Files\LabControl\app\<new version>\`, writes the
   outgoing version into `app\previous`, the new one into `app\current`, repoints the
   service (`ChangeServiceConfig` on the binary path) and asks the service manager to
   restart it. Nothing is overwritten, so nothing can be half-written.
4. **Probation.** The new version is on trial until it has completed a `Hello` and held
   the link for 10 uninterrupted minutes. Only then is it *accepted*: the console records
   the new `agent_version` and the agent clears `app\previous` and the staging directory.
5. **Rollback, by something that is not the agent.** The service's Windows recovery action
   runs `agent.exe --rollback` when the process dies repeatedly, and a scheduled task fires
   at the end of the probation window; either one repoints the service back at
   `app\previous` if the version was never accepted. A version that crashes on start, that
   cannot open its own certificate, or that cannot reach the console is therefore undone by
   the PC itself, with nobody in the room.

The session helper is a child process and is never locked, so it is simply replaced and
respawned along with the service.

**What exists since M2** (`D-33`): steps 2 and 3 without the signature, plus the rule that
the new version reports the result — the console's development action *Push agent build…*
sends a published `agent.exe` + `session.exe`; the agent pulls them under their hashes,
runs the new `agent.exe --version` once as a preflight, installs side by side, repoints the
service and has it restarted by `agent.exe --restart-service` from the outgoing version.
Steps 4 and 5 — probation, rollback, the scheduled task — are M4; until then a build that
starts and then crashes leaves the PC offline with the previous version still on disk.

### 7.3 Updating the console

The console is an interactive application on the teacher's own machine, so its binary is
replaced the ordinary way: quit, replace the self-contained publish, start. There is no
fleet, no service and no rollback machinery — the previous build is a copy of a directory.
The console's difficulty is not the binary but the data it leaves behind: `lab.json`,
`instance.json`, `enrollment.json`, the package catalog and the backup archive are read
by builds written months apart. That is what `schema_version` and the load-time migrations
of `D-20` exist for, and why they are built in M1 rather than retrofitted.

Because the binaries are unsigned (`D-15`), macOS quarantines a freshly downloaded console
once; this is a one-time `xattr -d com.apple.quarantine`, documented in the README rather
than engineered around.

## 8. Console UI (Avalonia)

- **Lab view**: one tile per PC in a grid mirroring the physical room layout (drag to
  arrange; saved in `lab.json`). Tile = live thumbnail + number + status (online /
  offline / sleeping / locked / in exam mode / broadcasting) + logged-on user. The grid
  scales from 1 to 30 PCs; beyond what fits, tiles shrink and the mosaic scrolls.
- **Selection model**: click = select, ⌘/Ctrl-click = multi, ⌘A = all. Toolbar
  actions apply to the selection: Wake, Shutdown, Reboot, Log off, Lock, Unlock,
  Broadcast, Exam mode…, Internet… (open / whitelist / blocked, §6.2), Run script…,
  Install package…, Reset profile, Send files… (§6, `D-23`), Collect files.
- **Single-PC view**: double-click a tile → its own window with the full-size stream
  (resolution, frame rate and bandwidth under the picture) and a *Control* toggle that sends
  the teacher's mouse and keyboard to the PC, with *Ctrl+Alt+Del* and *Win* buttons for the
  keys the teacher's keyboard cannot send (`D-36`). One window per PC; the tile keeps
  moving meanwhile.
- **Jobs panel**: every action becomes a job with per-PC rows (pending / running /
  ok / failed + log). Jobs persist in `logs/`.
- **Scripts** tab: the library on the left, the selected script edited in place on the
  right, *Run on selected PCs* sending the editor's text to the lab view's selection (M4,
  `D-31`, `D-38`).
- **Packages** panel (M6): manage the catalog; "Install on all missing".
- **Settings**: lab key (export backup, reprint the recovery code, change the
  passphrase), teacher machines (this one, others seen, *Take over*, revoke behind a
  confirmation — §3.7), enrollment codes,
  build USB installer.

## 9. Threat model (short)

Primary adversary = a curious student with physical access to a PC but only the
`student` account.

| Attack | Defence |
|---|---|
| Stop, uninstall or kill the agent | service and helper run as `LocalSystem`; `student` is a standard user with no rights over either |
| Read the PC's credentials | `C:\ProgramData\LabControl\` denies `Users`; the private key is DPAPI-protected at machine scope |
| Use one PC's material to attack the lab | a PC holds only its own key and the CA's *public* certificate; it can impersonate itself and nothing more, and can be revoked |
| Impersonate the console | requires a certificate signed by the lab key, which lives encrypted on the teacher machine and in the backup |
| Forge discovery beacons | beacons are signed and CA-endorsed; a forgery costs a rejected TCP handshake |
| Steal the USB installer stick | it carries no secret — only the public CA certificate and single-use enrollment codes; a bogus enrollment is visible in the console and removable |
| Watch another student's screen | video is addressed per agent and never relayed between agents |

Teacher-side risks, new since the console became replaceable:

| Situation | Consequence | Mitigation |
|---|---|---|
| Teacher machine stolen | holder can control the lab until revoked | revoke the instance (§3.6); leaf certificates are short-lived; OS keystore protects the leaf key at rest |
| Backup file leaked | holder owns the lab | the file is useless without the passphrase or the recovery code |
| Backup and passphrase both lost | the lab must be re-keyed PC by PC | first-run wizard forces a backup and a recovery code before it finishes |
| Binaries are unsigned | antivirus / SmartScreen may quarantine the agent | accepted (see D-15); the installer registers an exclusion and the console reports agents that stop reporting |

Out of scope: a student with a live USB or BIOS access (physical security is the
college's job), a student on a phone hotspot (the internet controls of §6.2 cover the
lab's wire, not the student's pocket), and network attackers on the lab LAN beyond the
students themselves.

## 10. Non-goals

Multi-lab, two consoles *sharing* one lab at the same time (§3.7.2 — alternating teacher
machines are in scope, a split room is tolerated, a shared room is not), cloud relay, mobile
console, Linux/macOS student agents
(possible later; keep `LabControl.Agent` behind an `IPlatformAgent` seam but do not
build it now), grading/LMS integration.
