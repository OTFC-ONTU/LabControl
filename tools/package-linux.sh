#!/usr/bin/env bash
# The Linux teacher-console package (D-59 item 4): a tarball with per-user install.sh
# and uninstall.sh. No root, no package manager, no repository.
#
#   ~/.local/opt/labcontrol/console/            the program
#   ~/.local/share/applications/…desktop        the launcher, Exec=… %F
#   ~/.local/share/mime/packages/…xml           the .lclab and .lcbak globs
#   ~/.local/share/icons/hicolor/*/apps/…png    the icon
#
# The script only BUILDS the tarball; it runs on the Mac. Installing and running it is
# manual acceptance on a Linux machine (D-59 item 4), and the documented matrix is
# Ubuntu 22.04 and 24.04.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/artifacts/package/linux"
configuration="${1:-Release}"
rid="${2:-linux-x64}"

version="$(dotnet msbuild "$root/src/LabControl.Console/LabControl.Console.csproj" -getProperty:Version -nologo | tr -d '[:space:]')"
[ -n "$version" ] || { echo "package-linux: could not read the console version" >&2; exit 1; }

name="labcontrol-console-$version-$rid"
stage="$out/$name"
master="$root/src/LabControl.Console/Assets/labcontrol.png"

echo "package-linux: $version ($configuration, $rid)"
rm -rf "$out"
mkdir -p "$stage/console" "$stage/share/applications" "$stage/share/mime/packages" "$stage/share/icons"

dotnet publish "$root/src/LabControl.Console/LabControl.Console.csproj" \
  --configuration "$configuration" \
  --runtime "$rid" \
  --self-contained true \
  --output "$stage/console" \
  --verbosity quiet \
  --nologo \
  -p:DebugType=embedded

[ -f "$stage/console/LabControl.Console" ] || {
  echo "package-linux: the publish has no LabControl.Console executable" >&2; exit 1; }
chmod +x "$stage/console/LabControl.Console"
# Third-party native symbol files, if the packages ship any for this RID: not runtime files.
find "$stage/console" -name '*.pdb' -delete

# Icons. sips is macOS-only; without a resizer the master is installed as the legacy
# pixmap only, which "Icon=labcontrol-console" still resolves.
if command -v sips > /dev/null 2>&1; then
  for size in 48 64 128 256 512; do
    mkdir -p "$stage/share/icons/hicolor/${size}x${size}/apps"
    sips -Z "$size" "$master" --out "$stage/share/icons/hicolor/${size}x${size}/apps/labcontrol-console.png" > /dev/null
  done
fi
mkdir -p "$stage/share/pixmaps"
cp "$master" "$stage/share/pixmaps/labcontrol-console.png"

cat > "$stage/share/applications/labcontrol-console.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=LabControl Console
GenericName=Classroom control
Comment=Drive the student PCs of one computer lab
Exec=labcontrol-console %F
TryExec=labcontrol-console
Icon=labcontrol-console
Terminal=false
Categories=Education;Network;
MimeType=application/x-labcontrol-lab;application/x-labcontrol-backup;
StartupWMClass=LabControl.Console
DESKTOP

cat > "$stage/share/mime/packages/labcontrol-console.xml" <<'MIME'
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="application/x-labcontrol-lab">
    <comment>LabControl lab file</comment>
    <icon name="labcontrol-console"/>
    <glob pattern="*.lclab"/>
  </mime-type>
  <mime-type type="application/x-labcontrol-backup">
    <comment>LabControl lab backup</comment>
    <icon name="labcontrol-console"/>
    <glob pattern="*.lcbak"/>
  </mime-type>
</mime-info>
MIME

cat > "$stage/install.sh" <<'INSTALL'
#!/usr/bin/env bash
# Installs LabControl Console for the user running this script. No root, no sudo.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
opt="${XDG_DATA_HOME:-$HOME/.local/share}"
prefix="$HOME/.local/opt/labcontrol/console"
bin="$HOME/.local/bin"

echo "LabControl Console -> $prefix"

if pgrep -f "$prefix/LabControl.Console" > /dev/null 2>&1; then
  echo "LabControl Console is running. Close it and run this again." >&2
  exit 1
fi

rm -rf "$prefix"
mkdir -p "$prefix" "$bin" "$opt/applications" "$opt/mime/packages"
cp -R "$here/console/." "$prefix/"
chmod +x "$prefix/LabControl.Console"

# One launcher on the PATH, so the .desktop entry and a terminal agree on the name.
cat > "$bin/labcontrol-console" <<LAUNCHER
#!/usr/bin/env bash
exec "$prefix/LabControl.Console" "\$@"
LAUNCHER
chmod +x "$bin/labcontrol-console"

