#!/usr/bin/env bash
# Run PowerShell in the Windows VM as SYSTEM via the QEMU guest agent; print its output.
# Usage: vmrun.sh [-t seconds] "<powershell code>"   or   vmrun.sh [-t seconds] -f script.ps1
set -uo pipefail
U=/Applications/UTM.app/Contents/MacOS/utmctl
VM="LabControl PC-01"
timeout=120
if [[ "${1:-}" == "-t" ]]; then timeout="$2"; shift 2; fi
if [[ "${1:-}" == "-f" ]]; then body="$(cat "$2")"; else body="$1"; fi
stamp=$(date +%s)-$RANDOM
g="C:\\LabControlDev\\run-$stamp"
{ printf '%s\r\n' "\$ErrorActionPreference='Continue'"; printf '%s\r\n' "$body"; printf '%s\r\n' 'Write-Output "__DONE__"'; } | $U file push "$VM" "$g.ps1"
$U exec "$VM" --cmd 'C:\Windows\System32\cmd.exe' '/c' "powershell -NoProfile -ExecutionPolicy Bypass -File $g.ps1 > $g.out 2>&1" >/dev/null 2>&1
for ((i=0; i<timeout; i++)); do
  sleep 1
  out="$($U file pull "$VM" "$g.out" 2>/dev/null)" || true
  if [[ "$out" == *__DONE__* ]]; then printf '%s\n' "${out%__DONE__*}"; exit 0; fi
done
echo "[vmrun: timeout after ${timeout}s]"; printf '%s\n' "$out"; exit 124
