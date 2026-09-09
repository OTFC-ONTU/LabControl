# Installer (`LabControl.Setup`)

Windows `Setup.exe` embeds the same application icon as the teacher console, from
`src/LabControl.Console/Assets/labcontrol.ico`. Its setup and reboot dialogs use that
embedded icon too. The installed standalone uninstaller retains it because it is a
copy of Setup; no loose icon file is required on the USB or student PC.


This document describes student-PC Setup and, in the final section, the separate
teacher-console packaging built in M5 (`D-54`, `D-59`). Teacher installation
does not run the student preparation pipeline below.

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

The M4 Setup executable is now being integrated (`D-52`); it is not yet Windows-verified. The shared account-state portion is
built (`D-45`): `InstallationState` persists `installation.json` with the mode,
installation id, pending creation flag and created SID. Its decisions are tested on the
Mac. The Windows preparation component is also built (`D-46`), but the executable
pipeline invokes account preparation and the newer ordered sign-in component.

Setup must hold an exclusive installation lock and establish the private data-directory
ACL before using this journal. Save creation intent before a create-new account call;
record only the SID returned by that successful call, before changing sign-in settings.
An existing account after an interrupted creation is an ownership conflict, never adopted
by name. The account component requires an explicit mode when legacy history is missing;
this does not authorize the executable to adopt an existing installation. M4 Setup supports
clean installation and repair of installer-owned installations only, and refuses unowned
legacy dev installs even with account creation off. Legacy migration is outside M4 by the
owner's decision of 2026-09-08. On supported installations, account-off preserves any earlier
ownership record but disables managed-profile operations and account removal.
An unreadable, malformed or future journal fails closed. The protected original-settings
journal core is built (`D-47`, below); the first two machine registry adapters are built
(`D-48`), as are the three AC power-plan timeout adapters (`D-49`), the active-hours
tuple (`D-50`) and the LSA sign-in secret adapter (`D-51`). Remaining settings
and the Winlogon backup/restore sequence are integrated in D-52, with Windows runtime verification still required.

`AccountSetupScope.Open()` is the pipeline's protected-state entry point: require
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
pipeline initializes it once for a positively identified fresh installation,
before changing any settings; never initialize it to replace missing repair/removal
history. Missing, invalid, future or mismatched history refuses settings operations.

`setup-settings.json` contains a schema version and ciphertext only. The encrypted
contents have their own schema version and installation id. Windows uses machine-scope
DPAPI with a separate purpose and this installation id as entropy; the existing private
ACL remains essential. The LSA adapter uses this store for original sign-in secrets;
remaining sign-in adapters must do the same rather than use plaintext JSON or logs. Temporary files also contain only
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
policies and AC power-plan timeouts below are built; firewall, remaining power/settings
adapters and executable integration are being completed in D-52. Those
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
pipeline must report conflicts and must not promise isolation from those writers.

The development pipeline invokes these policies. Mac tests cover the typed bridge, journal round trip,
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
native call, and Group Policy may override effective behavior. The development pipeline invokes these
methods; Windows verification is pending. Windows verification must cover both architectures, all three timeouts,
battery values unchanged, switch/delete scheme conflicts, missing settings, native access
and activation failures, apply/repair/remove from a VM snapshot, interrupted restoration,
and actual idle behavior after activation. Hibernation and NIC/WoL adapters
are implemented for integration in D-52; Windows verification remains pending.

### Windows Update active hours (D-50)

`AccountSetupScope.ApplyUpdateActiveHours` / `RestoreUpdateActiveHours` connect the
protected journal to a fixed Windows registry adapter. The desired tuple is
`SetActiveHours=1`, `ActiveHoursStart=7`, `ActiveHoursEnd=20`. All three values form one
ownership record: a later edit to any member preserves the whole tuple on repair/removal.
Absent values and all original DWORD bits round-trip; other native types are refused.

The adapter uses 64-bit HKLM under the existing Windows policy parent. It may create
only the fixed WindowsUpdate leaf; restoration removes originally absent values but
leaves the key, including any unrelated values/subkeys. A missing key reads as three
absent values. Reads check the tuple twice; each write checks the complete expected tuple
before and after mutation. Range values precede enabling; restoration of a disabled or
absent policy starts with the enable flag. A partial write is not transactional and is
preserved for review; a pending restore can finish automatically only when the complete
original tuple is already present or the complete applied tuple still matches.

