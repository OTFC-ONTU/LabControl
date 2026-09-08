# Installer (`LabControl.Setup`)

Windows `Setup.exe` embeds the same application icon as the teacher console, from
`src/LabControl.Console/Assets/labcontrol.ico`. Its setup and reboot dialogs use that
embedded icon too. The installed standalone uninstaller retains it because it is a
copy of Setup; no loose icon file is required on the USB or student PC.


This document describes student-PC Setup and, in the final section, the separate
lightweight teacher-console packaging planned for M5 (`D-54`). Teacher installation
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

## Teacher-console installation (planned M5, D-54)

Goal: install the interactive console, add several teacher lab files or administrator
`.lcbak` backups, and select a room. The same console serves both roles; importing a
backup adds a normal selectable lab with administrator authority. There is no separate
server product or system service to provision. The server starts with the selected lab
inside the console and stops when disconnected or closed.

Distribute the .NET runtime and application native assets for each currently supported teacher target
(`win-x64`, `osx-arm64`, `linux-x64`) so installation works without internet or a runtime
download. Linux still needs compatible system libraries; record tested distributions,
versions and prerequisites, with local prerequisite packages where needed for offline
installation. Packaging is planned, not implemented; choose/document the build tooling in
M5 and keep the ordinary solution build/test workflow intact.

| Platform | Installation and desktop integration |
|---|---|
| Windows | Simple installer copying the console to its application directory; Start-menu entry, optional desktop shortcut, Installed apps/uninstall registration and lab-file/`.lcbak` opening |
| macOS | `.app` bundle distributed in a `.dmg`, copied to Applications (user Applications where appropriate); bundle registration for both file types |
| Linux | Self-contained desktop package/install flow with a launcher, file-type opening and documented removal; no requirement to publish every distribution's native package format |

Prefer per-user installation where supported. The base installer lays down app files
and app-owned desktop registrations. It does not install Agent/Session, Windows services,
daemons, scheduled tasks or automatic control at login, and does not change accounts,
autologon, hostname, power/NIC settings or student policies. The student antivirus
exclusion policy does not apply to the teacher installer. OS firewall/network or privacy
consent needed for LAN operation and later capture is handled through the supported
platform flow with a clear explanation; do not disable protections to avoid prompts.
The console hosts Kestrel and receives incoming connections: Windows needs inbound TCP
`Defaults.ConsolePort` (47800) and UDP `Defaults.BeaconPort` (47801) for discovery of
other consoles, scoped to the intended LAN/network profile. A network-setup action may
need elevation even when file installation does not. Keep the executable path stable
across updates, preserve existing firewall rules, and remove only rules this installation
owns. A denied prompt/policy needs an actionable diagnostic, not a false success.

The current publishing script creates executable directories; it does not make desktop
packages. On macOS create a real bundle with stable identifier, `Info.plist`, icons,
version and file-type declarations. Under the current no-paid-signing policy (`D-15`),
document and test the actual unsigned/ad-hoc distribution path and supported user
approval flow. SmartScreen/Gatekeeper may still prompt or block; a `.dmg` alone does
not confer trust. Do not disable OS security or quietly make paid signing/notarization a
prerequisite; a later change to that distribution policy needs a recorded decision.

The installer contains no lab keys, backups or teacher credentials. The application
handles file import/unlock into its protected stores; installers never receive secrets
through command arguments or logs. File associations for both formats invoke the same
*Add labs…* flow and forward to an already running app. Opening a file imports it without
acquiring its room or creating a second active session. Files can also be selected in
bulk inside the app, so associations are a convenience rather than the sole entry point.
Implement the application-side command-line/document activation and same-user instance
forwarding before claiming file support; the current parser only handles developer
options. Quote paths safely, accept multi-file opening and preserve the user's default
handler choice. The process receiving forwarded files validates them as ordinary imports.

Upgrade/repair replaces app files and owned registrations after closing the application;
preserve saved labs, device identities, protected keys and history. Do not copy the
student agent's service-restart/rollback machinery. Removal deletes app-owned binaries
and registrations, retains user lab data by default, and leaves external backup files
and all student installations intact. Offer local data/credential deletion only as an
explicit separate choice; it must not revoke other devices or delete the lab itself.
Reinstallation must reopen retained profiles without another import.
That guarantee applies to the same device and OS account, with the protected store
retained. Verify Keychain/DPAPI/libsecret or file-fallback access after changing the app
binary; copying user files to another device/account is not credential migration.
On macOS app-bundle removal alone does not run a custom uninstaller; provide the explicit
local-data cleanup action inside the app before removal, and document retained data.

Acceptance in ROADMAP M5 covers clean offline installation, launch, both file types,
upgrade/repair/reinstall/removal and the one-active-lab invariant on all three platforms.
Verify LAN connectivity separately from installation, including the platform's required
network permissions. M6 verifies capture/broadcast permissions when that feature exists.

Packaging references: [Avalonia macOS deployment](https://docs.avaloniaui.net/docs/deployment/macos),
[Windows Firewall rules](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/rules),
[.NET Linux prerequisites](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual#dependencies).
