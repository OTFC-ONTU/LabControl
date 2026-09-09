# LabControl — instructions for Codex

## Shared project context (read first)

LabControl is a self-hosted classroom-management system: one cross-platform teacher
console manages up to 30 Windows student PCs in its active lab on an isolated LAN.
The current implementation has one lab per console profile. Planned M5 (`D-53`) adds
bulk import of lab files and fast switching among saved rooms, with at most one active
lab and no background connections/video for inactive rooms. Rosters are mostly stable;
teachers change rooms between lessons. The current room has 14 PCs. The system must
remain usable by its teacher-owner without an IT department, so changes must be zero-maintenance and
self-explanatory.

Read [`CLAUDE.md`](CLAUDE.md) completely before changing the project. It is the
detailed project brief and records the current implementation status. Then read
the documents relevant to the task:

- `docs/ARCHITECTURE.md` and `docs/PROTOCOL.md` before changing `src/`;
- `docs/INSTALLER.md` before changing provisioning, installation, updates, users,
  Windows services, firewall, power settings, or the USB payload;
- `docs/ROADMAP.md` before adding scope or changing milestone status;
- `docs/DECISIONS.md` before making or revisiting a non-obvious design choice.

## Non-negotiable constraints

- The teacher console is replaceable and may run on macOS, Windows, or Linux. Do
  not bind the lab to one console machine.
- Never hard-code the number of PCs; the product is designed for up to 30.
- M5 lab files are for routine classroom access, distinct from full administrator
  backups. Preserve existing backup recovery; do not distribute CA private keys or
  enrollment codes as ordinary teacher access. Keep lab data and session lifecycles
  isolated, and release the old lab before activating another (`D-53`).
- M5 also accepts administrator `.lcbak` backups in the same bulk import and lab
  selector; preserve full authority per lab without repeated restoration on switching.
  Teacher-console installers package/register the desktop app and file types, with
  no agent services or student preparation. Verify scoped LAN permissions and native
  prerequisites separately from copying files; retain profiles/keys on upgrade and by
  default on uninstall (`D-54`, `docs/INSTALLER.md`).
