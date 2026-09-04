# Decisions log

Format: `D-NN — title` / Context / Decision / Alternatives rejected / Status.
Add a new entry rather than editing an old one; mark superseded entries.

## D-01 — .NET 10 LTS, not .NET 8
Context: the owner's other projects are on .NET 8, but .NET 8 and .NET 9 both reach
end of support on 10 Nov 2026 (two months after project start). .NET 10 is the current
LTS (Nov 2025 → Nov 2028).
Decision: target `net10.0` everywhere. Bump to the next LTS in 2027 when it ships.
Rejected: .NET 8 (EOL), .NET Framework (not cross-platform for the console).

## D-02 — Avalonia UI for the console
Context: console must run on macOS today and Windows later.
Decision: Avalonia (MIT, mature, Skia-based, JetBrains/Rider support). Pinned at
**12.1.2**, the current stable release at project start — starting a three-year project on
the previous major would mean scheduling a migration before writing any features. Note
`Avalonia.Diagnostics` has no 12.x release and is not used.
Amended 2026-09-04 (M0): the original note said "11.x", written before 12 shipped.
Rejected: MAUI (macOS desktop support is via Catalyst and weak; no Linux),
Electron/Tauri + web UI (second language, heavier video path), WPF/WinUI (Windows-only).

## D-03 — Build screen capture/control ourselves, no Veyon/VNC on student PCs
Context: Veyon would give screen mosaic + broadcast + lock out of the box, but
there is no Veyon Master for macOS, it is a second product to configure per PC, and
its config would have to be replicated by our installer anyway.
Decision: own agent with DXGI Desktop Duplication + JPEG tiles + SendInput.
Rejected: Veyon (no macOS master), UltraVNC/TightVNC embedding (GPL implications,
separate service, weak input security), RDP shadowing (requires Pro edition config,
awkward for 14 simultaneous thumbnails).

## D-04 — gRPC over TLS as the only transport
Context: need control messages, two streaming video directions, file transfer,
reconnection, and code-generated contracts, without inventing a framing protocol.
Decision: Grpc.AspNetCore (Kestrel) in the console, Grpc.Net.Client in the agent,
self-signed cert pinned by fingerprint, per-agent token in metadata.
Rejected: raw TCP + custom framing (more code, no multiplexing), SignalR (JSON
overhead for video, less natural for binary streams), WebRTC (huge dependency for
a LAN with no NAT).

## D-05 — Agents dial out to the console, console broadcasts a beacon
Context: the console's IP changes (Wi-Fi/DHCP/another laptop); PC IPs also change.
Decision: agents are clients; console announces itself via signed UDP beacon.
Rejected: console connecting to each agent (needs inbound firewall rules and a
static inventory of IPs), mDNS/Bonjour only (Windows mDNS is flaky in services,
and we control both ends anyway — a plain beacon is simpler).
Status: still current, **except** the beacon's symmetric `beacon_key` signature, which
is superseded by the CA-endorsed signature in D-13.

## D-06 — Two processes on the student PC (service + session helper), both SYSTEM
Context: session-0 isolation; UAC secure desktop; student must not be able to kill
the capture process.
Decision: `agent.exe` service (LocalSystem) spawns `session.exe` into the active
session with a SYSTEM token. Named pipe between them.
Rejected: single service process (cannot capture), helper running as `student`
(killable, cannot see secure desktop/lock screen), scheduled task at logon (races).

## D-07 — Own package catalog instead of winget/chocolatey
Context: winget does not run under SYSTEM; chocolatey adds a third-party runtime;
student PCs may lack internet; installers are large and identical for 14 PCs.
Decision: `packages/<name>.yaml` (installer file, silent args, detect rule) with
binaries cached on the console and pushed over the LAN. Generic "run script" covers
everything else.
Rejected: winget (SYSTEM limitation), chocolatey (dependency + internet).

## D-08 — Installer is a C# exe, not a PowerShell script
Context: execution policy, PowerShell 5.1 vs 7 differences, hard to unit test,
and it would still need a compiled helper for LSA secrets.
Decision: `LabControl.Setup` self-contained exe with `requireAdministrator`
manifest, `ISetupStep` pipeline, `--dry-run`, idempotent re-runs.
Rejected: PowerShell script (see above), MSI/WiX (does not express our steps well,
harder to make idempotent and interactive), Intune/AD GPO (no domain).

