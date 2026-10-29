#!/bin/bash
set -euo pipefail

if (( $# < 5 || $# > 6 )); then
    echo 'Usage: build-appimage.sh RID ASSET_NAME PUBLISH_DIR BUILD_PROJECT VERSION [SEMANTIC_VERSION]' >&2
    exit 1
fi
SCRIPT_DIR=$(cd -- "$(dirname -- "$0")" && pwd)
PROJECT_ROOT=$(cd -- "$SCRIPT_DIR/../.." && pwd)
cd "$PROJECT_ROOT"

RUNTIME_ARCH=$1
ASSET_NAME=$2
PUBLISH_DIR=$3
BUILD_PROJECT=$4
VERSION=$5
SEMANTIC_VERSION=${6:-$VERSION}

case $RUNTIME_ARCH in
    "linux-x64")   ARCH="x86_64" ;;
    "linux-arm64") ARCH="aarch64" ;;
    *)             echo "Unsupported runtime: $RUNTIME_ARCH" >&2; exit 1 ;;
esac

APP_NAME="Everywhere"
APP_ID="com.Sylinko.Everywhere"
APPDIR="Everywhere.AppDir"
OUTPUT_DIR="./Releases"
OUTPUT_APPIMAGE="Everywhere-${ASSET_NAME}-v${VERSION}.AppImage"


# if --build-project is specified, build the project first
if [[ "$BUILD_PROJECT" == "true" ]]; then
    if [ -z "$VERSION" ]; then
        echo "Version not specified. Defaulting to \"1.0.0.0\"."
        VERSION="1.0.0.0"
    fi
    if [ -z "$SEMANTIC_VERSION" ]; then
        SEMANTIC_VERSION="$VERSION"
    fi
    VERSION_PREFIX="${VERSION%%-*}"
    echo "Building project for runtime '$RUNTIME_ARCH'..."
    dotnet clean Everywhere.Linux.slnx -c Release
    dotnet restore Everywhere.Linux.slnx -r "$RUNTIME_ARCH"
    dotnet publish src/Everywhere.Linux/Everywhere.Linux.csproj -c Release -r "$RUNTIME_ARCH" -o "$PUBLISH_DIR" /p:Version="$VERSION_PREFIX" /p:SemanticVersion="$SEMANTIC_VERSION" --self-contained true --no-restore
fi

if [ ! -d "$PUBLISH_DIR" ]; then
    echo "Publish directory '$PUBLISH_DIR' does not exist."
    exit 1
fi

echo "==== Starting AppImage Build for $ARCH ===="
test -x "$PUBLISH_DIR/Everywhere"
test -x "$PUBLISH_DIR/Everywhere.Watchdog"

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/lib/$APP_NAME"

cp -r "$PUBLISH_DIR"/* "$APPDIR/usr/lib/$APP_NAME/"

mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp img/Everywhere-icon.png "$APPDIR/usr/share/icons/hicolor/256x256/apps/$APP_ID.png"
ln -s "usr/share/icons/hicolor/256x256/apps/$APP_ID.png" "$APPDIR/$APP_ID.png"
ln -s "usr/share/icons/hicolor/256x256/apps/$APP_ID.png" "$APPDIR/.DirIcon"

cat > "$APPDIR/$APP_ID.desktop" <<EOF
[Desktop Entry]
Name=$APP_NAME
Exec=$APP_NAME
Icon=$APP_ID
Type=Application
StartupWMClass=Everywhere
Categories=Utility;
EOF
ln -s "$APP_ID.desktop" "$APPDIR/Everywhere.desktop"

cat > "$APPDIR/AppRun" <<EOF
#!/bin/sh
SELF=\$(readlink -f "\$0")
HERE=\${SELF%/*}
export PATH="\$HERE/usr/bin:\$PATH"
export LD_LIBRARY_PATH="\$HERE/usr/lib/$APP_NAME:\${LD_LIBRARY_PATH:-}"
exec "\$HERE/usr/lib/$APP_NAME/$APP_NAME" "\$@"
EOF
chmod +x "$APPDIR/AppRun"

echo "Creating Squashfs filesystem..."
rm -f root.squashfs
mksquashfs "$APPDIR" root.squashfs -root-owned -noappend -comp zstd

RUNTIME_URL="https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-${ARCH}"
RUNTIME_FILE="runtime-${ARCH}"

if [ ! -f "$RUNTIME_FILE" ]; then
    echo "Downloading runtime from $RUNTIME_URL..."
    curl --fail --location --retry 3 "$RUNTIME_URL" -o "$RUNTIME_FILE"
fi
test -s "$RUNTIME_FILE"
chmod +x "$RUNTIME_FILE"

mkdir -p "$OUTPUT_DIR"
cat "$RUNTIME_FILE" root.squashfs > "$OUTPUT_DIR/$OUTPUT_APPIMAGE"
chmod +x "$OUTPUT_DIR/$OUTPUT_APPIMAGE"

rm root.squashfs
rm -rf "$APPDIR"
rm "$RUNTIME_FILE"

echo -e "\033[0;32mAppImage created at $OUTPUT_DIR/$OUTPUT_APPIMAGE\033[0m"