This changes neither update availability nor notification/deadline policies. Registry
read-back does not guarantee effective restart behavior in the presence of other update
policies, Group Policy or MDM. No CLI invokes the adapter yet. Windows VM verification
must cover missing/existing keys, absent/DWORD/unsupported values, both architectures,
apply/repair/removal, another writer changing one member, partial-write failures, access
failures, unrelated key contents preserved, and effective active hours after reboot.

### LSA sign-in secret (D-51)

`AccountSetupScope.ApplyStudentSignInSecret` / `RestoreStudentSignInSecret` return null
when saved account creation is off, without consulting SAM, LSA or the settings journal.
When on, they require the local student's recorded SID to match. Missing/replaced accounts
and missing history are refused. The native boundary repeats account checks around writes.

`StudentSignInSecret` journals a versioned UTF-16LE snapshot of the fixed LSA
`DefaultPassword` secret, distinguishing absent and empty. Native read errors never mean
absence. The store rechecks the previous value on the same policy handle, writes through
`LsaStorePrivateData`, and verifies read-back. Later edits and ambiguous interrupted
applies are preserved. Owned byte buffers are cleared after use, including LSA memory;
original secrets reach disk only through the existing DPAPI-protected journal.

The standalone secret component is not called independently by the executable. The
integrated D-52 sign-in tuple coordinates existing autologon, identity, countdown and
LSA state with disable-first, enable-last ordering.
Changing only a password while another account's autologon remains enabled is not a safe
installation sequence. The adapter does not activate accounts or enable autologon itself.
Windows checks must cover absent/empty/nonempty originals, both architectures, access
denied, missing and replaced accounts, opt-out/repair/removal, external secret changes,
interrupted writes, ciphertext-only disk storage, and the eventual full sign-in round trip
from a VM snapshot. SAM and LSA checks are optimistic, not a cross-system transaction.

## Executable integration under verification (D-52)

The Windows Forms setup screen shows the PC number and checked-by-default account option.
`--number` supports unattended testing, with `--no-student` / `--create-student` as explicit
mode choices; saved mode and number survive repair. `--dry-run` prints the plan without
creating journals or changing native state. The manifest requests elevation. Setup and its
Windows Desktop runtime are self-contained. The Setup service/settings paths stop on a
conflict and preserve the journals for review; native runtime acceptance is still pending.

A machine-wide mutex supplements `AccountSetupScope` during executable operations and
cleanup. Fresh settings initialization is recorded before creating its protected journal,
so interruption does not authorize replacing missing legacy history. The Program Files
root carries a matching `installation-id` marker, and every used descendant must have
trusted ownership and no untrusted write access. A creation intent alone cannot authorize
adopting or deleting an existing directory. The service DisplayName atomically carries the installation
identity; its active path must match `app/current`, including after an update.

