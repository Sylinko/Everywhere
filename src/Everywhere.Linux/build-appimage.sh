#!/bin/bash
set -e

RUNTIME_ARCH=$1
ASSET_NAME=$2
PUBLISH_DIR=$3
BUILD_PROJECT=$4
VERSION=$5

case $RUNTIME_ARCH in
    "linux-x64")   ARCH="x86_64" ;;
    "linux-arm64") ARCH="aarch64" ;;
    *)             ARCH="x86_64" ;;
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
    echo "Building project for runtime '$RUNTIME_ARCH'..."
    dotnet clean Everywhere.Linux.slnx -c Release
    dotnet restore Everywhere.Linux.slnx
    dotnet publish src/Everywhere.Linux/Everywhere.Linux.csproj -c Release -r "$RUNTIME_ARCH" -o "$PUBLISH_DIR" /p:Version="$VERSION" --self-contained true --no-restore 
fi

if [ ! -d "$PUBLISH_DIR" ]; then
    echo "Publish directory '$PUBLISH_DIR' does not exist."
    exit 1
fi

echo "==== Starting AppImage Build for $ARCH ===="

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
Categories=Utility;
EOF
ln -s "$APP_ID.desktop" "$APPDIR/Everywhere.desktop"

cat > "$APPDIR/AppRun" <<EOF
#!/bin/sh
SELF=\$(readlink -f "\$0")
HERE=\${SELF%/*}
export PATH="\$HERE/usr/bin:\$PATH"
export LD_LIBRARY_PATH="\$HERE/usr/lib/$APP_NAME:\$LD_LIBRARY_PATH"
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
    curl -L "$RUNTIME_URL" -o "$RUNTIME_FILE"
fi
chmod +x "$RUNTIME_FILE"

mkdir -p "$OUTPUT_DIR"
cat "$RUNTIME_FILE" root.squashfs > "$OUTPUT_DIR/$OUTPUT_APPIMAGE"
chmod +x "$OUTPUT_DIR/$OUTPUT_APPIMAGE"

rm root.squashfs
rm -rf "$APPDIR"
rm "$RUNTIME_FILE"

echo -e "\033[0;32mAppImage created at $OUTPUT_DIR/$OUTPUT_APPIMAGE\033[0m"