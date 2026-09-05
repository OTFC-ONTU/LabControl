# Installer (`LabControl.Setup`)

Goal: **plug in the USB stick, run one file as the local admin, answer one question
(PC number), walk to the next PC.** Everything else is automatic and idempotent —
running it again on an already-configured PC repairs the installation instead of
breaking it.

## Building the USB payload (on the console)

Console → Settings → *Write USB payload* writes `ca.crt` and `setup.json` (all that
`FakeAgent --payload` needs); `tools/build-usb.sh <mount>` (M4) adds the binaries:

```
<USB>\LabControl\
   Setup.exe          self-contained win-x64, manifest requireAdministrator
   payload\app\<version>\agent.exe, session.exe   — one version directory, copied to the
                      PC as-is; this is the same layout self-update produces (D-19)
   ca.crt             the lab's PUBLIC certificate authority — this is what the agent pins
   setup.json         { schema_version, lab_id, console_host? (optional pin),
                        student: {name:"student", password:"1"}, naming: "PC-{n:00}",
                        power: {...}, next_number: 1,
                        enrollment_codes: ["...", ...],    // single-use, one per PC + spares
                        used_enrollment_codes: ["..."],    // moved here by Setup as it spends them
                        written_by, written_at }           // which console, when
   INSTALL.txt        3-line human instructions (in Ukrainian)
```

**The stick carries no secret.** `ca.crt` is public by nature and the enrollment codes
are single-use: whoever finds the stick can, at worst, enrol a bogus machine, which the
console shows as an unexpected PC and removes with one click — removal revokes its
certificate too (`D-28`). The lab key never leaves the teacher machine and its backup
(`docs/ARCHITECTURE.md` §3.2). The console writes a few more codes than there are PCs, so
a failed install can be retried, and **writing a new stick voids the unused codes of every
earlier one** unless the teacher unticks it (`D-28`): the stick in the drawer stops
working the moment a newer one exists. Untick it only when PCs were installed from the
earlier stick and have not yet enrolled — they still hold those codes.

A stick is enrolled by the console that wrote it, or by one that imported that console's
backup afterwards — the codes travel in the backup and nowhere else (`D-28`). Writing a
stick therefore marks the backup stale until it is exported again.

`Setup.exe` deletes nothing from the stick except marking the code it used and updating
`next_number`.

## What Setup.exe does, in order

Each step is a class implementing `ISetupStep { Name; Check(); Apply(); }`, executed
sequentially, logged to `C:\ProgramData\LabControl\setup.log` and echoed to the
console window. `Check()` returning "already done" skips `Apply()` — this is what
makes re-runs safe. `--dry-run` prints the plan only; `--number 7` skips the prompt.

1. **Preflight** — is admin (else self-elevate via `runas`), Windows 10 1809+ x64,
   free disk ≥ 2 GB, stop existing `LabControl` service if present.
2. **PC number** — read `--number`, else parse hostname `PC-07`, else prompt once
   (default = `next_number` from `setup.json`). Then set the hostname to
   `PC-07` (`SetComputerNameEx`, takes effect after the final reboot).