New adapters cover hostname, full-property firewall rules, Defender's exact parent-path
exclusion, hibernation native/registry state, supported standardized NIC settings,
privacy/Edge first-run policies, OneDrive sign-in notifications and original-admin visibility. NIC changes wait for reboot;
unknown hardware features are reported as unsupported, not guessed. In particular, generic
magic-packet-only enforcement uses the advertised writable Boolean in
`MSNdis_DeviceWakeOnMagicPacketOnly`, bound to the exact physical PNP instance. The Windows
schema is verified; actual hardware mutation remains unverified. Power-management enable
is applied before dependent wake controls, and restoration reverses dependency order.
Enrollment uses a uniquely selected physical Ethernet MAC (prefer the sole active wired
adapter); repair updates an older generic MAC with the owned agent stopped. Ambiguity is
reported instead of selecting a virtual or wireless wake target. Setup logs NIC name,
interface GUID, PNP identity, MAC and available driver provider/version. Hibernation metadata behavior must be
verified on the target Windows build. Third-party antivirus names produce a warning naming
the directory to exclude. Account-on privacy/Edge/OneDrive notification policies also affect other users and are
disclosed before installation; account-off skips them. OneDrive `DisableNewAccountDetection` suppresses existing-credential sign-in toast/activity
notifications without disabling manual sync; it does not suppress every OneDrive prompt. The original administrator SID is recorded for restoration by a different
operator, and hiding follows student activation. Before hiding, Setup journals and sets
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\CredUI\EnumerateAdministrators`
to DWORD `0` so UAC requests an explicit username and password. This affects all users;
account-off skips it. Uninstall restores the administrator tile before restoring the exact
previous nullable DWORD, preserving later edits and keeping credential entry available
if tile restoration conflicts. Setup does not enable/disable UAC. Native testing found
that hiding the sole administrator without this companion policy leaves only a No button;
the corrected UAC fields and separate manual Winlogon route still need native proof.

Setup stores a bounded, nonsecret `setup-readiness.json` in the private agent data
directory. It contains fixed antivirus/network advisory codes, not arbitrary messages or
credentials. The agent publishes a snapshot on reconnect and within ten seconds of a
repair changing it. The teacher console caches these advisories for offline tiles, shows
actionable issues in amber and renders plain-language details in the tooltip/event list.
Unverified physical wake remains an informational advisory. The installer's green result
certifies local files, identity and service checks; it does not assert that the teacher
console is reachable or that BIOS/UEFI and the physical LAN can wake the PC.

The actual sign-in tuple includes REG_SZ Winlogon enable/user/domain/plaintext-password,
LSA secret and absent/DWORD/REG_SZ `AutoLogonCount`. The latter is removed while managed
autologon is active and restored with its original type. Disable first, replace identity and
credentials, enable last; a changed tuple or uncertain partial write is a conflict. The
plaintext password value is removed, not used to store the configured student credential.

`--rekey` stages new trust in a separate DPAPI-purpose transaction, preserves agent id and
number, and can finish a staged operation without the USB. While staging exists the agent
refuses to open mixed trust. Removal uses the protected records and retains unresolved
history; accounts/profiles stay unless explicit owned-account deletion was requested and
confirmed. Installed apps retains a private retry worker under
`C:\ProgramData\LabControl Removal\<installationId>` until cleanup succeeds. The
ReadOnly attributes copied from installation media are cleared only on verified owned
copy/removal targets; source media and personal files are not modified. The
nonsecret completion receipt permits retry if only some final journals were deleted;
unexpected new contents or a different installation are refused. Worker cleanup that
requires reboot is reported explicitly. Active update probation blocks removal before
stopping the service, under a shared update/removal lock.

Password fallback is built for the two specified local-policy fields, with private
`secedit` exports and protected original values. It runs only after a classified policy
rejection while creating a new disabled account on a non-domain PC. Other creation errors
never relax policy. Native fallback/restore verification remains pending.

The optional profile ZIP accepts only Desktop/Documents documents and images (128 entries,
2 MiB/file, 16 MiB total, 200-character paths). It seeds future profiles using add-only
files on the Default profile's volume. Hives, executables, AppData and links are refused.
The protected file journal and a private root-bound plan permit USB-free restoration of
unchanged additions. Existing profiles are preserved, including when the template is first
supplied during repair. This is a document seed, not a full golden Windows profile image.

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
   so in its log. The generated English-primary USB instructions put console/key preparation before the first PC install. Issuing the certificate needs the lab key unlocked on the console
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
Windows acceptance found that automatic student logon also populates `AutoLogonSID`.
Fresh setup therefore journals its original absent-or-REG_SZ value separately, sets the
recorded managed SID before enabling automatic logon, and restores it after the main
Winlogon/LSA tuple. Unsupported registry types are refused. Older tuple-only ownership
history has no recoverable SID baseline: repair/removal preserves that SID and reports
the limitation instead of adopting its current value as the original. Account-off mode
does not access either setting.

Keep user accounts, profiles, personal files and installed applications by default.
`--remove-student` or an explicit unchecked removal option may delete only the account
and profile proven to have been created by this installation (track its SID), with a
clear data-deletion confirmation. Never delete a pre-existing account, even if named
`student`. With account creation off, removal leaves all account/sign-in settings alone.
An interrupted, explicitly confirmed student removal resumes from its recorded SID on the
next uninstall, including Installed apps. Pending account removal prevents deletion of the
ownership journal. Template restoration skips SAM and profile access when the protected
journal has no pending template work, including after the owned account was deleted or
an early installation failed before creating it. Pending template changes still require
the recorded SID. Interactive failures remain visible in a dialog; console diagnostics and
exit codes are retained. Uninstall does not reverse scripts, package installations or other remotely requested jobs.
The console may retain an offline PC entry, removable using its existing Remove action.

If Windows already has a different PC-name change waiting for restart, Setup reports
that restart is required before creating files or ownership journals. This also applies
when an earlier uninstall restored the old name and the PC has not yet rebooted.

`Setup.exe` with no args on an installed PC = repair (all `Check()`s pass → only
mismatches are fixed, service restarted). If the stick carries a newer version than
`app\current`, repair also installs it — the same side-by-side path a remote update
takes, so a PC that missed a rollout can be caught up with the stick that is already in
your hand. It is the fallback, not the normal route: the normal route is
`Job{self_update}` from the console.

`Setup.exe --rekey` replaces only the trust material: a new `ca.crt` is pinned, a new
keypair and enrollment code are used, and the replacement payload's console host/port
become the enrollment route (an absent host restores discovery). Everything else — the `student` account, the
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

## Teacher console installation (M5 portion 7, `D-59`)

This is how the console gets onto a teacher's machine: **one file per platform, no
administrator, no runtime download.** The console is an ordinary interactive desktop
application, and its packages behave like one. Nothing here installs a service, a daemon,
a scheduled task or an agent; nothing touches an account, automatic sign-in, the
hostname, power settings or student policy; nothing adds an antivirus exclusion.
Everything above in this document belongs to the student PCs and stays there.

The packages carry program files and nothing else — no lab, no key, no enrollment code,
no backup. Labs arrive later, inside the application, through *Add labs…* or by opening a
`.lclab` or `.lcbak` file.

### Building the packages

```bash
tools/package-all.sh          # all three; the macOS part is skipped off macOS
tools/package-windows.sh      # LabControl-Console-<version>-win-x64-Setup.exe
tools/package-mac.sh          # LabControl-Console-<version>-osx-arm64.dmg
tools/package-linux.sh        # labcontrol-console-<version>-linux-x64.tar.gz

