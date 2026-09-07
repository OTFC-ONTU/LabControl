# Installer (`LabControl.Setup`)

Goal: **plug in the USB stick, run one file as the local admin, answer one question
(PC number), walk to the next PC — with the default options.** Everything else is automatic and idempotent —
running it again on an already-configured PC repairs the installation instead of
breaking it.

## Optional student account (planned for M4 portion 3, D-40)

The PC-number screen includes **Create student account and enable automatic sign-in**,
checked by default on a fresh installation. The normal classroom path still needs only
one answer. Untick it to test on a home PC using an existing Windows account.

- With it off, skip steps 8 and 9 entirely: do not create or modify any account, change
  passwords or password policy, hide accounts, configure auto-logon, or alter profile
  defaults. The service/helper use the active interactive session for capture, input and
  user-session scripts; no account is silently selected by name or created later.
- Save the choice locally with a schema version. Repair, upgrade and rekey preserve it;
  repair must not reapply the fresh-install default. Changing the choice requires an
  explicit setup choice, and turning it off does not delete a previously created user.
- An existing account named `student` is not evidence of installer ownership. Refuse to
  overwrite/adopt it and explain how to continue with creation off. Never reset its
  password or permissions to make installation pass.
- Account-specific features (planned handout delivery to `student` and profile reset)
  must report that no managed student account is configured; never fall back to wiping
  or writing into the home user's profile. Explicit scripts still run with their chosen
  identity and can change that user's files; uninstall does not undo remote jobs.
- The checkbox controls account/profile setup, not all machine configuration. Before
  applying changes, show the planned hostname, power/WoL, firewall and antivirus changes,
  including when account creation is off. Track prior values for safe removal below.
  A home-PC install is still an elevated installation of the real agent.

The current Setup executable remains a skeleton. The shared account-state portion is
built (`D-45`): `InstallationState` persists `installation.json` with the mode,
installation id, pending creation flag and created SID. Its decisions are tested on the
Mac. The Windows preparation component is also built (`D-46`), but the executable
pipeline does not invoke it yet and it does not configure sign-in.

Setup must hold an exclusive installation lock and establish the private data-directory
ACL before using this journal. Save creation intent before a create-new account call;
record only the SID returned by that successful call, before changing sign-in settings.
An existing account after an interrupted creation is an ownership conflict, never adopted
by name. A missing legacy journal requires an explicit account-mode choice. Off preserves
any earlier ownership record but disables managed-profile operations and account removal.
An unreadable, malformed or future journal fails closed. The protected original-settings
journal core is built (`D-47`, below); the first two machine registry adapters are built
(`D-48`), as are the three AC power-plan timeout adapters (`D-49`). Remaining settings
and sign-in backup/restore adapters are pending.

`AccountSetupScope.Open()` is the future pipeline's account-step entry point: require
an elevated administrator, create fresh data storage with SYSTEM/Administrators-only
ACLs, refuse reparse points or existing untrusted storage, and hold `setup.lock` with
exclusive sharing until the scope closes. It never makes an unprotected old ownership
journal trusted merely by fixing its permissions. Existing history must already have
trusted ownership and private ACLs, including private inheritance for new files.

`Prepare` preserves the mode and skips all SAM calls when it is off. With it on,
`StudentAccountProvisioning` saves intent and calls `WindowsStudentAccountSystem`, using
local `NetUserAdd` and `NetUserGetInfo(23)`. Creation leaves the account **disabled**.
Because NetUserAdd returns status, not SID, a fresh per-call comment marker binds the
immediate SID read-back to this successful creation; no marker is used for recovery or
adoption after an error. A failed SID write leaves an unowned disabled account for
explicit review, never automatic deletion. Password-policy failure is reported without
relaxing policy. Activation, Users membership/verification and the final account
description belong to the subsequent account/sign-in steps, after the restoration
journal exists. This component is compiled for both Windows architectures; its native
behavior and ACL/lock enforcement still require VM verification.


### Protected settings journal core (D-47)

`SetupSettingsJournal` is built and tested on the Mac. `AccountSetupScope` provides
initialization, apply and restore methods while holding the installation lock. The
future pipeline must initialize it once for a positively identified fresh installation,
before changing any settings; never initialize it to replace missing repair/removal
history. Missing, invalid, future or mismatched history refuses settings operations.

`setup-settings.json` contains a schema version and ciphertext only. The encrypted
contents have their own schema version and installation id. Windows uses machine-scope
DPAPI with a separate purpose and this installation id as entropy; the existing private
ACL remains essential. Original sign-in secrets, when the native adapters are built,
will use this store rather than plaintext JSON or logs. Temporary files also contain only
ciphertext; plaintext serialization buffers are cleared after protection/decryption.