3. **Files** — copy `payload\app\<version>\` to
   `C:\Program Files\LabControl\app\<version>\` and write that version into
   `app\current`; leave `app\previous` absent (there is nothing to roll back to yet).
   Copy `Setup.exe` alongside for repair. Set ACLs (Users: read/execute). Create
   `C:\ProgramData\LabControl\` with ACL SYSTEM + Administrators only. Installing into a
   version directory from the very first PC is deliberate: the layout is what makes
   self-update reversible (`D-19`), and it cannot be introduced later without visiting
   every machine.
4. **Enrollment** — generate `agent_id`; generate the PC's **own** ECDSA keypair on the
   PC and protect the private key with DPAPI (machine scope); copy `ca.crt` in as the
   pinned trust anchor; take one unused enrollment code from `setup.json`; write
   `agent.json` with `lab_id`, `number`, MAC and the optional `console_host`. The
   certificate itself is obtained from the console at the agent's first connection
   (`EnrollmentService.Enroll`, `docs/PROTOCOL.md`) — Setup does **not** need the console
   to be running or reachable. Until then the agent is installed but unenrolled, and says
   so in its log. Issuing the certificate needs the lab key unlocked on the console
   (`D-24`), so after the round with the stick the teacher opens *Enrol PCs* in the console
   and types the passphrase once; a PC that connects before that is told to try again
   later and does, on its own.
5. **Service** — `CreateService("LabControl", LocalSystem, auto-start, delayed=false)`
   with the binary path pointing at `app\<version>\agent.exe`; recovery: restart on
   failure (3×, 10 s) and then `agent.exe --rollback` on the fourth, which is the
   outside-the-agent half of the update rollback (`D-19`, `docs/ARCHITECTURE.md` §7.2);
   description string, then start it.
6. **Firewall** — outbound is allowed by default; add an inbound allow rule for
   `agent.exe` anyway (future direct-connect / diagnostics) and an ICMP echo allow
   rule (so the console can ping). Rules are tagged with a LabControl group name so
   exam-mode rules (`docs/ARCHITECTURE.md` §6.1) can be added and removed as a set
   without touching anything the college configured.
6a. **Antivirus exclusion** — the binaries are unsigned (`D-15`), so add the
   `C:\Program Files\LabControl\` **directory** — not the individual executables — to the
   Microsoft Defender exclusion list, so that version directories created by a later
   self-update are covered without a second visit
   (`Add-MpPreference` equivalent via WMI `MSFT_MpPreference`). If a third-party
   antivirus is present, Setup cannot configure it: it detects it, prints exactly which
   product and which path needs excluding, and records the fact so the console can warn
   the teacher rather than leaving a PC that silently stops reporting.
7. **Power & Wake-on-LAN**
   - `powercfg /hibernate off`; registry `HiberbootEnabled=0` (Fast Startup kills WoL).
   - NIC advanced properties via WMI/`Set-NetAdapterAdvancedProperty` equivalents:
     *Wake on Magic Packet = on*, *Energy-Efficient Ethernet = off*,
     *Allow this device to wake the computer* = on, *Only allow a magic packet* = on;
     "Allow the computer to turn off this device to save power" = **on** (required for
     WoL from S5 on many Realtek/Intel NICs).
   - Power plan: never sleep on AC, display off after 20 min, "never" for disk.
   - Disable "Windows Update → automatic restart during active hours" nagging
     (active hours 07–20); do **not** disable updates.
8. **Student account**
   - `NetUserAdd("student", "1")`, description "Student", flags: password never
     expires, user cannot change password, not in Administrators (member of
     `Users` only).
   - Local password policy on a non-domain PC has no complexity requirement by
     default; if `NetUserAdd` fails with `NERR_PasswordTooShort`, Setup applies
     `secedit` template `MinimumPasswordLength=1, PasswordComplexity=0` and retries.
   - Auto-logon: `Winlogon\AutoAdminLogon=1`, `DefaultUserName=student`,
     `DefaultDomainName=<hostname>`, password stored as the LSA secret
     `DefaultPassword` (`LsaStorePrivateData`), **not** in plain registry.
   - Hide the admin account from the logon screen (`SpecialAccounts\UserList`)
     so the student sees only "student"; the admin still logs in via "Other user".
   - Disable the lock-screen/timeout for `student`? No — leave OS defaults; the
     console can log the student off anyway.
9. **Session hygiene** — turn off first-logon OOBE prompts for new profiles
   (privacy/Edge/OneDrive nags), set `DefaultUser` template if a "golden" profile
   is provided in the payload (`payload\profile-template.zip`, optional).
10. **Verify** — service running and its binary path inside `app\<version>\`,
    `app\current` matching it, `agent.json` and the private key readable by SYSTEM only,
    WoL flags read back, `student` exists and is not admin, Defender exclusion covering
    the parent directory. Print a green summary.
11. **Reboot** — prompt "Reboot now? [Y/n]" (auto-yes after 30 s). After reboot the
    PC auto-logs on as `student`, the service starts, connects to the console (if
    it is on) and appears as `PC-07` in the lab view.

## Uninstall / repair

`Setup.exe --uninstall` reverses steps 5, 6, 8 (keeps the `student` account unless
`--remove-student`), deletes `Program Files\LabControl` and `ProgramData\LabControl`.

`Setup.exe` with no args on an installed PC = repair (all `Check()`s pass → only
mismatches are fixed, service restarted). If the stick carries a newer version than
`app\current`, repair also installs it — the same side-by-side path a remote update
takes, so a PC that missed a rollout can be caught up with the stick that is already in
your hand. It is the fallback, not the normal route: the normal route is
`Job{self_update}` from the console.

`Setup.exe --rekey` replaces only the trust material: a new `ca.crt` is pinned, a new
keypair and enrollment code are used, everything else — the `student` account, the
installed software, the power settings, the PC number **and the `agent_id`** — is left
alone. (A full reinstall, by contrast, gets a new `agent_id`; the console recognises the
PC by its number and replaces the old record, `D-25`.) It takes about
30 seconds per PC and exists for exactly one situation: the lab key was lost entirely
and the lab has to be re-issued (`docs/ARCHITECTURE.md` §3.6, last row).

## Things the installer cannot do

- Enable Wake-on-LAN in BIOS/UEFI — one-time manual step per PC; the console shows
  "WoL failed" if a PC does not wake, so you know which ones to check.
- Install Windows or drivers.
- Configure the router.

## Testing the installer without a room full of real PCs

- `--dry-run` on any Windows box.
- Each `ISetupStep` has a unit test for `Check()` against a mocked registry/WMI
  surface (`tests/LabControl.Setup.Tests`).
- Full run on `PC-00` (the designated test PC) or a Windows VM snapshot that can be
  reverted between runs.