tools/package-windows.sh Release win-arm64   # the same installer for a Windows-on-ARM VM
```

`tools/package-windows.sh` takes the configuration and a runtime identifier — `win-x64` by
default, which is what the teacher machines are, and `win-arm64` for the Windows-on-ARM VM
that is the only Windows an Apple Silicon Mac can test on. Nothing else differs between
the two; the file name says which one it is.

Everything lands under `artifacts/package/{windows,mac,linux}/`. Each script reads the
version from the console project, so the file name, the Installed-apps entry, the
`Info.plist` and the tarball all say the same number. `tools/publish-all.sh` is
unchanged: it still produces the plain executable directories used for development and
for the USB payload — these scripts produce the things a teacher installs.

The native `.pdb` symbol files SkiaSharp and HarfBuzzSharp ship (105 MB of them for
`win-x64` alone) are dropped from every package; LabControl's own symbols are embedded in
its executables.

### Windows: one self-contained Setup executable

`src/LabControl.ConsoleSetup/` is a single-file, self-contained executable — `win-x64` for
the teacher machines, `win-arm64` for a Windows-on-ARM VM — that carries the published
console inside it as an embedded zip, so a Windows teacher machine needs nothing else — no
.NET install, no second download, no packaging toolchain (`D-59` item 1; WiX, Inno Setup
and MSIX were rejected there).

Its manifest is **`asInvoker`, never `requireAdministrator`**. The console installs into
the signed-in user's own profile, so installing it — and, far more often, updating it —
is not a UAC prompt. `--firewall` is the one step that elevates, and it does so by
relaunching itself once (see *LAN access* below).

**Nothing in `app.manifest` may contain two hyphens in a row, not even inside a comment.**
XML forbids `--` there, and the consequence is not a build error: the compiler embeds the
manifest happily, and Windows then refuses to build the activation context, so the
installer will not start at all — *"The application has failed to start because its
side-by-side configuration is incorrect"*, with an invalid-manifest-XML entry in the event
log. This is why the file names the `--firewall` switch in prose rather than spelling it,
and why a build of this executable is not finished until it has been started once on a
Windows machine (found on the first Windows run, 2026-09-09; `D-59` item 1).

The payload is the published console, zipped by `tools/package-windows.sh` and embedded
as a resource. The project deliberately compiles without it, so a plain `dotnet build`
still builds the executable on any machine; an executable built that way **refuses to
install and names the script that produces the real one** rather than writing an empty
program directory. `--dry-run` still works on such a build and says the plan counts no
files.

**Every step is data.** `ConsoleInstallPlan` builds the whole run before anything is
touched, `--dry-run` prints exactly those steps in exactly that order and changes
nothing, and every step is idempotent — a second run produces the same machine and a log
that says *already* instead of *done*.

| Step | What one install does |
|---|---|
| `check.lock` | Refuses while a console is using the program files. Both questions are asked, because either alone lies: a `LabControl.Console` process may be running, and `console.lock` (`D-55`) in the data directory may be held — a console started with `--data` on another directory holds no lock this installer can see |
| `files.replace` | Writes the payload into `%LOCALAPPDATA%\Programs\LabControl\Console\`, skipping any file already there byte for byte and retrying a write an antivirus still holds (`D-33` item 9). It copies itself in beside the console as `LabControl.ConsoleSetup.exe` — that copy is the uninstaller, with its `Zone.Identifier` cleared so Installed apps does not raise SmartScreen — and records every file it owns in `installed-files.txt`. Only a manifest this installation really wrote may prune what an older payload left behind |
| `shortcut.start-menu` | The Start-menu `.lnk`, through `IShellLinkW` (CsWin32) |
| `shortcut.desktop` | The same shortcut on the desktop, only with `--desktop-shortcut`: a desktop icon is a preference, not part of installing |
| `registry.uninstall` | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\LabControl Console` — display name, version, icon, publisher, install location, uninstall command, `NoModify` and `NoRepair` |
| `progid.lclab`, `progid.lcbak` | The two ProgIds `LabControl.LabFile` and `LabControl.Backup` under `HKCU\Software\Classes`, each with the console's icon and an open command of the form `"<console>" "%1"` — always quoted, always one `%1`, so a path with spaces arrives as a single argument |
| `assoc.lclab`, `assoc.lcbak` | Adds the ProgId to the extension's `OpenWithProgids` **always**, and becomes the default **only** where Windows holds no `UserChoice` and the extension has no default yet. A teacher's own choice is never overwritten (`D-54` item 4) |
| `shell.notify` | `SHChangeNotify(SHCNE_ASSOCCHANGED)`, so Explorer notices the new file types |
| `firewall.hint` | Nothing. Installing changes no firewall rule; the console asks for LAN access at the point of use |

