#!/bin/sh
set -eu

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="$PROJECT_ROOT/build"
OUT_DIR="$PROJECT_ROOT/dist"
WORK_DIR="$PROJECT_ROOT/.build-work"

PUBLISH_DIR="$PROJECT_ROOT/bin/Release/net10.0/linux-x64/publish"
APPDIR="$WORK_DIR/AppDir"

echo "[1/7] Prepare folders..."
rm -rf "$WORK_DIR"
mkdir -p "$OUT_DIR" "$WORK_DIR"

echo "[2/7] Publish self-contained (linux-x64)..."
cd "$PROJECT_ROOT"
dotnet publish Retromind.csproj -c Release -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false

if [ ! -f "$PUBLISH_DIR/Retromind" ]; then
  echo "ERROR: Publish output not found at '$PUBLISH_DIR/Retromind'."
  echo "       Check the dotnet publish step above for errors."
  exit 1
fi

echo "[3/7] Build VLC export container image..."
docker build -f "$BUILD_DIR/Dockerfile.vlc" -t retromind-vlc-export:stable-slim "$PROJECT_ROOT/build"

echo "[4/7] Export VLC libs/plugins from container..."
CID="$(docker create retromind-vlc-export:stable-slim)"
# Ensure the container always gets removed, even on failure.
cleanup_container() {
  if [ -n "${CID:-}" ]; then
    docker rm "$CID" >/dev/null 2>&1 || true
  fi
}
trap cleanup_container EXIT

docker cp "$CID:/out/vlc" "$WORK_DIR/vlc"
cleanup_container
trap - EXIT

if [ ! -d "$WORK_DIR/vlc" ]; then
  echo "ERROR: VLC export directory '$WORK_DIR/vlc' not found."
  echo "       Check Dockerfile.vlc and the docker cp step."
  exit 1
fi

echo "[5/7] Build AppDir layout..."
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/lib/vlc" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/metainfo"

cp "$BUILD_DIR/AppRun" "$APPDIR/AppRun"
chmod +x "$APPDIR/AppRun"

cp "$PUBLISH_DIR/Retromind" "$APPDIR/usr/bin/Retromind"
chmod +x "$APPDIR/usr/bin/Retromind"

# Try to bundle sidplayfp (SID player) for SID music playback.
# If sidplayfp is not available on the build host, we just log a notice and continue.
if command -v sidplayfp >/dev/null 2>&1; then
  SIDPLAY_PATH="$(command -v sidplayfp)"
  echo "Bundling sidplayfp from: $SIDPLAY_PATH"
  cp "$SIDPLAY_PATH" "$APPDIR/usr/bin/sidplayfp"
  chmod +x "$APPDIR/usr/bin/sidplayfp"
else
  echo "Notice: sidplayfp not found on build host (no SID playback in AppImage)."
fi

# Copy themes so that the AppImage has the same layout as the publish folder.
if [ -d "$PUBLISH_DIR/Themes" ]; then
  echo "Copying themes into AppDir..."
  cp -a "$PUBLISH_DIR/Themes" "$APPDIR/usr/bin/"
else
  echo "Notice: No Themes directory found in publish output (expected at '$PUBLISH_DIR/Themes')."
fi

cp -a "$WORK_DIR/vlc/vlc" "$APPDIR/usr/lib/vlc/"
cp -a "$WORK_DIR/vlc/lib" "$APPDIR/usr/lib/vlc/"

DESKTOP_FILE_NAME="io.github.dark574.Retromind.desktop"

# --- Desktop entry (write to standard location AND as root fallback) ---
cat > "$APPDIR/usr/share/applications/$DESKTOP_FILE_NAME" << 'EOF'
[Desktop Entry]
Type=Application
Name=Retromind
Exec=Retromind
Icon=retromind
Categories=AudioVideo;Video;Audio;Utility;
Terminal=false
EOF

cp "$APPDIR/usr/share/applications/$DESKTOP_FILE_NAME" "$APPDIR/$DESKTOP_FILE_NAME"

# --- AppStream metadata (AppImage warning fix) ---
APPSTREAM_META="$BUILD_DIR/io.github.dark574.Retromind.appdata.xml"
if [ -f "$APPSTREAM_META" ]; then
  cp "$APPSTREAM_META" "$APPDIR/usr/share/metainfo/io.github.dark574.Retromind.appdata.xml"
else
  echo "Notice: AppStream metadata missing at '$APPSTREAM_META'."
fi

# --- Icon (ensure Icon=retromind resolves) ---
cp "$BUILD_DIR/retromind.svg" "$APPDIR/retromind.svg"

# --- License and notice files ---
DOC_DIR="$APPDIR/usr/share/doc/retromind"
mkdir -p "$DOC_DIR"

# Main project licenses
if [ -f "$PROJECT_ROOT/COPYING" ]; then
  cp "$PROJECT_ROOT/COPYING" "$DOC_DIR/"
fi

if [ -f "$PROJECT_ROOT/THIRD-PARTY-NOTICES.md" ]; then
  cp "$PROJECT_ROOT/THIRD-PARTY-NOTICES.md" "$DOC_DIR/"
fi

# Third-party licenses (MIT/LGPL/GPL etc.)
if [ -d "$PROJECT_ROOT/Licenses" ]; then
  mkdir -p "$DOC_DIR/Licenses"
  cp -r "$PROJECT_ROOT/Licenses/." "$DOC_DIR/Licenses/"
fi

echo "[6/7] Download appimagetool (if missing)..."
APPIMAGETOOL="$WORK_DIR/appimagetool"
if [ ! -x "$APPIMAGETOOL" ]; then
  curl -L -o "$APPIMAGETOOL" "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
  chmod +x "$APPIMAGETOOL"
fi

echo "Debug: listing desktop files..."
find "$APPDIR" -maxdepth 4 -type f -name "*.desktop" -print

echo "[7/7] Build AppImage..."
cd "$WORK_DIR"
"$APPIMAGETOOL" "$APPDIR" "$OUT_DIR/Retromind-x86_64.AppImage"

echo "Done: $OUT_DIR/Retromind-x86_64.AppImage"
echo "Run it with: $OUT_DIR/Retromind-x86_64.AppImage"
