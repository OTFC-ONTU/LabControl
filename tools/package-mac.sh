#!/usr/bin/env bash
# The macOS teacher-console package (D-59 item 3): LabControl.app and a DMG.
#
#   1. publish the console for each requested osx RID
#   2. assemble LabControl.app/Contents/{Info.plist, MacOS/, Resources/labcontrol.icns}
#   3. sign ad hoc  (codesign --force --deep --sign -)
#   4. wrap it in a DMG with an /Applications symlink
#   5. verify:  plutil -lint,  codesign --verify --deep,  spctl --assess
#
# The binaries are unsigned in the sense of D-15: there is no Developer ID certificate and
# none is planned. The ad-hoc signature makes the bundle load on Apple Silicon; it does NOT
# satisfy Gatekeeper. `spctl --assess` therefore FAILS on purpose, and this script prints
# that failure rather than hiding it. The teacher opens the app the first time with
# right-click -> Open, or System Settings -> Privacy & Security -> "Open Anyway".
#
# Usage:  tools/package-mac.sh [Release|Debug] [rid ...]     default: Release osx-arm64
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/artifacts/package/mac"
configuration="${1:-Release}"
shift || true
rids=("$@")
[ ${#rids[@]} -gt 0 ] || rids=(osx-arm64)

[ "$(uname -s)" = "Darwin" ] || { echo "package-mac: this script needs macOS (codesign, hdiutil, plutil)" >&2; exit 1; }

version="$(dotnet msbuild "$root/src/LabControl.Console/LabControl.Console.csproj" -getProperty:Version -nologo | tr -d '[:space:]')"
[ -n "$version" ] || { echo "package-mac: could not read the console version" >&2; exit 1; }

icns="$root/src/LabControl.Console/Assets/labcontrol.icns"
[ -f "$icns" ] || { echo "package-mac: $icns is missing — run tools/make-icon.py" >&2; exit 1; }

echo "package-mac: $version ($configuration, ${rids[*]})"
rm -rf "$out"
mkdir -p "$out"

for rid in "${rids[@]}"; do
  app="$out/$rid/LabControl.app"
  echo "  console -> $rid"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

  dotnet publish "$root/src/LabControl.Console/LabControl.Console.csproj" \
    --configuration "$configuration" \
    --runtime "$rid" \
    --self-contained true \
    --output "$app/Contents/MacOS" \
    --verbosity quiet \
    --nologo \
    -p:DebugType=embedded

  [ -f "$app/Contents/MacOS/LabControl.Console" ] || {
    echo "package-mac: the publish has no LabControl.Console executable" >&2; exit 1; }
  chmod +x "$app/Contents/MacOS/LabControl.Console"
  # Third-party native symbol files, if the packages ship any for this RID: not needed at
  # runtime, and every extra file in the bundle is one more thing codesign has to seal.
  find "$app/Contents/MacOS" -name '*.pdb' -delete
  cp "$icns" "$app/Contents/Resources/labcontrol.icns"
  printf 'APPL????' > "$app/Contents/PkgInfo"

  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>org.ontfk.labcontrol.console</string>
    <key>CFBundleName</key>
    <string>LabControl</string>
    <key>CFBundleDisplayName</key>
    <string>LabControl Console</string>
    <key>CFBundleExecutable</key>
    <string>LabControl.Console</string>
    <key>CFBundleIconFile</key>
    <string>labcontrol</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.education</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSLocalNetworkUsageDescription</key>
    <string>LabControl Console reaches the student PCs of your classroom over the local network.</string>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>
            <string>LabControl lab file</string>
            <key>CFBundleTypeRole</key>
            <string>Editor</string>
            <key>LSHandlerRank</key>
            <string>Owner</string>
            <key>CFBundleTypeIconFile</key>
            <string>labcontrol</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>org.ontfk.labcontrol.lab</string>
            </array>
        </dict>
        <dict>
            <key>CFBundleTypeName</key>
            <string>LabControl lab backup</string>
            <key>CFBundleTypeRole</key>
            <string>Editor</string>
            <key>LSHandlerRank</key>
            <string>Owner</string>
            <key>CFBundleTypeIconFile</key>
            <string>labcontrol</string>
            <key>LSItemContentTypes</key>
            <array>
                <string>org.ontfk.labcontrol.backup</string>
            </array>
        </dict>
    </array>
    <key>UTExportedTypeDeclarations</key>
    <array>
        <dict>
            <key>UTTypeIdentifier</key>
            <string>org.ontfk.labcontrol.lab</string>
            <key>UTTypeDescription</key>
            <string>LabControl lab file</string>
            <key>UTTypeIconFile</key>
            <string>labcontrol</string>
            <key>UTTypeConformsTo</key>
            <array>
                <string>public.data</string>
            </array>
            <key>UTTypeTagSpecification</key>
            <dict>
                <key>public.filename-extension</key>
                <array>
                    <string>lclab</string>
                </array>
            </dict>
        </dict>
        <dict>
            <key>UTTypeIdentifier</key>
            <string>org.ontfk.labcontrol.backup</string>
            <key>UTTypeDescription</key>
            <string>LabControl lab backup</string>
            <key>UTTypeIconFile</key>
            <string>labcontrol</string>
            <key>UTTypeConformsTo</key>
            <array>
                <string>public.data</string>
            </array>
            <key>UTTypeTagSpecification</key>
            <dict>
                <key>public.filename-extension</key>
                <array>
                    <string>lcbak</string>
                </array>
            </dict>
        </dict>
    </array>
</dict>
</plist>
PLIST

  plutil -lint "$app/Contents/Info.plist" > /dev/null

  echo "  sign (ad hoc) -> $rid"
  codesign --force --deep --sign - "$app"
  codesign --verify --deep --strict --verbose "$app" 2>&1 | sed 's/^/    /'

  echo "  gatekeeper assessment (expected to refuse, D-15):"
  if spctl --assess --type execute --verbose "$app" 2>&1 | sed 's/^/    /'; then
    echo "    (accepted — unexpected for an ad-hoc signature; note it)"
  else
    echo "    refused, as documented: the teacher opens it once with right-click -> Open."
  fi

  echo "  dmg -> $rid"
  staging="$out/$rid/dmg"
  rm -rf "$staging"
  mkdir -p "$staging"
  cp -R "$app" "$staging/LabControl.app"
  ln -s /Applications "$staging/Applications"
  dmg="$out/LabControl-Console-$version-$rid.dmg"
  hdiutil create -quiet -volname "LabControl Console" -srcfolder "$staging" -ov -format UDZO "$dmg"
  rm -rf "$staging"
  echo "    $dmg"
done

echo
echo "artifacts written to $out"
du -sh "$out"/*.dmg 2>/dev/null || true
