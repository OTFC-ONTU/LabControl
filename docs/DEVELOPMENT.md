# Developing LabControl

The working notes for building, running and testing LabControl. The public overview is
the [README](../README.md); the milestone plan and its acceptance criteria are in the
[roadmap](ROADMAP.md).

## Requirements

Requires the .NET 10 SDK and nothing else. The `net10.0-windows` projects compile on
macOS and Linux too (they just cannot run there), so a broken Windows build is caught
immediately rather than at the next visit to the lab.

```bash
dotnet build
dotnet test
dotnet run --project src/LabControl.Console            # first run: the wizard creates a lab
#   Settings → Write USB payload… → choose a folder, e.g. ~/usb
dotnet run --project src/LabControl.FakeAgent -- --count 14 --payload ~/usb
#   in the console: Enrol PCs… → passphrase → the PCs enrol and appear
tools/publish-all.sh
tools/package-all.sh                                   # the teacher-console packages (below)
tools/docs-build.sh
tools/make-icon.py                                     # regenerate the console icon (png/ico/icns)
```

If `dotnet test` reports zero tests, run the built test executable directly
(`tests/<project>/bin/Debug/net10.0/<project>`).

## Teacher-console packages

The console is installed from one file per platform — no administrator, no runtime
download, nothing that installs a service or touches an account. The packages are built
on any machine that can run `dotnet` (the macOS `.app` and DMG need macOS) and land in
`artifacts/package/`:

```bash
tools/package-windows.sh   # one self-contained Setup.exe, installs per user; --dry-run prints the plan
tools/package-mac.sh       # LabControl.app, ad-hoc signed, in a DMG with an Applications symlink
tools/package-linux.sh     # a tarball with per-user install.sh / uninstall.sh
tools/package-all.sh       # all three; the macOS part is skipped off macOS
```

`tools/publish-all.sh` is unchanged and still produces the plain executable directories
used for development and for the USB payload. See
[the teacher-console chapter](INSTALLER.md) for what each package does, and for the
Windows LAN-access banner and the first-open steps on macOS.

## Console command line

```text
LabControl.Console [--data <directory>] [--port <n>] [--bind <address>]
                   [--dev-agent-cert-days <n>] [--import-only]
                   [<file>.lclab|.lcbak|.lcgrant|.lcreq …]
```

Every switch is a development affordance; a lab machine runs the console with no
arguments and keeps its data in `~/.labcontrol/` (macOS, Linux) or
`%APPDATA%\LabControl\` (Windows).

| Argument | What it does |
|---|---|
| `--data <directory>` | Use another data directory — one console profile per directory |
| `--port <n>` | Listen on another port (the beacon tells the PCs where to connect) |
| `--bind <address>` | Bind the gRPC server to one address instead of every interface |
| `--dev-agent-cert-days <n>` | Issue agent certificates with this lifetime, so renewal (`D-25`) can be exercised against `FakeAgent` without waiting five years |
| `--import-only` | Hand the named documents to the console already running on this data directory and exit |
| `<file>…` | Documents to import, exactly like *Add labs…* |

**Two console profiles on one machine** (the alternation and take-over tests):

```bash
dotnet run --project src/LabControl.Console -- --data ~/labA --port 47800
dotnet run --project src/LabControl.Console -- --data ~/labB --port 47810   # import the backup from A
```

**Documents.** The console also takes documents to open as positional arguments —
`.lclab` lab files, `.lcbak` backups, `.lcgrant` grants and `.lcreq` requests — which is
what a double-click or *Open with* passes it. They are imported exactly like *Add labs…*
and never open a lab:

```bash
dotnet run --project src/LabControl.Console -- ~/Downloads/room-444.lclab
dotnet run --project src/LabControl.Console -- --import-only ~/Downloads/room-444.lclab
```

One console runs per data directory. A second launch does not start a second console: it
hands its file paths to the running one and exits (0 once they were taken). `--import-only`
never starts a console of its own — it exits 1 when none is running — and is what a
file-type registration uses.

## The simulator: `FakeAgent`

`LabControl.FakeAgent` plays a room of student PCs on any operating system, so the console
is developed without a Windows machine.

```text
LabControl.FakeAgent [--count N] [--payload <dir>] [--data <dir>] [--console host[:port]]
                     [--fail SPEC]... [--reinstall N]... [--verbose]
