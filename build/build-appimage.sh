#!/bin/sh
set -eu

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="$PROJECT_ROOT/build"
OUT_DIR="$PROJECT_ROOT/dist"
WORK_DIR="$PROJECT_ROOT/.build-work"
APPDIR="$WORK_DIR/AppDir"
BUILDER_IMAGE="retromind-appimage-builder:bookworm"

echo "[1/8] Prepare folders..."
rm -rf "$WORK_DIR"
mkdir -p "$OUT_DIR" "$WORK_DIR"

echo "[2/8] Build Debian Bookworm appimage builder image..."
docker build -f "$BUILD_DIR/Dockerfile.appimage" -t "$BUILDER_IMAGE" "$PROJECT_ROOT"

echo "[3/8] Export publish output + runtime bundles from container..."
CID="$(docker create "$BUILDER_IMAGE")"
# Ensure the container always gets removed, even on failure.
cleanup_container() {
  if [ -n "${CID:-}" ]; then
    docker rm "$CID" >/dev/null 2>&1 || true
  fi
}
trap cleanup_container EXIT

docker cp "$CID:/out/publish" "$WORK_DIR/publish"
docker cp "$CID:/out/vlc" "$WORK_DIR/vlc"
docker cp "$CID:/out/tools" "$WORK_DIR/tools"
docker cp "$CID:/out/runtime-libs" "$WORK_DIR/runtime-libs"
cleanup_container
trap - EXIT

if [ ! -f "$WORK_DIR/publish/Retromind" ]; then
  echo "ERROR: Publish output not found at '$WORK_DIR/publish/Retromind'."
  echo "       Check Dockerfile.appimage and the docker cp step."
  exit 1
fi

if [ ! -d "$WORK_DIR/vlc" ]; then
  echo "ERROR: VLC export directory '$WORK_DIR/vlc' not found."
  echo "       Check Dockerfile.appimage and the docker cp step."
  exit 1
fi

echo "[4/8] Build AppDir layout..."
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/lib/vlc" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/metainfo"

cp "$BUILD_DIR/AppRun" "$APPDIR/AppRun"
chmod +x "$APPDIR/AppRun"

cp -a "$WORK_DIR/publish/." "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/Retromind"

for helper in sidplayfp ffplay; do
  if [ -f "$WORK_DIR/tools/bin/$helper" ]; then
    echo "Bundling $helper from Debian bookworm builder image."
    cp "$WORK_DIR/tools/bin/$helper" "$APPDIR/usr/bin/$helper"
    chmod +x "$APPDIR/usr/bin/$helper"
  else
    echo "Notice: $helper not found in builder output (feature may be unavailable in AppImage)."
  fi
done

if [ -d "$WORK_DIR/tools/lib" ]; then
  cp -a "$WORK_DIR/tools/lib/." "$APPDIR/usr/lib/" || true
fi

cp -a "$WORK_DIR/vlc/vlc" "$APPDIR/usr/lib/vlc/"
cp -a "$WORK_DIR/vlc/lib" "$APPDIR/usr/lib/vlc/"

if [ -d "$WORK_DIR/runtime-libs" ]; then
  cp -a "$WORK_DIR/runtime-libs/." "$APPDIR/usr/lib/" || true
fi

echo "[5/8] Copy themes and app metadata..."
THEMES_SOURCE_DIR="$PROJECT_ROOT/Themes"
if [ ! -d "$THEMES_SOURCE_DIR" ]; then
  echo "ERROR: Themes source directory not found at '$THEMES_SOURCE_DIR'."
  exit 1
fi

echo "Copying themes from '$THEMES_SOURCE_DIR' into AppDir..."
cp -a "$THEMES_SOURCE_DIR" "$APPDIR/usr/bin/"

THEME_FILE_COUNT="$(find "$APPDIR/usr/bin/Themes" -type f | wc -l | awk '{print $1}')"
if [ "$THEME_FILE_COUNT" -eq 0 ]; then
  echo "ERROR: No theme files were copied into AppDir (expected files under '$APPDIR/usr/bin/Themes')."
  exit 1
fi

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

echo "[6/8] Download appimagetool (if missing)..."
APPIMAGETOOL="$WORK_DIR/appimagetool"
if [ ! -x "$APPIMAGETOOL" ]; then
  curl -L -o "$APPIMAGETOOL" "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
  chmod +x "$APPIMAGETOOL"
fi

echo "[7/8] Debug: listing desktop files..."
find "$APPDIR" -maxdepth 4 -type f -name "*.desktop" -print

echo "[8/8] Build AppImage..."
cd "$WORK_DIR"
"$APPIMAGETOOL" "$APPDIR" "$OUT_DIR/Retromind-x86_64.AppImage"

echo "Done: $OUT_DIR/Retromind-x86_64.AppImage"
echo "Run it with: $OUT_DIR/Retromind-x86_64.AppImage"