## D-09 — Password "1" for `student`, auto-logon, LSA-stored secret
Context: owner's explicit requirement; isolated lab LAN; standard user only.
Decision: as stated. The password is stored via `LsaStorePrivateData`, not in the
plaintext `DefaultPassword` registry value. Remote access for `student` (RDP, admin
shares) stays disabled by virtue of being a standard user.
Status: accepted risk — documented in ARCHITECTURE.md threat model.

## D-10 — Network assumptions (TO VERIFY ON SITE)
Same subnet Wi-Fi ↔ wired; broadcasts pass; BIOS WoL enabled. Fill in after the
first visit: router model, subnet, whether AP isolation is on, NIC models of the PCs.

## D-11 — JPEG tiles first, H.264 later
Context: H.264 via Media Foundation on the agent is doable but adds a decoding
dependency on the console (FFmpeg/ffmpeg.autogen or platform codecs) for three OSes.
Decision: dirty-rect JPEG tiles (SkiaSharp both ends) in M3; H.264 as an M6 option
behind the same envelope, and only if M3's measurements demand it.

## D-13 — The lab is a private CA; the teacher machine is replaceable

Context: the owner has one teacher machine today (a MacBook), but the software is meant
to move to other labs where the master is a Windows PC, and a laptop can be lost, stolen
or replaced mid-year. The original design had agents pin the console's own certificate
fingerprint and derive their tokens from a shared `lab_secret` carried on the USB stick.
That makes a change of teacher machine a walk to every PC with a USB stick, which
contradicts the "zero-maintenance" premise of the whole project.
Decision: separate the identity of the **lab** from the identity of the **console**.
The lab is an ECDSA P-256 certificate authority created once. Agents pin the CA's public
key. Each teacher machine mints itself a leaf certificate signed by that CA and is
therefore interchangeable: importing the lab key onto a new machine and naming the new
instance is the entire migration, with **no student PC touched**. Connections are mutual
TLS validated against the CA on both sides; the discovery beacon carries the instance
public key plus a CA endorsement, so it is verifiable offline with no shared secret.
Old console instances can be revoked, and the revocation list reaches agents on connect.
The lab key is stored encrypted (AES-256-GCM) under a master key wrapped twice — by a
PBKDF2-SHA256 passphrase (≥ 600 000 iterations) and by a printable recovery code — so a
forgotten passphrase is recoverable and a leaked backup file is inert. Owner's explicit
choice: **passphrases and recovery codes, not a USB-borne key file**, and **more than one
key holder** — a colleague must be able to open the lab if the owner is ill on an exam
day, so the master key carries one wrapping per named holder plus the recovery code.
Equally by the owner's choice, only **one console drives the lab at a time** — but several
teacher machines may hold a console permanently and take turns; see `D-21` for what that
means in practice. Everything needed is
in the .NET base class library (`ECDsa`, `CertificateRequest`, `AesGcm`,
`Rfc2898DeriveBytes`) — no new NuGet dependency.
Rejected: pinning the console leaf and re-running the installer on every PC after a
migration (the problem being solved); trust-on-first-use with a shared secret (turns the
secret into the one thing that must never leak, and it would have to sit on the USB
stick and on every PC); a public CA / Let's Encrypt (needs internet and DNS the lab does
not have); keeping the CA key on the USB stick (owner explicitly does not want the key
tied to a stick, and a stick in a drawer is a worse safe than an encrypted file plus a
passphrase).
See `docs/ARCHITECTURE.md` §3.

## D-14 — Certificate enrollment with single-use codes; the USB carries no secret

Context: with D-13 the agent needs a certificate, and Setup runs offline, possibly long
before the console is switched on.
Decision: the agent generates its own keypair on the PC (private key DPAPI-protected,
machine scope) and Setup writes only the public CA certificate plus one **single-use
enrollment code** taken from a batch the console generated. At the agent's first
connection it presents the code and a CSR; the console issues the certificate and burns
the code. A lost USB stick therefore allows at most the enrolment of one bogus machine,
which is visible in the console and removable — it is no longer a credential that owns
the lab.
Rejected: `HMAC(lab_secret, agent_id)` tokens (requires the lab secret on the stick),
issuing certificates at install time from a CA key on the stick (same problem), manual
approval of every enrolment (14–30 clicks and an easy thing to get wrong under time
pressure — the codes already carry the authorisation).