Everything is appended to `%LOCALAPPDATA%\LabControl\console-setup.log`: the plan header,
one line per step with its outcome, and the failure if there is one. The installer never
opens the console's data directory beyond testing the lock file, so no lab, key or
passphrase can reach that log.

Note the two different directories: the program lives under `%LOCALAPPDATA%`, the labs
under `%APPDATA%\LabControl` (ARCHITECTURE §4). Uninstall touches only the first.

#### Uninstall, and removing the data

`LabControl.ConsoleSetup.exe --uninstall` — which is what Installed apps runs — asks the
same lock question and then removes exactly what this installation owns, roughly in
reverse: the associations (a `UserChoice` is left alone), the two ProgIds, the
Installed-apps entry, both shortcuts, this installer's firewall rules (only when that run
happens to be elevated — see below), the shell notification, and finally the files listed
in `installed-files.txt`. **The saved labs, lab keys, scripts and logs stay**, and the
last step says where they are.

`--remove-data` is a separate switch, never part of uninstalling. It prints what will be
lost — every saved lab, lab key, script and log — and requires the word `REMOVE` typed at
the keyboard; with redirected input it refuses rather than assuming a yes. It deletes the
console's data directory and, at the end, the installer log itself, because that log
would otherwise be the last file on the computer still naming the labs that were on it.
It never touches an exported `.lcbak` backup and never touches a student installation.

The removal rules the security review added, all of them because the pieces involved sit
where the signed-in user can edit them:

- **`installed-files.txt` is untrusted input.** A manifest line that is rooted, names a
  drive, climbs out with `..` or resolves anywhere but inside the install directory is
  refused and reported in the log — never deleted. The same guard resolves every entry of
  the embedded payload, so a hostile zip cannot write outside either. When there is no
  usable manifest at all, uninstall falls back to the two known program files rather than
  reporting a clean removal over a directory it never touched.
