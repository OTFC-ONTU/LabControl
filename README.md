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

**M2 — the real Windows agent: in progress.** Portion 1 of 4 is built: the service host, the
DPAPI-protected store, provisioning from the USB payload, real inventory, the side-by-side
version layout and `scripts/dev-install.ps1`. Next it runs in a Windows VM — see below —
then the session helper, power and scripts, and push-and-restart follow
([`docs/ROADMAP.md`](docs/ROADMAP.md)).

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
3. **Publish and share.** `tools/publish-all.sh`, then expose `artifacts/win-arm64` and the
   USB payload folder (Settings → *Write USB payload…*) to the VM: a UTM shared directory
   (mounted as a drive by the SPICE tools), or a network share.
4. **Install** from an elevated PowerShell in the VM:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   \\path\to\scripts\dev-install.ps1 -Build Z:\win-arm64 -Payload Z:\usb -Number 1 -ConsoleHost 192.168.64.1
   ```

   The script lays out `C:\Program Files\LabControl\app\<version>\`, locks down
   `C:\ProgramData\LabControl\`, runs `agent.exe --install` (keypair under DPAPI, pinned
   `ca.crt`, one enrollment code from the stick, `agent.json`), registers the `LabControl`
   service and starts it. Then open *Enrol PCs* in the console: the VM enrols and appears
   as `PC-01` with its real inventory.
5. **Watch and iterate.** The log is
   `C:\ProgramData\LabControl\logs\agent-<date>.log`; `agent.exe --run --verbose` runs the
   same agent in the foreground. Re-running `dev-install.ps1` with a new build installs it
   side by side and repoints the service; `-Uninstall` removes everything but the PC's
   identity, `-Uninstall -PurgeData` removes that too.

Requires the .NET 10 SDK and nothing else. The `net10.0-windows` projects compile on
macOS and Linux too (they just cannot run there), so a broken Windows build is caught
immediately rather than at the next visit to the lab.
