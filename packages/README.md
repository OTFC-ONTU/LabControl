# Package catalog

This directory is only the format's description. The console keeps each lab's catalog in its
own data directory (`labs/<lab_id>/packages/`, ARCHITECTURE §7) and fills it with the
*Add package…* wizard on the *Packages* tab (M7 portion 3, `D-88`) — there is no need to
write these files by hand.

One `<id>.yaml` per program; its installer is copied beside it into
`<id>/<first 12 hex of the SHA-256>/<file>` (git-ignored, never in a backup).

```yaml
schema_version: 1
name: IntelliJ IDEA Community Edition
version: "2026.2"
family: nsis                       # msi | burn | nsis | inno | installshield | exe
file: ideaIC-2026.2.exe            # pushed to the PCs over the LAN
size: 1018234567
sha256: "<64 hex>"
silent: /S                         # an MSI runs as msiexec /i <file> followed by these
detect:
  uninstall_name: IntelliJ IDEA    # or  path: "%ProgramFiles%\\JetBrains\\IDEA\\bin\\idea64.exe"
timeout_minutes: 15                # the PC ends the installer after this (1–180, D-89)
changed_by: MacBook                # the console instance that last changed the entry
changed_at: 2026-10-27T09:00:00Z
```

`uninstall_name` matches the start of a program's name in Windows' list of installed
programs, ignoring case. The installer always runs silently as SYSTEM (`D-23`). A new version
of the same program replaces its entry. Installing is done from the *Packages* tab
(`install_package`, `D-89`); what each PC answered is kept in `package-status.json` beside
`lab.json`, not here. Keys a console does not know are ignored; a file
written by a newer console is listed as unreadable rather than guessed at.