- **`--finish-removal` accepts only this installer's own install directory.** An
  uninstaller cannot delete the file it is running from, so it copies itself into the
  temp directory and hands that copy the directory to finish and its own process id. The
  copy compares the directory against the layout's own and refuses anything else, waits
  for the parent to exit (up to five minutes), retries the deletes for two minutes, then
  deletes itself. Copies an interrupted uninstall left behind are swept by any later run
  of the installer — each one is a working installer (148 MB on the ARM64 drill) and must
  not sit in the temp directory waiting to be double-clicked. **The self-deletion is handed
  to `cmd.exe` as one argument string, never as an argument list**: an argument list quotes
  each argument the way a C runtime parses `argv`, so the inner quotes come out as `\"`,
  which `cmd.exe` does not understand — it answers *"The filename, directory name, or
  volume label syntax is incorrect"*, deletes nothing, and leaves the copy behind. That is
  exactly what the first Windows run found.
- **A registry key that points at another installation is preserved and reported.** A
  second copy of the console, or one installed elsewhere, owns its own Installed-apps
  entry and ProgIds; taking them would be wrong, and throwing mid-uninstall would leave a
  machine with no entry to retry from. The step says *left in place* and the run
  continues.
- **Only a step that provably wrote nothing may say nothing changed.** `check.lock`,
  `firewall.hint` and `data.keep` merely look; any other failing step has predecessors
  that already ran and may itself be half done, so the log says so and tells the teacher
  to fix the problem and run the installer again — which is safe, because every step is
  idempotent.
- **Uninstall never asks for an administrator**, because installing never did. Removing a
  firewall rule does need one, so unless that particular run happens to be elevated the
  two rules are left exactly as they are and the exact `netsh … delete rule` lines are
  printed instead. An open port with nothing listening on it is not a hazard; a UAC
  prompt in the middle of an uninstall started from Installed apps, or deleting a
  namesake rule somebody else created, would be worse. Even when the run *is* elevated, a
  rule of that name outside this installer's own group is preserved for review, because
  Windows deletes rules by name.

### LAN access: the console's banner

The console hosts the gRPC server, so the student PCs connect **to** it and Windows
Firewall is in the way by default. When a lab activates, the console reads the firewall
**read-only** — no elevation — and asks whether inbound TCP `47800` (`ConsolePort`) and
UDP `47801` (`BeaconPort`) are open on the **Private and Domain** profiles.

A port counts as reachable only when some enabled inbound *allow* rule really opens it
**for this console**:

- unrestricted, or scoped to exactly this console's executable — a rule for a
  conferencing tool that happens to cover the port opens nothing for the classroom;
- no service name at all — the console is not a Windows service;
- unrestricted or `LocalSubnet` local *and* remote addresses — a rule cut down to one
  host is not evidence that the room can connect;
- all interface types, because the teacher machine is on Wi-Fi one day and on the wire
  the next (`D-21`);

**and** no matching block rule exists. A block rule is judged generously — any of the
lab's profiles, any address range, any interface list is enough to stop the classroom, so
it is never explained away. Windows applies the most specific block first, and saying
*allowed* while the lab cannot connect is the one answer this check must never give.
Somebody else's port-scoped rule, on the other hand, is a perfectly good answer: the
console never adds a second rule for a port that is already open.

When a port is blocked the console raises a **non-blocking** banner — it keeps running
and serving either way. *Allow…* runs `LabControl.ConsoleSetup.exe --firewall` elevated
and then **reads the rules again**, because the helper's exit code is not evidence: a
window closed after both rules were added exits non-zero and has still opened the ports,
and a helper that exits 0 without adding them has not. If the teacher declines the
Windows prompt, if no installer sits beside the console (a development run, a copied
publish directory), or if the rules are still missing afterwards, the banner turns into
the exact commands with a *Check again* action:

```
netsh advfirewall firewall add rule name="LabControl Console (control)" dir=in action=allow protocol=TCP localport=47800 profile=private,domain enable=yes
netsh advfirewall firewall add rule name="LabControl Console (discovery)" dir=in action=allow protocol=UDP localport=47801 profile=private,domain enable=yes
```

**There is no `group=` on those lines, on purpose.** `netsh advfirewall firewall add rule`
has no such argument and refuses the whole command when it is given one — *"'group' is not
a valid argument for this command"* — so a line carrying it creates nothing at all, which
is what the first Windows run found. Without it both lines answer *Ok.* The price is that a
rule made by hand belongs to no group, so an uninstall cannot prove it is the installer's
own and leaves it for review with the `netsh … delete rule` lines printed. That is the safe
half of the trade: Windows deletes rules by name, and a namesake rule somebody else created
must never be taken.

