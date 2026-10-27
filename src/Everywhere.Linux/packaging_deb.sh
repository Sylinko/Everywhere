#!/usr/bin/env bash
set -euo pipefail

if (( $# < 2 || $# > 3 )); then
    echo 'Usage: packaging_deb.sh linux-{x64,arm64} VERSION [PUBLISH_DIR]' >&2
    exit 1
fi
workspace=$PWD
script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
runtime=$1
version=$2
case "$runtime" in
    linux-x64) deb_arch=amd64; asset_arch=x64 ;;
    linux-arm64) deb_arch=arm64; asset_arch=arm64 ;;
    *) echo "Unsupported RID: $runtime" >&2; exit 1 ;;
esac
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-canary\.[0-9.]+)?$ ]] || { echo 'Invalid version' >&2; exit 1; }
deb_version=${version/-canary./~canary.}
publish_dir=$(cd -- "${3:-$script_dir/bin/Release/net10.0/$runtime/publish}" && pwd)
test -x "$publish_dir/Everywhere"
test -x "$publish_dir/Everywhere.Watchdog"
package_dir=$(mktemp -d)
trap 'rm -rf "$package_dir"' EXIT
install -d "$package_dir/DEBIAN" "$package_dir/opt/Everywhere" "$package_dir/usr/bin"
cp -a "$publish_dir/." "$package_dir/opt/Everywhere/"
ln -s /opt/Everywhere/Everywhere "$package_dir/usr/bin/Everywhere"
install -Dm644 "$script_dir/../../img/Everywhere-icon.png" "$package_dir/usr/share/icons/hicolor/256x256/apps/Everywhere.png"
install -Dm644 "$script_dir/../../LICENSE" "$package_dir/usr/share/doc/everywhere/copyright"
install -d "$package_dir/usr/share/applications"
cat > "$package_dir/usr/share/applications/Everywhere.desktop" <<'EOF'
[Desktop Entry]
Name=Everywhere
Comment=A context-aware AI assistant for your desktop.
Exec=/usr/bin/Everywhere %U
Icon=Everywhere
Type=Application
Terminal=false
Categories=Utility;
Keywords=AI;tool;
EOF
desktop-file-validate "$package_dir/usr/share/applications/Everywhere.desktop"
cat > "$package_dir/DEBIAN/control" <<EOF
Package: everywhere
Version: $deb_version
Architecture: $deb_arch
Maintainer: Sylinko
Description: Context-aware AI assistant for your desktop
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3, libicu74 | libicu76 | libicu72, libfontconfig1, libx11-6, libice6, libsm6, libxext6, libxcb1, libxrandr2, libxrender1, libxi6, libxcursor1, libwayland-client0, libxkbcommon0, libglib2.0-0, libglib2.0-bin, libatspi2.0-0, xdg-utils, desktop-file-utils
Recommends: libnotify-bin
Suggests: grim, wl-clipboard
Section: utils
Priority: optional
Homepage: https://everywhere.sylinko.com
EOF
chmod 755 "$package_dir" "$package_dir/DEBIAN"
dpkg-deb --root-owner-group --build "$package_dir" "$workspace/Everywhere-Linux-${asset_arch}-v${version}.deb"
