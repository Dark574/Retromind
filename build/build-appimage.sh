#!/bin/sh
set -eu

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="$PROJECT_ROOT/build"
OUT_DIR="$PROJECT_ROOT/dist"
WORK_DIR="$PROJECT_ROOT/.build-work"
APPDIR="$WORK_DIR/AppDir"
BUILDER_IMAGE="retromind-appimage-builder:bookworm"

# Keep the packaging tool and embedded runtime reproducible. The type2-runtime
# project currently publishes only a mutable "continuous" release, so its
# commit and checksum are pinned here. A changed upstream asset must therefore
# be reviewed and updated explicitly instead of silently entering a release.
APPIMAGETOOL_VERSION="1.9.1"
APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-x86_64.AppImage"
APPIMAGETOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
APPIMAGE_RUNTIME_COMMIT="75849dce7cc37e4319b633df1f116ca895c71a12"
APPIMAGE_RUNTIME_URL="https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64"
APPIMAGE_RUNTIME_SHA256="1cc49bcf1e2ccd593c379adb17c9f85a36d619088296504de95b1d06215aebbf"

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

for helper in sidplayfp ffplay secret-tool; do
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
Categories=Utility;
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

echo "[6/8] Download verified AppImage packaging tools..."
APPIMAGETOOL="$WORK_DIR/appimagetool"
APPIMAGE_RUNTIME="$WORK_DIR/runtime-x86_64"

is_elf_file() {
  [ -f "$1" ] || return 1
  magic="$(head -c 4 "$1" | od -An -tx1 | tr -d ' \n')"
  [ "$magic" = "7f454c46" ]
}

download_verified_elf() {
  artifact_name="$1"
  artifact_url="$2"
  expected_sha256="$3"
  artifact_destination="$4"
  artifact_tmp="$artifact_destination.tmp"

  rm -f "$artifact_tmp"
  echo "Downloading $artifact_name from: $artifact_url"
  if ! curl --fail --location \
      --retry 5 --retry-delay 2 --retry-connrefused --retry-all-errors \
      -o "$artifact_tmp" "$artifact_url"; then
    echo "ERROR: Download failed for $artifact_url"
    rm -f "$artifact_tmp"
    return 1
  fi

  if ! is_elf_file "$artifact_tmp"; then
    echo "ERROR: Downloaded $artifact_name is not a valid ELF binary."
    if head -c 120 "$artifact_tmp" | tr -d '\000' | grep -Eiq "<html|<body|gateway|error"; then
      echo "Hint: server returned an HTML error response. Please retry in a few minutes."
    fi
    rm -f "$artifact_tmp"
    return 1
  fi

  actual_sha256="$(sha256sum "$artifact_tmp" | awk '{print $1}')"
  if [ "$actual_sha256" != "$expected_sha256" ]; then
    echo "ERROR: SHA-256 verification failed for $artifact_name."
    echo "       Expected: $expected_sha256"
    echo "       Actual:   $actual_sha256"
    rm -f "$artifact_tmp"
    return 1
  fi

  mv "$artifact_tmp" "$artifact_destination"
  chmod +x "$artifact_destination"
  return 0
}

if ! command -v sha256sum >/dev/null 2>&1; then
  echo "ERROR: sha256sum is required to verify AppImage packaging downloads."
  exit 1
fi

if ! download_verified_elf \
    "appimagetool $APPIMAGETOOL_VERSION" \
    "$APPIMAGETOOL_URL" \
    "$APPIMAGETOOL_SHA256" \
    "$APPIMAGETOOL"; then
  exit 1
fi

if ! download_verified_elf \
    "AppImage type2 runtime ($APPIMAGE_RUNTIME_COMMIT)" \
    "$APPIMAGE_RUNTIME_URL" \
    "$APPIMAGE_RUNTIME_SHA256" \
    "$APPIMAGE_RUNTIME"; then
  echo "Hint: the upstream continuous runtime may have changed; review and update the pinned commit and checksum together."
  exit 1
fi

echo "[7/8] Debug: listing desktop files..."
find "$APPDIR" -maxdepth 4 -type f -name "*.desktop" -print

echo "[8/8] Build AppImage..."
cd "$WORK_DIR"
# Extract-and-run keeps the packaging step independent of host FUSE support.
# The generated Retromind AppImage still uses the explicitly pinned static runtime.
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 \
  "$APPIMAGETOOL" --runtime-file "$APPIMAGE_RUNTIME" \
  "$APPDIR" "$OUT_DIR/Retromind-x86_64.AppImage"

echo "Done: $OUT_DIR/Retromind-x86_64.AppImage"
echo "Run it with: $OUT_DIR/Retromind-x86_64.AppImage"