Windows numbers the profiles **Domain 1, Private 2, Public 4** (`NET_FW_PROFILE_TYPE2`),
so *Private and Domain* is the value 3. One constant,
`ConsoleFirewallRuleSpec.Profiles`, both creates the rules and decides whether an existing
rule covers a port, so a wrong value does two things at once: it opens the teacher's
machine on the very profile this document says is never requested, and it rejects a genuine
private-and-domain rule as missing. It was 6 — Private and Public — until the first Windows
run; a created rule now reads back as *Domain, Private* and the banner then reports the port
as allowed.

The Public profile is never requested, nothing is ever disabled or excluded, and macOS
and Linux report *not applicable* and show no banner at all.

### macOS: an app bundle in a DMG

`tools/package-mac.sh` assembles `LabControl.app` — `Contents/Info.plist`,
`Contents/MacOS/` (the self-contained publish), `Contents/Resources/labcontrol.icns` and
`PkgInfo` — with `CFBundleIdentifier org.ontfk.labcontrol.console`, the console's version
in both version keys, `LSMinimumSystemVersion 12.0`, `NSHighResolutionCapable`, an
education category and `NSLocalNetworkUsageDescription` explaining why the app reaches
the classroom. Both file types are declared as `CFBundleDocumentTypes` with
`LSHandlerRank Owner` over two exported UTIs, `org.ontfk.labcontrol.lab` (`lclab`) and
`org.ontfk.labcontrol.backup` (`lcbak`) — LabControl invented both extensions, so it
owns them. The plist is checked with `plutil -lint`.

The bundle is signed **ad hoc** (`codesign --force --deep --sign -`) and verified
(`codesign --verify --deep --strict`), then wrapped by `hdiutil` in a compressed DMG
containing the app and an `/Applications` symlink.

An ad-hoc signature makes the bundle load on Apple Silicon; it does **not** satisfy
Gatekeeper, and there is no Developer ID certificate (`D-15`). `spctl --assess`
therefore fails on purpose and **the script prints that failure instead of hiding it**.
The teacher opens the app the first time with right-click → *Open*, or through *System
Settings → Privacy & Security → Open Anyway*; after that macOS remembers.

There is no macOS uninstaller: dragging `LabControl.app` to the Bin removes the
application. Local data removal is an explicit action inside the console — *My labs →
Remove from this device*, per lab, with a second confirmation before a lab key is
deleted.

### Linux: a per-user tarball

`tools/package-linux.sh` builds a tarball with `install.sh` and `uninstall.sh`. No root,
no sudo, no package manager, no repository. `install.sh` installs into
`~/.local/opt/labcontrol/console/`, writes a launcher `~/.local/bin/labcontrol-console`,
and registers under `${XDG_DATA_HOME:-~/.local/share}`: the `.desktop` entry
(`Exec=… %F`), the MIME definitions of `application/x-labcontrol-lab` and
`application/x-labcontrol-backup` with their `*.lclab` and `*.lcbak` globs, and the
icons. It then runs `update-mime-database`, `update-desktop-database` and
`gtk-update-icon-cache` where they exist. Both scripts refuse while the installed console
is running.

Two details that are easy to get wrong and are therefore fixed in the script:

- **`Exec` and `TryExec` are rewritten to the absolute launcher path** at install time.
  On a first install `~/.local/bin` is usually not on the PATH of the session that is
  running — the shell adds it at the *next* login — and an application menu hides any
  entry whose `TryExec` it cannot resolve. The teacher would install the console and find
  nothing.
- **`xdg-mime default` runs only where the desktop has no handler for that type.** An
  existing choice is reported and left alone (`D-54` item 4).

`uninstall.sh` removes exactly those files and keeps `~/.labcontrol` — the labs — saying
so, and saying why a lab key that exists nowhere else cannot be recovered.

The console is self-contained .NET, but a .NET GUI still needs system libraries. The
tarball's `README.txt` names them: `libicu` (`libicu70` on 22.04, `libicu74` on 24.04),
`libfontconfig1`, `libx11-6`, `libice6`, `libsm6`, `libgl1`, and optionally
`libsecret-1-0` — without the keyring the console falls back to its own file-backed
secret protector and says so per lab. On a machine with no internet those `.deb` files
are copied across from a connected one; nothing else is needed. The documented, tested
matrix is **Ubuntu 22.04 LTS and 24.04 LTS**, x86-64; other distributions are expected
to work and are not verified. Desktop Ubuntu has `ufw` disabled by default; the two `ufw
allow` lines for 47800/47801 are in the same README for machines where it is on.

