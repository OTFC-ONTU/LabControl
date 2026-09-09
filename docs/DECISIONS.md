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
Decision: dirty-rect JPEG tiles (SkiaSharp both ends) in M3; H.264 as an M7 option
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
countdown, allowed-programs whitelist, internet control (`D-22`), collect-work-at-the-end — saved
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

Scope update: planned `D-53` / M5 adds several saved rooms per device and separates
ordinary teacher access from full lab ownership. This entry records the original
single-lab design; its takeover/ownership-reporting and revocation gaps are M5 work.

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

## D-22 — Internet control is its own action: open, whitelist, blocked

Context: the owner wants to control the internet during ordinary lessons, not only inside
an exam — sometimes nothing at all, sometimes "only the documentation sites". `D-16` had
the internet switch only as a part of an exam session, as a plain on/off.

Decision: a standalone **internet policy** with three modes — *open*, *whitelist* (a list
of hostnames with optional leading wildcard), *blocked* — applied per PC or lab-wide,
saved as named presets, and reused as the exam mode's internet switch. Enforcement is
Windows Firewall in every mode (outbound blocked in a LabControl rule group, the lab's
own subnet exempt). The whitelist resolves names through a resolver the agent runs on
the loopback interface while the policy is active: allowed names are forwarded upstream
and their answers added as time-limited allow rules; everything else is refused. Because
the firewall, not the resolver, is the gate, a browser that resolves elsewhere
(DNS-over-HTTPS) gains nothing. A standalone policy has a duration and the same fail-safe
shape as an exam: persisted on the agent, an absolute hard limit (8 h), restore-first on
service start. An active exam's policy overrides a standalone one and the standalone one
comes back when the exam ends.
Accepted limits: hostnames only, never URL paths (invisible under HTTPS); CDN hostnames
may need listing (the console shows recently refused names to make that easy); a phone
hotspot is out of reach, as it always was.
Rejected: a filtering HTTP proxy on the agent or on the console (would have to terminate
TLS to see paths — a private CA on every PC intercepting student traffic is exactly the
kind of thing this project should not do, and it breaks certificate pinning in tools like
IDE update checks); hosts-file editing (no wildcards, does not stop DoH, easy to miss on
restore); blocking by IP lists maintained by hand (sites move); a whitelist only inside
exam mode (the owner's everyday case is a lesson, not a test).
See `docs/ARCHITECTURE.md` §6.2 and `docs/PROTOCOL.md` (`InternetPolicy`, `InternetState`).

## D-23 — One file channel, two landings: installers and handouts

Context: the file channel (`PullFile`) exists **for installing software** — pushing an
IDE or a JDK to every PC without walking a USB stick around the room is the first thing
the project was started for. The owner also wants to hand out materials (a `.docx`
methodical guide, a task sheet) to every student PC. The two must not get in each other's
way.

Decision: the same hash-verified, resumable channel, with two jobs that never share a
landing:

- `install_package` (unchanged): the installer is pulled into the agent's private staging
  directory under `ProgramData`, run silently as SYSTEM, `detect` is verified, the file
  is deleted. Students never see it; it never touches the `student` profile.
- `send_file` (new landing): the file is pulled into `Materials` on the `student` desktop
  (path in `Defaults.cs`), overwriting an older copy of the same name, and is optionally
  opened at once in the student session through the helper. It is never executed, whatever
  its extension. One job per file, so every file on every PC has its own result row.

The console offers *Send files…* on the toolbar; the batch is one entry in the jobs panel
that expands to per-file, per-PC rows. Profile reset (full or light) wipes `Materials` with
the rest of the desktop, which is the intended way to clear last week's handouts.
Rejected: a generic "copy file to path" action (invites installers onto the desktop and
handouts into `Program Files`; a teacher under time pressure should not be choosing
paths); a shared network folder instead of the channel (needs SMB open on every PC and a
password somewhere; the channel is already there and authenticated); executing handouts
"if they are scripts" (that is what `run_script` and the scripts panel are for).
See `docs/ARCHITECTURE.md` §6 and `docs/PROTOCOL.md` (`send_file`).

## D-24 — How the trust model is actually built (M1)

Context: `D-13`, `D-14` and `D-21` decide *what* the trust model is. Building it in M1
forced a handful of smaller choices that would look arbitrary later, and that both sides of
the wire have to agree on exactly.

Decisions:

1. **Identity lives in a subject alternative name URI, not in a hostname.**
   `labcontrol://<lab_id>/console/<instance_id>` and
   `labcontrol://<lab_id>/agent/<agent_id>/<number>`. Agents reach the console by whatever
   IP DHCP handed out today, so hostname validation is meaningless here; both sides switch
   off the built-in name check and validate the chain against the pinned CA plus this URI
   instead. It also means a certificate says which *role* it is, so an agent certificate
   offered where a console belongs is refused as a role error rather than accepted.
2. **Everything the lab key signs outside a certificate is a pipe-joined UTF-8 string.**
   The beacon endorsement is `lab|inst|base64(pub)`; a beacon signature covers
   `v|lab|inst|name|host|port|ts|take|base64(pub)|base64(end)` with `take` written as `0`
   when absent; a revocation entry covers `serial|revoked_at_unix|reason`. Canonical JSON
   would have been the alternative and is a well-known source of "my serializer orders keys
   differently" bugs; a fixed field order in a flat string cannot drift, and it is
   reimplementable in ten lines by whatever writes the next agent.
3. **The beacon carries a compressed P-256 point (33 bytes).** The .NET BCL imports affine
   coordinates only, so LabControl decompresses the point itself. That is ~40 lines of
   modular arithmetic in exchange for 44 bytes of a 512-byte budget that also has to hold
   two 64-byte signatures — with the uncompressed form a slightly longer instance name
   would silently push the beacon over one datagram.
4. **Enrolment needs the lab key unlocked.** Issuing an agent certificate is a CA
   operation, so the console holds the unlocked lab key in memory only while the teacher has
   enrolment open, and refuses `Enroll` with a clear message otherwise. Rejected: minting an
   intermediate CA for each console instance so it could issue on its own — that turns every
   teacher machine into something that can enrol PCs unattended, which is exactly what
   `D-13`'s "a stolen laptop is revocable, not catastrophic" depends on not being true.
5. **PBKDF2 iterations are stored per wrapping, not compiled in.** The count only ever goes
   up, and a file written years ago must still open. Tests use a deliberately low count for
   speed, which is possible precisely because the number travels with the wrapping.
6. **Certificate serials are normalised to uppercase hex with leading zeros trimmed**, in
   one place, because revocation matches on the string and the two sides must not spell it
   differently.
7. **The console instance key is protected by the OS keystore, with an honest fallback.**
   macOS Keychain through `SecItem` (never `/usr/bin/security`, which would put a private key
   on a command line), Windows DPAPI, Linux libsecret through `secret-tool` with the secret on
   standard input. Where none is available the key is sealed with AES-256-GCM under a key
   derived from the machine identity and the user account: weaker — anything running as that
   user can re-derive it — but a console that will not start because a keyring daemon is
   missing is worse for this lab, and the instance key is reissuable from the lab key anyway.
   A document is always reopened with the protector that *wrote* it, so installing a keyring
   later never orphans a key.
8. **The last key holder cannot be removed.** A lab openable only by a sheet of paper in a
   drawer is a lab with no teacher.

## D-25 — Four questions the M1 review left open, answered

Context: reviewing the M1 trust code against `docs/ARCHITECTURE.md` turned up four places
where the documents and the code could each be read two ways. The owner decided; this
entry records the decisions so the next reader does not re-open them.

1. **Enrolment needs the lab key, and the documents now say so.** `D-24` item 4 stands;
   `ARCHITECTURE.md` §3.2 previously listed the passphrase as needed only for first run,
   minting, migrating and revoking, which was wrong by omission. A console with the key
   locked refuses `Enroll` with a plain message and the agent retries on its reconnect
   schedule. Rejected: keeping the lab key unlocked in memory for the whole console
   session — it would make every teacher machine a CA for as long as it is open, which is
   the exposure `D-13` is built to avoid; and an intermediate CA per instance, for the same
   reason (`D-24`).
2. **Certificates are renewed over the link, never by visiting a PC.** Leaf lifetimes had
   been chosen (console 1 year, agent 5 years, authority 20) with no story for what happens
   at the end — which for agents would have been a walk to every PC in 2031, the exact thing
   `D-13` forbids. `AgentService.Renew` re-issues an agent certificate from a fresh CSR for
   the identity the agent *proved* on the current connection; it starts 60 days out
   (`Defaults.CertificateRenewalLeadTime`), needs the lab key like enrolment does, and is
   refused politely until the key is unlocked. Console instances re-mint themselves from the
   lab key on their own machine. Rejected: leaf lifetimes equal to the authority's — simpler,
   but then a compromised key is closed only by revocation, and a 20-year agent key on a PC
   in a student lab is not a comfortable thing.
3. **The PC number is the identity of a machine.** Setup generates a fresh `agent_id` on a
   full reinstall (`--rekey` keeps it), so the console would otherwise accumulate one dead
   record per reinstall, each with the same sticker number. A `Hello` or `Enroll` whose
   number another record holds replaces that record and raises an event naming both.
   Rejected: keeping both until the teacher deletes one (the "unexpected machine" path is
   for bogus enrolments, not for the PC the teacher just reinstalled); refusing enrolment
   while the old record exists (a second trip to the console for nothing).
4. **`online_only` means never queued.** The first cut delivered a pending `shutdown` if the
   PC reconnected within the job's timeout, which is a grace period `PROTOCOL.md` never
   promised. Now: created for an offline PC it is `not_delivered` at once; still pending
   when the PC's link drops, it is closed the same way. A `shutdown` clicked at 15:00 can
   never fire at 08:00 the next day, and nothing depends on a timer.

Smaller corrections made in the same pass: job timeouts count inactivity, not time since
delivery, so a long install that keeps reporting is never cut off; the backup check compares
a fingerprint of `lab-key.lck` rather than a date, since the file changes on whichever
teacher machine added a holder; `RemoveHolder` drops the wrapping and the document no
longer claims it re-wraps the master key (equivalent in strength, see `ARCHITECTURE.md`
§3.2); an `Enroll` with a non-UUID `agent_id` is refused before a code is spent, because the
id ends up in the certificate's name and the lab's own trust rules would reject it forever.

## D-26 — The backup archive and the lab-key unlock window (M1)

Context: building the transport and the console UI (M1) needed two things the documents
had left open: what exactly the "encrypted backup archive" of `ARCHITECTURE.md` §4 is, and
how long an unlocked lab key stays in memory, since `D-24` said "while enrolment is open"
but renewal (`D-25`) needs the key too and agents ask for it on a backoff. The owner chose;
this entry records it.

1. **The backup is sealed under the lab's master key.** One JSON file
   (`<lab name> <date>.lcbak`, `schema_version` first): the `lab-key.lck` document verbatim —
   it is already encrypted under every holder's passphrase and the recovery code — plus
   `lab.json`, `enrollment.json` (since `D-28`) and the package catalog serialized and
   sealed with AES-256-GCM under the same master key. Whoever can open the lab key can open the backup; nobody else can read even
   the machine list. Nothing new to remember. Rejected: a separate archive passphrase (one
   more secret to lose, and the lab key inside would still be the real one); a plain zip with
   `lab-key.lck` inside (the MAC addresses and the PC list would be readable by whoever finds
   the stick).
2. **The unlocked key lives for 15 minutes after its last use, or until locked.** The console
   shows *Lab key unlocked — locks itself at HH:MM* with a *Lock* button, every CA operation
   (enrol, renew, revoke, add or remove a holder, reprint the recovery code, export a backup)
   extends the window, and closing the console always ends it
   (`Defaults.LabKeyUnlockWindow`). Long enough to enrol a room and let its PCs renew in one
   sitting, short enough that a console left open is not a certificate authority for the
   afternoon. This refines `D-24` item 4, which tied the key to the enrolment panel; that
   wording was written before renewal existed. Rejected: unlocking for the whole console
   session (every open teacher machine becomes a CA, the exposure `D-13` avoids); unlocking
   per operation (fourteen PCs enrolling one after another would ask fourteen times).

## D-28 — No invisible credentials: a new stick voids the old one, removing a PC revokes it (M1)

Context: two things surfaced in the M1 live run. Every *Write USB payload* added a fresh
batch of codes while the unused codes of earlier sticks stayed valid, so the list in
Settings only ever grew and a stick forgotten in a drawer kept its power indefinitely. And
*Remove from lab* deleted the PC's record but left its certificate valid: the PC came
straight back through the self-healing list (§3.7), or, if it was off, sat somewhere as a
credential the teacher could no longer see, let alone revoke.

Decision: the console never leaves a valid credential it cannot show.

1. **A new stick replaces the old one — by default.** Writing a payload voids every
   still-unused code first (`VoidedAtUnix` on the code record), then generates the new
   batch. A voided code is refused with `VoidedCode`, naming the stick it came from and the
   date it was voided, and the refusal is an event. Burned codes are untouched — they are
   history, not power. Settings shows the numbers before the click ("19 codes = 15 PCs +
   4 spares; the 34 unused codes written earlier will stop working").
   The voiding is a ticked checkbox, not a law, because Setup runs offline (`D-14`): a PC
   installed from stick 1 holds its code until the console is next switched on, and a
   teacher who writes stick 2 in between must not lose the PCs already installed. The
   hint says exactly that, and the box is unticked for that one case.
2. **Removing a PC revokes its certificate.** *Remove from lab* asks for the lab key,
   revokes the PC's leaf (the entry travels to every agent as in §3.7.3) and only then
   forgets the record. A PC that never presented a certificate is simply forgotten.
   *Revoke certificate…* stays as the separate action for a PC that should remain visible
   as refused (a stolen one).
3. **Enrollment codes travel in the backup.** `enrollment.json` was per console and not in
   the archive, so a stick written on teacher machine A enrolled nothing on machine B — and
   nothing on the machine that replaced a dead A, which is the one case `D-13` exists for.
   The sealed payload now carries the enrollment document (`D-26` amended), import restores
   it, and the backup counts as *stale* when a stick was written after the last export, so
   the console nags until the codes are safe. The refusal for an unknown code says where
   to look. Codes still do not move between two *live* consoles: a stick is enrolled on the
   machine that wrote it or on one that imported its backup afterwards. Rejected for now:
   codes verifiable by the lab key alone (an HMAC under the master key) — any console
   could then accept any stick, but single-use could no longer be enforced across
   consoles, and each lost stick would be worth one bogus PC *per console*.

Rejected: expiring codes after N days (a stick written on Friday for a Monday install
would die over the weekend; time-based rules are exactly the maintenance this project
avoids); unconditional voiding (breaks the offline install above); a separate "Void
unused codes" button as the only way (one more thing to remember, and forgetting it is
the failure mode — the ticked box on the write itself is the reminder); a *Removed PCs*
list in Settings with a Revoke button (a second place to look for something that should
not have been left behind).

## D-27 — Development affordances that ship in the product (M1)

Context: the M1 acceptance tests run two console profiles and thirty fake PCs on one Mac.
A few switches and one test-project choice make that possible; they are listed here so
that nobody mistakes them for configuration a teacher should touch.

1. **Console command line: `--data <dir>`, `--port <n>`, `--bind <address>`.** Defaults are
   `~/.labcontrol` (or `%APPDATA%\LabControl`), 47800 and every interface; a lab machine
   runs with no arguments. Two profiles on one computer need different data directories and
   ports, and the beacon carries the port, so agents follow either. `--bind` is also how the
   "change the console's bound address" acceptance criterion is exercised. Settings shows
   the values but does not edit them: the beacon makes the address self-describing, so
   there is nothing for a teacher to configure.
2. **`--dev-agent-cert-days <n>`** issues *enrolment* certificates with a short lifetime, so
   the renewal path can be watched against `FakeAgent` without waiting five years. Renewed
   certificates always get the full lifetime, even with the switch — otherwise a 30-day
   certificate would renew into another 30-day certificate for ever.
3. **`FakeAgent --payload <dir>`, `--data <dir>`, `--console host[:port]`, `--fail N:…`,
   `--reinstall N`.** The simulator installs PCs from the same `setup.json` + `ca.crt` the
   real installer reads, keeps each PC's state under `~/.labcontrol-fake/PC-NN/`, and plays
   failures on demand (never connects, connects late, dies mid-job, job errors, burned code,
   forged revocation, outdated protocol). `--console` exists because of the next item.
4. **One beacon socket per process.** macOS delivers a unicast datagram to exactly one of
   several sockets sharing a port with `SO_REUSEPORT`, and the console sends a loopback
   copy of every beacon precisely for agents on its own machine. `BeaconListener` therefore
   shares one socket per port inside a process and fans out to every subscriber; across
   processes on one machine the network broadcast reaches everyone as long as the machine
   has a network interface, and a machine with none pins the address with `--console`.
5. **`tests/LabControl.Console.Tests` references the console executable.** The non-UI core
   (`LabSession`, the gRPC server, the bootstrap) lives in the console project so the product
   stays at six projects, and the tests reference that assembly directly rather than
   introducing a seventh. The UI tests use `Avalonia.Headless` with Skia, so the real windows
   are built and drawn off screen; with `LABCONTROL_UI_SHOTS=<dir>` they also save PNGs, which
   is how the UI is checked from a terminal.

## D-29 — The agent's install seam: one provisioning routine, a service that never exits (M2)

Context: M2 needs the agent on a real PC before Setup.exe exists (M4), and the agent's
private key, `agent.json` and the pinned `ca.crt` must be written the same way by the
development install, by the installer and by the simulator — a difference between them is
a bug that would only surface on a student PC.

Decisions:

1. **`AgentProvisioning` in `LabControl.Shared` is INSTALLER.md step 4.** It takes an
   enrollment code the caller already spent on the stick, generates the keypair, pins the
   authority and writes `agent.json`. `FakeAgent`, `agent.exe --install` and the M4 installer
   all call it; the only thing that differs is the `KeyProtection` — plain PKCS#8 for the
   simulator, DPAPI at machine scope for a real PC (`MachineKeyProtection`, entropy bound to
   the key's purpose). `SetupPayload` moved from `FakeAgent` to `Shared` for the same reason.
2. **`agent.exe --install --payload <dir> --number N` is the seam.** The installer is a
   separate executable, and a PowerShell script cannot do DPAPI-at-machine-scope with the
   same entropy as C#. Making the agent able to provision itself means every installer —
   `scripts/dev-install.ps1` today, Setup.exe in M4 — delegates the trust material to the
   one binary that will later read it. It does not need the console to be reachable.
3. **The service never exits on a bad state.** No `agent.json` yet, an unreadable key, a
   schema from a newer build: the service logs once, waits `UnprovisionedRetryInterval` and
   looks again. Exiting would trigger the recovery action (restart ×3, then `--rollback` in
   M4) and turn an ordinary "Setup wrote the binaries before the identity" into a rollback.
4. **ACLs are verified by the agent, not only set by the installer.** At every start the
   agent checks that `Users`, `Authenticated Users`, `Everyone` and `INTERACTIVE` have no
   read access to `ProgramData\LabControl\` and reports a violation as an event through the
   link. `AgentLink.Report()` queues events raised while unlinked and flushes them after the
   next `Welcome`, so a problem found before the first link still reaches the console.
5. **`dev-install.ps1` does what Setup will, and nothing Setup would not.** Same layout
   (`app\<version>`, `app\current`), same service configuration (LocalSystem, auto-start,
   restart ×3 at 10 s), same firewall group and Defender exclusion. It skips the hostname,
   Wake-on-LAN and power because none of them is needed to test the agent, and it is
   documented as development-only in the README. Since 2026-09-07 it also performs
   INSTALLER.md step 8 on request (`-Student`): the M2 acceptance criterion "`student`
   cannot stop the service, kill `session.exe` or read `ProgramData\LabControl`" needs the
   account on `PC-00` before Setup.exe exists, and the VM runs so far were signed in as an
   administrator. Same shape as Setup: standard user in `Users` only, password never
   expires, the auto-logon password in the LSA secret `DefaultPassword` (two `advapi32`
   calls compiled with `Add-Type`, since Windows PowerShell has no cmdlet for it), never in
   the plain registry; the local password policy relaxed with `secedit` only if "1" is
   refused. Groups are resolved by SID, not name, so a Ukrainian Windows works. Hiding the
   administrator from the logon screen is left to Setup: the script cannot know which
   account the owner uses, and it is not needed for the test.

6. **Never an ephemeral TLS key on Windows.** The first VM run (2026-09-05) enrolled fine
   and then failed every mutual-TLS handshake with SChannel's *the credentials supplied to
   the package were not recognized*: `TlsCertificate.ForTls` imported the PKCS#12 with
   `EphemeralKeySet`, which SChannel cannot use for client or server authentication. The
   key now lands in a CNG container for the object's lifetime — the machine store when the
   process is SYSTEM (the service), the user store otherwise (a console on Windows). macOS
   and Linux were never affected, which is why M1's tests could not catch it.
7. **Scripts that Windows PowerShell 5.1 will read are ASCII with a UTF-8 BOM.** Without
   a BOM, 5.1 parses the file as ANSI and a UTF-8 em dash decodes to a curly quote that
   terminates a string. `dev-install.ps1` carries a BOM and no non-ASCII character.

Rejected: provisioning in PowerShell (no `ExportPkcs8PrivateKey` on Windows PowerShell
5.1, and a second implementation of step 4 to keep in step); a `LabControl.Agent.Core`
library shared by the agent and Setup (a seventh project for a routine that fits in one
file of `Shared`); exiting the service when unprovisioned (see 3); using the DPAPI user
scope (the service is SYSTEM, and the key must survive profile resets).

## D-30 — Supervising the session helper (M2)

Context: `D-06` put a second SYSTEM process into the interactive session for capture, input
and the overlay. M2 portion 2 has to make that process *exist reliably* — through logon,
logoff, lock, a crash, an administrator killing it — before M3 puts anything visible in it.
Every choice below favours the behaviour that needs no one to walk to the PC.

Decisions:

1. **The service's own token, not the user's.** `WTSQueryUserToken` yields the student's
   token; a helper started with it could be killed from Task Manager and cannot see the
   secure desktop. Instead the service duplicates its own SYSTEM token, rewrites its
   session id (`SetTokenInformation(TokenSessionId)`, which needs `SeTcbPrivilege` — SYSTEM
   has it) and calls `CreateProcessAsUser` on `winsta0\default` with `CREATE_NO_WINDOW`.
   When `agent.exe --run` is started from a terminal *in* the interactive session, a plain
   `Process.Start` is used instead, so a developer without SYSTEM can still see the helper
   connect. `ARCHITECTURE.md` §2 was corrected to say so.
2. **The helper runs whenever there is a console session** — at the logon screen too, not
   only after a logon (owner's choice, 2026-09-05). That is where the lock screen and UAC
   prompts live, which is exactly what §2 gives the helper SYSTEM for; and with auto-logon
   the distinction hardly exists on a student PC. It is **restarted on logon and logoff**
   (a fresh process per user session, as `ARCHITECTURE.md` promised), when the console
   session id changes, when it exits, when it stays silent for 10 s, or when it has not
   connected 15 s after being started.
3. **Poll is the truth, notification is the alarm.** The service re-reads the interactive
   session through WTS every 2 s (`WTSGetActiveConsoleSessionId`, `WTSUserName`,
   `WTSSessionInfoEx.SessionFlags` for the lock state) and publishes a `SessionState` when
   anything differs. `SERVICE_CONTROL_SESSIONCHANGE` — obtained by subclassing the hosting
   package's `WindowsServiceLifetime` with `CanHandleSessionChangeEvent` on — only wakes
   the poll early and names the `kind`. So the foreground `--run` mode, which gets no SCM
   notifications, behaves the same a little later, and a notification lost during a
   restart cannot leave the console with a stale picture. The lock notification is trusted
   over WTS for the tick it arrives in, because WTS lags it.
4. **The service is the pipe server, created before any helper, `CurrentUserOnly` on both
   ends.** One instance for the life of the service, so the name cannot be claimed by
   anyone else once the service is up; if it is already taken at start (a second agent
   instance) the service reports `session.pipe_unavailable` and keeps retrying. The
   helper's `CurrentUserOnly` makes it refuse a server that is not SYSTEM, so a pipe
   squatted by the student before boot cannot feed it `Input` or `Overlay` commands. The
   first frame must be a `HelperHello` whose `process_id` is the process the service just
   started; anything else is dropped.
5. **Crash policy: fast once, slow when it keeps happening.** One exit → back in 1 s
   (ROADMAP's "within 5 s" with room for the spawn). Five exits within a minute → an
   `session.helper_crash_loop` error to the console and a 30-second pause between
   attempts, so a broken helper build costs a warning row, not a CPU pegged at 100 % on
   fourteen PCs. Spawn failures count as exits and are reported once per distinct message.
6. **`SessionState` carries the current state, `kind` the transition.** `user`, `locked`
   and `helper_alive` are always current, so any single message is enough to draw the
   tile; the latest one is re-sent by `AgentLink` right after every `Welcome`. The console
   shows *user (locked)* on the tile, an orange *session helper not running* line when the
   helper is down, and writes logon / logoff / lock / unlock to the events panel.
7. **The helper logs to its own rolling file** next to the agent's
   (`ProgramData\LabControl\logs\session-<date>.log`) and has `session.exe --probe`
   for a hand check of what a process in the session can see. It is a hidden console
   process rather than a WinExe so that probe output is visible in a terminal.
8. **Only Serilog logs in the service.** `AddWindowsService` quietly registers the Windows
   Event Log provider; at OS shutdown the Event Log service can stop before the agent, and
   the first log call after that threw *RPC server is unavailable* through the whole loop
   (VM run, 2026-09-05). Providers are now cleared *after* `AddWindowsService`, so the only
   sink is the rolling file, which needs no other service to be alive.

Rejected: `WTSRegisterSessionNotification` in the service (needs a window and a message
loop in a service); polling only (works, but a lock would show up to 2 s late and the
event's `kind` would have to be guessed); notification only (a lost notification leaves the
tile wrong until the next change); the helper as the pipe server (the service would have
to find the right instance, and a helper that died mid-connect leaves a dangling name);
letting the helper survive logoff (a SYSTEM process does — but the per-user state M6's
whitelist will keep belongs to one logon); a per-launch shared secret on the command line
instead of `CurrentUserOnly` (visible to administrators, and unnecessary).

## D-31 — Scripts: delivered through `PullFile`, kept in the console (M2 portion 3 / M4)

Context: `run_script` (ROADMAP M2) needs two things settled before it is built: how the
script gets to the PC, and what the teacher sees. The first looked like a small choice
between putting the text into `Job.args` and using the file channel; the second was first
sketched as a "pick a `.ps1` from disk" button, which the owner rejected on 2026-09-05: a
button that makes the teacher open Explorer to find a file and a separate editor to learn
what it does is not a feature, it is homework.

Decisions:

1. **The script travels as a file through `PullFile`, hash-verified.** The console stores
   the text under its SHA-256 and the job carries `ref`, `sha256`, `shell`
   (`powershell|cmd`), `as` (`system|user`) and `timeout_s`; the agent pulls, verifies,
   writes the file under `ProgramData\LabControl\jobs\<id>\` and runs it. `PullFile` is
   built in M2 portion 3 in its minimal form (no resume) because M2 portion 4 needs it for
   the bundle anyway and M4 needs it for packages and materials; one channel for every
   file, verified the same way, rather than a second "small file" path in `Job.args` that
   would have stayed in the protocol forever. Resume after a reconnect is added in M4 with
   the files that are large enough to need it.
2. **Every `run_script` parameter is in the protocol and the agent from portion 3**, even
   though the console does not expose them until M4. The M4 view is then only an interface
   over a finished mechanism; nothing new has to be proven on the agent later.
3. **Portion 3 gets a development-only console action, not a UI.** *Run test script* sends
   the built-in acceptance scripts (100 lines then `exit 3`; a script that hangs) to the
   selected PCs; results show in the existing jobs panel. It is documented as development
   affordance in the spirit of `D-27` and is replaced by the library in M4.
4. **The script library is part of the console's data, not files on the teacher's disk
   (M4).** A script has a name, a one-line description, shell, run-as, timeout and text;
   the *Scripts* view shows the list and the full text side by side and edits in place, so
   what a script does is visible where it is run from. The library is `scripts.json`
   beside `lab.json`, carries a `schema_version` (`D-20`) and is therefore inside the
   backup: it moves with the lab to the next teacher machine (`D-13`), which a folder of
   `.ps1` files on one laptop would not. The repository's `scripts/` directory is the seed
   imported on first run and nothing more afterwards.
5. **Plain text editor, no more.** Monospaced multi-line text, no highlighting, no
   completion, no parameters, no schedule. Scripts here are ten lines; anything larger is
   written elsewhere and pasted.

Rejected: the script text in `Job.args` (message-size bound, no attachments, a permanent
second delivery path); a file picker over `scripts/` (see context); a script library only
on disk (not in the backup, not visible from the console); building the library in portion
3 (the agent has to be proven on the VM first — both earlier portions found a Windows-only
bug there); syntax highlighting and parameters (add when a real script needs them).

## D-32 — Power, scripts and the file channel on a real PC (M2 portion 3)

Context: portion 3 puts the first *actions* on a real Windows PC — shutdown, reboot, log
off, run a script — and the first file transfer. Each raised a small question that would
otherwise be answered differently in the agent, the simulator and the docs.

Decisions:

1. **Shutdown and reboot are immediate and forced** (owner's choice, 2026-09-05):
   `InitiateSystemShutdownEx` with a zero timeout and `bForceAppsClosed`, so a hung program
   never keeps a PC on. Warning the class is Lock / Broadcast's job in M6, not a system
   dialog. The privilege (`SE_SHUTDOWN_NAME`) is enabled *before* the job answers, so a PC
   that cannot shut down says so in the result; the call itself happens
   `Defaults.PowerJobDelay` (2 s) later, after the `JobResult` has left, and a failure at
   that point is an event (`power.failed`) because the result is already gone.
2. **Log off is `WTSLogoffSession`** on the interactive session, not `ExitWindowsEx`, which
   only ever logs off the caller's own session — session 0 for a service. It is synchronous
   enough to answer with the real outcome.
3. **`as: user` runs the script as the student, from the service, not through the helper.**
   The helper is SYSTEM in the student's session; the point of `as: user` is the student's
   *rights* (their profile, their `HKCU`, their desktop), and that needs the student's token
   either way. The service gets it with `WTSQueryUserToken`, builds the environment from it,
   starts the interpreter with `CreateProcessAsUser` on `winsta0\default` and reads stdout /
   stderr through anonymous pipes the child inherits (`UserProcessLauncher`). The service
   never impersonates the student for its own work.
4. **Where the script lands follows who runs it.** As SYSTEM: `ProgramData\LabControl\jobs\<id>\`,
   which `Users` cannot read (ARCHITECTURE §5). As the student: `%PUBLIC%\LabControl\jobs\<id>\`,
   because the student must be able to read the file, and the only account that could tamper
   with it there is the student — who is the account it runs as anyway, so nothing is gained.
   The job directory is **deleted once the result is sent** (owner's choice): the console's
   `logs/jobs-<day>.jsonl` already keeps the output, and fourteen PCs must not accumulate
   debris.
5. **Both shells are made to print UTF-8, and output is decoded as UTF-8.** The first
   version decoded the OEM code page (`GetOEMCP`), which is what the shells print in by
   default — and on the en-US VM (code page 437) every Cyrillic character came back as `?`.
   A lab PC's locale must not decide whether a student's name survives, so the agent runs
   `powershell.exe -Command "[Console]::OutputEncoding = UTF8; & '<file>'; exit $LASTEXITCODE"`
   and `cmd.exe /d /s /c "chcp 65001 >nul && call "<file>""`. The console sends every script
   as UTF-8 with a byte-order mark; the agent keeps the BOM for `.ps1` (PowerShell 5.1
   otherwise reads ANSI, `D-29` item 7) and, for `.cmd`, strips it (cmd.exe would read it as
   junk before the first command) **and normalises line endings to CRLF**: the console
   writes LF on the Mac, and cmd.exe's parser eats the first characters of lines that
   follow a bare LF (`'AME' is not recognized` for `echo user: %USERNAME%`) — proved on the
   VM with the same file in both forms. The rules live in `Shared/Jobs/ScriptText` so the
   Mac tests hold them. stderr lines are prefixed `[stderr] `; output is capped at 10 000
   lines. *(Amended 2026-09-06 after the VM run.)*
6. **`timeout_s` is the agent's inactivity timeout; the console's `timeout_seconds` is that
   plus 30 s.** Both are measured from the last line of output, but the console starts its
   clock at delivery, before the agent has even received the job, so with equal values the
   console could give up a moment before the agent's own *killed after N s* result arrives.
   The grace makes the agent's verdict the one the teacher sees. `Process.Kill(entireProcessTree)`
   takes the children; no Job Object is needed for that.
7. **A job belongs to the PC, not to the link.** `AgentLink` used to run a job under the
   stream's cancellation token, so a dropped link cancelled the job, discarded its result and
   left its id marked *running* in the ledger for ever — the re-sent job was then ignored as a
   duplicate and the console timed out. That never showed in M1 because simulated jobs
   finish in milliseconds. Now a job runs under the agent's lifetime, progress goes to the
   current link or nowhere, the result is queued and flushed after the next `Welcome`, and the
   ledger answers the re-sent copy. Tested in `FileAndScriptTests`.
8. **`PullFile` in its minimal form** (`D-31`): the reference is the SHA-256, chunks are
   64 KiB, `offset` must be 0 (`Unimplemented` otherwise), the last chunk carries the hash
   and the total, the agent verifies against both the job's hash and the chunk's, and a
   chunk that does not arrive within 30 s abandons the pull. Offers live in a console-side
   table (`FileOffers`): text and bytes in memory now, files on disk for M4's packages and
   bundles through the same table. `PullFile` checks the peer certificate like `Link` does.
9. **Wake-on-LAN is bookkeeping in the console, not a job.** A *pending wake* per PC with a
   90 s deadline; `wake.sent` / `wake.woke` (with the seconds it took) / `wake.failed` (naming
   BIOS, Fast Startup, the NIC property and the cable) / `wake.no_mac` / `wake.send_failed`
   as events; *waking…* on the tile until it resolves. Three packets a second apart, to the
   limited broadcast, every subnet's directed broadcast and `last_ip`, because the MacBook is
   on Wi-Fi and the PCs on the wired side of the same router.
10. **The development dialog exposes every `run_script` parameter** and a third built-in
    script, *who am I* (owner's choice, 2026-09-05): without a script that prints the account
    and session, and without choosing `cmd` and `as: user`, those branches of the agent could
    not be proved on the VM before M4 builds a view on them. The dialog goes with the library
    in M4 (`D-31` item 3).
11. **`FakeAgent` pulls the real file and pretends only the shell.** It runs the genuine
    `PullFile` and hash check, then recognises the constructs the built-in scripts use — a
    printed line, `exit N`, a sleep — with the same inactivity timeout the real agent
    enforces, so the console's jobs panel behaves the same against the simulator and the VM.

12. **A planned helper restart is not a crash, and an empty session is not locked.** Two
    corrections from the VM run (2026-09-06): at logoff Windows ends `session.exe` before
    the supervisor's tick sees the session change, and the exit used to be reported as
    `session.helper_exited` with a `session.helper_down` warning on the console before the
    expected `session.helper_ready`. The supervisor now checks the planned reasons (no
    session, session moved, logon/logoff) before the exit code, holds an unexplained exit
    for `HelperExitGrace` (3 s) so the logoff notification that Windows delivers a moment
    later can claim it, and the console skips the warning on a `Logon`/`Logoff` state. Likewise WTS and the SCM both call a session with
    nobody logged on *locked* — that is the logon screen — so the lock flag is unknown
    unless a user is present, and a lock notification for an empty session is dropped.
    Cosmetic but visible: the console's log no longer prints gRPC's Kestrel stack at
    Information for every dropped link (`Grpc` overridden to Warning).

Rejected: `ExitWindowsEx` for log off (wrong session); a countdown dialog before shutdown
(the class is warned by Lock / Broadcast, and a dialog invites the student to cancel);
running user scripts through the helper (SYSTEM, needs the token anyway); giving `Users`
read on `ProgramData\LabControl` for user runs (breaks §5); keeping job directories on the
PC; a `-Command` wrapper to force UTF-8 output (changes `-File`'s exit-code semantics for
no gain over OEM decoding); Job Objects for the tree kill; a per-job `wake` job kind
(the PC is off — nothing can run it).

## D-33 — The minimal push-and-restart (M2 portion 4)

Context: from here on every agent build has to reach the VM and `PC-00` many times a day,
and carrying it there on a stick or through `dev-install.cmd` is exactly the cost the
project exists to remove. The full update of ARCHITECTURE §7.2 — signed manifest,
probation, rollback — is M4; portion 4 builds the smallest honest form of its steps 2 and 3.

Decisions:

1. **The `self_update` job carries only `version`, `ref` and `sha256`; the manifest and the
   files travel through `PullFile`.** The job is in the frozen subset (`D-19`), so it stays
   three short strings. `ref`/`sha256` name a serialized `UpdateManifest` (version, and per
   file its name, size and SHA-256), offered like a script; each binary is then pulled under
   its own hash. M4 adds a `signature` argument over the same manifest bytes and nothing
   else changes shape. The `UpdateAgent` message in the proto is left unused and reserved.
2. **A pushed build is named `<version>+<first 8 hex digits of the bundle digest>`** (extended from agent-only hashing by D-52), for
   example `0.1.0+1a2b3c4d`. `VersionPrefix` does not change with every build, two builds of
   `0.1.0` must land side by side, and a running binary cannot be overwritten; semver build
   metadata is made for this. An installed agent reports the name of the directory it runs
   from as `agent_version` (`Program.InstalledVersion`), so the tile says which build a PC
   runs and the new version recognises the job that installed it by a string comparison. The
   console cannot run `agent.exe` on the Mac to learn the number, so the push dialog asks for
   it (prefilled with the console's own, both come from the same tree) and the agent checks
   it with a **preflight**: the staged `agent.exe --version` must start — which also proves
   the build is for this CPU, `win-arm64` versus `win-x64` — and print the number the bundle
   claims; otherwise nothing is installed and the job says what it printed.
3. **The outgoing agent never reports the result; the new version answers the re-sent job.**
   PROTOCOL said so from the start and the minimal form keeps it, because a "success" sent
   before the restart is a claim the sender cannot back up. Mechanics: after repointing the
   service the agent asks for the restart and waits for the stop as cancellation of the job;
   `AgentLink` now *forgets* a job cancelled that way (`JobLedger.Forget`), so the copy the
   console re-sends on the next link is admitted instead of being taken for a duplicate; the
   new version sees `version` equal to its own and answers *Running X now (was Y)*. The
   console's inactivity timeout on the job is 5 minutes, because the silence between the
   last progress line and the new version's first link is the restart itself. If the stop
   does not arrive within 60 s the outgoing agent puts the service, the markers and the
   directory back and fails the job.
4. **The restart is done by a separate process running the old executable.** A service
   cannot outlive its own stop to issue the start, so the agent spawns
   `agent.exe --restart-service` (`ControlService` stop, wait for *stopped*, `StartService`)
   from the version that is already proven on this PC — the new one has so far only proved
   it can print its version. Rejected: exiting with a non-zero code so the recovery action
   restarts the service (burns the daily restart budget and looks like a crash in the log),
   `sc.exe` or PowerShell from the service (the agent is C# and Win32 only), a separate
   `updater.exe` (`D-19` already rejected it).
5. **Staging under `ProgramData\LabControl\update\<version>\`, then one move into `app\`.**
   A pull that fails leaves nothing under `app\`, and the service manager never sees a
   half-written version directory. `app\previous` names the version before the last push
   and is not cleared in M2 — acceptance is M4's probation. When the new version answers the
   re-sent job it **prunes** every version directory other than `current` and `previous`, so
   a day of pushes does not fill the disk (`D-32` item 4's rule against debris on fourteen
   PCs); the two directories the acceptance criterion asks for stay.
6. **Every refusal happens before anything is written** (`UpdateBundle`, shared with the
   simulator): a manifest whose version is not the job's, a path where a file name should
   be, a bundle without `agent.exe` or `session.exe`, a size or hash that does not fit, a
   file that arrives with another size, a preflight that does not start, times out or prints
   another version. The staging directory is removed and the job fails with the reason. An
   agent that is not running from `app\<version>\` — a build directory, `--run` from a share
   — refuses the push and says to install first; a foreground `--run` from inside `app\`
   installs the files and stops there, since it has no service to repoint.
7. **`FakeAgent` pulls everything for real and pretends only the install**, then drops the
   link, throws cancellation and comes back claiming the new version — the console's entire
   view of a push, exercised on the Mac and tested in `PushBuildTests`.
8. **The firewall rule is by port, never by program path** (added 2026-09-07 after the
   first push on `PC-00`). `dev-install.ps1` used to allow inbound traffic for
   `app\0.1.0\agent.exe`; the pushed version ran from `app\0.1.0+d82bee07\`, matched no
   rule, and Windows Firewall silently dropped the console's beacon (inbound UDP 47801) —
   the service was *Running*, the layout was right, and the PC stayed offline for twelve
   minutes until the rule was replaced. The VM never showed it because its console address
   was pinned and no beacon was needed. The rule is now *LabControl Beacon*, UDP 47801 by
   port, in the same group; Setup.exe (M4) does the same, and the updater has nothing to
   touch. Rejected: rewriting a per-program rule on every push (one more thing that can
   fail between stop and start), the agent adding a rule for itself at start-up (a
   privileged side effect in the service loop for a problem the installer can avoid).

What this form deliberately lacks (M4): a signature, so the push is only as trustworthy as
the mutual-TLS link and the console it came from; probation and rollback, so a new build
that starts and then crashes leaves the PC to its three recovery restarts and then offline,
with the previous version still on disk and `app\previous` naming it — repointing back is
`dev-install.cmd` with the old build or `sc config` by hand until M4. One thing to know:
Windows 11 on ARM runs x64 binaries under emulation, so a `win-x64` build passes the
preflight on the VM and runs there slowly; the lab PCs are x64 and cannot run `win-arm64`,
which the preflight refuses.

9. **Placing the version is retried for 45 s** (added 2026-09-07 after the first M3 portion-2
   push to `PC-10`): the move from `update\<version>` into `app\<version>` failed with
   *Access to the path 'agent.exe' is denied* right after the preflight had run the new
   90 MB executable once — the antivirus was scanning it. An earlier push had hung at the
   same step for its whole 300 s timeout. The agent now retries every 2 s for
   `Defaults.UpdatePlaceTimeout` and reports *waiting for the new files to be released*
   as a progress line, instead of failing on the first denial. The staged files are
   never left behind: a final failure still deletes both directories.

Rejected: a zip bundle (one pull, but a second format to write and read, and per-file
hashes are what M4's manifest needs anyway); the manifest as base64 inside `args` (the job
is frozen; keep it small and readable in the journal); naming directories by hash alone
(loses the version number the teacher reads); parsing `agent.exe`'s PE version resource on
the Mac (a hand-written `VERSIONINFO` reader for a value the preflight verifies anyway);
letting the outgoing agent report success (see item 3).

## D-34 — The screen stream: one JPEG per frame, latest wins, the console asks (M3 portion 1)

Context: M3 is the feature the project exists for, and its two halves live on different
machines — capture on Windows, decoding and drawing on the Mac. M2 showed that anything
only a Windows run can prove should meet the VM already finished on the console side. So
the wire, the frame formats and the console's pictures were built first against a
simulator that draws desktops with real dirty rectangles; `session.exe` gets the same
`VideoUplink` in portion 2.

Decisions:

1. **A full-mode frame is one JPEG of the bounding box of its dirty rectangles, not one
   JPEG per tile.** PROTOCOL's "64×64 tiles" is kept as the *unit of change*: a change is
   grown to whole tiles, so a moving cursor costs one tile and typing a line costs a strip.
   Encoding each tile separately would mean dozens of tiny JPEGs (each with its own
   headers and Huffman tables) per frame and a message per tile; one JPEG of the bounding
   box is cheaper to encode, cheaper on the wire and one message. The console decodes the
   box and blits only the listed rectangles, so a box that spans two distant changes does
   not overwrite what lies between them. A keyframe is the same frame with the screen as
   its one rectangle.
2. **`width`/`height` are the screen, the JPEG is whatever the frame carries.** A thumbnail
   says "this is a 1920×1080 screen" while carrying 320×180 pixels; a delta says the same
   while carrying a box. The console learns the PC's resolution from every frame and knows
   a delta for a screen of another size cannot be applied.
3. **Latest wins, held by the producer.** The uplink queues one frame beyond the one on the
   wire and refuses the rest; a refused frame stays with the producer *together with its
   dirty state*, and the next tick sends the union. Dropping a delta would lose pixels the
   console never sees until the next keyframe; queuing without bound would let a slow Wi-Fi
   pile megabytes up on the PC and show the teacher a screen from ten seconds ago. Holding
   makes a congested link degrade to fewer, larger frames of the current picture.
4. **The console asks; the PC never volunteers video.** A thumbnail control goes to every
   PC right after `Welcome`, the full control when a window opens, thumbnails again when it
   closes, and `request_keyframe` when a delta lands on nothing. The link ending is an
   implicit stop. This keeps the policy — which PCs stream, at what rate, what a second
   console does — in one place, and an older agent that knows no `VideoControl` just says
   `session.not_in_this_build` once.
5. **Two pictures per PC on the console, and the thumbnail keeps moving in full mode.** The
   mosaic tile and the single-PC window read different `ScreenImage`s; while a PC is in full
   mode it sends no thumbnails, so the console scales each full keyframe (one per 5 s) into
   the thumbnail itself. The alternative — the PC sending both streams — doubles the capture
   work on the student's PC for a picture the teacher is not looking at.
6. **SkiaSharp moves into `LabControl.Shared`**, pinned to the version Avalonia 12.1.2
   ships (3.119.4) so the console carries one native Skia. The codec, the geometry, the
   persistent picture and the pacer are pure code used by three producers (simulator,
   helper, and the console's own capture for M6's broadcast) and one consumer; the
   simulator draws with it too. `Shared` therefore allows unsafe code for the two places
   that hand SkiaSharp a pointer into a buffer they must not copy.
7. **Per-PC caps, not a lab-wide budget, in this portion.** 512 kbit/s for thumbnails and 8
   Mbit/s for the one full stream; thirty thumbnails at their ceiling are 15 Mbit/s, inside
   the headroom `D-10` will measure. A shared budget with fair sharing is a later step if
   the measurement demands it.
8. **The UI redraws on a counter, not on the pixels.** `ScreenImage` bumps a version per
   applied frame on a gRPC thread; the view model bumps an observable counter on the UI
   thread at most once per burst per PC; the `ScreenView` control copies pixels into its
   bitmap only when the version moved. Thirty PCs at 2 fps and one at 20 fps are a few
   dozen UI hops a second, and a tile that is not visible costs nothing.

Rejected: one JPEG per 64×64 tile (item 1); a separate thumbnail stream in full mode (item
5); H.264 now (`D-11` — the measurements come first); `WriteableBitmap` as the model type
(ties the store to Avalonia and the UI thread; the tests render headless anyway); dropping
refused frames on the PC (item 3); letting the PC decide when to stream (item 4).

## D-35 — Real capture: one producer for the helper and the simulator, DXGI with a GDI fallback, the service relays (M3 portion 2)

Context: portion 1 finished the console against a simulator; portion 2 has to put real
pixels into the same wire from `session.exe`, a process the Mac can compile but never run.
The VM the owner tests on has a basic display adapter — no desktop duplication — while the
lab PCs have real GPUs, so both capture paths will be exercised, on different machines.

Decisions:

1. **The producer loop is shared code, the source is the only Windows part.** The thumbnail
   / keyframe / delta / pacer logic that `FakeScreenStreamer` carried in portion 1 moved to
   `Shared/Video/ScreenProducer`, driven by an `IScreenSource` that hands out one
   `IScreenFrame` per look (BGRA pixels, row stride, what changed). `FakeScreen` is a source;
   `DxgiScreenSource` and `GdiScreenSource` are the helper's. So the loop the VM runs is the
   one the Mac tests (`ScreenProducerTests`), and a bug in it is found without a VM.
2. **DXGI Desktop Duplication first, GDI `BitBlt` second, chosen on every open.** Duplication
   is the cheap path (the GPU copies, Windows names the dirty rectangles, the staging texture
   is the persistent picture so a keyframe costs no capture). Where `DuplicateOutput` is
   refused — the Microsoft Basic Display Adapter, a VM without a WDDM 1.2 driver, another
   duplication already running — the helper falls back to `BitBlt` into two alternating DIB
   sections and `TileDiff` compares them tile by tile (a few megabytes of vectorised
   `SequenceEqual`, cheap next to the JPEG). The choice is announced once as
   `capture.fallback` and is made again each time the producer opens the screen, so a PC
   whose duplication comes back gets it back.
3. **The helper follows the input desktop and is DPI aware.** A lock screen or a UAC prompt
   switches the input desktop to `Winlogon`; duplication then reports `ACCESS_LOST` and GDI
   would capture the wrong desktop. Before every duplication and every `BitBlt` the capture
   thread is attached to whatever desktop has the input (`SetThreadDesktop`), which a SYSTEM
   process may do and the student's could not — the reason §2 runs the helper as SYSTEM.
   `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` is called first thing, otherwise a
   125 % display would be captured, and reported by `HelperStatus`, at its virtualised size.
4. **A source opens when video is wanted and closes when it stops.** A PC nobody is watching
   holds no duplication, no DIBs, no D3D device and burns no CPU; the switch between
   thumbnail and full mode keeps the source (the loop reads its settings every tick) and
   only starts a fresh pacer and a keyframe.
5. **A failing source is reported once per reason and reopened every 2 s.** `no_desktop`,
   `access_lost` that re-duplication could not cure, `no_duplication`, `gdi_failed` — each
   becomes a `capture.<reason>` event the first time, `capture.recovered` when a source
   opens again. Portion 3 turns these into the tile's reason line; the events are already
   there for the VM run.
6. **The service drops a refused frame and asks the helper for a keyframe.** `D-34` holds a
   refused frame with the producer; over the pipe that would mean the helper waits for the
   service to read, and the pipe carries the `HelperStatus` that keeps the helper from being
   killed as silent. So the relay drops the frame and sends the current control with
   `request_keyframe` once, until a frame is accepted again — one whole frame per congestion
   event, nothing lost. In thumbnail mode the request forces the next thumbnail regardless of
   change, which is exactly what a dropped thumbnail needs.
8. **Nobody awaits a pipe write from inside a read loop, and `PipeFraming` no longer
   flushes** (found on `PC-10`, 2026-09-07 15:21). On Windows `PipeStream.Flush` is
   `FlushFileBuffers`, which returns only when the peer has *read* everything written. With
   the console already asking for video, the service wrote `Ping` and then `VideoControl`
   to a fresh helper and flushed; the helper's read loop was answering the `Ping` with a
   `HelperStatus`, waiting for the write lock held by its status loop, whose own flush was
   waiting for the service to read — and the service was in its flush. Every new helper
   hung after `Hello`, was killed as silent 12 s later, and the cycle repeated until the
   console was restarted (no control at connect time). Pipe writes are unbuffered in .NET,
   so the flush bought nothing; it is gone, the pipe has explicit buffers (2 MiB in for
   frames, 64 KiB out), and every write issued from a read loop — the service's `Ping`,
   the initial control, the keyframe request; the helper's `Ping` answer and events — is
   fire-and-forget with its failure logged. The rule is now in PROTOCOL.
9. **The primary display only, no cursor.** The mosaic shows one picture per PC and the lab's
   PCs have one monitor; DXGI's pointer shape is a separate stream and drawing it belongs
   with input (portion 3), when the teacher's own pointer matters.

Rejected: keeping two producers (the simulator's and the helper's) in step by hand; blocking
the pipe read as back-pressure (kills the helper as silent); GDI only (fine for thumbnails,
too slow for 20 fps at 1080p); DXGI only (the VM cannot test it); one DIB and a managed copy
for the comparison (an 8 MB copy per frame the alternating pair avoids); `WTSQueryUserToken`
so the helper runs as the student (cannot attach to the secure desktop, `D-30`).

## D-36 — Remote control: text as text, shortcuts by position, the service raises Ctrl+Alt+Del (M3 portion 3)

Context: the teacher drives a student's PC from a MacBook (tomorrow a Windows PC) whose
keyboard layout, modifier keys and wheel units differ from the PC's. The acceptance test
is typing a sentence with Ukrainian text into Notepad on `PC-00` from the Mac, plus
enough mouse to help a student. The pieces are the console's window (Avalonia events),
the link (`Input` on the `Link` stream), the service (a relay, `D-35`) and the helper
(`SendInput`), with the simulator standing in for the last two on the Mac.

Decisions:

1. **Characters travel as text, never as keys.** The console sends what the platform's
   `TextInput` produced — the Mac's layout, dead keys, Caps Lock already applied — and the
   helper injects it with `KEYEVENTF_UNICODE`. So the PC types the same characters
   whatever layout is active there, and Ukrainian works without switching the student's
   layout. Sending virtual keys instead would type whatever the PC's layout maps that
   key to: the acceptance sentence would come out in Latin.
2. **Keys go by physical position, only when they command or when a shortcut modifier is
   held.** Enter, Tab, Backspace, arrows, F-keys and friends are keys; under Ctrl, Alt or
   Win a character key is sent as the key at that position (`PhysicalKey` → the US scan
   code and its VK), so ⌘C is Ctrl+C whichever layout the Mac has. Avalonia's
   layout-dependent `Key` is not used; its `PhysicalKey` is what Windows itself uses to
   resolve shortcuts. The table is `Console/Services/KeyMap.cs`.
3. **⌘ is Ctrl on the Mac; the Windows key is a button.** A Mac teacher pressing ⌘C means
   copy; mapping ⌘ to the Windows key would open the Start menu on every shortcut. Both ⌘
   and Ctrl become Ctrl; the Windows key and Ctrl+Alt+Del — which the Mac's keyboard
   cannot produce and macOS would intercept — are toolbar buttons. On a Windows console
   Meta is the Windows key. Caps Lock and Num Lock are never sent: they would toggle the
   PC's state while the Mac's own state already shaped the text.
4. **`SendSAS` from the service, with the policy set on first use.** Only a service (or
   Winlogon) may raise the secure attention sequence, and only when
   `SoftwareSASGeneration` allows services; `SendInput` cannot fake it by design. The
   service checks the value before every call and sets it to 1 (services) when nothing
   allows services yet — a one-time registry write under `HKLM\...\Policies\System` that
   the M4 installer will do at install time as well, so a PC already in the lab needs no
   reinstall. `SendSAS` is in the Win32 metadata, so CsWin32 generates it like everything
   else (`NativeMethods.txt`).
5. **The helper injects on its own thread, attached to the input desktop.** `SendInput`
   from a thread on the `Default` desktop does nothing while a UAC prompt or the lock
   screen (`Winlogon`) has the input; the input thread joins the input desktop before
   every call, the way the capture thread does (`D-35` item 3), which is the second reason
   the helper runs as SYSTEM. The pipe's read loop only queues (`InputQueueLength` 1024,
   newest dropped when full) — it never waits on Windows.
6. **Mouse moves are held, the latest wins, flushed every 16 ms; everything else goes at
   once behind the held move.** A pointer sweep at the display's rate stays under ~60
   messages a second and a click always lands where the pointer last was. No back-pressure
   from the link is needed: the messages are tens of bytes and the `Link` stream is the
   control channel anyway.
7. **Everything held is released when control ends.** The mapper tracks pressed keys and
   buttons; turning *Control* off, the window losing focus (⌘-Tab away with Shift down)
   and the window closing all send the ups, so a student never inherits a stuck modifier.
   The mapper is pure and tested (`InputTests` in `Console.Tests`); the window only feeds
   it events.
8. **Absolute coordinates on the primary display, normalised 0..1.** The frame is the
   primary output (`D-35` item 9), the window maps pointer positions through the picture's
   drawn bounds, and the helper multiplies by 65535 for `MOUSEEVENTF_ABSOLUTE`. Resolution
   and window size never enter the protocol.
10. **A character key's `KeyDown` is left unhandled** (found on `PC-10`, 2026-09-07
   16:20: mouse, shortcuts and Ctrl+Alt+Del worked, no text arrived). Avalonia's macOS
   backend asks the OS to interpret a key press — which is what produces `TextInput` —
   only when the `KeyDown` was *not* handled; the window marked every key handled, so no
   letter ever became text. Only a press the mapper sent as a key (modifier, command key,
   shortcut) is marked handled, which also keeps ⌥-characters from being typed twice.
11. **The full-mode cap is 24 Mbit/s, not 8** (measured on `PC-10`, 2026-09-07 16:30).
   Driving the PC gave 15–16 fps, scrolling a page in Edge 3–4: a scroll dirties nearly
   the whole screen, so every frame is a 250–350 KB JPEG and 1 MB/s carries three or four
   a second. The limit was the pacer, not capture or encoding. One full stream at a time
   at 24 Mbit/s is 3 MB/s on a Wi-Fi console — the `D-10` headroom measurement will say
   whether it is comfortable; thumbnails keep their 512 kbit/s each. The console sends
   the cap in every full `VideoControl`, so the number lives in `Defaults` on the console
   side and needs no agent push. Next levers, in order, if scrolling still misses 15 fps:
   a lower full-mode quality for large frames, then H.264 (`D-11`, M7).
9. **Reasons on the tile come from the PC's own events.** The connection keeps the last
   `capture.<reason>` and `input.<reason>` until `…recovered`, plus `session_id` from
   `SessionState`; the tile prefers the PC's reason (*no user session*, *session helper not
   running*, *cannot capture: duplication unavailable*) over the console's own *picture
   stalled*, and the single-PC window greys *Control* out with the same reason. The
   simulator answers Ctrl+Alt+Del with an `input.sas` event and draws the teacher's pointer,
   clicks and text, so the whole path is tested on the Mac.

Rejected: sending every key as a virtual key (item 1); Avalonia's layout-dependent `Key`
(item 2); ⌘ as the Windows key (item 3); `SendSAS` from the helper (not a service); a
low-level hook or `keybd_event` (superseded by `SendInput`); the helper as the student
(`D-30`, and the secure desktop again); an ack per input message (latency for nothing —
video is the feedback); a lab-wide input budget (one PC is controlled at a time).

## D-37 — Full-mode quality: auto by default, three manual steps, the frame says what it used (M3 portion 3)

Context: with the cap at 24 Mbit/s (`D-36` item 11) scrolling a page in Edge gave 10–17
fps, text typed, and the owner asked for a quality setting and an *auto* mode — a lever
between "sharp" and "smooth" the teacher can pull without knowing about bandwidth.

Decisions:

1. **`VideoControl.quality = 0` in full mode means auto**, not "the default". The console's
   full control carries 0 unless the teacher chose a step; the producer starts at q75,
   steps down by 10 each time a delta had to wait for the pacer, floors at q40 (below
   that a page's text is hard to read), and climbs back by 5 after 30 deltas that went
   out free. Keyframes are big whatever the quality and the delta behind one waits for
   the keyframe's bytes, so neither votes. An agent older than this reads 0 as q75 —
   the old default, nothing breaks. Thumbnails have no auto: q50 is cheap already.
2. **The signal is the pacer's wait, not the frame size.** A big frame that fits the budget
   is fine; a small frame that had to wait means the link is behind. The pacer already
   knows, so auto mode is a dozen lines in the producer and no new measurement.
3. **Three manual steps** — High q75, Medium q60, Low q45 — in a combo box in the
   single-PC window, remembered per PC in `AgentScreen.RequestedQuality` for the console's
   lifetime (not persisted: a quality is a lesson-time choice, not lab configuration). A
   keyframe request re-sends the same quality, otherwise the PC would see a changed control
   and restart its budget with a keyframe.
4. **`VideoFrame.quality`** says what each frame was encoded at, so the status line under
   the picture shows *q55 auto* or *q60* — the teacher sees the lever move. An agent that
   predates the field sends 0 and the console shows *quality —*.
5. **This is the second lever `D-36` item 11 named; H.264 stays the third** (`D-11`, M7)
   and now has its number to beat: measured on `PC-10` with build 0.1.4, *Auto* settles
   at q40–50 while scrolling Edge and holds 14–18 fps at 24 Mbit/s, and *Low* is readable.

Rejected: quality by frame size (item 2); a per-lab or persisted quality setting (item 3);
adapting the frame rate instead of the quality (the pacer already does that; a lower
rate at q75 is what 3–4 fps looked like); a quality slider (four choices are enough to
find the one that reads well on the room's Wi-Fi).

## D-38 — M4 in four portions, and the script library as the first (M4 portion 1)

Context: M4 is the largest milestone and three of its four pieces need Windows —
`Setup.exe`, the grown-up file channel on the agent, and the signed `self_update` with
probation and rollback. M2 and M3 both found their Windows-only bugs on the VM, so the
split has to keep every VM day small. The script library, decided in `D-31` item 4, has
nothing to prove on Windows: `run_script` was verified in M2 portion 3.

Decisions:

1. **Four portions, console-only first.** (1) The script library; (2) the file channel —
   resume, `PushFile`, `send_file`, the fan-out and the log bundle; (3) `Setup.exe`, the
   USB payload and the *Build USB installer* action; (4) the full `self_update`. Portion 2
   is the last one the simulator can carry most of; portions 3 and 4 are VM days. The
   ROADMAP's *How it is being built* paragraph names what each portion has to show.
2. **`scripts.json`, not a `scripts/` directory in the data directory.** The M0 layout
   reserved `~/.labcontrol/scripts/` for "scripts pushed to PCs". A directory of files is
   not in the backup unless every file is packed, has no place for the shell, run-as and
   timeout, and invites editing outside the console. One document beside `lab.json`, with
   a `schema_version` (`D-20`), goes into `BackupPayload.scripts` like the machine list
   does; the directory is dropped from `LabStore` and from ARCHITECTURE §4.
3. **The seed is embedded in the console.** `scripts/library/*.ps1|*.cmd` are compiled
   into `LabControl.Console` as resources and imported into `scripts.json` on the first
   run only (`ScriptsDocument.seed_imported_at`). A published console therefore carries its
   seed without a folder next to the binary, and the repository directory stays the place
   to edit it. Once imported, the seed is never read again — the teacher's edits and
   deletions win (`D-31` item 4). The seed files are their own documentation: the first
   comment line is the description, `# run-as: user` and `# timeout: 300` (or `rem` in a
   `.cmd`) set the two fields the file name cannot. `scripts/dev-install.*` stay outside
   `library/`: they are run by hand on the VM, not from the console.
4. **The record speaks the job's vocabulary.** `ScriptRecord.shell` and `run_as` hold the
   same strings `run_script` carries (`powershell|cmd`, `system|user`) rather than a
   second enum serialisation, so the document, the wire and the agent never disagree.
5. **Unsaved text runs once; drafts are kept, not asked about.** *Run on selected PCs* sends
   what the editor holds and the status line says *as typed (not saved)*. Switching to
   another script keeps the unsaved draft in memory and marks the row with a dot; nothing
   is saved silently and no "discard changes?" dialog interrupts the teacher mid-lesson.
   Drafts do not survive closing the console — *Save* is the only way to disk.
6. **The development dialog goes.** *Run test script…* was the placeholder for this view
   (`D-31` item 3, `D-27`). The three built-in scripts stay in `Shared/Jobs/TestScripts`
   because the simulator's pretend shell understands them and the tests use them; the
   teacher who wants them pastes them in.

Rejected: a `scripts/` folder synced through the backup (item 2); reading the seed from
the repository or from a folder next to the binary at run time (a published console has
neither); re-importing missing seed scripts on every start (the teacher's deletion would
never stick); a confirmation on switching scripts with unsaved edits (item 5); parameters,
highlighting and a schedule (`D-31` item 5).

## D-39 — Script editor, quick launch and desktop application templates

Context: the owner requested syntax highlighting, validation and opening/closing Word,
PyCharm, IntelliJ IDEA, Visual Studio and VS Code, and asked whether an administrator
launch could solve PyCharm's interpreter discovery problem (2026-09-07).

Decisions:

1. **Run from the Lab view.** A script selector and the same run command sit above the
   mosaic. The Scripts tab remains the library/editor. Both share the selected script
   and draft; a dirty draft is explicitly labelled in the quick runner. Target selection
   and job/result handling are unchanged.
2. **AvaloniaEdit plus the PowerShell parser, on the console only.** AvaloniaEdit supplies
   editing, undo, scrolling and line numbers; `System.Management.Automation` supplies
   tokens and parse errors without executing code or creating a runspace. Highlighting
   and diagnostics refresh after a 250 ms typing pause. Every run reparses synchronously,
   including quick runs, and refuses parse errors. Saving broken drafts remains possible.
   The bundled parser is PowerShell 7; known newer operators are rejected for the agent's
   Windows PowerShell 5.1. The UI explicitly says that full 5.1 compatibility, paths and
   command availability still require a PC. cmd gets lexical highlighting and only a
   basic NUL check, explicitly labelled; it has no portable parser. This supersedes the
   no-highlighting choice in D-31 item 5 and D-38's rejected alternatives.
3. **Built-ins remain editable, self-contained scripts.** The five apps each get open,
   open with UAC, and graceful close scripts. Discovery checks App Paths, PATH and common
   per-machine/per-user/JetBrains Toolbox locations, with an editable override for custom
   installs. Launch sets the application's directory, uses shell execution and does not
   wait for the GUI to exit. Close requests target visible windows in the current session,
   never force-kill: saving work may require attention at the PC. A job reports the
   request, not that every window has actually disappeared. Elevated applications may
   refuse a close request from the student account.
4. **UAC is explicit, not SYSTEM on the desktop.** The admin variants use `Start-Process
   -Verb RunAs` in the logged-on user's session. Standard students must supply admin
   credentials on the PC's UAC desktop; credentials are never collected or stored by
   LabControl. Existing running instances may reuse their original privileges. SYSTEM's
   profile is different and is not a substitute for the student's interpreter settings.
   `find-python` lists Python registry/PATH candidates visible to the student without
   running them, to help diagnose the underlying setup. No agent/protocol change.
5. **Existing libraries opt in once through Add missing built-in scripts.** This imports
   only missing seed filenames and skips name collisions, preserving existing edits.
   Deleted built-ins stay deleted on startup as before; explicitly pressing the button
   can restore them. Seed import and backups otherwise retain D-38's behavior.

Validation on macOS: solution build, parser/template tests, library persistence tests,
headless editor input and quick-run jobs over fake-agent links. Application discovery,
window lifetime after script exit, save prompts, UAC cancellation/credentials and real
PyCharm interpreter selection still need the Windows VM or a lab PC.

Sources: [AvaloniaEdit setup](https://github.com/AvaloniaUI/AvaloniaEdit/blob/master/README.md),
[PowerShell ParseInput](https://learn.microsoft.com/en-us/dotnet/api/system.management.automation.language.parser.parseinput),
[Start-Process](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/start-process?view=powershell-5.1).

## D-40 — Optional student provisioning and standalone removal for home-PC testing

Context: the owner needs to test the real Windows agent on a home PC without creating a
new user, and to remove it afterwards independently of the USB installer (2026-09-07).

Decision (planned for M4 portion 3):

1. Add *Create student account and enable automatic sign-in*, checked on fresh installs,
   to the PC-number screen. Off skips all account, password-policy, auto-logon and profile
   provisioning. The active session supplies capture/control and user-script execution.
   Repair, update and rekey preserve the persisted choice; no silent account creation.
2. Preserve existing accounts, including one already named `student`; never adopt it or
   change its password. Managed-account actions refuse when none is configured, rather
   than targeting a personal profile. This qualifies the default classroom setup of D-09
   and the managed-account landing of D-23; default classroom behavior is unchanged.
3. Ship a separate `Uninstall.exe` entry point and Windows Installed apps registration,
   backed by the same pipeline as `Setup.exe --uninstall`. It needs no USB, console or
   running agent and handles partial installs. No separate uninstaller project or runtime
   is required by this decision.
4. Record setup-owned changes and prior settings in a protected, schema-versioned journal.
   Restore only values still matching Setup's changes; preserve later edits and report
   conflicts. Keep accounts/profiles by default. Explicit account removal requires proof
   of creation by this installation (SID) and confirmation; never delete existing users.
   Existing sign-in secrets must never enter plaintext files or logs. Missing ownership
   records mean conservative cleanup with a clear report, not guessed account deletion.
5. Show other system changes before installation: the account checkbox alone does not
   disable hostname, power or firewall configuration. Uninstall reverses installer-owned
   changes, not arbitrary scripts or software installations requested from the console.

Acceptance: the home-PC install/reboot/control/script/repair/update/uninstall round trip,
plus default classroom setup, existing-account collision and interrupted installation,
are required in ROADMAP M4. Details and removal boundaries live in INSTALLER.md.

Rejected: requiring a VM or a new Windows user for every test; only providing a USB-bound
removal command; deleting any account merely because its name matches `student`;
restoring guessed system defaults instead of the recorded previous values.

Status: planned only; no installer or agent implementation in this documentation change.

## D-41 — Resume a running file pull at completed chunk boundaries (M4 portion 2)

Context: scripts and update binaries already use `PullFile`; the next planned file-channel
step is surviving a reconnect without re-downloading a large file from the beginning.

1. Keep the destination open, its completed byte count and the incremental SHA-256 across
   RPC attempts. A local chunk write uses the job/agent lifetime, not the link token, so
   a disconnect cannot cancel it halfway through and invalidate the offset. No seeking
   or reading back the destination is required. Restart-persistent partial files are not
   introduced; existing caller cleanup still discards failed transfers.
2. Resume through the current authenticated client at that byte count. Retry transport
   unavailability, cancellation of a connection and premature EOF, with a short bounded
   delay. One 30-second inactivity budget covers RPCs and reconnect waits; only new bytes
   reset it. Caller cancellation and service stop interrupt recovery immediately.
3. Validate reference, absolute offset, chunk size, terminal size and both hashes. Protocol
   and integrity failures are terminal. The server accepts EOF offsets and always emits
   final metadata, including for zero bytes and exact chunk-size multiples. Size changes
   to an offered disk file fail; other mutations fail the end-to-end hash check.
4. Preserve the existing wire fields and offset-zero behavior. An old console's refusal
   to resume is a clear failure; never append its offset-zero response over an existing
   prefix. Offers are not replicated between consoles or persisted by this change.

Validation: boundary sizes and suffix requests, invalid offsets, two mid-transfer link
breaks with an append-only destination over real loopback TLS, and cancellation during
reconnect. Windows runtime and the fleet-scale file acceptance remain pending.

## D-42 — Explicit upload grants and a committed-offset query (M4 portion 2)

Context: the next file-channel step after D-41 is resumable `PushFile`, the transport
needed for returning per-PC logs and, later, collected work.

1. The console grants a specific PC an opaque reference, exact size, expected SHA-256
   and a locally selected staging stream. No network-provided path is opened. The caller
   controls storage lifetime and discards an unverified destination. Grants implement
   async disposal so in-flight writes finish before the caller closes its stream.
2. Add `GetUploadStatus(FileRequest) -> FileAck` rather than overloading empty chunks
   as queries. It reports committed bytes and verified completion. This resolves both
   an uncertain mid-stream write and a lost final acknowledgement without retransmitting
   the whole file. An additive `FileAck.sha256` binds every response to the grant's content,
   so reusing a completed reference with a different hash cannot report success. Existing
   fields and the frozen subset retain their meanings.
3. Serialize calls per grant. Retain the incremental SHA-256 and completed byte count
   across attempts; truncate a cancelled partial write before accepting a retry. Enforce
   64 KiB chunks, absolute offsets, expected length and both terminal/expected hashes.
   A failed hash or storage error closes the grant. Completion includes flushing.
4. The agent requires a seekable, unchanged source, queries before every attempt, and
   retries transient transport loss under the shared file inactivity timeout. Repeated
   sends of the same bytes do not extend that budget. Cancellation stops promptly.
5. This step is transport only: no collection job, UI, automatic filesystem destination
   or persisted upload registry. Callers must dispose grants; unfinished state lasts only
   for that grant in the current console process. A migrated console must issue a new
   grant, and an older console's `Unimplemented` is a clear failure.

Validation on macOS: loopback TLS round trips, boundary sizes, repeated link loss,
completion acknowledgement loss, cancellation, peer isolation and malformed/corrupt data.
Windows runtime and fleet-scale verification remain pending. D-43 subsequently builds
the fan-out log bundle from existing job output, without an upload consumer.

## D-43 — Per-PC result bundles from the existing job stream (M4 portion 2)

Context: group actions already fan out over independent agent links and share a batch id.
M4 needs a durable per-PC log bundle that the teacher can inspect and export.

1. Record the complete roster before dispatch, then atomically save a schema-versioned
   snapshot at creation and on every terminal update in `logs/batches/<batch-id>.json`.
   Capture PC numbers when the batch is registered, so later inventory changes do not
   relabel the result. Deduplicate repeated target IDs within one action. Late results
   replace a timeout in the snapshot, following the existing queue semantics.
2. Copy status and output under the queue lock; serialize detached copies. Serialize
   automatic saves under a separate log lock, taking a fresh snapshot inside it so an
   older callback cannot overwrite a newer result. Disk errors raise `jobs.log_failed`
   without aborting job delivery or the agent link.
3. *Jobs → Export batch logs…* captures the selected row's whole batch as it stands now.
   The ZIP contains a schema-versioned manifest and one schema-versioned JSON per PC,
   including identity, timestamps, state, exit code, message and captured output. An
   incomplete batch is explicitly marked incomplete. Write beside the chosen destination
   and replace only after closing the ZIP; failed exports remove the temporary file.
4. Use only existing `Link` output. Do not include job argument maps, script source,
   certificates, enrollment data or other files from either machine. Output intentionally
   printed by scripts is retained just as in the Jobs panel. This is not a diagnostic
   upload or collected-work consumer of `PushFile` and adds no RPC or dependency.
5. Reports are local to the originating console and remain readable after it closes;
   they are not a persisted execution queue or part of lab backup. On-disk snapshots are
   from creation/terminal updates, while explicit export captures current progress.
   UI history reloading and resuming jobs after console restart are outside this step.

Validation: mixed online/offline outcomes over loopback TLS, 30 concurrent PC results,
Unicode output, late results after timeout, snapshot isolation, schema refusal, export
cleanup and failed log storage without interrupting delivery. Headless UI covers the
export action's selection state and layout. Real Windows/fleet acceptance remains pending.

## D-44 — Handout dispatch and simulator delivery before managed Windows profiles

Context: M4 portion 2 has resumable transport and batch logs. D-40 requires recorded
ownership before delivery may touch a Windows student profile; Setup does not yet record it.

1. Build the console and simulator part now. *Send files…* selects local files and captures
   the PC selection when opened. Validate all names and hash all sources before dispatch;
   reject duplicate Windows names, paths, alternate streams, reserved devices and trailing
   dots/spaces. Preserve Unicode names. Hashing runs off the UI thread. Sources remain
   on disk, must remain available and unchanged until all queued jobs finish, and are
   re-read through the existing file offers; no whole-file RAM copy is made. The toolbar
   wraps at narrow window widths so every action remains reachable.
2. One click has one batch id, with a job for each file/PC pair. Record the complete roster
   before dispatch. Names appear in the Jobs rows; the existing per-PC ZIP holds every
   file job. Offline PCs remain pending under the existing process-local queue semantics.
3. FakeAgent really pulls and verifies bytes into a random temporary file under its own
   data directory's `Materials`, then replaces the named file only after verification and
   closing the stream. Failures discard the temporary file and preserve an older handout.
   Download progress keeps the job active, using a new optional local pull callback;
   reconnect and hash rules are unchanged. This simulator landing is not a privileged
   Windows filesystem implementation and must not be reused as one without profile and
   reparse-point protection under the student identity.
4. `open` defaults off. With it on, only `.pdf`, `.docx`, `.xlsx`, `.pptx`, `.odt`, `.ods`,
   `.odp`, `.txt`, `.png`, `.jpg`, `.jpeg` are eligible. Executables, scripts, shortcuts
   and all other formats are delivered without opening. FakeAgent reports the requested
   opening as simulated and launches nothing on the teacher's computer.
5. The dialog explicitly states current availability: simulator only. Windows agents still
   refuse unsupported `send_file`. Managed-account resolution, delivery under that account,
   default-application opening as that user and Windows/fleet acceptance remain pending.
   This is a portion-2 slice, not completion of M4 or of Windows handout delivery.

Validation: shared wire/name/open rules, multi-file two-PC TLS delivery, zero-length and
chunk-boundary files, replacement, corrupt-hash preservation/cleanup, offline roster and
multi-file batch logs, plus headless dialog/toolbar rendering. Existing reconnect tests
continue to cover the shared transport. No RPC, dependency or installation setting added.

## D-45 — Persist account mode and creation evidence before Windows Setup integration

Context: D-44 handout delivery is waiting for the D-40 managed-account prerequisite.
Build and test that prerequisite's state rules on the Mac before adding privileged calls.

1. Keep `installation.json` separate from enrollment/trust and version directories. Its
   schema-versioned account section records an installation id, the explicit mode, pending
   creation and the SID returned by successful account creation. No password, sign-in
   backup, arbitrary setting values or profile paths enter this document.
2. Only a positively identified fresh install defaults creation on. Repair retains the
   saved choice. Missing legacy history requires an explicit choice and cannot authorize
   adoption or removal. Invalid or future documents are refused rather than overwritten.
3. Persist intent before creating an account; persist the resulting SID before configuring
   sign-in or enabling managed-profile operations. The completion method also requires a
   creation begun in this process. After a crash, an existing account without a saved SID
   remains unowned. Retrying is permitted only after Windows reports the name absent;
   create-new must still refuse races with another creator. Prefer a reported unresolved
   account over guessing ownership and risking a personal profile.
4. Account plans require the current SID to match the saved SID. A deleted or recreated
   account is a conflict, not permission to recreate/adopt it. Explicit opt-out retains
   history while disabling account-specific actions. Removal defaults to keep and requires
   both an explicit request and deletion confirmation, enabled mode and matching SID.
   These are plans, not authorization to delete a path: Windows must operate by verified
   identity and protect against reparse points and account changes at execution time.
5. `InstallationState` uses the existing atomic JSON store. Its Windows caller must hold
   an exclusive setup lock and establish SYSTEM/Administrators-only directory ACLs first.
   Lookup errors must propagate, never masquerade as account absence. The Windows adapter,
   protected prior sign-in store, other setting restoration and executable pipeline remain
   pending; this slice does not retrofit ownership into dev-script installations.

Validation: shared tests exercise fresh/repair modes, legacy history, account collision,
SID replacement, interrupted creation, opt-out/in, deletion gates, invalid/future journals
and a failed intent write. No new dependency, RPC or Windows runtime verification.

## D-46 — Windows account preparation under private storage and an exclusive lock

Context: D-45's journal is the prerequisite for Windows handout delivery, but it needs
an actual create-new adapter before Setup can record any legitimate account ownership.

1. `AccountSetupScope` is the preparation component used by the future Setup pipeline.
   Require elevation, reject reparse points in the storage path, create a fresh directory
   with its final private ACL, and reject existing storage whose owner or ACL is not
   SYSTEM/Administrators-only. Require the directory to pass private permissions to new
   files; validate existing journal/temporary/lock files too. Never repair a permissive
   journal's ACL and then accept its old ownership claims.
2. Hold `setup.lock` open with `FileShare.None` through intent, native creation and SID
   recording. Closing the scope releases it, including on failure; leave the empty file
   in place to avoid racing another opener. This serializes cooperating Setup processes;
   it is not protection against another administrator changing Windows accounts.
3. The shared `StudentAccountProvisioning` coordinator preserves D-45's saved mode,
   skips even lookup with mode off, and writes intent before any create-new call. Native
   lookup uses local `NetUserGetInfo(23)`; only `NERR_UserNotFound` means absent. Every
   other native failure propagates to the setup caller with an operation/numeric status,
   never account passwords or native call arguments in the error.
4. `NetUserAdd` returns status, not a SID. Create a **disabled** account with a fresh
   per-call marker in its comment, then immediately read the SID and verify marker and
   disabled flag. This read-back is allowed only after success; never after already-exists
   or on repair. The marker is public correlation data, not a secret or persisted proof
   of ownership. A replacement or uncertain result fails closed; no cleanup deletes a
   name. Persist the resulting SID, then recheck current identity before reporting success.
5. Account activation, Users membership/verification, the final description, auto-logon
   and password-policy fallback wait for the protected settings journal and executable
   pipeline. A failed preparation can leave a disabled account, explicitly unowned when
   SID recording failed. Existing accounts remain untouched. No current CLI command runs
   this component, and Windows behavior is not claimed verified on the Mac.

Validation: Mac coordinator tests cover intent ordering, idempotent repair, opt-out,
lookup errors, native refusal, a concurrent creator, replacement after creation and
failed intent/SID writes. Cross-compilation checks the native adapter; VM tests must
verify local SAM behavior, elevation, ACL inheritance, redirects and concurrent scopes.
No new package family or protocol field; Setup now references the already pinned CsWin32.

Sources: [NetUserAdd](https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/nf-lmaccess-netuseradd),
[USER_INFO_23](https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/ns-lmaccess-user_info_23).

## D-47 — Encrypt the settings journal and restore only confirmed, unchanged values

Context: D-46 deliberately leaves the created account disabled until settings can be
restored safely. D-40 also requires preserving existing sign-in secrets and later edits.

1. Keep a separate `setup-settings.json` with an encrypted inner document carrying its
   own schema version and installation id. `installation.json` remains account evidence
   without setting values. `AccountSetupScope` uses existing machine-scope DPAPI with
   purpose `labcontrol/setup-settings/<installation-id>`; no fallback, new package or
   secret on USB. Both final and temporary files contain ciphertext only. The scope
   checks their private ACLs and rejects redirects; it owns all journal operations.
2. Initialize once before a positively identified fresh setup changes settings. Never
   recreate missing repair/removal history or infer settings ownership for dev installs.
   Unknown schema, malformed data, decryption failure or another installation's document
   prevents native operations. Do not attach decrypted parser details to errors.
3. Native adapters are supplied by code, not constructed from journal paths/commands.
   Each names one setting and encodes its type/value canonically; null means absent.
   Save and flush original/desired values before mutation, reread before writing, verify
   read-back and then save completion. Already-correct values are left unowned, so a
   pre-existing exclusion or rule is never removed by this journal.
4. Repair retains the first original value and refuses a changed desired value or a
   later user edit. A pending apply may retry only when the original still matches. If
   the value changed before completion was recorded, equality with the desired value
   cannot prove who wrote it: report a conflict and preserve it on removal too.
5. Restore only a confirmed applied value that still matches. Save restoration intent
   before writing the original; after an interruption, retry from the applied value or
   record completion when the original is already present. Leave later edits intact.
   Retain restored records to make repeat removal harmless; a new setup after removal
   needs a fresh installation or an explicit future migration flow.
6. This is a component, not a claim that Setup.exe can install/remove a PC yet. The
   registry/LSA/firewall/power adapters and pipeline remain pending. Adapters must guard
   native identity and concurrent changes (the Setup lock only serializes Setup), keep
   values out of errors, and skip account/sign-in operations when account mode is off.
   Multi-setting sign-in sequencing is a future pipeline responsibility.

Validation: Mac tests use authenticated encryption and a fake setting surface to exercise
ciphertext-only storage, all write interruption boundaries, absent versus empty values,
pre-existing settings, repair and user conflicts, failed intent/completion saves, read
failures and corrupt/future/missing/mismatched history. Windows DPAPI runtime verification
and the D-40 home-PC round trip remain pending. No protocol change or new dependency.

## D-48 — Start native settings integration with two fixed DWORD policies

Context: D-47 has durable restoration rules but no Windows settings adapter. Add the
two existing installer step-7 registry policies before the multi-setting sign-in flow.

1. `WindowsMachineRegistryStore` only addresses Fast Startup and SoftwareSASGeneration
   through a code-defined enum. Paths/names live in `Defaults`; use 64-bit HKLM explicitly
   for both Windows builds. Journal bytes cannot supply paths or select account settings.
2. Accept DWORD or absent values only. Encode the DWORD type plus all four little-endian
   bytes; absence stays null. Unexpected native types fail unchanged rather than being
   coerced and losing their original representation. Existing system parent keys are
   required; do not create/delete registry trees or infer their ownership.
3. Fast Startup requests DWORD 0. SAS requests DWORD 1, preserving an existing DWORD 3.
   Already-correct settings remain unowned. A later change from an owned 1 to 3 is still
   a journal conflict; do not broaden ownership merely because 3 is acceptable.
   `ApplyFromCurrent` validates history before reading Windows or choosing the desired
   value; missing history never triggers a native read just to choose a policy.
4. Enumerate value names and check type before reading; a failed read must not masquerade
   as absence. Recheck the expected value using the same writable key handle immediately
   before writing/deleting, flush and verify. This is optimistic conflict detection,
   not atomic isolation from Group Policy or other administrators. Keep that limitation
   explicit; concurrent Windows configuration is outside the setup lock's protection.
5. Scope methods expose apply/restore through the encrypted journal. They do not activate
   accounts, configure sign-in or change agent behavior; no CLI invokes them yet. Native
   failures contain no values or nested native error details. No new package or protocol.

Validation: Mac tests cover typed snapshots through the encrypted journal, all DWORD bits,
absence, SAS 3 preservation, idempotent repair, later user changes, the native write guard,
failed reads and malformed saved snapshots. Native registry/DPAPI behavior still needs
the Windows snapshot checks in INSTALLER.md; M4 remains in progress.
Build passed with two existing Avalonia window warnings; all 369 solution tests passed
on macOS, and self-contained Setup publishes succeeded for win-x64 and win-arm64.

API reference: [RegistryKey.GetValue](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.registrykey.getvalue?view=net-10.0).

## D-49 — Bind AC power timeouts to a scheme and confirm native activation

Context: installer step 7 requires AC sleep never, display off after 20 minutes and disk
idle never. A power setting is more than a DWORD: its scheme identity matters, and writing
its index does not activate it.

1. `PowerPlanSetting` exposes only three code-defined AC timeout policies. Native GUIDs
   and desired seconds live in `Defaults`; `WindowsPowerPlanSystem` uses CsWin32 power
   APIs, without shell commands, localized text parsing or new dependencies. No DC,
   hibernation, NIC or other scheme settings are included in this slice.
2. Snapshot the active scheme GUID plus uint32 seconds in a versioned canonical format.
   Null/invalid snapshots and native failures are errors, never absence/defaults. Journal
   IDs stay fixed so repair on another active scheme conflicts with the first baseline.
   Journal data cannot select a new native target: writes require its scheme to match
   the freshly observed active identity. Never create, delete or switch to an old scheme.
3. Read the scheme before/after reading its value; reread before mutation, write to the
   explicit observed GUID and verify read-back. Before activation repeat identity/value
   checks and verify afterwards. This is optimistic conflict detection, not isolation:
   an external writer can race the final check/call, and Group Policy may override it.
4. Add optional `ISetupSettingActivation` to the protected journal. Call it after saved
   value read-back but before completion is persisted. Confirmed repair also reactivates;
   an interrupted restore whose original value is already present must activate it before
   being called restored. Repeated restoration checks the original before activation so
   later user changes are preserved. Existing adapters keep their previous behavior.
5. An apply interrupted after writing remains ambiguous under D-47, including an activation
   failure; do not infer ownership just because the desired index is present. A pending
   restore may retry activation from the original value. Already-correct unowned settings
   are neither changed nor activated. The scope exposes the component, but executable
   integration and Windows runtime verification remain pending.

Validation: Mac tests use separate persisted/effective values to exercise activation
failure/recovery, repair, changed schemes and values, races around native operations,
malformed snapshots and original-value restoration. No protocol change. Windows checks
are listed in INSTALLER.md; M4 remains in progress. Build passed with two existing
Avalonia warnings, all 392 tests passed on the full rerun (23 new power cases), and
self-contained Setup publishes passed for win-x64 and win-arm64. The first full run
also had one existing file-resume TLS failure that did not reproduce on the rerun.

API references: [PowerWriteACValueIndex](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powerwriteacvalueindex),
[PowerGetActiveScheme](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powergetactivescheme),
[PowerSetActiveScheme](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powersetactivescheme).

## D-50 — Journal the Windows Update active-hours tuple as one policy

Context: installer step 7 requests active hours 07–20 without disabling updates.
Three independent ownership records could restore a mixture of Setup and user choices.

1. `UpdateActiveHoursSetting` journals enable/start/end together with versioned,
   canonical absent-or-DWORD slots. Original DWORD bits are preserved even outside the
   normal hour range. A change to any member makes the whole policy a conflict.
2. `WindowsUpdateActiveHoursStore` addresses only the three fixed policy names in
   64-bit HKLM; paths, names and desired hours are in `Defaults`. Unsupported native
   types and failed reads are refused before mutation. The existing Windows policy
   parent is required. The fixed WindowsUpdate leaf can be created when missing;
   removal restores values but retains the leaf, which may now contain others' settings.
   An empty key and a missing key are equivalent only for these three values.
3. Read the tuple twice, then recheck the entire expected tuple before and after each
   mutation on the same writable handle. Write start/end before enabling; restore a
   disabled or absent enable flag before restoring the range. These writes are not
   atomic: partial apply/restore failures remain explicit conflicts unless a pending
   restore has already reached the complete original tuple. Never guess ownership or
   overwrite another writer's edits to repair a partial write.
4. Use the existing encrypted journal and scope. Account mode does not gate machine
   update policy. No update-disable, notification suppression, scheduled restart or
   competing policy is changed. Registry read-back is not proof of effective Windows
   Update behavior; Group Policy, MDM and deadlines may override active hours. This
   standalone LAN installer uses the registry without introducing management infrastructure.
5. This completes a settings component, not the executable installation pipeline.
   No CLI calls it yet. Windows execution and the home-PC round trip remain pending.

Validation: Mac tests cover tuple restoration, absent and unusual originals, unowned
correct settings, edits to each member, interrupted apply/restore, malformed snapshots,
missing history and native failures (20 new cases, all passed). Build passed with zero
warnings and self-contained Setup publishes passed for win-x64 and win-arm64. The full
suite passed 410/412: the existing two BeaconTests failed while a running console shared
the discovery port; diagnostics heard its beacons instead of the test consoles.
Windows checks are in INSTALLER.md. No new NuGet package or protocol field.

Source: [Microsoft: manage device restarts after updates](https://learn.microsoft.com/en-us/windows/deployment/update/waas-restart).

## D-51 — Gate the LSA autologon secret on recorded student ownership

Context: D-47 can protect prior sign-in state, but Setup still needs a native adapter
for the LSA password required by installer step 8. This slice does not enable autologon.

1. `StudentSignInSecret` checks saved account mode before SAM, LSA or settings-journal
   access. Off returns a skipped (null) result. On requires the current local student
   SID to match recorded creation evidence, including before each native read/write.
   Missing ownership/settings history fails closed; there is no name-based adoption.
2. Journal only the code-defined `DefaultPassword` LSA secret. A version byte precedes
   raw UTF-16LE data; null is absence, an empty payload is a present empty secret.
   Preserve original code units, including embedded nulls; never round-trip through a
   null-terminated managed string. Reject malformed lengths/formats.
3. `WindowsStudentSignInSecretStore` uses CsWin32 `LsaOpenPolicy`,
   `LsaRetrievePrivateData` and `LsaStorePrivateData` on the local machine. Read requests
   only private-information access; write additionally requests create-secret access.
   Only STATUS_OBJECT_NAME_NOT_FOUND means absence. A null store argument deletes the
   secret; an empty value uses a non-null native string and buffer.
4. Re-read the expected secret on the same policy handle immediately before writing,
   recheck account identity and verify native read-back. Guard failures include no values
   or native exception details. This is optimistic checking, not atomic isolation from
   other SAM/LSA writers. D-47 retains ambiguous interrupted applies and later edits;
   a pending restore can complete when the original is already present.
5. Clear adapter-owned managed read/write buffers and returned native secret bytes
   before freeing them. Journal copies remain transient managed data under D-47; only
   DPAPI-protected ciphertext is persisted, including temporary files. No plaintext
   registry password or diagnostic output is introduced.
6. Expose apply/restore through `AccountSetupScope`, but no executable calls it yet.
   The future sign-in pipeline must disable/coordinate existing autologon before changing
   its secret, journal Winlogon identity/enable values, enable only after complete setup,
   and restore in a safe order. This adapter alone must not be used as a complete
   autologon setup step. Account activation and group configuration remain pending.

Validation: Mac tests cover absent/empty/Unicode originals, opt-out without native access,
missing/replaced accounts, missing journals, later edits, read/write failures, interrupted
apply/restore, malformed native bytes and clearing owned buffers. Native LSA/DPAPI and
actual sign-in need a Windows VM snapshot. All 18 new tests passed; the full suite passed
428/430 with the same two existing UDP discovery failures alongside the running console.
Final build and self-contained Setup publishes for win-x64 and win-arm64 passed.
No new dependency or protocol field.

Sources: [LsaRetrievePrivateData](https://learn.microsoft.com/en-us/windows/win32/api/ntsecapi/nf-ntsecapi-lsaretrieveprivatedata),
[LsaStorePrivateData](https://learn.microsoft.com/en-us/windows/win32/api/ntsecapi/nf-ntsecapi-lsastoreprivatedata).

## D-52 — Integrate M4 with guarded native ownership and external recovery

Context: D-45 through D-51 established components. M4 requires runnable deployment,
removal, Windows handouts and recovery, with independent review of failure boundaries.
This decision records the implementation being integrated; Windows acceptance is pending.

Owner scope decision, 2026-09-08: M4 provides clean installation and repair of owned
installations. Unowned legacy development installations remain refused; implementing a
legacy migration flow is outside M4. This does not change the fresh-install account checkbox
or permit adopting an existing account by name.

1. The update signature is Base64 ECDSA P-256 / SHA-256, 64-byte IEEE P1363, over
   ASCII `labcontrol/update-manifest/v1\0` followed by the exact protobuf manifest bytes.
   The pinned CA verifies it before staging or execution. The signed minimum installed
   numeric version is checked without build metadata; no unsigned fallback is allowed.
   A signing-capable console is required to update a hardened agent. Old agents continue
   to accept new consoles, but their first bootstrap push still uses their old behavior.
2. `UpdateTrial` flushes a process-locked journal before native switching. The outgoing
   known-good executable handles crash recovery and a SYSTEM scheduled deadline task;
   both callbacks name the exact update job. Acceptance requires ten continuous linked
   minutes measured monotonically. The absolute deadline adds two service-restart waits
   for startup. Interrupted rollback retries; terminal cleanup resets service recovery
   to repeated restarts, removes the matching task and clears trial markers under the lock.
   Old callbacks cannot affect a later trial. Agent update staging is serialized and holds
   the same private Setup lock as install/removal/rekey, including service-switch preparation. Hello reports probation/rollback, and the
   `update.stable` event refreshes the live fleet view after acceptance.
3. The complete Winlogon identity/enable/plaintext-password/countdown plus LSA secret is
   one protected setting. Disable first, change credentials/countdown, enable last.
   `AutoLogonCount` preserves absent, DWORD and REG_SZ originals and is removed while
   managed autologon is active. User edits to any member preserve the entire tuple.
   Native Windows acceptance also observed `AutoLogonSID` populated with the student SID
   after logon. A separate protected `student.autologon-sid` record retains its exact
   absent-or-REG_SZ baseline without changing existing tuple serialization. Fresh setup
   applies the recorded managed SID before the tuple; removal restores the tuple first,
   then this SID. Later SID edits conflict, and unsupported registry types are refused.
   A tuple-only older journal cannot establish the original SID: repair/removal neither
   reads nor adopts that value, and reports the missing-baseline limitation. Account-off
   skips both records. Pending SID-only work is included in partial-install removal.
   Standard Users membership is verified by SID before and after explicit activation.
4. Hostname snapshots track the pending name so reboot is not an external edit. Firewall
   rules are create-only, compare complete supported properties, and preserve collisions.
   Defender adds/removes only the fixed installation exclusion. An absent provider may be
   skipped only with registered third-party antivirus and no pending owned exclusion;
   denied access and write failures remain actionable failures. Hibernation includes
   native file state and typed metadata; unsupported changes conflict. NIC snapshots bind
   interface GUID, PNP identity and native setting identity. Only advertised standardized
   features are changed; unsupported magic-only behavior is reported rather than guessed.
   NIC changes wait for reboot. Machine privacy/Edge/OneDrive notification policies apply only in account-on
   mode, but their effect on other users is disclosed. Admin hiding follows student
   activation and records the original local administrator SID for later restoration.
   Visibility restoration binds that recorded administrator SID and its unchanged journaled
   tuple independently of whether the managed student still exists or was replaced. It
   never reads or changes the replacement student, grants privileges, or adopts accounts;
   account-off mode still skips all visibility access. Hiding retains the strict managed
   student/activation guards. Administrator rename or later visibility edits remain conflicts.
   Native acceptance found that hiding the sole administrator can remove UAC credential
   fields. Before hiding, a separate protected `student.admin-credential-prompt` DWORD
   tuple sets `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\CredUI\EnumerateAdministrators`
   to `0`, bound to the recorded local administrator SID. Microsoft documents that this
   setting requires explicit username/password entry instead of administrator enumeration:
   [CredentialsUI policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-credentialsui#enumerateadministrators).
   Setup discloses this machine-wide effect. Uninstall unhides the unchanged owned tile
   before restoring the exact nullable DWORD baseline; a visibility conflict retains the
   credential-entry policy. Account-off never accesses either setting. `EnableLUA` and
   other UAC security policies remain unchanged. Native verification of restored credential
   fields and the separate manual Winlogon route is required; a registry readback alone is not proof.
5. Setup uses a Windows Forms number/checkbox dialog and an elevation manifest; the
   Windows Desktop framework is published self-contained, with no per-PC runtime install.
   A machine-wide mutex supplements the private file lock across cleanup. The fresh
   settings-initialization intent makes a crash between journal creation and confirmation
   recoverable without treating missing legacy history as empty. An installation-id
   marker and protected descendant ACLs prove the binary directory's ownership.
   The service's atomically created, installation-specific DisplayName survives signed version changes;
   its ImagePath must still match the recorded active version. Only adapters with an explicit
   atomic-creation proof may resolve pending journal creation from exact desired bytes;
   ordinary settings retain the fail-closed ambiguous-write rule. Binary roots appear by
   same-volume rename from a bounded installation-ID staging directory; Installed apps
   registration uses an installation-ID sibling key and atomic `RegRenameKey`. USB upgrades use the same
   external recovery machinery. Later native edits are preserved during removal.
6. Rekey stages new trust in a separate DPAPI-protected transaction, retaining agent id,
   number and unrelated settings. It adopts the validated replacement payload's console
   host/port, including clearing an old pin when the new payload selects discovery;
   pending-enrollment idempotency includes this route. Fixed trust files may roll forward only from recorded
   original or staged bytes. The agent refuses mixed trust until Setup finishes recovery.
7. Windows handouts resolve the managed SID and its real Desktop. Download staging is
   private; final file operations run under that user's token, with hash verification,
   reparse refusal and atomic replacement. Delivery and opening reject administrator
   tokens, including UAC-filtered tokens with a deny-only Administrators SID. Opening uses a matching interactive user token,
   never a SYSTEM shell. Redirected/out-of-profile desktops and unavailable profiles are
   refused. A logged-out profile may require one initial user sign-in.
8. USB construction copies an explicit executable allowlist beside the public enrollment
   payload; it cannot copy a backup or private key by copying an entire directory.
   USB and network bundles share an identity derived from SHA-256 of the UTF-8 domain
   `LabControl.AgentBundle.v1`, a NUL, then ordinal-name-sorted executable name / NUL /
   lowercase SHA-256 / NUL pairs. Its first eight hex digits follow the base version.
   This includes `session.exe`: a helper-only fix must not take the updater's already-running
   shortcut. Existing agent-only version directories remain readable; staged executable
   preflight still compares the numeric base version. No wire format changes are needed.
   In-process discovery tests use their own UDP port so a desktop console cannot consume
   their loopback packets. Production discovery keeps the default port.

9. A password-policy rejection of create-new may invoke the journaled local fallback
   only in account-on mode on a positively verified non-domain PC. A private `secedit`
   export reads the minimum-length/complexity tuple; a fresh two-field template changes
   only that tuple, then reads it back. Removal restores it even if account creation never
   completed; later edits conflict. General account errors never trigger policy changes.
10. Optional profile templates are add-only Desktop/Documents document/image ZIPs for
    future profiles, not arbitrary Windows profile copies. Bounds are 128 entries, 2 MiB
    per file, 16 MiB total and 200-character paths. Executables, hives, AppData and links
    are refused. A private plan binds the resolved Default profile root and hashes;
    staged files move on the same volume. Removal deletes only unchanged files and empty
    owned directories. Existing student and personal profiles are never overwritten.
    Restoration checks pending protected template entries before querying SAM or resolving
    the Default profile. An install that never created the account and a retry after
    completed template restoration therefore need no account access; actual pending
    template work still requires the matching managed SID and intact path plan.
11. Removal keeps a private retry executable and Installed apps entry through cleanup.
    Its nonsecret completion receipt authorizes only final deletion for the same recorded
    installation; a different installation or unexpected recreated file blocks it. The
    remaining executable/receipt are removed on reboot. An active update is refused under
    the same trial lock held through service removal; only the exact terminal task is deleted.
    After ownership and path checks, copying/removal clears only inherited ReadOnly media
    attributes on owned targets, including the private retry worker before delayed deletion.
    Read-only ISO copies exposed this native cleanup failure; links and unexpected contents
    after the completion receipt still cause refusal.
12. The default reboot dialog counts down 30 seconds, with a visible Later choice. Successful
    installation advances the USB number without overwriting the consumed enrollment code.
13. Wake addressing comes from a positively identified physical Ethernet GUID/MAC pair,
    cross-checked against the IP Helper wired-interface type (some Wi-Fi drivers report
    the generic Win32 Ethernet adapter type). Wireless, tunnel and other interface types
    are excluded from both wake selection and NIC configuration.
    Selection prefers exactly one active wired adapter. Ambiguity never silently chooses a virtual
    adapter. Repair stops the owned service before updating a previously generic MAC.
    Advertised power-management enable precedes dependent wake controls; restore reverses
    this dependency order even for older journals. Magic-only enforcement uses the exact
    advertised writable WMI Boolean, never guessed vendor bits. Hardware/driver inventory
    and unsupported controls remain visible; Windows read-back cannot certify BIOS/S5 wake.
14. Task Scheduler XML uses UTF-16LE, verified with the production native registration
    path. Persisted recovery deadlines and XML share the same rounded-up whole UTC second.
    Missing-task cleanup accepts the native mapped file-not-found exception; cleanup errors
    cannot hide the original failure to arm recovery. No service switch precedes a
    successfully armed external recovery path.
15. Installer readiness uses a bounded fixed-code snapshot in private agent data, carried
    by the existing Event envelope on reconnect and change. The console translates codes
    and persists the latest valid snapshot for offline tiles; malformed/newer payloads
    never clear known warnings or render arbitrary JSON. Repair clears resolved warnings
    without requiring a restart. Local settings checks do not certify physical wake.

Validation is tracked in ROADMAP M4. Native VM tests and milestone close-out remain
required; successful compilation is not evidence of native behavior.

Sources: [service recovery](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/cc742019(v=ws.11)),
[scheduled tasks](https://learn.microsoft.com/en-us/windows/win32/taskschd/schtasks),
[autologon count](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-shell-setup-autologon-logoncount),
[known-folder user tokens](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath),
[local-policy export](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/secedit-export),
[OneDrive notification policy](https://learn.microsoft.com/en-us/sharepoint/use-group-policy#hide-toast-and-activity-center-notifications-that-prompt-users-to-sign-in-to-the-onedrive-sync-app-with-existing-credentials).

## D-53 — Lab files and one active room per teacher console (planned M5)

Extension: `D-54` explicitly permits administrator backups in the same lab selector
and adds teacher-console packaging with the platform and lifecycle review constraints.

Context (2026-09-08): the owner teaches successive lessons in different computer labs
and wants to import all lab files at once, then simply choose the room. Room membership
is mostly stable; teacher/device allocation changes during the day. The existing backup
import supports another owner device for one lab, but repeatedly restoring backups is
not a room selector. The preceding audit also exposed the distinction between classroom
access and possession of the CA private key.

Decisions:

1. **Insert M5 before classroom control.** M5 delivers lab files, teacher access and fast
   switching. Former M5 becomes M6 (broadcast/lock/exam); former M6 becomes M7
   (catalog/localization/polish). M0–M4 keep their numbers and completion state. This
   entry records approved planning scope, not completed implementation.
2. **One app, several saved labs, one active session.** Bulk file selection/drop adds
   rooms to *My labs*. Import never acquires a room. Startup shows the chooser with the
   last-used room highlighted; selecting a room activates it, and the same selector
   switches without restart or repeated authorization. Inactive labs have saved metadata
   only, with no beacons, agent links, screen streams or background connection attempts.
   Different teachers can use different rooms simultaneously; one console cannot use
   two rooms at once. The mostly stable roster is a useful offline snapshot, not a reason
   to remove the existing refresh from authenticated agent connections.
3. **A routine lab file is not an administrator backup.** Keep full `.lcbak` recovery
   compatible and visibly privileged. Ordinary lab files contain authenticated room
   metadata/public trust and support device authorization without distributing CA private
   keys, recovery material or enrollment codes. First-time authorization must support
   batching and isolated-LAN/offline use; daily selection needs no secret entry. Devices
   get distinct revocable identities, not copies of one shared console key. The concrete
   extension, signed envelope and authorization exchange are to be specified during M5,
   before code; do not add a cloud or permanently running server to solve issuance.
4. **Preserve identity and isolate data.** Deduplicate imports by lab id and authority.
   Per-lab trust, keys, roster, layout, jobs/logs, scripts and catalog cannot bleed across
   rooms, even when each contains PC-01. Import updates are atomic per file; corrupt or
   older files cannot destroy working profiles, revoke unrelated access or roll back
   revocations. Migrate the existing profile without changing its identity or touching
   student PCs. Removing a saved room is local, not classroom deletion or agent uninstall.
5. **Release before acquiring.** Serialize switches, close old links/streams and revoke
   stale UI callbacks before enabling the next room. Target <=2 seconds to the responsive
   cached destination view and <=15 seconds for reachable running agents on a healthy
   supported LAN during idle switches. Missing PCs do not block. Running operations
   need explicit safe departure choices and retained ownership of results; do not kill
   update recovery or silently deliver a previous teacher's output to the next one.
   M6 must honor the same boundary for restrictions, expiry and work collection.
6. **Close the relevant access/handover audit gaps.** Ordinary teacher access does not
   authorize CA issuance, enrollment or update signing. Identify/revoke a device across
   certificate renewal, tolerate allowed clock skew in takeover, and distinguish unknown
   or offline PCs from positively observed ownership. Revocation is effective once
   delivered; unreachable agents have an explicit pending state. Administrator recovery
   must not silently re-enable spent/voided enrollment codes from an old copy. Existing
   holders of the full CA key retain that power even after a wrapping or leaf is removed;
   changing a file label is not a downgrade, and migration must disclose this limit.

Rejected: opening one live console/session per saved room (wastes resources and risks
commands to the wrong room); repeated backup restore or command-line profile switching
between lessons (slow, destructive-looking UX); handing ordinary teachers renamed full
backups (retains unrestricted CA authority); automatic timetable-based room acquisition
and continuous cross-room discovery (unneeded scope and background work). Full real-time
synchronization of scripts, catalogs and job histories between teachers remains out of
scope; room-file refresh and locally retained per-lab state are sufficient for this plan.

Validation: ROADMAP M5 defines bulk-import, compatibility, access, resource and multi-device
acceptance drills. No implementation or new dependency is introduced by this decision.

## D-54 — Administrator backup onboarding and lightweight console installation (planned M5)

Context (2026-09-08): the owner clarified that administrators must bulk-add backups and
switch rooms just like teachers, and requested simple installers for teacher devices.
They also requested a review of the whole M5 plan against the actual application, rather
than treating "just copy files" as a proven installation design.

Decisions:

1. **Both file types use one workflow.** *Add labs…* accepts teacher files, `.lcbak`
   backups or mixed batches. After password/recovery unlock, a backup creates a saved
   administrator profile, usable by the same chooser without conversion or repeated
   restoration. Distinguish authority per lab. A backup import can upgrade an existing
   teacher profile without duplicating the room or losing its stable device id/history;
   explicit credential renewal is allowed when needed. Teacher-file refresh cannot
   silently downgrade administrator access. Routine switching uses the device identity;
   CA operations still need unlock, and leaving a room locks its CA key.
2. **Package an interactive app, not a student service.** Windows gets a simple desktop
   installer/removal entry, macOS an app bundle/DMG, Linux a documented desktop install
   flow. Include the runtime/application assets, launchers and both file associations.
   No Agent/Session service, daemon, scheduled task, account/autologon or student policy
   preparation. Do not add an always-on server: Kestrel belongs to the selected session.
3. **Copying is only one step.** Network readiness requires appropriate LAN permissions
   (inbound console TCP and discovery UDP), potentially elevation for scoped firewall
   setup. Native Linux prerequisites need a tested platform matrix and an offline supply
   path. A macOS bundle needs stable identity/metadata; preserve D-15's signing policy
   and test/document actual OS launch approval rather than promising no warnings. Keep
   stable installed paths and do not disable protections or apply student AV exclusions.
4. **Opening documents is application work too.** Registering extensions is insufficient:
   implement document/argument activation and same-user instance forwarding, safely
   handling multi-file paths and validating each import. An already running app receives
   files without another control session or automatic room acquisition. Preserve the
   user's default-handler choice. Installation packages themselves contain no lab data
   or secrets and never perform backup unlock.
5. **Update and removal preserve user data.** Replace app files only after safe close;
   keep profiles, history and OS-protected keys by default. Remove owned registrations
   and any owned network rules, preserving others. Local-data cleanup is a separate
   explicit action (inside the app for macOS bundle removal), never deletion of external
   backups or student installations. Verify upgrade/reinstall on the same account/device;
   an OS-bound key is not portable just because its metadata directory was copied.
6. **Correct M5's assumptions before implementation.** Current `App`/bootstrap assume one
   full-owner lab; publishing creates binaries, and the CLI has no document-open support.
   Refactor profiles and session lifecycle, implement a concrete offline device-key
   request/approval flow before promising teacher authorization from a file, and measure
   switching with valid access/permissions. The 2/15-second goals concern graceful idle
   switching, not first authorization, OS prompts, update probation or failure detection
   after a dead laptop. Preserve the frozen protocol path for administrator-led agent
   migration; explicitly negotiate any capabilities needed by new access controls.
7. **Backup import is not concurrent enrollment recovery.** Keep legacy enrollment
   history, but do not automatically activate imported pending codes merely to add a
   switchable room. Define one issuer per enrollment batch or equivalent coordination
   for explicit recovery. Disconnected independent CA owners cannot enforce global
   one-time redemption using separate local journals; do not claim old copies cease to
   work through local metadata changes. This restriction does not prevent administrators
   from controlling rooms or generating new enrollment batches through the defined flow.

Rejected: treating backups as recovery-only files that administrators must convert;
putting teacher-console installation through Windows student Setup; equating
self-contained publish with a desktop package or unrestricted network access;
an installer that duplicates lab secrets or runs the console as SYSTEM.

Validation: M5 now includes mixed import and authority-upgrade cases, desktop lifecycle,
key retention, document activation, denied-network recovery and the clean-platform
matrix. This is a documentation/design review, not verification of unbuilt installers.
No new packaging dependency or paid signing requirement is selected here.

Sources: [Avalonia macOS packaging](https://docs.avaloniaui.net/docs/deployment/macos),
[Windows Firewall rules](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/rules),
[Linux runtime dependencies](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual#dependencies).

## D-55 — The profile store: `profiles.json`, `labs/<lab_id>/`, and a resumable migration (M5 portion 1)

Context (2026-09-08): `D-53` and `D-54` promised several saved labs per console and left
the on-disk shape open. The console's data directory (`Defaults.ConsoleDataDirectory`,
`--data`) holds exactly one lab today — `lab-key.lck`, `instance.json`, `lab.json`,
`enrollment.json`, `scripts.json`, `packages/`, `logs/` — and `LabStore` is written
against that flat layout. The M5 design (`D-55`…`D-60` are the entries it proposed) is
recorded here before the code; implementation status is tracked in ROADMAP M5.

Decisions:

1. **One index, one directory per lab.** The data directory root does not move and
   `--data` keeps working. It gains `profiles.json` (`ProfilesDocument`,
   `Defaults.ProfilesFileName`), an app-level `console-.log`, a `console.lock` held open
   with `FileShare.None` for the process lifetime (one process per data directory, `D-59`),
   and `labs/` (`Defaults.LabsDirectoryName`). Each `labs/<lab_id>/` — `lab_id` is the
   UUID from the CA's SAN — is one profile and contains exactly the files the flat layout
   had, plus `access.json` on teacher profiles: `lab-key.lck` and `enrollment.json` exist
   only on administrator profiles; `instance.json` is this device's identity for this lab;
   `lab.json`, `scripts.json`, `packages/`, `logs/` (events, jobs, batches) are per lab.
   `LabStore` keeps its API and is constructed with the profile directory, so
   `LabSession`, `ScriptLibrary`, `EventLog` and `JobBatchLogs` need no path changes; it
   gains `AccessPath`, `LoadAccess`/`SaveAccess`.
2. **`profiles.json` is metadata only.** Schema:

   ```json
   {
     "schema_version": 1,
     "last_used_lab_id": "5b1c…",
     "labs": [
       {
         "lab_id": "5b1c…",
         "lab_name": "ОНТФК lab 214",
         "directory": "labs/5b1c…",
         "authority_fingerprint": "<sha256 hex of the CA DER>",
         "access": "administrator" | "teacher",
         "authorization": "authorized" | "needs_authorization" | "request_pending" | "expired" | "revoked",
         "instance_id": "…", "instance_name": "MacBook-2026",
         "access_expires_unix": 0,
         "pc_count": 14,
         "added_at_unix": 0, "last_used_unix": 0,
         "source": "migrated" | "backup" | "lab_file" | "created"
       }
     ]
   }
   ```

   `directory` is relative to the data directory. `authority_fingerprint` pins trust:
   the same `lab_id` with a different CA is refused, never merged (`D-53` item 4). The
   index carries no key material, no certificate and nothing an inactive lab could use to
   connect; the chooser reads only this file and never listens for beacons (default
   chosen 2026-09-08, owner may change). `ProfileStore` (`Load`, `Save` through
   `JsonStore`, `Find`, `Directory`, `Touch`, `Remove(labId, deleteData)`, `Upsert`) is
   the only writer; `ConsoleBootstrap` becomes per-profile (`OpenExisting(labId)`,
   `CreateLab` into `labs/<newId>/`).
3. **Migration is copy, rename, commit, delete — and resumable at every step.** Trigger:
   at startup `profiles.json` is absent and `<data>/lab-key.lck` or `<data>/instance.json`
   exists. Sequence: (1) read `lab_id` from `lab-key.lck`, falling back to
   `instance.json`; a mismatch refuses with the existing `OpenExisting` message; (2) create
   `labs/<lab_id>.migrating/` and *copy* — never move — `lab-key.lck`, `instance.json`,
   `lab.json`, `enrollment.json`, `scripts.json`, `packages/**`, `logs/**`, skipping
   `console-*.log`; (3) rename `labs/<lab_id>.migrating` → `labs/<lab_id>` in one
   same-volume rename; (4) write `profiles.json` with one entry — `access: administrator`,
   `authorization: authorized`, `source: migrated`, the instance id from `instance.json`,
   `pc_count` from `lab.json` — this is the commit point; (5) delete the originals.
   Resume rules: a `.migrating` directory without `profiles.json` is deleted and step 2
   restarts; `labs/<id>` present without `profiles.json` redoes 4–5; `profiles.json`
   present with the originals still there runs 5 only. Reversal for a pre-M5 build is a
   copy back from `labs/<id>/`. Identity is preserved because `instance.json` moves
   verbatim and the keystore reference `instance-<instanceId>` is path-independent; no
   student PC sees anything. The migration dialog states the `D-53` item 6 limit: a device
   that holds the CA key keeps administrator authority, whatever label it is given.
4. **`lab.json` schema 1 → 2** is the first real `SchemaMigrations` step:
   `InstanceRecord.Access` (`administrator` | `teacher` | unknown), `AuthorizedAtUnix`,
   `RevokedAtUnix` — the administrator's device book (`D-56`);
   `MachineRecord.RevocationSerialsSeen` — entries this PC confirmed holding (`D-56`);
   `MachineRecord.LastInstanceObservedUnix` — when `LastInstanceId` was positively learned
   (`D-58`). Absent fields default. A schema-2 file is unreadable by a pre-M5 console by
   design (`D-20`); the reversal note says so.

Added while building portion 1 (2026-09-08), all in `ProfileMigration`, `ConsoleLock`
and `LabBackup`:

5. **Nothing at the root is deleted unless it matches its copy.** Before the delete step
   every root document (`lab-key.lck`, `instance.json`, `lab.json`, `enrollment.json`,
   `scripts.json`) is compared byte for byte with its copy under `labs/<id>/`, and
   `packages/` and `logs/` file by file and size by size. A root file already gone counts
   as matching (a delete that stopped half-way is the expected way to get there); extra
   files in the copy are the copy's business. Copying is `File.Copy` plus clearing a
   read-only attribute, because the console saves over the copy from now on.
6. **A differing copy is discarded or the root is parked — by commit state.** A copy that
   differs and is not yet in `profiles.json` is deleted and made again from the root: the
   root is still the only truth. A copy that differs and *is* committed is kept, and the
   root files are moved, complete, to `<data>/migration-conflict-<yyyyMMdd-HHmmss>/`
   (`Defaults.MigrationConflictDirectoryPrefix`) and logged as a warning; the console
   opens the saved lab. This is the downgrade-then-upgrade case (README, *Downgrading*),
   where a pre-M5 build ran on the root after the copy and the root is newer. The
   conflict directory is never deleted by the console.
7. **Sentinel ordering on delete.** The originals go in the order `lab.json`,
   `enrollment.json`, `scripts.json`, `packages/`, `logs/`, then `instance.json`, then
   `lab-key.lck`. The two files that mark a root as pending (`IsPending`) are deleted
   last, so a crash anywhere in between leaves a root the next launch still recognises
   and finishes; once the index exists, any other leftover lab file also counts as
   pending and is finished from the lab it names.
8. **Symlinks and junctions are skipped, not followed.** A link under the root or under
   `packages/`/`logs/` (`LinkTarget` set or `ReparsePoint`) is logged and not copied:
   following one could loop forever or copy something far outside the data directory.
   A symlink loop is a test case.
9. **Free space is checked before the copy** (`DriveInfo.AvailableFreeSpace` against the
   size of the originals plus a 64 MiB margin); too little refuses with an `IOException`
   naming the sizes rather than failing part-way, and the rename is retried five times
   at 250 ms for an antivirus hold (`D-33` item 9).
10. **An instance-only root is refused, not committed.** `instance.json` without
    `lab-key.lck` is a pre-M5 installation that lost its key, not a lab: the migration
    throws (`Migration.InstanceWithoutKey`) instead of writing an index entry that could
    never open — unless the index already names that lab, in which case an earlier
    delete stopped between the two sentinels and the leftover is finished. A key naming
    no lab and a key/instance `lab_id` mismatch are refused the same way.
11. **The app-level log moves to the data root.** Serilog now writes
    `console-<date>.log` (`Defaults.ConsoleLogFileName`) at the root, not under `logs/`,
    because `logs/` is per lab from now on. The migration moves a pre-M5 console's
    `console-*.log` files out of `logs/` once (a file with a namesake at the root stays)
    and does not copy them into the profile, so the retention limit applies to all of
    them and `logs/` holding only app logs does not count as pending lab data.
12. **`console.lock` — one process per data directory.** `ConsoleLock.TryAcquire` opens
    `console.lock` with `FileShare.None` and holds it for the process lifetime; a second
    launch on the same directory is told so and quits, and the OS releases the lock when
    the process dies, so the file's existence means nothing — only holding it open does.
    Caveat: on Unix this rests on .NET's advisory `flock`, which
    `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` turns off; with that variable set two
    consoles can share a directory unnoticed, and the console does not try to detect it.
13. **`LabBackup.Open` upgrades the nested documents.** Each sealed document inside a
    backup — `lab`, `scripts`, `enrollment` — carries its own `schema_version` and is
    walked through its own `SchemaMigrations` on open (the same 1 → 2 step as the file on
    disk), so an M4 backup restores into a schema-2 profile; a document written by a
    newer build is refused by name (`lab.json (in the backup)`), per `D-20`.

Rejected: one data directory per lab chosen on the command line (the switcher is the
product, `D-53`); moving files instead of copying during migration (an interrupted move
leaves neither layout complete); keeping the flat layout for the first lab and nesting only
later ones (two code paths for one store); storing certificates or keys in `profiles.json`
(the index would become sensitive and an inactive lab could connect).

Validation: Shared tests round-trip `profiles.json`, refuse a newer schema and migrate
`lab.json` 1 → 2; console tests migrate a populated directory (same instance id, key
reopened, logs/scripts/packages present), crash after every step and resume with
byte-identical originals, produce the conflict directory, skip a symlink loop, survive a
crash mid-delete and a keystore protector that throws on `Remove`, confirm the second
launch is a no-op and that two `--data` directories coexist — 702 tests on 2026-09-08.
The trial on a copy of the owner's live directory is in ROADMAP M5 *Progress*.

## D-56 — Lab files, device requests and grants: the offline authorization exchange, the role in the subject OU, `instance:` revocation (M5 portion 3)

Context (2026-09-08): `D-53` item 3 required a routine lab file that is not a backup and
an offline way for a teacher's device to obtain its own revocable identity, and left the
format to be specified before code. `D-54` item 6 asked for a concrete request/approval
flow. Every agent today validates a console by chaining to the pinned CA and parsing the
SAN URI `labcontrol://<lab>/console/<instance>` (`LabTrust.TryValidate`, `LabName`); a
URI with a new segment is `Malformed` to every agent already installed.

Decisions:

1. **Three files, one signed envelope.** All are JSON documents through `JsonStore`
   (snake_case, `schema_version` first) with the shape
   `{schema_version, kind, lab_id, lab_name, payload, signature}`: `payload` is base64 of
   the UTF-8 JSON payload bytes; `signature` is base64 of a P-256/SHA-256 signature in
   IEEE P1363 form over *domain + payload bytes*, the pattern of
   `UpdateManifestSignature` (`D-52`). Domains: `labcontrol/lab-file/v1\0`,
   `labcontrol/device-request/v1\0`, `labcontrol/device-grant/v1\0`.

   | Kind | Extension | Signed by | Verified against |
   |---|---|---|---|
   | lab file | `.lclab` | the lab CA key | the CA certificate inside the file on first import; the pinned CA on re-import |
   | device request | `.lcreq` | the device's own key (a self-signed PKCS#10 CSR) | the CSR's self-signature |
   | device grant | `.lcgrant` | the lab CA key | the pinned CA of the profile it targets |

   A corrupt, unsigned, wrongly signed or newer-schema file is `ImportOutcome.Failed`
   with a reason and changes nothing.
2. **What a `.lclab` carries, and what it must never carry.** Payload: `lab_id`,
   `lab_name`, `issued_at_unix`, `issued_by_instance_id`/`_name`, `authority` (DER of the
   public CA) and `authority_fingerprint`, `snapshot_version` (monotonic per issuing
   console), `roster[]` (`agent_id`, `number`, `hostname`, `mac`, `last_ip`,
   `certificate_serial`, `certificate_not_after_unix`), `layout[]`, `revocations[]`
   (signed entries, self-authenticating), and an optional `scripts` library. It must not
   contain the `LabKeyDocument`, any wrapping, the CA private key or recovery material;
   the `EnrollmentDocument` or any code; any `instance.json` or private key; package
   binaries; logs. A test serialises a file and asserts every one of these absences. Lab
   files are produced only by an explicit *Export lab file…* in Settings, never as a side
   effect of a backup export; the embedded scripts are imported only into a profile that
   has no `scripts.json` yet (defaults chosen 2026-09-08, owner may change).
3. **Import is a merge that cannot go backwards.** A new lab creates `labs/<lab_id>/`
   with `lab.json` from the snapshot, `access.json = {state: needs_authorization}` and a
   `teacher` profile; a `PendingDeviceIdentity` — a new key pair under `SecretProtector`,
   a new `instance_id`, the machine name — is created, but with no certificate nothing
   beacons. For an existing lab the `authority_fingerprint` must match ("same lab id,
   different key — refused"); then the roster is added/updated by agent id and number and
   never loses a PC this console has itself seen linked, the layout is replaced only when
   `snapshot_version` is newer, revocations are unioned, and scripts, `instance.json`,
   the access level and local history are untouched. Importing a backup into an existing
   teacher profile writes `lab-key.lck` and a dormant `enrollment.json` (`D-60`) into the
   same directory, sets `access: administrator`, keeps `instance.json` and history, and
   re-mints the leaf as an administrator only after an explicit *Renew this device's
   certificate*. A backup for a new lab behaves like today's import, into `labs/<id>/`.
   *Add labs…* accepts `.lclab`, `.lcbak`, `.lcgrant` and — on administrator profiles —
   `.lcreq` in one selection with a per-file result; nothing activates.
4. **The request/grant exchange.** The teacher device writes `<device> – <lab>.lcreq`
   with payload `{lab_id, instance_id, instance_name, requested_access: teacher,
   created_at_unix, csr, console_version}`; the CSR is PKCS#10 from the pending key with
   `CN = instance name`, and `access.json` records `{state: request_pending, instance_id,
   requested_at, csr_fingerprint}` so a re-run regenerates the same request from the same
   key. The administrator's *Settings → Teacher devices → Authorize requests…* takes
   several files: verify the CSR self-signature, `lab_id` = this profile, a UUID
   `instance_id`, no `instance:<id>` revocation; a second request from an authorized
   device is a renewal under the same instance id. After `EnsureUnlockedAsync`,
   `LabCertificates.IssueTeacherDevice` issues a leaf with the *same* SAN URI and EKUs,
   `OU=LabControl Teacher`, and `Defaults.TeacherCertificateLifetime` = 365 days, the
   console leaf's lifetime (default chosen 2026-09-08, owner may change); the endorsement
   is `Beacon.Endorse(lab, instanceId, P256.Compress(csrPublicKey))`. The console records
   `InstanceRecord{Access: teacher, AuthorizedAtUnix}`, raises `device.authorized`, and
   writes the CA-signed `<device> – <lab>.lcgrant`: `{lab_id, instance_id, certificate
   DER, endorsement, issued_at_unix, expires_unix, snapshot: LabFilePayload}`. The device
   imports the grant through *Add labs…*: verify against the pinned CA, `instance_id`
   equal to the pending one, public key equal to the pending key,
   `LabTrust.TryValidate(cert, LabRole.Console)`; then write `instance.json`, mark
   `access.json` and the profile `authorized`, and apply the snapshot as a refresh.
   Fewer than 60 days of validity offers a renewal request from the chooser; an expired
   leaf leaves the profile listed as `expired`, with its history.
5. **The role lives in the subject OU, not in the SAN.** `OU=LabControl Console`
   (administrator, unchanged) versus `OU=LabControl Teacher`; the SAN stays
   `console/<instance_id>` so every agent already in the field links to a teacher console.
   `LabName` gains `ConsoleAccess Access` (`Administrator`, `Teacher`, `Unknown`) parsed
   from the OU; `LabTrust.TryValidate` is unchanged and callers inspect `name.Access`. On
   the console a teacher profile has no `lab-key.lck`, so `LabSession.Vault` becomes
   nullable: `Enroll`/`Renew` answer `Closed` naming the administrator; `PushAgentBuild`,
   `TryRevoke`, `WritePayload`, backup export and holders are unavailable with
   *Administrator access needed*. An M5 agent reads the peer's OU (`ConsoleChannel.PeerName`,
   `AgentLink.LinkedConsoleAccess`) and answers `self_update`/`rekey` on a teacher link
   with `JobResult{ok:false, "refused: this console has teacher access"}` and the event
   `job.refused_by_role` before pulling any manifest; `RenewalLoopAsync` is skipped on
   teacher links. Older agents treat a teacher leaf as a full console — a documented gap
   closed by the next agent push, not by a wire negotiation; `Welcome.console_access` is
   informational only.
6. **A device is revoked across renewal with a pseudo-serial.** *Settings → Teacher
   devices → Withdraw access…* (unlock required) issues two signed entries through
   `Registry.Revoke`: the current leaf serial and `instance:<instance_id>`, in the same
   signed `serial|revoked_at_unix|reason` form (older agents were expected to store it
   inertly — corrected in item 9). The M5
   `LabTrust.TryValidate` also checks `instance:` entries for console peers, so a renewed
   leaf for a withdrawn device is refused; `InstanceRecord.RevokedAtUnix` is set.
   Propagation: pushed to linked agents at once (`TryRevoke`), carried in later `.lclab`
   and `.lcgrant` files, and carried agent-to-console as today. Delivery is confirmed,
   not assumed: an M5 agent answers every `Revocation` with `RevocationState`, the console
   records `MachineRecord.RevocationSerialsSeen`, and Settings shows *delivered to 11 of
   14 PCs; pending on PC-03 (offline since …)* — never "revoked everywhere". Re-importing
   an old `.lclab` after withdrawal cannot bypass it; a new request mints a new instance id
   and key and needs a fresh approval.

Added while building portion 3 (2026-09-08, after the security review):

7. **`Approve` never re-certifies an id that is not a teacher device.** A request naming
   an existing instance id is a renewal only when that record is a teacher device;
   a request naming this machine, an administrator record or an unknown record is
   refused. `LabRegistry.RecordAuthorization` is the registry-side guard, so the rule
   holds even for a caller that skips `DeviceAuthorization.Approve`. The review found
   that a forged `.lcreq` carrying the administrator's own instance id could otherwise
   have been issued a teacher leaf under that id.
8. **A renewal must present a fresh key.** `InstanceRecord.PublicKeyFingerprint` records
   the key certified last; a request carrying the same key is refused with a pointer to
   the grant written then. On the device the pending key is a separate keystore item
   (`instance-<id>-pending`, `AccessDocument.PendingKeyReference`), the CSR is P-256
   only with a verified self-signature, the name is bounded by
   `Defaults.MaxInstanceNameLength` (64), and grant import protects and saves the new
   key before the pending and previous items are forgotten. Withdrawal revokes every
   serial in `InstanceRecord.CertificateSerials`, not only the current one.
9. **Correction: pre-M5 agents do not hold `instance:` entries.** Their serial
   normalisation strips the entry to hex, which breaks its signature, so they drop it
   and the console re-pushes it on every link. `RevocationDelivery` therefore has a third
   list, `CannotHold` (`LabSession.DeliveryOf`), recognised through the sibling leaf
   serial that the same agent did confirm, and Settings shows those PCs as *cannot
   hold* until the agent is updated. The withdrawn device is still locked out of such a
   PC by its leaf serial; only a *renewed* leaf would be accepted by a pre-M5 agent,
   which is the documented gap closed by the next agent push. An M5 agent additionally
   leaves a console whose serial or instance id becomes revoked (`console.revoked`).
10. **The role is read strictly.** `LabName.Access` comes from exactly one single-valued
   OU: `LabControl Console` → `Administrator`, `LabControl Teacher` → `Teacher`,
   anything else — no OU, two OUs, a multi-valued OU — → `Unknown`. An agent refuses
   `self_update`/`rekey` and skips renewal unless the validated peer is `Administrator`;
   `Unknown` is refused, not tolerated.
11. **Merge details as implemented.** An older snapshot contributes only its revocations
   and reports *older snapshot; nothing rolled back*; a PC this console has seen linked
   is only filled in, never rewritten; a serial revoked locally blocks re-adding that
   PC; the layout comes only from a newer snapshot; scripts go only into an empty
   library; the access level is never downgraded; `lab.json` records
   `imported_snapshot_version` and `exported_snapshot_version`. The merged Settings
   panel *Teacher devices* replaces *Other teacher machines*.

Rejected: a new SAN segment or a private OID for the role (every installed agent would
parse `Malformed`); a shared teacher key copied with the file (`D-53` item 3); a
per-device password or an online issuance step; carrying enrollment codes in a lab file;
a revocation list with a version and an owner (the union of self-authenticating entries
already works across independent consoles, `D-21`); silently treating a re-imported old
file as newer than local state.

Validation: Shared tests sign, verify, tamper and newer-schema-refuse each envelope,
assert the must-not-contain list, exercise the merge rules and the request/grant round
trip, parse a teacher leaf as `Console` with `Access = Teacher`, and refuse a renewed leaf
under an `instance:` entry. Console tests link agents to a teacher console and run a
script, see `Enroll`/`Renew` `Closed`, withdraw a device (link closed, entry reaches
agents, pending list shrinks) and upgrade a teacher profile from a backup keeping its
instance id and logs. Built and reviewed 2026-09-08 (M5 portion 3): the envelope tests
also refuse a wrong kind for the extension and a signature made under another domain;
the authorization tests refuse a request naming this machine, an administrator or an
unknown record, and a renewal with the already-certified key; the delivery tests see a
pre-M5 agent in `CannotHold` and an M5 agent leave on `console.revoked`. 753 tests
(622 Shared + 131 Console) and a manual export → import → request → approve → grant
round trip on two copies of the data directory; no Windows or real-agent run yet —
status in ROADMAP M5.

## D-57 — One active session: `ActiveLabController`, release before acquire, the departure report, result ownership (M5 portions 2 and 4)

Context (2026-09-08): `App.StartAsync` builds one `LabSession` at startup and shows the
main window; `ConsoleServer` is one Kestrel per session; `AgentLink` drains finished job
results to the next console that links, whichever instance that is; `JobQueue` on the
receiving console drops ids it does not know, so the originating console loses them.
`D-53` items 2 and 5 require at most one active lab, release before acquire and retained
result ownership.

Decisions:

1. **One controller owns the only session.** `ActiveLabController` (`Idle`, `Activating`,
   `Active`, `Deactivating`, `Failed`; `Active: LabSession?`; `StatusChanged` raised on a
   background thread, the UI posts) exposes `ActivateAsync(labId)`, `DeactivateAsync(reason)`
   and `DescribeDepartureAsync()`. Requests are serialised with a `SemaphoreSlim(1,1)`, a
   generation counter and a single `_requested` slot, so a rapid A → B → C ends with exactly
   one activation of C and one Kestrel. Activation: (1) `Activating(labId)` — the chooser
   shows the destination's saved mosaic from its `lab.json` immediately; (2) deactivate
   the current session; (3) `bootstrap.OpenExisting(labId)` — an administrator profile
   opens with the vault locked and the instance open, prompting to re-mint if due; a
   teacher profile needs a present `instance.json` and a valid leaf, else `Failed`;
   (4) `new LabSession(...)` and `StartAsync()`; (5) `profiles.Touch`. Any exception
   disposes what was built and leaves `Failed(labId, message)` with *Retry* in the chooser
   — never two half-active labs.
2. **Deactivation order is fixed.** `_stopping.Cancel()`; beacons stop first; every link
   gets `connection.Close("the console left this lab")`; the beacon listener stops; the
   server `StopAsync` (2 s budget) and dispose; housekeeping joins; `SaveLab()`;
   `Screens.Dispose()`; `Vault.Dispose()` (locks the CA); `Instance.Dispose()`.
   `FileOffers`/`FileUploads` die with the session. `LabSession` gains a `Disposed` event
   and a `Generation`; `MainViewModel.Detach()` unsubscribes, closes every `ScreenWindow`
   and generation-checks posted lambdas. Pending jobs are never re-created by the next
   session. The new session's `LabTrust` is built from the new lab's authority, so a
   lab-A agent reaching the lab-B server is refused as `NotIssuedByThisLab`/`WrongLab`,
   and `RequireAgent` consults `ProfileStore` to log the truthful `link.refused` —
   *belongs to lab A, which is not the active lab*. A second process on the same `--data`
   is refused by `console.lock`; a second launch forwards its files (`D-59`).
3. **Departure is described, not guessed.** `LabSession.DescribeDeparture()` returns a
   `DepartureReport`: running jobs grouped — `run_script`/`send_file` continue on the PC
   and show their result on return, power jobs complete, `self_update` cannot be aborted
   and leaving is safe; uploads in progress will fail; PCs in probation report on return;
   pending wakes are dropped. The dialog offers *Leave anyway*, *Stay* and *Wait for N
   jobs*. Timing targets: 2 s to the cached mosaic, 15 s for reachable agents; a
   `SwitchTimings` record is logged per switch and asserted in the drill.
4. **A result belongs to the instance that delivered the job.** The console journals
   under `labs/<id>/logs/` and `JobRecord`, `JobLogSnapshot`, `JobJournal` and
   `JobBatchLogs` rows carry `lab_id` and `instance_id`. The agent's `JobLedger` binds
   each job to the instance id read from the SAN URI of the console leaf the TLS handshake
   validated (`ConsoleChannel.PeerName`) and **never** from `Welcome.instance_id`, which is
   only a claim: a link whose validated peer carries no console identity is refused, and a
   `Welcome` that disagrees raises `console.instance_mismatch` with the certificate winning
   — the certified id is what `LinkedInstanceId`, the beacon gate, the stored
   `LastInstanceId` and the next `Hello.previous_instance_id` use. `_pendingResults` and
   progress lines are drained only to a link with that instance id; another instance's copy
   of the job id is refused (`job.other_instance`), never answered from the cache and never
   run a second time. Retention is bounded and stated honestly: at most
   `Defaults.MaxPendingJobResults` (500) finished results per PC wait for a console that is
   not linked, the oldest dropped with a `job.result_dropped` event; the ledger's own 500
   entries are a separate cache; nothing survives an agent restart; progress produced while
   the deliverer is away is dropped rather than buffered; and nobody is told if that console
   never returns. No proto change is needed.

Recorded with portion 2 (built and reviewed 2026-09-08):

5. **The cached mosaic precedes `StartAsync`, not the release.** The order is release
   the current session → `OpenExisting` → `Build` → `SessionBuilt` → `StartAsync`, so
   the roster tiles the app shows on `SessionBuilt` come from the destination's own
   `lab.json` and belong to a session that already exists; the main window is shown with
   the toolbar disabled and *Connecting…* in the lab chip, and enabled on `Active`.
   `SwitchTimings` therefore has four phases — departure, open (documents and the
   instance key from the OS keystore: a Keychain prompt lands here), build (registry,
   scripts, logs, screens) and start (server and beacons) — plus `MosaicReadyMs` and
   `ServerUpMs` from the start of the switch; every switch logs one line.
6. **The host is disposed in a `finally`.** `ConsoleServer.DisposeAsync` calls
   `StopAsync` with the 2 s budget and disposes the host whatever the stop did, because
   the next lab binds the same port right after; a stop that times out or throws still
   frees the listener.
7. **Every close step is isolated.** `LabCloseStep` names the steps; `CloseAsync` runs
   each through `Step`/`StepAsync`, which logs a failure and continues, and the tail —
   save, screens, vault, instance — sits in a `finally`, so no exception on the way can
   leave the CA unlocked or the instance key open. `IsDisposed` and `Disposed` are set
   there, once. `BeforeCloseStep` is a test hook that makes one step fail. Activation and
   deactivation run on the thread pool (`Task.Run`), never on the UI thread; disposing the
   controller cancels an activation, including one waiting on a prompt.
8. **Import is atomic per file.** `LabImports` runs one handler per extension
   (`Register(extension, label, handler)` — `.lcbak` now, `.lclab`/`.lcreq`/`.lcgrant`
   in portion 3) and returns an `ImportFileResult` per file; a wrong passphrase fails only
   that file, and a failure after writing undoes the profile directory, the keystore item
   and the in-memory index entry, so a mixed batch leaves exactly the good labs behind.
9. **Closing the main window returns to the chooser.** Windows come and go with the
   active lab: closing the main window (after the departure report) releases the lab and
   shows *My labs* again; only the chooser's *Quit* ends the process. *Disconnect* shows
   *Leaving…* in the chooser before the release runs. A profile record without an access
   label reads as *Teacher*, the less privileged reading. *Remove from this device* asks
   twice when the device holds the lab key, the second time naming the last exported
   backup (or that none was ever exported).
10. **Which side refuses a foreign PC.** A real agent refuses the other lab's console
   certificate itself (its own `LabTrust` check) before presenting its leaf, so the
   console-side `link.refused` — *belongs to lab "A", which is not the active lab on
   this console* (via `LabNameResolver`) — is for clients without that check; the
   switching test proves both.
11. **Timing observations and limits.** In-process, 30 agents per lab over real TLS/UDP
   on one port: departure 12–30 ms, server up 156–965 ms, all 30 PCs linked in
   1.0–2.6 s, 0 lab-A beacons in the 5 s after departure; 20 × A → B → C → A with bounded
   threads and no leaked session. Under sustained refusal load a departure can spend the
   whole 2 s stop budget, and a PC refused for a long stretch returns within the 30 s
   reconnect cap rather than 15 s — a `BeaconGate` follow-up for portion 5. On the owner's
   Mac (Debug build, ad-hoc signed) the server was up 4–6 s after *Open* and after a
   re-open; the prime suspect is a Keychain prompt per rebuilt ad-hoc binary (the cdhash
   changes with every Debug build; the tests use the file protector).
   `SecretProtectorTiming` now warns about any keystore call slower than 250 ms, the
   open/build/start split isolates it, and it is expected to disappear with the signed
   packaged app (portion 7); to be re-measured.

Recorded with portion 4 (built, reviewed and fixed 2026-09-09):

12. **What a returning console may send again.** `logs/jobs-inflight.json`
   (`InFlightJobsDocument`, schema 1, stamped with the lab and instance that wrote it)
   holds the rows a session had delivered and not closed. `InFlightJobPolicy` with
   `LabSession.RefuseRestore` sends a row again only when it is a `run_script` whose text
   is still in `scripts.json` — found by the SHA-256 the job carries and re-offered through
   the new session's `FileOffers`, because the old session's offer died with it — only
   while the row is younger than the smaller of the job's own timeout and
   `Defaults.InFlightJobsMaxAge` (one hour), and only to a PC whose `Hello.boot_time_unix`
   predates the delivery: a rebooted PC has lost the ledger that would answer the re-send,
   so the copy would *run*. Never restored: `shutdown`, `reboot`, `logoff`,
   `reset_profile`, `self_update`, `rekey`, `send_file`, `install_package`,
   `collect_files`. Every other saved row appears as a closed row in `JobState.TimedOut`
   reading *Outcome unknown — the console left this lab*, with a `job.outcome_unknown`
   event — `TimedOut` and not `Failed`, so a result that does arrive later still replaces
   it (`D-43`). A restored row is marked `JobRecord.RestoredFromDisk` and left out of the
   next `SnapshotInFlight`, so it cannot outlive two sessions. An unreadable, newer-schema
   or foreign-lab file means nothing is owed, is reported as `jobs.inflight_unreadable` or
   `jobs.inflight_foreign`, and never blocks activation: a lab must not fail to open
   because of a log.
13. **The write side.** `SaveInFlightJobsSoon` coalesces the save the way `lab.json`'s is
   coalesced, so thirty PCs closing one batch produce one write. `JsonStore.Save` claims
   the familiar `<document>.tmp` when it is free and a unique `<document>.<guid>.tmp` when
   it is taken, so two savers never interleave their bytes into one temporary; the
   collision filter cannot ask "does the name still exist", because the holder may already
   have moved its temporary onto the document, so a saver that loses the race takes a fresh
   name instead of rethrowing. `JobBatchLogs.Restore` merges a restored batch into the
   document already on disk rather than overwriting it, so the PCs that finished before the
   console left keep their results, and restored rows show in the jobs panel.

Rejected: keeping inactive labs connected in the background (`D-53` item 2); one Kestrel
shared across sessions with per-lab routing (trust is per lab and the listener would
outlive its session); a process restart per switch (slow, and loses the departure
report); delivering a previous teacher's output to whichever console links next
(`D-53` item 5).

Validation: console tests with a `TestRig` of two labs, one controller and one port —
A → B → A with 30 agents per lab, no lab-A beacon within 5 s of departure, all A links
closed, B agents linked within 15 s, an A agent refused with the lab-mismatch event, rapid
A, B, C, A ending with one active lab and one Kestrel, a failed activation leaving `Failed`,
`ScreenStore` disposed, a mixed batch with one wrong passphrase importing the rest, and a
headless chooser render. Portion 2 (2026-09-08): the `TestRig` tests above pass, plus a close step that throws and the rest still running, a release that throws never leaving the controller `Activating`, a selection arriving while the previous lab starts, disposal cancelling an activation waiting on a prompt, 20 rounds of switching leaking neither sessions nor threads nor handles, and an import that fails after writing leaving no directory, index entry or keystore item — 728 tests in all (603 Shared + 125 Console, `LabSwitchTests` a non-parallel collection). Portion 4 (2026-09-09): 785 tests (637 Shared + 148 Console), including the ownership and restoration rules above, and a drill on the isolated Windows VM clone with a real agent — a 110-second SYSTEM script delivered by console instance X, X stopped 21 s in, the agent linked to instance Y of the same lab, the script finished under Y, Y never saw the job, and X came back with the same instance id and received the result with its output and exit code (hand-over 15–20 s); the same session saw a teacher leaf refused for `self_update` with `job.refused_by_role` before any manifest pull. Two limits of that drill, recorded rather than smoothed over: progress produced while X was away reached nobody, as designed, and its ad-hoc script was not a library script, so the console first closed the restored row as *outcome unknown* and the agent's pending-result flush then replaced it with the true result — a library script would have been re-offered and re-sent instead. Not covered there: a network push of this build from an administrator console, and `run_as: user` (the clone has no interactive user). The measured numbers are item 11; the real-Mac 4–6 s is under investigation. Implementation status is tracked in ROADMAP M5.

## D-58 — Take-over without comparing clocks, and truthful ownership states (M5 portion 5)

Context (2026-09-08): `BeaconGate` honours a `take` beacon from another instance when
`beacon.TakeAtUnix > _linkedAtUnix` — the taker's clock against the agent's clock, so a
console 40 s behind cannot take a room from a console that linked 30 s ago. `HeldElsewhere()`
returns every known PC not linked here — presumed ownership, shown as fact. `D-53` item 6
asked for both to be fixed.

Decisions:

1. **Only the agent's clock is compared with itself.** A linked agent honours a verified
   beacon from a *different* instance with `take != 0` when (a) the `(inst, take)` token
   has not been honoured before, (b) the beacon *arrived* after the link was established,
   measured on the agent's own clock, and (c) `take` is within `TakeOverWindow +
   BeaconMaxSkew` of the beacon's own `ts`. The taker's timestamp is never compared with
   the agent's link time. No beacon field changes.
2. **Ownership has four states, and only one of them claims another holder.**
   `HeldElsewhere()` is replaced by `Ownership(machine)`: `LinkedHere`;
   `ObservedElsewhere(instance, at)` only when positively learned — an `Unlinked` reason
   naming a take-over, or `Hello.previous_instance_id` within a fresh sighting of that
   instance's beacon — recorded as `MachineRecord.LastInstanceObservedUnix` (`D-55`);
   `Offline(lastSeen)`; `Unknown`. The other-console banner counts only observed PCs —
   *holds at least N* — and an offline PC is shown offline, not *held by …*.

Rejected: a clock-sync step between consoles (no server, no channel between them, `D-21`);
correcting the taker's `take` by the `Welcome.server_time_unix` skew the agent knows (the
agent knows the skew of the console it is linked to, not of the taker); treating every PC
not linked here as held elsewhere (the current behaviour; false on an idle lab).

Validation: Shared tests drive the gate with skewed clocks, the honour-once rule and the
window; console tests run two consoles with ±30 s skew and show an offline PC as offline.
Implementation status is tracked in ROADMAP M5.

## D-59 — Teacher-console packaging: a C# per-user Windows installer, a scripted `.app`/`.dmg`, a Linux tarball; single instance and scoped firewall rules (M5 portions 6 and 7)

Context (2026-09-08): `tools/publish-all.sh` produces executable directories, not
packages; the console has no document activation and no notion of a second launch;
`D-54` items 2–5 fixed the lifecycle rules and left the tooling open.

Decisions:

1. **Windows: a C# per-user self-installer, no third-party toolchain.**
   `src/LabControl.ConsoleSetup/` is a single-file, self-contained `asInvoker` exe;
   `tools/package-windows.sh` publishes the console for `win-x64` and embeds it as a
   resource. It installs to `%LOCALAPPDATA%\Programs\LabControl\Console\`, creates the
   Start-menu `.lnk` (`IShellLinkW` via CsWin32) and an optional desktop shortcut, writes
   `HKCU\…\Uninstall\LabControl Console`, registers the `HKCU\Software\Classes` ProgIds
   `LabControl.LabFile` (`.lclab`) and `LabControl.Backup` (`.lcbak`) with an OpenWith
   handler, and becomes the default only when no `UserChoice` exists. `--uninstall`
   removes owned files, keys and rules and keeps the data; `--remove-data` is separate.
   Product and Start-menu name: *LabControl Console* (default chosen 2026-09-08, owner
   may change).
2. **Scoped firewall rules, requested at the point of use.** `NetworkReadiness` checks
   for inbound, port-scoped rules in the group `LabControl Console` (TCP `ConsolePort`
   47800, UDP `BeaconPort` 47801, Private and Domain profiles). When they are missing the
   console shows a banner whose *Allow…* runs `ConsoleSetup.exe --firewall` with `runas`;
   a denied elevation yields a diagnostic with the exact `netsh` lines. Nothing disables
   the firewall and no broad exclusion is created.
3. **macOS: a scripted bundle with an ad-hoc signature.** `tools/package-mac.sh` builds
   `LabControl.app/Contents/{Info.plist, MacOS/, Resources/labcontrol.icns}` with
   `CFBundleIdentifier org.ontfk.labcontrol.console` (default chosen 2026-09-08, owner
   may change), `CFBundleDocumentTypes` and `UTExportedTypeDeclarations` for `lclab` and
   `lcbak`, `NSLocalNetworkUsageDescription`, signs with `codesign --force --deep --sign -`
   and wraps it with `hdiutil` in a DMG carrying an Applications symlink. The Gatekeeper
   prompt is documented, not promised away (`D-15`). File activation goes through
   Avalonia 12's `IActivatableLifetime`/`FileActivatedEventArgs`; files that arrive before
   the chooser exists are queued. *Remove local data…* lives inside the app.
4. **Linux: a tarball with per-user scripts and XDG registration.** `tools/package-linux.sh`
   produces `install.sh`/`uninstall.sh` installing to `~/.local/opt/labcontrol/console/`,
   a `.desktop` entry (`Exec=… %F`, `MimeType`), MIME XML and icons, then runs
   `update-mime-database`/`update-desktop-database` and `xdg-mime default` only when no
   default exists. Prerequisites: `libicu`, `libfontconfig1`, `libx11-6`, `libice6`,
   `libsm6`, `libgl1`; `libsecret-1-0` optional (its absence means the file-backed secret
   protector, shown per profile). The documented matrix is Ubuntu 22.04 and 24.04, and
   Linux acceptance in the first M5 pass is documented and manual (default chosen
   2026-09-08, owner may change).
5. **Document activation and the single instance.** `ConsoleOptions.TryParse` accepts
   positional existing files with a known extension as `FilesToOpen` and `--import-only`
   for the forwarder. `Services/SingleInstance.cs` derives a name from the first 16 hex
   digits of `sha256(DataDirectory)`: a Windows named pipe `labcontrol-console-<hash>`
   (`CurrentUserOnly`) or a Unix socket `console-<hash>.sock` (0600) inside this user's
   private directory `labcontrol-<uid>` (mode 0700) under `$XDG_RUNTIME_DIR` or the temp
   directory. The client sends one JSON line `{"schema_version":1,"open":[…]}`, the
   server validates it and raises `FilesArrived` on the UI thread, and the client exits 0
   after the acknowledgement. `console.lock` (`D-55`) distinguishes a stale socket from a
   running console. Files opened this way enter the same import flow; they never activate
   a lab. On macOS a LaunchServices open reaches the same path through Avalonia's
   `IActivatableLifetime`/`FileActivatedEventArgs` (item 3), with the first activation
   filtered against the process's own arguments, which AppKit echoes back as opened files.

   Six rules added when portion 6 was built and reviewed (2026-09-09):

   - **The socket lives in this user's private directory, and the peer is checked.** The
     original path — `labcontrol-console-<hash>.sock` directly in the runtime or temp
     directory — was both too long and unsafe: `sun_path` holds 104 bytes on macOS (108 on
     Linux) including the terminator, and the temp directory there is already 48 of them,
     while in a world-writable `/tmp` another local user could pre-create that predictable
     path and receive the teacher's document paths. The directory is therefore verified — a
     real directory, not a symbolic link, mode 0700 — by the server before it binds and by
     every client before it connects, and each accepted connection is additionally checked
     with `LOCAL_PEERCRED` (macOS) or `SO_PEERCRED` (Linux); a kernel that answers neither
     leaves the 0700 directory as the guard.
   - **An unusable endpoint never costs the console its startup.** A path longer than
     `sun_path`, or a directory that is not ours, is an `EndpointUnusableException` the
     app logs and continues from — a second launch is then refused rather than forwarded.
     Before the fix it was an unhandled exception that could leave the console running
     with no window at all.
   - **A departing console refuses instead of acknowledging.** The endpoint is disposed
     first in the shutdown and answers *the console is shutting down* while stopping, so
     the launcher starts its own console. Previously the batch was acknowledged, the
     launcher exited 0, and the file was dropped on the way to the UI.
   - **Caps and an ordinary-file rule.** At most `Defaults.SingleInstanceMaxOpenPaths`
     (64) paths per launch — refused by the forwarder before it connects and again by the
     server, which also drops duplicates within a batch — a line size cap, and every
     forwarded path validated exactly like a command-line path and additionally required
     to be absolute. A document must be an ordinary file no larger than
     `Defaults.ConsoleDocumentMaxBytes` (8 MiB): reading a FIFO or a huge file on the UI
     thread froze the console permanently.
   - **One import at a time.** The drop target, *Add labs…* and forwarded launches queue
     behind a single pump in `App` and the chooser's counted busy flag (file flows nest);
     two concurrent imports could otherwise stack two passphrase prompts over the same
     profile store.
   - **The exit code is the lifetime's.** `Main` returns what the Avalonia lifetime
     returns, so a launch the data directory refuses does not look to a script like a
     console that ran and quit normally.

Rejected: WiX, Inno Setup or MSIX for the console (a second toolchain to maintain, or
store/signing prerequisites); the Windows student `Setup.exe` as the console installer
(`D-54`); a machine-wide install (elevation for every update); an always-on helper for
file activation; a mutex without a data-directory key (two `--data` directories are
legitimate at once).

Validation: forwarder → running server → import; a second process exits 0; a stale socket
is recovered; dry-run step tests for the Windows installer; the manual matrix on Windows,
macOS and Linux in ROADMAP M5. Implementation status is tracked in ROADMAP M5. Item 5 was
built, reviewed and fixed on 2026-09-09 (portion 6): 16 single-instance tests including
hostile input, eight concurrent launches, the shutdown window and the socket directory's
mode, plus a manual macOS run on a copy of the data directory. The Windows named-pipe path
and the Linux `SO_PEERCRED` path are compile- and logic-checked only; they belong to
portion 7's manual matrix.

## D-60 — Enrollment codes imported from a backup are dormant until activated (M5 portion 3)

Context (2026-09-08): `D-54` item 7 forbids treating a backup import as concurrent
enrollment recovery, because two independent holders of the CA cannot enforce single use
of a code from separate local journals. Today `ImportBackup` restores `enrollment.json`
as it was, codes included.

Decisions:

1. **Imported codes are dormant.** `EnrollmentCodeRecord` gains `DormantSinceImportUnix`
   and `IssuedByInstanceId`; `EnrollmentDocument` gains `Batches[]`, one issuer per batch.
   A backup imported into a profile marks every usable code dormant; `Redeem` answers
   `EnrollmentOutcome.DormantCode` naming *Settings → Enrollment → Use codes from the
   imported backup*. Activation is an explicit administrator action on that profile.
2. **Migration is not an import.** The single-lab migration (`D-55`) keeps its codes
   active: nothing was copied between holders.
3. **No global claim.** Two administrator copies that both activate the same batch can
   both redeem a code; the console does not pretend otherwise, and the documented remedy
   is a fresh batch, which voids the old stick (`D-28`).

Rejected: voiding imported codes outright (a lost laptop would force a new stick even when
the old one is in the administrator's hand); silently keeping them active (the `D-54`
item 7 hazard).

Validation: Shared tests refuse a dormant code and accept it after activation; console
tests import a backup and see the codes dormant. Implemented 2026-09-08 (M5 portion 3):
`enrollment.json` is schema 2 (`DormantSinceImportUnix`, `IssuedByInstanceId`,
`Batches[]`, upgraded from schema 1 on load), `Redeem` answers
`EnrollmentOutcome.DormantCode`, *Settings → Enrollment → Use codes from the imported
backup* activates them, and `Supersede` voids dormant codes together with the active
ones, so a fresh batch is always the whole truth. Status in ROADMAP M5.

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
| Avalonia.AvaloniaEdit | Console | Script editor with line numbers, undo and token coloring (D-39) |
| System.Management.Automation | Console | Offline PowerShell syntax parsing/tokenization only; no execution or per-PC runtime (D-39) |
| Grpc.AspNetCore | Console | gRPC server |
| Grpc.Net.Client, Grpc.Tools, Google.Protobuf | Shared/Agent | gRPC client + codegen |
| System.Security.Cryptography.ProtectedData | Shared | Windows DPAPI for the console instance key; the BCL dropped it from the shared framework (D-24) |
| SkiaSharp, SkiaSharp.NativeAssets.Linux | Shared (so Console, FakeAgent, Agent.Session) | JPEG encode/decode, scaling, the simulator's synthetic desktops (M3, D-34); pinned to the version Avalonia ships |
| Microsoft.Windows.CsWin32 | Agent, Agent.Session, Setup | Win32 P/Invoke source generator; names listed in `NativeMethods.txt`, never a hand-written `DllImport` (M2) |
| Vortice.Direct3D11, Vortice.DXGI | Agent.Session | DXGI Desktop Duplication and the D3D11 staging texture it is read through (M3, D-35); the maintained successor of SharpDX |
| Microsoft.Extensions.Hosting.WindowsServices | Agent | Windows service hosting (M2) |
| System.Management | Setup | WMI (profiles, NIC properties) |
| YamlDotNet | Console | package catalog |
| Serilog.Extensions.Logging, Serilog.Sinks.Console, Serilog.Sinks.File | all | logging |
| xunit.v3 | tests | testing; it hosts its own Microsoft.Testing.Platform runner, so no VSTest packages are needed (D-18) |
| Avalonia.Headless, Avalonia.Skia | tests | the console's windows built and rendered off screen for the UI tests (D-27) |
| NSubstitute | tests | *planned* — added when the first interface actually needs faking |
| Markdig | tools/DocsBuild | Markdown → HTML mirror of the documentation (D-12) |
