#!/usr/bin/env bash
# The Windows teacher-console package (D-59 item 1): one self-contained
# LabControl.ConsoleSetup.exe that carries the published console inside it.
#
#   1. publish the console for the chosen runtime (win-x64 by default), self-contained, single file
#   2. zip that publish into artifacts/package/windows/console-payload.zip
#   3. publish LabControl.ConsoleSetup for that same runtime — the zip is embedded as a resource
#
# The result is ONE file to copy to a teacher's Windows PC. It installs per user, into
# %LOCALAPPDATA%\Programs\LabControl\Console\, and needs no administrator except for the
# separate "--firewall" step. It contains program files only: no lab, no key, no code.
#
# The installer cannot be run or tested on macOS. Building it here is a compile and a
# packaging step; its behaviour is verified on a Windows machine.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/artifacts/package/windows"
configuration="${1:-Release}"
# The teacher machines are win-x64; a Windows-on-ARM VM (the only Windows this project can
# be tested on from an Apple Silicon Mac) needs win-arm64. Everything else is identical.
rid="${2:-win-x64}"
case "$rid" in
  win-x64|win-arm64) ;;
  *) echo "package-windows: unsupported runtime identifier \"$rid\" (win-x64 or win-arm64)" >&2; exit 1 ;;
esac

version="$(dotnet msbuild "$root/src/LabControl.Console/LabControl.Console.csproj" -getProperty:Version -nologo | tr -d '[:space:]')"
[ -n "$version" ] || { echo "package-windows: could not read the console version" >&2; exit 1; }

echo "package-windows: $version ($configuration, $rid)"

rm -rf "$out"
mkdir -p "$out"

echo "  console -> $rid"
dotnet publish "$root/src/LabControl.Console/LabControl.Console.csproj" \
  --configuration "$configuration" \
  --runtime "$rid" \
  --self-contained true \
  --output "$out/console" \
  --verbosity quiet \
  --nologo \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=embedded

[ -f "$out/console/LabControl.Console.exe" ] || {
  echo "package-windows: the console publish has no LabControl.Console.exe" >&2; exit 1; }

# SkiaSharp and HarfBuzzSharp ship native .pdb symbol files — 105 MB of them for win-x64.
# LabControl's own symbols are inside the executable (DebugType=embedded), so nothing that
# a teacher installs needs these, and an installer that carries them is twice the size.
find "$out/console" -name '*.pdb' -delete

echo "  payload -> console-payload.zip"
( cd "$out/console" && zip -q -X -r "$out/console-payload.zip" . )

echo "  installer -> $rid"
dotnet publish "$root/src/LabControl.ConsoleSetup/LabControl.ConsoleSetup.csproj" \
  --configuration "$configuration" \
  --runtime "$rid" \
  --self-contained true \
  --output "$out/installer" \
  --verbosity quiet \
  --nologo \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=embedded \
  -p:ConsolePayloadZip="$out/console-payload.zip"

installer="$out/LabControl-Console-$version-$rid-Setup.exe"
mv "$out/installer/LabControl.ConsoleSetup.exe" "$installer"
rm -rf "$out/installer"

echo
echo "installer: $installer"
du -h "$installer" | cut -f1
echo
echo "On the Windows PC:"
echo "  double-click it, or run it from a prompt;  --dry-run prints the plan and changes nothing"
echo "  it is unsigned (D-15), so SmartScreen shows \"More info\" -> \"Run anyway\" the first time"
