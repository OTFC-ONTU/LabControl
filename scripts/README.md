# Scripts

PowerShell (`.ps1`) or batch (`.cmd`) scripts for the student PCs. This directory is the
**seed** of the console's script library (ROADMAP M4, `D-31`): on first run the console
imports these into `scripts.json` in its data directory, where the teacher edits and runs
them from the *Scripts* view; after that this directory is not read again. Until M4 the
console runs only its built-in test scripts (M2 portion 3).

Keep scripts idempotent; print one line per step; exit non-zero on failure so the job row
turns red. Each script starts with a comment line giving its one-line description, which
becomes the description in the library.
