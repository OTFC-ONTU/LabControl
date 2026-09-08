#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
mode="${1:?Choose crash or no-link}"
rid="${2:-win-arm64}"
case "$mode" in crash|no-link) ;; *) echo 'Choose crash or no-link.' >&2; exit 2 ;; esac
case "$rid" in win-arm64|win-x64) ;; *) echo 'Use win-arm64 or win-x64.' >&2; exit 2 ;; esac
dotnet publish "$script_dir/BrokenAgent.csproj" -c Release -r "$rid" --self-contained true \
  -p:FixtureMode="$mode" -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false -p:UseSharedCompilation=false \
  --disable-build-servers --ignore-failed-sources -o "$script_dir/out/$rid/$mode"
