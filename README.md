# LabControl

Classroom fleet control for a single computer lab: **one teacher console
(macOS / Windows / Linux)** managing **14 Windows student PCs** on the same LAN —
live screen mosaic with remote control, Wake-on-LAN / shutdown / reboot, silent
software installs and scripts on all PCs at once, teacher-screen broadcast, screen lock,
a composable exam mode, and a **one-shot USB installer** that prepares a PC completely
(service, firewall, WoL, `student` user with auto-logon) in one run.

Self-hosted, no server, no cloud, no domain, no internet required on the student PCs.

The teacher machine is **replaceable**: the lab's identity is a private certificate
authority, not the laptop's certificate, so moving the console to another computer —
macOS, Windows or Linux — means importing one encrypted backup file, and no student PC is
touched. The software is designed for labs of up to 30 PCs, not just the first one's 14.

Built with .NET 10 + Avalonia + gRPC. See [`CLAUDE.md`](CLAUDE.md) for the project
brief and [`docs/`](docs/) for architecture, protocol, installer and roadmap.

## Documentation

| Document | What it answers |
|---|---|
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | What gets built, in what order, and how each milestone is judged done |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Components, processes, data flow, threat model |
| [`docs/PROTOCOL.md`](docs/PROTOCOL.md) | gRPC services, discovery, video encoding, job lifecycle |
| [`docs/INSTALLER.md`](docs/INSTALLER.md) | Exactly what the USB installer does on a student PC |
| [`docs/DECISIONS.md`](docs/DECISIONS.md) | Why each choice was made, and what was rejected |

The same documents are mirrored as a small self-contained website in
[`docs/html/`](docs/html/index.html) — open `docs/html/index.html` in any browser, no
internet required. **The Markdown files are the source of truth**; the HTML is
generated. After editing any `.md`:

```bash
tools/docs-build.sh
```

## Status

**M0 — skeleton and toolchain: done (2026-09-04).**

**M1 — lab identity, link and presence: done (2026-09-05).** The trust model, the beacon,
mutual TLS, enrolment, the link with jobs and renewal, take-over between two teacher
machines, the sealed backup, the console UI and a `FakeAgent` that plays a room of PCs —
demonstrated live by the owner with two console profiles and 30 fake PCs.

**M2 — the real Windows agent: in progress.** Portion 1 of 4 — the service host, the
DPAPI-protected store, provisioning from the USB payload, real inventory, the side-by-side
version layout and `scripts/dev-install.ps1` — and portion 2 — `session.exe` living on the
student's desktop as SYSTEM, supervised by the service over a named pipe, with the console
showing who is logged on, whether the screen is locked and whether the helper is up — are
verified on the Windows VM. Portion 3 — shutdown / reboot / log off, Wake-on-LAN from the
console, the minimal `PullFile` and `run_script` with streamed output and a kill at the
timeout, plus a development-only *Run test script…* — is verified on the VM too, down to a
script surviving a 30-second network cut; only Wake-on-LAN itself waits for the real
`PC-00`, since the VM has no such thing. Portion 4 — the minimal push-and-restart: *Push
agent build…* in the console sends a published `agent.exe` + `session.exe` to a PC, which
installs it side by side, repoints its service, restarts and reports back as the new
version — is verified on the VM and on the first real lab PC (`PC-10`, x64, 2026-09-07);
only Wake-on-LAN is deferred to M4 with the installer ([`docs/ROADMAP.md`](docs/ROADMAP.md)).

**M3 — screens: in progress.** Portion 1 of 3 (2026-09-07) — the `PushVideo` channel, the
thumbnail and full-mode frame formats, the console's per-PC pictures, live thumbnails on the
tiles, the single-PC window with dirty-rectangle deltas, and `FakeAgent` drawing synthetic
desktops — is built and tested on the Mac. Portion 2 (2026-09-07) puts real capture into
`session.exe` — DXGI Desktop Duplication with a GDI fallback, relayed through the service's
pipe, on the same producer the simulator uses — and is verified on `PC-10`. Portion 3
(2026-09-07) adds remote control: a *Control* toggle in the single-PC window sends the
teacher's mouse and keyboard to the PC (text as Unicode, so Ukrainian typed on the Mac is
Ukrainian on the PC), with *Ctrl+Alt+Del* raised by the service; it awaits its run on
`PC-10`.

## Quick start
```bash
dotnet build
dotnet test
dotnet run --project src/LabControl.Console            # first run: the wizard creates a lab
#   Settings → Write USB payload… → choose a folder, e.g. ~/usb
dotnet run --project src/LabControl.FakeAgent -- --count 14 --payload ~/usb
#   in the console: Enrol PCs… → passphrase → the PCs enrol and appear
tools/publish-all.sh
tools/docs-build.sh
tools/make-icon.py                                     # regenerate the console icon (png/ico/icns)
```

Two console profiles on one machine (the alternation and take-over tests):

```bash
dotnet run --project src/LabControl.Console -- --data ~/labA --port 47800
dotnet run --project src/LabControl.Console -- --data ~/labB --port 47810   # import the backup from A
```

