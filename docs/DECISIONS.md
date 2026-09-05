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
   Wake-on-LAN, power and the `student` account because none of them is needed to test the
   agent, and it is documented as development-only in the README.

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
| System.Security.Cryptography.ProtectedData | Shared | Windows DPAPI for the console instance key; the BCL dropped it from the shared framework (D-24) |
| SkiaSharp | Console, Agent.Session | JPEG encode/decode, scaling |
| Microsoft.Windows.CsWin32 | Agent, Agent.Session, Setup | Win32 P/Invoke source generator; names listed in `NativeMethods.txt`, never a hand-written `DllImport` (M2) |
| Vortice.Direct3D11, Vortice.DXGI | Agent.Session | Desktop Duplication |
| Microsoft.Extensions.Hosting.WindowsServices | Agent | Windows service hosting (M2) |
| System.Management | Agent, Setup | WMI (profiles, NIC properties) |
| YamlDotNet | Console | package catalog |
| Serilog.Extensions.Logging, Serilog.Sinks.Console, Serilog.Sinks.File | all | logging |
| xunit.v3 | tests | testing; it hosts its own Microsoft.Testing.Platform runner, so no VSTest packages are needed (D-18) |
| Avalonia.Headless, Avalonia.Skia | tests | the console's windows built and rendered off screen for the UI tests (D-27) |
| NSubstitute | tests | *planned* — added when the first interface actually needs faking |
| Markdig | tools/DocsBuild | Markdown → HTML mirror of the documentation (D-12) |
