#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
rid="${1:-win-arm64}"
case "$rid" in win-arm64|win-x64) ;; *) echo 'Use win-arm64 or win-x64.' >&2; exit 2 ;; esac
output_dir="${2:-$script_dir/out/$rid}"
dotnet publish "$script_dir/NativeSmoke.csproj" -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:UseSharedCompilation=false --disable-build-servers \
  -p:NuGetAudit=false --ignore-failed-sources -o "$output_dir"
