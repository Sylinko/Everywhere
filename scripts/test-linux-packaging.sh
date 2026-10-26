#!/usr/bin/env bash
set -euo pipefail
runtime=${1:?Pass linux-x64 or linux-arm64}
repo_root=$(cd "$(dirname "$0")/.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/src/Everywhere.Linux" "$fixture/img" "$fixture/publish"
cp "$repo_root/src/Everywhere.Linux/packaging_deb.sh" "$repo_root/src/Everywhere.Linux/build-appimage.sh" "$fixture/src/Everywhere.Linux/"
cp "$repo_root/img/Everywhere-icon.png" "$fixture/img/"
cp "$repo_root/LICENSE" "$fixture/"
printf '#!/bin/sh\nprintf "packaging-fixture\\n"\n' > "$fixture/publish/Everywhere"
cp "$fixture/publish/Everywhere" "$fixture/publish/Everywhere.Watchdog"
chmod +x "$fixture/publish/Everywhere" "$fixture/publish/Everywhere.Watchdog"
cd "$fixture"
version=1.2.3-canary.20261026.1
case "$runtime" in
    linux-x64) asset=Linux-x64; arch=amd64 ;;
    linux-arm64) asset=Linux-arm64; arch=arm64 ;;
    *) exit 1 ;;
esac
bash src/Everywhere.Linux/packaging_deb.sh "$runtime" "$version" ./publish
deb="Everywhere-${asset}-v${version}.deb"
test "$(dpkg-deb -f "$deb" Package)" = everywhere
test "$(dpkg-deb -f "$deb" Architecture)" = "$arch"
test "$(dpkg-deb -f "$deb" Version)" = 1.2.3~canary.20261026.1
dpkg-deb -x "$deb" unpacked-deb
test -L unpacked-deb/usr/bin/Everywhere
test -x unpacked-deb/opt/Everywhere/Everywhere.Watchdog
desktop-file-validate unpacked-deb/usr/share/applications/Everywhere.desktop
test ! -e unpacked-deb/DEBIAN/prerm
bash src/Everywhere.Linux/build-appimage.sh "$runtime" "$asset" ./publish false "$version"
appimage="Releases/Everywhere-${asset}-v${version}.AppImage"
"$appimage" --appimage-extract >/dev/null
test "$(env -u LD_LIBRARY_PATH squashfs-root/AppRun)" = packaging-fixture
test -x squashfs-root/usr/lib/Everywhere/Everywhere.Watchdog
desktop-file-validate squashfs-root/com.Sylinko.Everywhere.desktop
echo "DEB and AppImage packaging fixtures passed for $runtime."
