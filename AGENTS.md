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
