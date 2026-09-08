#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet build "$script_dir/TransferDrill.csproj" -c Release -p:NuGetAudit=false \
  -p:UsedAvaloniaProducts= -p:UseSharedCompilation=false --disable-build-servers --ignore-failed-sources
dotnet "$script_dir/bin/Release/net10.0/LabControl.TransferDrill.dll" "$@"
cp "$script_dir/bin/Release/net10.0/transfer.results.json" "$script_dir/latest.results.json"
