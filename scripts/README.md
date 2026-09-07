# Scripts

PowerShell (`.ps1`) or batch (`.cmd`) scripts for the student PCs.

`library/` is the **seed** of the console's script library (ROADMAP M4, `D-31`, `D-38`):
its files are compiled into the console and imported into `scripts.json` in the data
directory on the very first run, where the teacher edits and runs them from the *Scripts*
tab. After that this directory is never read again — the teacher's edits and deletions win.

A seed file describes itself in its header comments (`#` in PowerShell, `rem` or `::` in a
`.cmd`, before the first line of code):

```powershell
# Closes every browser window in the student's session.   ← the first comment line is the description
# run-as: user                                             ← system (default) | user
# timeout: 30                                              ← seconds without output before the script is killed (default 120)
```

The name in the library is the file name without its extension; the shell is the
extension. Keep scripts idempotent; print one line per step; exit non-zero on failure so
the job row turns red. Everything a script prints goes to the jobs panel, so say what you
did rather than staying silent.

`dev-install.ps1` / `dev-install.cmd` are not seed scripts: they are the development-only
agent installer run by hand on the VM until `Setup.exe` exists (M4 portion 3).
