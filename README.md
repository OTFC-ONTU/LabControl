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

**M0 — skeleton and toolchain: done (2026-09-04).** The solution builds and tests green on
macOS, the protobuf contract compiles, the console opens a window, the fake lab simulates
up to 30 machines, and self-contained binaries publish for `osx-arm64`, `win-x64`,
`win-arm64` and `linux-x64`. No product behaviour yet — every component except
`LabControl.Shared` is a skeleton.

Next: [`docs/ROADMAP.md`](docs/ROADMAP.md) → **M1 — lab identity, link and presence**.

## Quick start (once M0 exists)
```bash
dotnet build
dotnet test
dotnet run --project src/LabControl.Console
dotnet run --project src/LabControl.FakeAgent -- --count 14
tools/publish-all.sh
tools/docs-build.sh
```

Requires the .NET 10 SDK and nothing else. The `net10.0-windows` projects compile on
macOS and Linux too (they just cannot run there), so a broken Windows build is caught
immediately rather than at the next visit to the lab.
