# Scripts

PowerShell (`.ps1`) or batch (`.cmd`) scripts pushed to PCs by the console's
"Run script" action. Keep them idempotent; print one line per step; exit non-zero
on failure so the job row turns red.