- The M5 design is recorded in `D-55`…`D-60` (2026-09-08), extended by `D-68`
  (2026-09-09): profile store and resumable migration, signed `.lclab`/`.lcreq`/`.lcgrant`
  files with the role in the subject OU and `instance:` revocation, one
  `ActiveLabController` with release-before-acquire and result ownership, take-over on
  the agent's clock, per-user console packaging with single-instance forwarding, dormant
  imported enrollment codes. Follow those entries and ROADMAP M5 *How it is being built*
  (eight portions; **all eight built and reviewed**, 1–3 on 2026-09-08 — portion 1: profile
  store, resumable migration, `lab.json` schema 2, `console.lock`; portion 2:
  `ActiveLabController`, the *My labs* chooser, *Disconnect*, the departure report and bulk
  `.lcbak` import; portion 3: the signed `.lclab`/`.lcreq`/`.lcgrant` exchange, teacher
  sessions without a vault, `instance:` withdrawal with confirmed delivery, the *Teacher
  devices* panel and dormant imported codes, 753 tests — and portion 4 on 2026-09-09:
  results and progress bound to the console instance the agent's TLS handshake validated,
  `jobs-inflight.json` restoration with narrow re-send rules, 785 tests, verified with a
  real agent on the isolated Windows clone — and portion 5 on 2026-09-09: take-over decided
  on the agent's own clock (arrival order, the press bound to its own beacon, honoured
  once), the four ownership states a console can prove — linked here, observed elsewhere
  within `Defaults.OwnershipObservationLifetime`, unknown, offline — the banner's *holds at
  least N*, and the additive informational `Welcome.console_access` that never decides a
  refusal, followed the same day by the portion-5 security fixes — a withdrawn instance's
  beacon refused where the beacon is judged (the agent ahead of both the take-over and the
  dial branch, the console in the same way), ownership attributed only on the PC's own
  `link.taken_over` departure report, an expiring banner and the arrival stamped in the
  listener's receive loop — still not run on the Windows VM with two consoles and
  skewed clocks — and portion 6 on 2026-09-09: console documents
  on the command line, `--import-only`, a second launch forwarding its file paths over a
  per-data-directory named pipe or private-directory Unix socket instead of starting a
  second console, macOS file activation, 788 tests, checked by hand on the Mac, with the
  Windows pipe since exercised by portion 7's Windows drill and the Linux `SO_PEERCRED` path
  still not run — and portion 7
  on 2026-09-09: teacher-console packaging (`D-59` items 1–4) — the single-file per-user
  Windows installer with an untrusted `installed-files.txt`, a finishing temporary copy and
  an uninstall that never elevates, the read-only LAN-access banner that calls a port
  reachable only when a rule opens it for this console and nothing blocks it, the
  ad-hoc-signed macOS `.app`/DMG, the per-user Linux tarball and `tools/package-*.sh` into
  `artifacts/package/`, 936 tests after the merge with portions 4 and 6, packages built and
  the Linux scripts round-tripped on the Mac, then **run on Windows for the first time on
  2026-09-09** (isolated Windows 11 ARM64 clone, guest `PC-27`, no interactive session, so
  every unelevated step ran as `LOCAL SERVICE` and the elevated one as `SYSTEM`), which
  found four defects, all fixed and merged (`a7fbe33`): an `app.manifest` double hyphen
  inside an XML comment that made Windows refuse the activation context so the installer
  would not start at all, a firewall profile constant of 6 that opened Public and rejected a
  real private-and-domain rule (`NET_FW_PROFILE_TYPE2` is Domain 1, Private 2, Public 4, so
  3), a `group=` argument in the printed `netsh` line that `add rule` refuses outright, and
  an `ArgumentList`-built self-delete command `cmd.exe` cannot parse, which left a 148 MB
  copy in the temp directory — the install, the plan and dry run, the shortcut, the
  Installed-apps entry, both file-type registrations, an idempotent repeat run, the lock
  refusal, document forwarding into a running console over the Windows named pipe, the real
  firewall rules and the banner, uninstall with its temporary copy, `--remove-data` and a
  DPAPI-sealed key surviving replacement are verified, while Explorer's own double-click, a
  real elevation prompt a teacher answers, `win-x64` (only ARM64 was built and run), the
  installer under an ordinary interactive profile, the unsigned-download warning, the Linux
  `SO_PEERCRED` path and a real Linux desktop menu are not — and portion 8
  on 2026-09-09: the acceptance drills and the six gaps an adversarial audit of every M5
  acceptance criterion found (`D-68`) — the combined picker filter that makes a mixed
  selection possible, batched authorization written into one folder, the departure flow
  *Disconnect*, the window close and quit all ask, the beacon-resume step-over that relinks
  thirty refused PCs in 2.7 s instead of 30.3 s, and agent events bound to the console that
  delivered their job while machine events stay unbound; main stands at 984 tests, 755
  Shared + 229 Console, run twice on the merged tree). Still unverified: the Windows and
  Linux runs of the console packaging, portion 5 on Windows with two consoles and skewed
  clocks against a real agent, the Avalonia quit hook by hand, and the milestone in the
  physical lab. The `Welcome.console_access`
  `.proto` change landed with portion 5 and `docs/PROTOCOL.md` was updated in that commit.
- The Windows student agent must survive reboots and must not let a failed Win32
  operation escape the service loop.
- Student PCs may have no internet. Do not introduce a cloud or per-PC runtime
  dependency.
- Never log or persist secrets outside their designed stores. In particular, do
  not expose the lab-key passphrase, private keys, enrollment material, or the
  `student` password.
- Ports, names, paths, and student-account details belong in
  `src/LabControl.Shared/Defaults.cs`, not in repeated literals.
- Keep the solution buildable with plain `dotnet build` and `dotnet test` on the
  Apple Silicon development machine. Windows-only behavior needs a Windows VM or
  a lab PC for runtime verification.
- Planned M4 Setup must support opting out of student-account creation (default on)
  and standalone uninstall. Preserve personal accounts and profiles; repair retains the
  selected mode and removal uses recorded ownership (`docs/INSTALLER.md`, D-40).
- M4 supports clean installation and repair of owned installs only; unowned legacy dev
  installations are refused. Legacy migration is outside M4 (owner decision 2026-09-08).
- The account journal, Windows preparation component (`D-45`, `D-46`), protected
  settings journal core (`D-47`), two machine DWORD adapters (`D-48`) and AC power-plan
  adapters with activation confirmation (`D-49`) and the Windows Update active-hours
  tuple adapter (`D-50`) and ownership-gated LSA sign-in secret adapter (`D-51`) are built,
  and Setup executable integration is in progress (`D-52`); Windows acceptance is pending. `AccountSetupScope` owns the private storage/lock; new accounts
  remain disabled until final successful setup activation. Binary-directory ownership also
  requires its matching installation-id marker and protected descendant ACLs. Never infer ownership for legacy dev installs from the account name.
