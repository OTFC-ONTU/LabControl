#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
rid="${1:-win-arm64}"
case "$rid" in win-arm64|win-x64) ;; *) echo 'Use win-arm64 or win-x64.' >&2; exit 2 ;; esac
dotnet publish "$script_dir/InteractiveUninstall.csproj" -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:NuGetAudit=false -p:UseSharedCompilation=false --disable-build-servers --ignore-failed-sources -o "$script_dir/out/$rid"