A setting adapter is defined by code, with a stable id and canonical typed bytes (null
means absent). The journal never supplies registry paths or executable commands. It saves
and flushes original/desired values before a write, checks again before writing, verifies
read-back, and saves completion. An already-correct setting gains no ownership record.
Repair preserves the original baseline and reports later user changes as conflicts.

Removal restores only a confirmed change whose current value still matches Setup's
applied value. It journals restoration intent first and can resume interrupted restoration.
An interrupted apply with no completion record is ambiguous if the value changed: neither
repair nor removal adopts it merely because it matches the desired value. Preserve it and
report a conflict. Retry is possible while the value still matches the saved original.
Changing a recorded desired value or reapplying after restoration also requires a future
explicit migration/reinstall flow, not silently replacing the original baseline.

This is the journal component, not a working installer/uninstaller. The two registry
policies and AC power-plan timeouts below are built; LSA, firewall, remaining power/settings
adapters and pipeline integration remain pending. Those
adapters must preserve native types, recheck identity/concurrent changes, omit values from
errors and gate account/sign-in operations on the saved account mode. DPAPI and native
behavior still require VM verification; no real Windows settings were changed here.

### First machine registry adapters (D-48)

`AccountSetupScope.ApplyMachineRegistryPolicy` and `RestoreMachineRegistryPolicy` connect
the protected journal to `WindowsMachineRegistryStore`. The fixed policies are Fast
Startup (`HiberbootEnabled`, desired DWORD 0) and remote secure attention
(`SoftwareSASGeneration`, desired DWORD 1, preserving existing DWORD 3). These are
machine settings and apply regardless of account mode; they never access sign-in keys.

Only absent values and DWORDs are supported. Other native types are refused unchanged,
without converting or claiming ownership. Snapshots preserve all 32 bits and distinguish
absence from zero. Keys must already exist in 64-bit HKLM; the adapter neither creates
nor removes parent keys. It rereads on the writable key handle immediately before a
mutation, flushes and verifies read-back. This guard detects observed concurrent changes,
but is not an atomic transaction against another administrator or Group Policy; the
future pipeline must report conflicts and must not promise isolation from those writers.

No CLI invokes these methods yet. Mac tests cover the typed bridge, journal round trip,
existing acceptable policies and conflict handling. Windows verification remains:
both registry views, absent/DWORD/unsupported-type originals, permission failures,
apply/repair/restore from a snapshot and interference from another writer. This does not
disable hibernation or finish the other power/WoL steps.


### AC power-plan timeouts (D-49)

`AccountSetupScope.ApplyPowerPlanPolicy` / `RestorePowerPlanPolicy` now journal the
active scheme's AC sleep, display and disk idle timeouts. Desired values are 0 (never),
1200 seconds and 0 respectively. The code-defined policy selects the native setting;
constants and GUIDs live in `Defaults`. Battery settings and other schemes are untouched.
Snapshots include the active scheme GUID and all 32 bits of the timeout. Missing settings,
read failures and a changed scheme are refused; no scheme is created or selected from
journal data. A scheme change at repair/removal preserves both schemes for review.

`WindowsPowerPlanSystem` uses the Windows power APIs through CsWin32. Reads check the
scheme before and after reading the value. Writes recheck the expected scheme/value,
use an explicit scheme GUID and verify read-back. `ISetupSettingActivation` lets the
journal call guarded `PowerSetActiveScheme` before recording completion. Persisted index
read-back alone cannot prove that Windows has activated the setting. Confirmed repair
refreshes the setting without rewriting it; interrupted restoration refreshes the original
even if its index was already written. A failed apply after index mutation remains
ambiguous under D-47 and is not silently adopted. Repeated restoration checks for later
edits before activating. Already-correct, unowned settings are not activated.

These checks are optimistic: another administrator can still race the last check and
native call, and Group Policy may override effective behavior. No CLI invokes these
methods yet. Windows verification must cover both architectures, all three timeouts,
battery values unchanged, switch/delete scheme conflicts, missing settings, native access
and activation failures, apply/repair/remove from a VM snapshot, interrupted restoration,
and actual idle behavior after activation. Hibernation, NIC/WoL and update-policy adapters
remain pending alongside the Setup executable pipeline.

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
   Copy `Setup.exe` alongside for repair and install `Uninstall.exe` plus a Windows
   Installed apps removal entry (D-40). Set ACLs (Users: read/execute). Create
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
   later and does, on its own. This step is the shared routine `AgentProvisioning`, also
   reachable as `agent.exe --install --payload <dir> --number N`, which is how
   `scripts/dev-install.ps1` provisions a PC before Setup.exe exists (`D-29`; with
   `-Student` the script also performs step 8 below).
