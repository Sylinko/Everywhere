#!/usr/bin/env bash
set -euo pipefail

if (( $# < 2 || $# > 3 )); then
    echo 'Usage: packaging_rpm.sh linux-{x64,arm64} VERSION [PUBLISH_DIR]' >&2
    exit 1
fi
workspace=$PWD
script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
runtime=$1
version=$2
case "$runtime" in
    linux-x64)
        rpm_arch=x86_64
        asset_arch=x64
        ;;
    linux-arm64)
        rpm_arch=aarch64
        asset_arch=arm64
        ;;
    *)
        echo "Unsupported RID: $runtime" >&2
        exit 1
        ;;
esac
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-canary\.[0-9.]+)?$ ]]; then
    echo 'Invalid version' >&2
    exit 1
fi
rpm_version=${version/-canary./~canary.}
publish_dir=$(cd -- "${3:-$script_dir/bin/Release/net10.0/$runtime/publish}" && pwd)
test -x "$publish_dir/Everywhere"
test -x "$publish_dir/Everywhere.Watchdog"
package_dir=$(mktemp -d)
trap 'rm -rf "$package_dir"' EXIT
install -d "$package_dir/payload/opt/Everywhere" "$package_dir/payload/usr/bin"
cp -a "$publish_dir/." "$package_dir/payload/opt/Everywhere/"
ln -s /opt/Everywhere/Everywhere "$package_dir/payload/usr/bin/Everywhere"
install -Dm644 "$script_dir/../../img/Everywhere-icon.png" "$package_dir/payload/usr/share/icons/hicolor/256x256/apps/Everywhere.png"
install -Dm644 "$script_dir/../../LICENSE" "$package_dir/payload/usr/share/licenses/everywhere/LICENSE"
install -d "$package_dir/payload/usr/share/applications"
cat > "$package_dir/payload/usr/share/applications/com.Sylinko.Everywhere.desktop" <<'EOF'
[Desktop Entry]
Name=Everywhere
Comment=A context-aware AI assistant for your desktop.
Exec=/usr/bin/Everywhere %U
Icon=Everywhere
Type=Application
StartupWMClass=Everywhere
Terminal=false
Categories=Utility;
Keywords=AI;tool;
EOF
desktop-file-validate "$package_dir/payload/usr/share/applications/com.Sylinko.Everywhere.desktop"
cat > "$package_dir/everywhere.spec" <<EOF
Name: everywhere
Version: $rpm_version
Release: 1
Summary: Context-aware AI assistant for your desktop
License: BUSL-1.1
URL: https://everywhere.sylinko.com
# Dependencies are explicit because the self-contained .NET payload includes
# optional native backends that must not become unconditional requirements.
AutoReqProv: no
Requires: glibc, libgcc, libstdc++, zlib, openssl-libs, libicu
Requires: fontconfig, libX11, libICE, libSM, libXext, libxcb
Requires: libXrandr, libXrender, libXi, libXcursor, libwayland-client.so.0()(64bit), libxkbcommon
Requires: glib2, at-spi2-core, xdg-utils, desktop-file-utils
Recommends: libnotify

%description
A context-aware AI assistant for your desktop.

%install
mkdir -p "%{buildroot}"
cp -a "$package_dir/payload/." "%{buildroot}/"

%files
/opt/Everywhere
/usr/bin/Everywhere
/usr/share/applications/com.Sylinko.Everywhere.desktop
/usr/share/icons/hicolor/256x256/apps/Everywhere.png
%license /usr/share/licenses/everywhere/LICENSE
EOF
# Preserve the already-published .NET binaries, including executable metadata.
rpmbuild -bb --target "$rpm_arch" \
    --define "_topdir $package_dir" \
    --define '_build_id_links none' \
    --define '__os_install_post %{nil}' \
    "$package_dir/everywhere.spec"
cp "$package_dir/RPMS/$rpm_arch/everywhere-$rpm_version-1.$rpm_arch.rpm" \
    "$workspace/Everywhere-Linux-${asset_arch}-v${version}.rpm"