### What is verified, and what is not

Done on the owner's Mac (M5 portion 7, 2026-09-09): the full solution build; 888 tests at
the portion's own head and **936 after merging with portions 4 and 6** (726 Shared and
210 Console, of which 13 macOS bundle tests skip unless `tools/package-mac.sh` has
actually been run); all three packages produced, with the Windows installer assembly
really carrying its ~59 MB payload; the DMG built, its `Info.plist` keys and ad-hoc
signature checked and the expected Gatekeeper refusal observed; the app bundle launched
once against a copy of a data directory; and the Linux tarball genuinely installed and
uninstalled into a throwaway `HOME`, with the generated `.desktop` entry inspected —
absolute `Exec`/`TryExec`, an executable launcher, and an uninstall that leaves nothing
behind.

**The first Windows run (2026-09-09)** took `LabControl-Console-0.1.4-win-arm64-Setup.exe`
onto the isolated UTM clone *M4 isolated native tests 2026-09-08* — Windows 11 Pro
10.0.26200 ARM64, guest `PC-27`, one host-only NIC — with the binary hash-compared on host
and guest before every run, and the clone stopped and left in its pre-drill state
afterwards. It found the **four defects above**, all now fixed: the manifest's double
hyphen, which stopped the installer from starting at all; `Profiles = 6`, which opened the
Public profile and rejected a real private-and-domain rule; the `group=` argument, which
made the printed `netsh` line create nothing; and the argument-list quoting, which left a
148 MB copy in the temp directory.

One caveat colours every step: the clone has **no interactive session** and no password may
be entered there, so every unelevated run was made as `LOCAL SERVICE` — a non-administrator
with a real profile — through a scheduled task, and the single elevated step ran as
`SYSTEM`.

Verified on Windows with the rebuilt installer: that it starts at all; the whole ordered
plan, and a `--dry-run` that changes nothing; a clean per-user install by a
non-administrator into the per-user programs directory; the Start-menu shortcut, and that
it launches the console; the Installed-apps entry with its name, version, publisher and
uninstall command; both ProgIds, both associations and their open commands; a
byte-identical machine after a repeat run; the refusal to install while a console holds its
lock; document forwarding through the registered command into a running console's import
flow, for both file types, including a path with spaces and Ukrainian characters; the real
firewall rules and the profiles Windows reports for them, the banner moving from *missing*
to *allowed*, a rule belonging to another program correctly not counting as coverage, and
an uninstall that removed only its own rule while leaving a foreign one for review; the
unelevated uninstall's firewall policy; the temporary-copy hand-over including its
self-deletion; all three answers to `--remove-data`, including its refusal with redirected
input and its insistence on the exact word; and that a lab key sealed by DPAPI still opens
after the application has been replaced.

Still not verified on Windows:

- **Explorer's own double-click.** It needs a desktop session. The registration, the
  forwarding and the import are proven; the shell's resolution step from a double-click is
  not.
- **A real elevation prompt with a teacher answering yes or no.** With no interactive
  session Windows cannot show one, so the `--firewall` relaunch was exercised only as
  `SYSTEM`.
- **Anything on `win-x64`.** Only the ARM64 package was built and run; the teacher machines
  are x64.
- **The installer under an ordinary interactive user profile** rather than a service
  account.
- **The unsigned-download warning** (SmartScreen / *More info → Run anyway*).

Not verified anywhere: **a real GNOME or KDE application menu** — the `.desktop` entry and
the MIME registration were inspected as files, not seen in a desktop.

Rough edges worth a later pass, none of them wrong behaviour: `--dry-run` prints its plan
header twice; a repeat install reports its steps as *written* where the code's own comments
promise *already*, so the machine is idempotent but the wording is not; and uninstall leaves
the now-empty parent programs directory behind.

Forwarding a document into a console that was already running is what portion 6's Windows
**named pipe** does, so that path is exercised too; the Linux `SO_PEERCRED` path is not, and
stays in the same matrix. What remains belongs to portion 7's manual matrix in ROADMAP M5,
and none of it may be reported as passing until it is run.

Packaging references: [Avalonia macOS deployment](https://docs.avaloniaui.net/docs/deployment/macos),
[Windows Firewall rules](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/rules),
[.NET Linux prerequisites](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual#dependencies).