5. **Service** — `CreateService("LabControl", LocalSystem, auto-start, delayed=false)`
   with the binary path pointing at `app\<version>\agent.exe`; recovery: restart on
   failure (3×, 10 s) and then `agent.exe --rollback` on the fourth, which is the
   outside-the-agent half of the update rollback (`D-19`, `docs/ARCHITECTURE.md` §7.2);
   description string, then start it.
6. **Firewall** — outbound is allowed by default, but the console's discovery beacon
   is an **inbound** UDP datagram on port 47801, so add an inbound allow rule for
   **UDP 47801 by port** and an ICMP echo allow rule (so the console can ping). Never a
   per-program rule: the agent's path changes with every self-update (`app\<version>\`,
   `D-19`) and Windows Firewall would then drop the beacon for the new version — the PC
   keeps running and never finds the console (found on `PC-00`, 2026-09-07, `D-33`
   item 8). Rules are tagged with a LabControl group name so
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
   - Registry `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\SoftwareSASGeneration = 1`
     (DWORD), so the service may raise Ctrl+Alt+Del with `SendSAS` for remote control
     (`D-36`); a value of 3 is left alone. The agent sets it on first use too, so PCs
     installed before this step exist need no reinstall.
   - NIC advanced properties via WMI/`Set-NetAdapterAdvancedProperty` equivalents:
     *Wake on Magic Packet = on*, *Energy-Efficient Ethernet = off*,
     *Allow this device to wake the computer* = on, *Only allow a magic packet* = on;
     "Allow the computer to turn off this device to save power" = **on** (required for
     WoL from S5 on many Realtek/Intel NICs).
   - Power plan: never sleep on AC, display off after 20 min, "never" for disk.
   - Disable "Windows Update → automatic restart during active hours" nagging
     (active hours 07–20); do **not** disable updates.
8. **Student account** — only when account creation is enabled (D-40).
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
9. **Session hygiene** — only when account creation is enabled (D-40); turn off first-logon OOBE prompts for new profiles
   (privacy/Edge/OneDrive nags), set `DefaultUser` template if a "golden" profile
   is provided in the payload (`payload\profile-template.zip`, optional).
10. **Verify** — service running and its binary path inside `app\<version>\`,
    `app\current` matching it, `agent.json` and the private key readable by SYSTEM only,
    WoL flags read back, `student` exists and is not admin when managed (otherwise verify
    account/profile steps were skipped), Defender exclusion covering
    the parent directory. Print a green summary.
11. **Reboot** — prompt "Reboot now? [Y/n]" (auto-yes after 30 s). After reboot the
    PC auto-logs on as `student` only when account creation is enabled; otherwise its
    existing sign-in behavior is preserved. The service starts, connects to the console
    (if it is on) and appears as `PC-07` in the lab view.

## Uninstall / repair

**Planned standalone removal (D-40):** `Uninstall.exe` is installed alongside Setup and
registered in Windows Installed apps. It runs elevated, works offline without the USB or
teacher console, and shares the removal implementation with `Setup.exe --uninstall`.
It must remain usable if the agent cannot start or installation stopped halfway.

Removal stops the service and helper, removes LabControl service/recovery tasks, owned
firewall rules, its Installed apps entry and installed binaries/data (including all
version directories). Arrange final cleanup outside the running executable; explicitly
report any cleanup requiring a reboot rather than claiming removal is complete.

Maintain a schema-versioned installation journal with original values and ownership of
changes (hostname, power/NIC settings, update policy, SoftwareSASGeneration, Defender
exclusion and any account/sign-in/profile settings). Restore a value only if it still
matches what Setup applied; preserve later user changes and report conflicts. Never
remove a pre-existing firewall rule or exclusion. Journal sensitive sign-in state only
in its designed protected store, never in plaintext or setup logs. If ownership/history
is missing, leave accounts and uncertain settings intact and report what remains.

Keep user accounts, profiles, personal files and installed applications by default.
`--remove-student` or an explicit unchecked removal option may delete only the account
and profile proven to have been created by this installation (track its SID), with a
clear data-deletion confirmation. Never delete a pre-existing account, even if named
`student`. With account creation off, removal leaves all account/sign-in settings alone.
Uninstall does not reverse scripts, package installations or other remotely requested jobs.
The console may retain an offline PC entry, removable using its existing Remove action.

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
- The D-40 home-PC round trip in ROADMAP M4: creation off, repair/update preserving it,
  standalone removal, pre-existing account collision and interrupted-install cleanup.
- Each `ISetupStep` has a unit test for `Check()` against a mocked registry/WMI
  surface (`tests/LabControl.Setup.Tests`).
- Full run on `PC-00` (the designated test PC) or a Windows VM snapshot that can be
  reverted between runs.