cp "$here/share/applications/labcontrol-console.desktop" "$opt/applications/"
cp "$here/share/mime/packages/labcontrol-console.xml" "$opt/mime/packages/"
if [ -d "$here/share/icons" ]; then
  mkdir -p "$opt/icons"
  cp -R "$here/share/icons/." "$opt/icons/"
fi
mkdir -p "$opt/pixmaps"
cp "$here/share/pixmaps/labcontrol-console.png" "$opt/pixmaps/"

command -v update-mime-database > /dev/null 2>&1 && update-mime-database "$opt/mime" || true
command -v update-desktop-database > /dev/null 2>&1 && update-desktop-database "$opt/applications" || true
command -v gtk-update-icon-cache > /dev/null 2>&1 && gtk-update-icon-cache -f -t "$opt/icons/hicolor" > /dev/null 2>&1 || true

# Become the default handler only where the desktop has none: a choice already made by
# the person using this computer is never overwritten (D-54 item 4).
if command -v xdg-mime > /dev/null 2>&1; then
  for type in application/x-labcontrol-lab application/x-labcontrol-backup; do
    current="$(xdg-mime query default "$type" 2>/dev/null || true)"
    if [ -z "$current" ]; then
      xdg-mime default labcontrol-console.desktop "$type" || true
    else
      echo "  $type already opens with $current — left alone"
    fi
  done
fi

case ":$PATH:" in
  *":$bin:"*) ;;
  *) echo "  note: $bin is not on your PATH; the Start-menu entry works anyway" ;;
esac

echo "Done. Look for \"LabControl Console\" in the application menu."
echo "Your labs live in ~/.labcontrol and are never touched by install or uninstall."
INSTALL
chmod +x "$stage/install.sh"

cat > "$stage/uninstall.sh" <<'UNINSTALL'
#!/usr/bin/env bash
# Removes what install.sh installed for this user. Keeps ~/.labcontrol.
set -euo pipefail

opt="${XDG_DATA_HOME:-$HOME/.local/share}"
prefix="$HOME/.local/opt/labcontrol/console"
bin="$HOME/.local/bin"

if pgrep -f "$prefix/LabControl.Console" > /dev/null 2>&1; then
  echo "LabControl Console is running. Close it and run this again." >&2
  exit 1
fi

rm -rf "$prefix"
rmdir "$HOME/.local/opt/labcontrol" 2> /dev/null || true
rm -f "$bin/labcontrol-console"
rm -f "$opt/applications/labcontrol-console.desktop"
rm -f "$opt/mime/packages/labcontrol-console.xml"
rm -f "$opt/pixmaps/labcontrol-console.png"
find "$opt/icons" -name 'labcontrol-console.png' -delete 2> /dev/null || true

command -v update-mime-database > /dev/null 2>&1 && update-mime-database "$opt/mime" || true
command -v update-desktop-database > /dev/null 2>&1 && update-desktop-database "$opt/applications" || true

echo "Removed. Your labs are still in ~/.labcontrol; delete that directory yourself if you"
echo "really want them gone — a lab key that exists nowhere else cannot be recovered."
UNINSTALL
chmod +x "$stage/uninstall.sh"

cat > "$stage/README.txt" <<README
LabControl Console $version for Linux ($rid)

    ./install.sh      install for the user running it (no root)
    ./uninstall.sh    remove it again; your labs in ~/.labcontrol stay

Tested matrix (D-59 item 4): Ubuntu 22.04 LTS and Ubuntu 24.04 LTS, x86-64, on a normal
desktop session. Other distributions are expected to work and are not verified.

Native prerequisites — the console is self-contained .NET, but it still needs the system
libraries every .NET GUI needs:

    libicu (libicu70 on 22.04, libicu74 on 24.04)   globalization; the console needs it
    libfontconfig1                                   font lookup
    libx11-6, libice6, libsm6                        X11 session
    libgl1                                           Skia rendering

    sudo apt install libicu74 libfontconfig1 libx11-6 libice6 libsm6 libgl1

Optional:

    libsecret-1-0    the desktop keyring. Without it the console protects this device's
                     private key with its own file-backed protector instead, and says so
                     per lab. Both work; the keyring is the better one.

On a machine with no internet, download those .deb files on a connected machine and copy
them across; nothing else is needed.

The firewall: the console listens on TCP 47800 and UDP 47801. A desktop Ubuntu has ufw
disabled by default and needs nothing. If ufw is on:

    sudo ufw allow in proto tcp to any port 47800
    sudo ufw allow in proto udp to any port 47801

Opening a .lclab or .lcbak file from the file manager starts the console (or hands the
file to the one already running) and takes you to the import dialog. It never activates
a lab by itself.
README

echo "  tarball"
tar -czf "$out/$name.tar.gz" -C "$out" "$name"
rm -rf "$stage"

echo
echo "artifacts written to $out"
du -h "$out/$name.tar.gz" | cut -f1