`FakeAgent --fail 7:never --fail 8:late=20 --fail 9:die-mid-job --fail 10:job-error
--fail 11:burned-code --fail 12:forged-revocation --fail 13:outdated` injects failures;
`--reinstall 7` plays a reinstalled PC; `burned-code` implies a reinstall of that PC,
since only an enrolment can present a code; `--console 127.0.0.1` pins the console on a
machine with no network. Screenshots of the UI from the headless tests:
`LABCONTROL_UI_SHOTS=/tmp/shots dotnet test --project tests/LabControl.Console.Tests`.

## Testing the agent in a Windows VM

The agent is Windows-only and cannot run on the Mac, so M2 is tested in a VM on the
MacBook (fast, revertible) and on `PC-00` in the lab (real NIC, real antivirus). On Apple
Silicon the VM is **Windows 11 ARM**, so the agent is published as `win-arm64`; the lab PCs
get `win-x64`. `tools/publish-all.sh` produces both.

1. **Install a VM.** [UTM](https://mac.getutm.app) is free: *Create a New Virtual
   Machine → Virtualize → Windows*, tick *Install Windows 10 or higher* and let UTM
   download the Windows 11 ARM installer; 4 GB RAM and 64 GB disk are plenty. Install the
   SPICE guest tools when prompted (shared folders, clipboard). Take a snapshot once Windows
   is on the desktop — every destructive test starts from it.
2. **Network.** Leave UTM's *Shared Network*; the Mac is reachable from the VM at the
   gateway address of the `vmnet` bridge (usually `192.168.64.1`). UDP broadcasts from the Mac
   do reach the VM on that bridge, but if the PC stays *searching*, pin the console with
   `-ConsoleHost 192.168.64.1` — that is what `console_host` in `agent.json` is for.
3. **Publish and share.** Make a folder to share, say `~/LabVM`, and put in it the
   published build as `win-arm64\` (`agent.exe`, `session.exe`), the USB payload as `usb\`
   (Settings → *Write USB payload…* in the console, pointed at `~/LabVM/usb`) and both
   files from `scripts/` (`dev-install.ps1`, `dev-install.cmd`). Point the VM's shared
   directory at it (UTM: the folder icon in the VM window); the SPICE tools mount it as
   drive `Z:`.
4. **Install** in the VM: open `Z:` in Explorer and double-click `dev-install.cmd`
   (edit the PC number and the console address at the top of it first if they differ; the
   default is `-Number 1 -ConsoleHost 192.168.64.1`). Accept the SmartScreen and UAC
   prompts. The script raises the WebDAV size limit the shared drive needs, lays out
   `C:\Program Files\LabControl\app\<version>\`, locks down `C:\ProgramData\LabControl\`,
   runs `agent.exe --install` (keypair under DPAPI, pinned `ca.crt`, one enrollment code
   from the stick, `agent.json`), registers the `LabControl` service and starts it. Then
   open *Enrol PCs* in the console and type the passphrase: the VM enrols within half a
   minute and appears as `PC-01` with its real inventory.
5. **Watch and iterate.** The log is
   `C:\ProgramData\LabControl\logs\agent-<date>.log`; `agent.exe --run --verbose` runs the
   same agent in the foreground. Re-running `dev-install.cmd` with a new build installs it
   side by side and repoints the service, keeping the PC's identity. Two things to know
   about the shared drive: Windows caches WebDAV files, so a rebuilt file with the same
   name may be served stale for a while — publish a new build into a differently named
   folder if in doubt — and restarting the `WebClient` service (which the script does once)
   can leave `Z:` unavailable until the VM is rebooted. `dev-install.ps1 -Uninstall`
   removes everything but the PC's identity, `-Uninstall -PurgeData` removes that too. On a
   real PC add `-Student` (or `STUDENT=1` in `dev-install.cmd`) to also create the standard
   `student` account with auto-logon, the way Setup.exe will; `-Uninstall -RemoveStudent`
   takes it away again.
6. **Check the session helper** (M2 portion 2). Within a few seconds of the service
   starting, Task Manager → *Details* shows `session.exe` running as SYSTEM in the user's
   session (not session 0), and the console's events panel shows *Session helper … is up in
   session 1 (…, desktop Default)*. Then: end `session.exe` from Task Manager — it is back
   within 5 s and the console logs a *session.helper_exited* warning; press Win+L — the tile
   reads *student (locked)* (or your account name) and *screen locked* appears in the
   events, unlocking clears it; sign out — the tile reads *nobody logged on* and a new
   helper appears on the logon screen; sign in — the user is back. The helper's own log is
   `C:\ProgramData\LabControl\logs\session-<date>.log`; `session.exe --probe` from an
   elevated prompt prints what a process in the session sees. A standard user cannot end
   `session.exe` and cannot read the logs.
7. **Push the next build from the console** (M2 portion 4) instead of re-running
   `dev-install.cmd`: publish, select the PC, *Push agent build…*, choose the folder with
   `agent.exe` and `session.exe` (or `artifacts/win-arm64` straight from `publish-all.sh`)
   and press *Push*. The Jobs panel shows the files being pulled and the restart; the PC is
   back within a minute with the tile reading `agent 0.1.0+<build id>` and the job ending
   *Running … now (was …)*. Both version directories stay under `app\`; older ones are
   removed. There is no rollback yet (M4): if a pushed build fails to start, install a good
   one with `dev-install.cmd`.

Requires the .NET 10 SDK and nothing else. The `net10.0-windows` projects compile on
macOS and Linux too (they just cannot run there), so a broken Windows build is caught
immediately rather than at the next visit to the lab.
