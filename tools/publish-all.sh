#!/usr/bin/env bash
# Self-contained, single-file publishes for every runtime LabControl targets.
#
#   console : the teacher machine — macOS today, Windows tomorrow, Linux nice-to-have
#   agent   : student PCs are win-x64; a Windows VM on an Apple Silicon Mac is win-arm64,
#             and M2 is tested on both, so both must publish at all times.
#
# Output: artifacts/<rid>/<project>/
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/artifacts"
configuration="${1:-Release}"

console_rids=(osx-arm64 win-x64 linux-x64)
windows_rids=(win-x64 win-arm64)

publish() {
  local project="$1" rid="$2" name
  name="$(basename "$project" .csproj)"
  echo "  $name -> $rid"
  dotnet publish "$root/$project" \
    --configuration "$configuration" \
    --runtime "$rid" \
    --self-contained true \
    --output "$out/$rid/$name" \
    --verbosity quiet \
    --nologo \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=embedded
}

rm -rf "$out"
echo "publish-all: configuration=$configuration"

echo "console:"
for rid in "${console_rids[@]}"; do
  publish "src/LabControl.Console/LabControl.Console.csproj" "$rid"
done

echo "agent side (Windows only):"
for rid in "${windows_rids[@]}"; do
  publish "src/LabControl.Agent/LabControl.Agent.csproj" "$rid"
  publish "src/LabControl.Agent.Session/LabControl.Agent.Session.csproj" "$rid"
  publish "src/LabControl.Setup/LabControl.Setup.csproj" "$rid"
done

echo
echo "artifacts written to $out"
du -sh "$out"/* 2>/dev/null || true