LabControl.FakeAgent --lab <payload-dir> [--count N] [--lab <payload-dir> [--count N]]...
                     [--data <dir>] [--verbose]
```

| Argument | What it does |
|---|---|
| `--count N` | How many PCs to simulate, 1–30; before the first `--lab` for every lab, after a `--lab` for that lab |
| `--payload <dir>` | The USB payload written by the console (`setup.json` + `ca.crt`); needed to install new PCs |
| `--lab <dir>` | A lab's payload directory; several `--lab` groups run from one process (M5), each under `<data>/lab-<id>` |
| `--data <dir>` | Where the PCs keep their state (default `~/.labcontrol-fake`) |
| `--console h[:p]` | Pin the console address instead of listening for beacons |
| `--fail SPEC` | Inject a failure into one PC (below) |
| `--reinstall N` | Wipe PC-N's state first: a new agent id with the same number (`D-25`) |
| `--verbose` | Debug logging |

`FakeAgent --fail 7:never --fail 8:late=20 --fail 9:die-mid-job --fail 10:job-error
--fail 11:burned-code --fail 12:forged-revocation --fail 13:outdated` injects failures;
`--reinstall 7` plays a reinstalled PC; `burned-code` implies a reinstall of that PC,
since only an enrolment can present a code; `--console 127.0.0.1` pins the console on a
machine with no network.

## UI screenshots from the headless tests

The console tests render the real views headlessly. Set `LABCONTROL_UI_SHOTS` to a folder
to keep the PNGs:

```bash
LABCONTROL_UI_SHOTS=/tmp/shots dotnet test --project tests/LabControl.Console.Tests
```

The screenshots in the README's `docs/images/` come from this run.

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
7. **Network updates now use the M4 signed recovery flow.** For M4 acceptance, use a
   clean, installer-owned installation from [the USB Setup instructions](INSTALLER.md);
   Setup does not migrate the unowned development installation described above. Unlock
   the console's lab key, select the PC, choose *Push agent build…*, select the published
   Windows agent/session pair and press *Push*. The console signs the bundle and displays
   per-PC update state. Acceptance requires ten continuous minutes connected on the new
   build. An actual crashing or unlinked release has been verified to recover automatically
   to the previous version on the isolated VM; see the roadmap for fleet acceptance.

The Windows acceptance fixtures used on the isolated VM clones (native round trips, the
update-recovery drill, the transfer drill and the UTM helpers) are in
[`tools/windows-tests/`](../tools/windows-tests/README.md).

## Downgrading to a pre-M5 build

An M5 console keeps each lab in `labs/<lab_id>/` under its data directory
(`~/.labcontrol/` or `%APPDATA%\LabControl\`). To run an older build on the same lab,
copy `labs/<lab_id>/*` back to the data root; the old build reads it, but on its next save
it drops the schema-2 `lab.json` fields it does not know. The next launch of an M5 build
notices the root differs from its saved copy, moves the root files to
`migration-conflict-<timestamp>/` and opens the saved lab — nothing is deleted, but the
two copies have to be reconciled by hand.

## Status notes by milestone

The README keeps a one-line status per milestone; [the roadmap](ROADMAP.md) is the
authoritative record. These are the longer notes the README used to carry.

**M0 — skeleton and toolchain: done (2026-09-04).**

**M1 — lab identity, link and presence: done (2026-09-05).** The trust model, the beacon,
mutual TLS, enrolment, the link with jobs and renewal, take-over between two teacher
machines, the sealed backup, the console UI and a `FakeAgent` that plays a room of PCs —
demonstrated live by the owner with two console profiles and 30 fake PCs.

**M2 — the real Windows agent.** Portion 1 of 4 — the service host, the DPAPI-protected
store, provisioning from the USB payload, real inventory, the side-by-side version layout
and `scripts/dev-install.ps1` — and portion 2 — `session.exe` living on the student's
desktop as SYSTEM, supervised by the service over a named pipe, with the console showing
who is logged on, whether the screen is locked and whether the helper is up — are
verified on the Windows VM. Portion 3 — shutdown / reboot / log off, Wake-on-LAN from the
console, the minimal `PullFile` and `run_script` with streamed output and a kill at the
timeout, plus a development-only *Run test script…* — is verified on the VM too, down to a
script surviving a 30-second network cut; only Wake-on-LAN itself waits for the real
`PC-00`, since the VM has no such thing. Portion 4 — the minimal push-and-restart: *Push
agent build…* in the console sends a published `agent.exe` + `session.exe` to a PC, which
installs it side by side, repoints its service, restarts and reports back as the new
version — is verified on the VM and on the first real lab PC (`PC-10`, x64, 2026-09-07);
only Wake-on-LAN is deferred to M4 with the installer.

**M3 — screens.** Portion 1 of 3 (2026-09-07) — the `PushVideo` channel, the thumbnail
and full-mode frame formats, the console's per-PC pictures, live thumbnails on the tiles,
the single-PC window with dirty-rectangle deltas, and `FakeAgent` drawing synthetic
desktops — is built and tested on the Mac. Portion 2 (2026-09-07) puts real capture into
`session.exe` — DXGI Desktop Duplication with a GDI fallback, relayed through the service's
pipe, on the same producer the simulator uses — and is verified on `PC-10`. Portion 3
(2026-09-07) adds remote control: a *Control* toggle in the single-PC window sends the
teacher's mouse and keyboard to the PC (text as Unicode, so Ukrainian typed on the Mac is
Ukrainian on the PC), with *Ctrl+Alt+Del* raised by the service.

**M4 deployment is being integrated** (`D-52`). USB building, signed updates with
external recovery, Windows handout delivery and Setup/removal/rekey code are under test.
Isolated Windows install, file delivery and recovery checks have passed; remaining
removal, administrator-access and physical-lab acceptance is tracked in
[the roadmap](ROADMAP.md). The USB installer asks at most one question; account creation
is checked by default but can be turned off for testing with an existing home-PC account,
and a standalone uninstaller removes LabControl without the USB or console
([installer](INSTALLER.md), `D-40`).

The owner designated `1.0.0` as the clean physical-lab release candidate on 2026-09-08.
It is not called released until those remaining M4 checks pass. Agents installed from
this candidate remain update-compatible with the additive M5 portion-5 access field.

**Send files…** prepares a batch for the selected PCs. The Windows path now requires an
installer-owned student SID and a usable local profile; it never substitutes a personal
profile. Keep source files available until jobs finish. Files are hash-verified before
replacement; optional opening uses the student's interactive token. The Jobs panel shows
a separate result for every file and PC. Delivery, replacement and PDF opening under the
standard student token have passed on the VM; Word and the physical fleet remain to verify.

In **Jobs**, select any result row and click **Export batch logs…** to save a ZIP
with results and output for every PC in that group action. Running and offline PCs
are included with their current status. Automatic batch snapshots also remain in
`logs/batches/` inside the console's data directory.

**M5 — several saved labs, one active room.** Teachers add files for all their labs at
once, then select the room for each lesson without restarting the app or re-importing a
backup. Inactive rooms have no background connections or video. Routine teacher lab files
are distinct from administrator recovery backups; administrators can add `.lcbak` backups
directly to the same list and switch rooms with their administrator access preserved. M5
also includes simple offline console installers/packages for Windows, macOS and Linux:
application files, launchers and opening both file types, without installing student
services or changing student-account/system settings on the teacher's device. All eight
portions are built and reviewed; what is still unverified on Windows, on Linux and in the
physical lab is listed in M5 in [the roadmap](ROADMAP.md). Classroom control is M6 and
catalog/localization/polish M7.

**Languages.** The product UI is English today. M7 must ship the complete product-facing
experience in **Ukrainian, English and Russian**, including the teacher console, Windows
Setup/removal, stock student-facing text and generated USB instructions. The console will
have a saved language selector; English remains the safe fallback (`D-67`).
