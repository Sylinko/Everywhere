#! /usr/bin/sh
if [ "$#" -lt 2 ]; then
    echo "More args required:"
    echo "ARCH: same with Runtime ID. e.g. linux-x64"
    echo "VERSION: package version"
    echo "Usage: packaging_deb.sh ARCH VERSION [BIN_DIR]"
    exit 1
fi 

WORKSPACE=$(pwd)
SHELLFOLDER=$(cd "$(dirname "$0")";pwd)
cd $WORKSPACE

BINARCH="$1"
VERSION="$2"
BINDIR="$SHELLFOLDER/bin/Release/net10.0/$BINARCH"
if [ -z "$3" ]; then
    echo "Using default bin path: $BINDIR"
else
    BINDIR="$3"
fi
PACKAGINGPATH="/tmp/Everywhere"
INSTALLPATH="/opt/Everywhere"
PROJECTROOT="$SHELLFOLDER/../.."
ARCHSUFFIX="${BINARCH#*-}"

if [ -x "$(command -v "dpkg-deb")" ]; then
    echo "Packaging .deb for $BINARCH ..."
else      
    echo "dpkg-deb not found. Please install 'dpkg-dev' with apt in advance."
    exit 1
fi

rid_to_deb_arch() {
    case "$1" in
        linux-x64)     echo "amd64" ;;
        linux-arm64)   echo "arm64" ;;
        *)
            echo "Unsupported RID suffix: $1" >&2
            return 1
            ;;
    esac
}

DEBARCH=$(rid_to_deb_arch $BINARCH) || { echo "Invalid arch"; exit 1; }
rm -rf "$PACKAGINGPATH"
mkdir -p "$PACKAGINGPATH/DEBIAN"
mkdir -p "$PACKAGINGPATH$INSTALLPATH"
cp -r "$BINDIR"/* "$PACKAGINGPATH$INSTALLPATH"
cp "$SHELLFOLDER/../../img/Everywhere.ico" "$PACKAGINGPATH$INSTALLPATH"
cd "$PACKAGINGPATH"
cat > "$PACKAGINGPATH$INSTALLPATH/Everywhere.desktop" <<EOF
[Desktop Entry]
Name=Everywhere
Comment=A context-aware AI assistant for your desktop.
Exec=/usr/bin/Everywhere
X-KDE-StartupNotify=true
Icon=$INSTALLPATH/Everywhere.ico
Type=Application
Terminal=false
Categories=Utility
Keywords=AI;tool;
EOF

cat > DEBIAN/control <<EOF
Package: Everywhere
Version: $VERSION
Architecture: $DEBARCH
Maintainer: Sylinko 
Description: Everywhere
Depends: libc6,libx11-6,libglib2.0-0,libatspi2.0-0
Section: utils
Priority: optional
Homepage: https://everywhere.sylinko.com
EOF

cat > DEBIAN/postinst <<EOF
#!/bin/sh
set -e

# Create symlink
ln -sf "$INSTALLPATH/Everywhere" /usr/bin/Everywhere
desktop-file-install "$INSTALLPATH/Everywhere.desktop"

exit 0
EOF

chmod +x DEBIAN/postinst

cat > DEBIAN/prerm <<EOF
#!/bin/sh
set -e

rm -f $HOME/.config/systemd/user/graphical-session.target.wants/Everywhere.service
rm -f $HOME/.config/systemd/user/Everywhere.service
# Remove symlink on uninstall
rm -f /usr/bin/Everywhere

exit 0
EOF

chmod +x DEBIAN/prerm

dpkg-deb --build "$PACKAGINGPATH/" "$WORKSPACE/Everywhere-Linux-$ARCHSUFFIX-v$VERSION.deb" 