## D-15 — Unsigned binaries plus an antivirus exclusion, not code signing

Context: an unsigned Windows service triggers SmartScreen and can be quarantined by
Defender. A code-signing certificate costs money annually and the owner has none.
Decision (owner's choice): ship unsigned. The installer registers
`C:\Program Files\LabControl\` as a Microsoft Defender exclusion during setup, and
detects a third-party antivirus rather than pretending to configure it — it names the
product and the path to exclude, and records the fact so the console can warn about
PCs at risk of silently losing their agent. Revisit if the project spreads to labs whose
policy forbids exclusions.
Rejected: buying an OV/EV certificate (cost, and a yearly renewal chore for a solo
maintainer); a self-signed certificate plus a trusted-publisher policy on each PC (does
not stop SmartScreen, and adds a machine-level trust root that is worse than an
exclusion scoped to one directory).

## D-16 — Exam mode is four independent switches, and it is fail-safe

Context: the owner asked for a "test mode", and then for it to be switchable per
feature, because written work varies — sometimes only a timer is wanted, sometimes a
locked-down machine.
Decision: one *exam session* composed of four independent switches — timer with a visible
countdown, allowed-programs whitelist, internet block, collect-work-at-the-end — saved
as named presets. The enforced state lives on the agent and is persisted, so an exam
survives a console restart, a network drop or a reboot. Every session carries an absolute
hard limit; past it the agent restores the machine on its own, and on service start the
agent restores first and re-applies only if the stored session is still valid. A crashed
console, a closed laptop or a flat battery can therefore never leave a PC without
internet or with programs being killed.
Two scoping choices, both the owner's: the whitelist matches **executable names only**
(a program launched by an allowed program is judged on its own name), because following
process trees to allow "an IDE and whatever it spawns" is materially harder and was not
worth it; and *collect work* takes **one folder chosen when the exam is set up**, a
dedicated work directory rather than the whole student desktop, so the result is
predictable and small.
Rejected: a single all-or-nothing "exam mode" (does not fit real lessons); enforcing the
restrictions from the console in real time (a dropped link would silently unlock the
room, or worse, leave it locked forever); parent-aware whitelisting (see above).

## D-17 — Design for up to 30 student PCs

Context: the first lab has 14 PCs; other labs may be larger. The number affects the
mosaic layout, the video bandwidth budget and the console's threading.
Decision: nothing assumes 14. The count comes from `lab.json`; the mosaic scales and
scrolls; the video budget and the job fan-out are sized and load-tested for **30** agents
(FakeAgent makes this cheap to test). Beyond 30 is out of scope and will be revisited
with measurements rather than guesses.
Rejected: hard-coding 14 (immediate rewrite when the software moves); designing for 50+
now (a materially harder streaming problem for a lab that does not exist).

## D-21 — Several teacher machines take turns; two at once is tolerated, never shared

Context: the lab is taught by more than one person. The owner uses a MacBook; a colleague
uses the Windows PC at the teacher's desk. `D-13` made the teacher machine *replaceable*,
but "replacement" implies the old machine goes away. Here both stay, and the question is
what happens on the day they swap — and on the day someone forgets to close the console
on the other one.

Decision, in three parts, all from the owner:

1. **Alternation is the baseline.** Any number of teacher machines may carry a console
   for the same lab, each with its own instance certificate minted from the same lab key.
   Closing the console on one machine and opening it on another is the entire handover;
   agents re-home themselves via the beacon within ~15 s. To make this cheap, a console's
   machine list is treated as a cache of what the agents know: an agent presenting a valid
   lab-issued certificate is added to the list on connect, never rejected as unknown.
   Revocation entries are signed by the lab key and merged as a set across consoles and
   agents, so a revocation made on one machine reaches the others through the agents.
2. **Two live consoles is an exception, not a mode.** It must not break the lab, and it
   must not become a shared mode either: no PC ever takes commands from two consoles.
   Agents keep the connection they have (already the rule from `D-05`), so two consoles
   split the room; each shows an informational banner naming the other and the PCs it
   holds, and offers *Take over the lab*, implemented as a signed `take` timestamp in the
   beacon that makes agents re-home to the taker. Last button press wins. There is no
   console-to-console channel, no locking, no merged view.
3. **Revoke is not the tool for this.** Revocation stays a security action for a stolen
   machine, behind the passphrase and a confirmation in Settings, and is no longer
   offered from the "another instance is running" banner.

Rejected: a shared mode where both consoles see and control the whole room (needs
arbitration of lock/broadcast/exam/input per PC and a console-to-console channel —
real complexity for a situation the owner expects never to occur); a console-side
lease or lock file (no shared storage exists on an isolated LAN, and a stale lease would
block the next teacher); one-click revoke from the banner (turns a forgotten laptop into
a re-import job for the owner); syncing the package catalog automatically between
machines (would need a console-to-console channel; a visible "catalog last changed on
…" label plus backup import is enough for a catalog that changes a few times a year).
See `docs/ARCHITECTURE.md` §3.7 and `docs/PROTOCOL.md` (beacon `take`, `Revocation`,
`RevocationState`).

## D-12 — Documentation: Markdown is the source, HTML is generated

Context: the owner wants every document available as a readable `.html` next to the
`.md`. Maintaining two hand-written copies guarantees they drift apart, and a drifted
document is worse than no document.
Decision: `docs/*.md` (plus `README.md` and `CLAUDE.md`) are the single source of truth.
`tools/DocsBuild` (a small .NET 10 console app using **Markdig**) renders them into
`docs/html/*.html` — self-contained pages, CSS inlined, no external requests, so they
open by double-click on a machine with no internet. `tools/docs-build.sh` runs it, and
must be run in the same change as any `.md` edit. `docs/html/` is committed (the owner
wants the HTML in the repository) but is never edited by hand.
Rejected: hand-written HTML duplicates (drift), a Python script with a home-made
Markdown parser (worse tables/anchors than Markdig for no benefit), a static-site
generator such as Docusaurus/MkDocs (Node/Python toolchain to maintain for six pages),
GitHub Pages rendering only (the lab has no internet).

## D-18 — Build and test toolchain (M0)

Context: four small choices made while standing the solution up, each of which would look
arbitrary later.
Decisions:
1. **`EnableWindowsTargeting=true`** rather than a separate `Windows` solution
   configuration. The three `net10.0-windows` projects then compile during an ordinary
   `dotnet build` on the Mac. They cannot *run* there, but they can never silently rot
   either — which a build-excluded project always eventually does.
2. **Central package management** (`Directory.Packages.props`). Versions in one file,
   projects reference names only; a bump cannot be applied to four projects and forgotten
   in the fifth.
3. **Classic `.sln`, not `.slnx`.** The .NET 10 SDK now defaults to the XML `.slnx`
   format; the classic format is what every Rider version in use understands, and the
   solution file is not where this project should be adventurous.
4. **`Microsoft.Testing.Platform`, opted into via `global.json`.** .NET 10 no longer
   supports VSTest for `dotnet test`, and `xunit.v3` hosts the new runner itself. Which is
   also why `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are *not* referenced.
   `global.json` also pins the SDK band (`10.0.100`, `rollForward: latestFeature`).
Rejected: solution configurations that exclude projects (rot); floating package versions
(irreproducible builds); `.slnx` (tooling risk for no benefit).

## D-19 — Updating LabControl itself: side-by-side versions and a self-rollback

Context: new versions arrive constantly during development and keep arriving afterwards,
so updating is an ordinary operation, not an event. The danger is not the file copy — it
is that a bad release on fourteen machines can only be repaired by walking to fourteen
desks, which is the exact cost this project exists to avoid.

Decisions:

1. **Side-by-side version directories.** Binaries live in
   `C:\Program Files\LabControl\app\<version>\`; `app\current` and `app\previous` are
   text files naming versions. An update creates a new directory and repoints the service
   (`ChangeServiceConfig`). Nothing is ever overwritten in place, so no update can leave a
   half-written binary, and rolling back is one line of text plus a restart.
2. **A frozen protocol subset.** `Hello`, `Heartbeat`, `Job{self_update}` and `JobResult`
   keep their v1 meaning permanently (`docs/PROTOCOL.md`, *Versioning*). Consequently the
   console **never refuses an agent for being old** — it marks the tile outdated, greys out
   what that agent cannot do, and offers *Update*. This single rule is what converts "a bad
   release costs a walk around the room" into "a bad release costs another push".
3. **The bundle is signed by the lab key, not merely carried over TLS.** The manifest
   (version, per-file SHA-256, minimum installed version) is signed by the CA whose public
   certificate every agent pinned at install, so an update is verifiable on its own. This
   costs nothing — the key already exists — and it is the only authenticity the unsigned
   binaries of `D-15` will ever have.
4. **Probation with a rollback driven from outside the agent.** A new version is on trial
   until it has completed a `Hello` and held the link for 10 minutes. The Windows service
   recovery action (`agent.exe --rollback`) and a scheduled task at the end of the window
   both repoint the service back at `app\previous` if that never happened. The rollback
   must not be the responsibility of the code being tested: an agent that crashes on start
   cannot roll itself back.
5. **The console updates by replacing its binary.** It is an interactive app on the
   teacher's own machine — quit, replace the self-contained publish, start. No fleet, no
   service, no rollback machinery; the previous build is a copy of a directory. Its real
   compatibility problem is on-disk data, which is `D-20`.
6. **Sequenced by cost, not by milestone tidiness.** Items 1 and 2 are nearly free now and
   unaffordable later — the directory layout cannot be retrofitted without visiting every
   PC, and the frozen subset cannot be declared after the protocol has already broken it.
   They land in M1/M2. The bundle signing, probation and rollback land in M4 with the rest
   of deployment.

Rejected: overwriting the binaries after stopping the service (a failed copy leaves a PC
with no working agent and no way back); a separate `updater.exe` copied to a temp
directory to swap files behind the service's back (a second privileged binary to keep
correct, for a problem the directory layout already removes); an MSI with Windows
Installer versioning (a second technology, and `D-08` already rejected that style of
dependency for the installer); refusing old agents on `protocol_version` (correct-looking
and exactly the trap described above); trusting the TLS channel alone to authenticate a
bundle (works, but throws away a signature that is already free).

## D-20 — Every file on disk carries a schema version

Context: `lab.json`, `instance.json`, `enrollment.json`, `agent.json`, the package catalog
YAML and above all the **backup archive** are written by one build and read by another,
months apart. `D-13` promises that a lab survives the loss of the teacher machine by
restoring one backup file — a promise that is only worth anything if a backup written a
year ago still opens.

Decision: every persisted file begins with a `schema_version` integer. On load the console
(or agent) migrates forward through a chain of small, tested, one-directional migration
steps; it **refuses** a file whose version is newer than it understands, with a message
naming the version it needs, rather than parsing what it can and dropping the rest. A
silent partial read of `lab.json` would lose machines; a silent partial read of a backup
would lose the lab.

This is built in M1, when there is one version and the migration list is empty, because a
format with no version field cannot be given one later without guessing which files
predate the change.

Rejected: inferring the version from which fields are present (works until two changes
happen in the same release); a single global version for all files (couples unrelated
formats, so a catalog change invalidates a backup); tolerant parsing with defaults for
missing fields (the failure mode is silent data loss, which is the one failure mode this
project cannot notice on its own).

## NuGet dependencies (keep this list current)

| Package | Where | Why |
|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | Console | UI and a bundled font, so text renders the same on every OS |
| CommunityToolkit.Mvvm | Console | MVVM source generators |
| Grpc.AspNetCore | Console | gRPC server |
| Grpc.Net.Client, Grpc.Tools, Google.Protobuf | Shared/Agent | gRPC client + codegen |
| SkiaSharp | Console, Agent.Session | JPEG encode/decode, scaling |
| Microsoft.Windows.CsWin32 | Agent, Agent.Session, Setup | Win32 P/Invoke source generator |
| Vortice.Direct3D11, Vortice.DXGI | Agent.Session | Desktop Duplication |
| Microsoft.Extensions.Hosting.WindowsServices | Agent | Windows service hosting |
| System.Management | Agent, Setup | WMI (profiles, NIC properties) |
| YamlDotNet | Console | package catalog |
| Serilog.Extensions.Logging, Serilog.Sinks.Console, Serilog.Sinks.File | all | logging |
| xunit.v3 | tests | testing; it hosts its own Microsoft.Testing.Platform runner, so no VSTest packages are needed (D-18) |
| NSubstitute | tests | *planned* — added when the first interface actually needs faking |
| Markdig | tools/DocsBuild | Markdown → HTML mirror of the documentation (D-12) |
