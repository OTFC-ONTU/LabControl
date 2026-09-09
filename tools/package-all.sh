#!/usr/bin/env bash
# Every teacher-console desktop package (D-59): Windows installer, macOS app + DMG,
# Linux tarball. tools/publish-all.sh still produces the plain executable directories
# used for development and for the USB payload; this script produces the things a
# teacher installs.
#
# Output: artifacts/package/{windows,mac,linux}/
#
# The macOS part needs macOS (codesign, hdiutil) and is skipped elsewhere with a notice.
# Nothing here can be *run* except on its own operating system: the Windows installer is
# compiled and packaged on the Mac, and verified on a Windows machine.
set -euo pipefail

tools="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
configuration="${1:-Release}"

echo "package-all: configuration=$configuration"
echo
"$tools/package-windows.sh" "$configuration"
echo
"$tools/package-linux.sh" "$configuration"
echo
if [ "$(uname -s)" = "Darwin" ]; then
  "$tools/package-mac.sh" "$configuration"
else
  echo "package-mac: skipped — the .app and the DMG need macOS."
fi

echo
echo "packages:"
find "$tools/../artifacts/package" -maxdepth 2 -type f \( -name '*.exe' -o -name '*.dmg' -o -name '*.tar.gz' \) \
  -exec du -h {} \; 2> /dev/null || true