- Before hiding the original administrator, account-on Setup must journal and verify
  CredUI `EnumerateAdministrators=0`; restore the tile before restoring that policy.
  Account-off touches neither setting. Do not change UAC security policy to bypass
  native administrator-maintenance acceptance (`D-52`).
- Do not invent features outside `docs/ROADMAP.md`; propose the scope there first.

## Shared documentation contract for Codex and Claude Code

`AGENTS.md` and `CLAUDE.md` are companion agent documents. Neither belongs only
to the agent named by the file: together they are shared project documentation.

- When project facts, requirements, status, workflows, commands, repository
  layout, or shared conventions change, inspect **both** files and update every
  applicable section in the same task.
- Never update only the current agent's file while leaving the other agent with
  stale or contradictory guidance. Agent-specific instructions may stay in one
  file only when they are explicitly labeled as agent-specific.
- Documentation is part of the implementation. If behavior described by any
  Markdown file changes, update that Markdown in the same task.
- Markdown is the source of truth. After changing any `.md` file, run
  `tools/docs-build.sh`; never edit `docs/html/` by hand.
- Any `.proto` change requires a matching `docs/PROTOCOL.md` update. Any
  non-obvious design decision requires a new `D-NN` entry in
  `docs/DECISIONS.md`.

A task that leaves either agent's instructions, the relevant project Markdown,
or the generated HTML stale is not finished.

## Engineering conventions

- Code, identifiers, comments, and commit messages are English.
- UI strings are resources, not hard-coded text. English is the primary locale;
  Ukrainian localization is planned.
- Prefer simple, well-supported libraries. Record every new NuGet dependency and
  its reason in `docs/DECISIONS.md`.
- Preserve protocol compatibility rules documented in `docs/PROTOCOL.md`,
  especially the frozen subset and the job lifecycle across reconnects.
- Develop the console against `LabControl.FakeAgent` on macOS. Do not claim a
  Windows-only behavior is verified until it has run in the VM or on a lab PC.
- Preserve unrelated work in a dirty worktree.

## Repository map

```text
LabControl/
├── AGENTS.md                      Codex instructions and shared agent contract
├── CLAUDE.md                      detailed project brief and Claude Code rules
├── README.md                      human overview and quick start
├── docs/                          architecture, protocol, installer, ADRs, roadmap
│   └── html/                      generated Markdown mirror; never hand-edit
├── src/LabControl.Shared/         contracts, shared models, constants, core logic
├── src/LabControl.Console/        cross-platform Avalonia teacher console
├── src/LabControl.Agent/          Windows service and privileged operations
├── src/LabControl.Agent.Session/  interactive-session capture/input helper
├── src/LabControl.Setup/          one-shot Windows USB installer
├── src/LabControl.FakeAgent/      cross-platform simulated student PCs
├── tests/                         shared and console test projects
├── tools/                         documentation and publish tooling
├── packages/                      package catalog and ignored installer cache
└── scripts/                       library/ = seed of the console's script library (D-38); dev-install.* for the VM
```

The console and Windows Setup share `src/LabControl.Console/Assets/labcontrol.ico`,
regenerated by `tools/make-icon.py`. Setup embeds it in its executable and dialogs;
the installed uninstaller is a copy of that executable.

## Common commands

```bash
dotnet build
dotnet test
dotnet run --project src/LabControl.Console
dotnet run --project src/LabControl.FakeAgent -- --count 14 --payload ~/usb
tools/publish-all.sh
tools/docs-build.sh
tools/make-icon.py
```

Use `docs/ROADMAP.md` and the `Current status` section of `CLAUDE.md` for the
latest milestone state; do not duplicate that volatile detail here unless Codex
needs a specific additional instruction.

M4 integration: password fallback is allowed only for a classified create-new policy
rejection on a non-domain PC, through the protected tuple journal. Profile templates add
only bounded documents to the resolved Default profile; never import arbitrary hives or
overwrite existing profiles. Keep removal retry metadata until cleanup can finish.
Fresh sign-in setup journals `AutoLogonSID` separately before enabling autologon and
restores it after the main tuple. Never infer its original value for older tuple-only
history; preserve it with the explicit warning. Account-off skips both settings.

The owner explicitly requires Setup to finish the Windows prerequisites for console-pushed
updates and Wake-on-LAN. Select the wake MAC from positively identified physical Ethernet,
never an arbitrary active virtual adapter. Apply dependent NIC power controls in order and
restore in reverse dependency order. Report unsupported capabilities; a local settings
read-back is not proof of network reachability or physical wake from shutdown.
Keep installer advisories bounded to the fixed readiness-code schema; translate them in
the console, retain valid cached warnings offline and never display arbitrary report JSON.
