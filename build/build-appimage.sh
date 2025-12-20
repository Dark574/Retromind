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

echo "[3/7] Build VLC export container image..."
docker build -f "$BUILD_DIR/Dockerfile.vlc" -t retromind-vlc-export:stable-slim "$PROJECT_ROOT/build"

echo "[4/7] Export VLC libs/plugins from container..."
CID="$(docker create retromind-vlc-export:stable-slim)"
docker cp "$CID:/out/vlc" "$WORK_DIR/vlc"
docker rm "$CID" >/dev/null

echo "[5/7] Build AppDir layout..."
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/lib/vlc" "$APPDIR/usr/share/applications"

cp "$BUILD_DIR/AppRun" "$APPDIR/AppRun"
chmod +x "$APPDIR/AppRun"

cp "$PUBLISH_DIR/Retromind" "$APPDIR/usr/bin/Retromind"
chmod +x "$APPDIR/usr/bin/Retromind"

cp -a "$WORK_DIR/vlc/vlc" "$APPDIR/usr/lib/vlc/"
cp -a "$WORK_DIR/vlc/lib" "$APPDIR/usr/lib/vlc/"

# --- Desktop entry (write to standard location AND as root fallback) ---
cat > "$APPDIR/usr/share/applications/retromind.desktop" << 'EOF'
[Desktop Entry]
Type=Application
Name=Retromind
Exec=Retromind
Icon=retromind
Categories=AudioVideo;Video;Audio;Utility;
Terminal=false
EOF

cp "$APPDIR/usr/share/applications/retromind.desktop" "$APPDIR/retromind.desktop"

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