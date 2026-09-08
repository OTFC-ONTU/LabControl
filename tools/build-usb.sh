#!/usr/bin/env bash
# Add binaries to the enrollment payload written from console Settings.
# Default: artifacts/win-x64. For the Apple Silicon Windows VM, pass artifacts/win-arm64.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ $# -lt 1 || $# -gt 3 ]]; then
  echo 'Usage: tools/build-usb.sh <USB mount> [publish directory] [version]' >&2
  exit 2
fi
destination="$1"
publish_directory="${2:-$root/artifacts/win-x64}"
arguments=("$destination" "$publish_directory")
if [[ $# -eq 3 ]]; then arguments+=("$3"); fi
dotnet run --project "$root/tools/UsbBuild/UsbBuild.csproj" -- "${arguments[@]}"
