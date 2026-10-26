#!/bin/sh
set -eu

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="$PROJECT_ROOT/build"
OUT_DIR="$PROJECT_ROOT/dist"
WORK_DIR="$PROJECT_ROOT/.build-work"
APPDIR="$WORK_DIR/AppDir"
LEGENDARY_VERSION_FILE="$BUILD_DIR/legendary-version.env"
COMET_VERSION_FILE="$BUILD_DIR/comet-version.env"
ENVIRONMENT_FILE="$BUILD_DIR/appimage-environment.env"
BUILDER_IMAGE="retromind-appimage-builder:bookworm"
BUILDX_BUILDER="${RETROMIND_BUILDX_BUILDER:-retromind-appimage-v2}"
BUILDX_CACHE_LIMIT="${RETROMIND_BUILDX_CACHE_LIMIT:-20gb}"
BUILDKIT_CONFIG_FILE="$BUILD_DIR/buildkitd.appimage.toml"
BUILDER_IMAGE_LABEL="org.retromind.appimage-builder=true"
LOCAL_BUILD_SECRETS_DIR="$PROJECT_ROOT/.build-secrets"
CID=""
BUILDX_CLEANUP_ENABLED=0

if [ ! -f "$LEGENDARY_VERSION_FILE" ]; then
  echo "ERROR: Legendary version file not found at '$LEGENDARY_VERSION_FILE'."
  exit 1
fi

if [ ! -f "$COMET_VERSION_FILE" ]; then
  echo "ERROR: Comet version file not found at '$COMET_VERSION_FILE'."
  exit 1
fi

if [ ! -f "$ENVIRONMENT_FILE" ]; then
  echo "ERROR: AppImage environment lock not found at '$ENVIRONMENT_FILE'."
  exit 1
fi

if [ ! -f "$BUILDKIT_CONFIG_FILE" ]; then
  echo "ERROR: BuildKit configuration not found at '$BUILDKIT_CONFIG_FILE'."
  exit 1
fi

. "$LEGENDARY_VERSION_FILE"
. "$COMET_VERSION_FILE"
. "$ENVIRONMENT_FILE"

if [ -z "${LEGENDARY_VERSION:-}" ]; then
  echo "ERROR: LEGENDARY_VERSION is missing from '$LEGENDARY_VERSION_FILE'."
  exit 1
fi

if [ -z "${COMET_VERSION:-}" ] || [ -z "${COMET_RUST_IMAGE:-}" ]; then
  echo "ERROR: COMET_VERSION or COMET_RUST_IMAGE is missing from '$COMET_VERSION_FILE'."
  exit 1
fi

if ! printf '%s' "$COMET_RUST_IMAGE" | grep -Eq '^rust@sha256:[0-9a-f]{64}$'; then
  echo "ERROR: COMET_RUST_IMAGE must pin an exact rust image digest."
  exit 1
fi

if ! printf '%s' "${DEBIAN_BOOKWORM_IMAGE:-}" | grep -Eq '^debian:bookworm@sha256:[0-9a-f]{64}$' ||
   ! printf '%s' "${DEBIAN_SNAPSHOT_MAIN:-}" | grep -Eq '^[0-9]{8}T[0-9]{6}Z$' ||
   ! printf '%s' "${DEBIAN_SNAPSHOT_SECURITY:-}" | grep -Eq '^[0-9]{8}T[0-9]{6}Z$' ||
   ! printf '%s' "${DOTNET_SDK_VERSION:-}" | grep -Eq '^10\.[0-9]+\.[0-9]+$' ||
   ! printf '%s' "${DOTNET_RUNTIME_VERSION:-}" | grep -Eq '^10\.0\.[0-9]+$' ||
   ! printf '%s' "${DOTNET_SDK_LINUX_X64_URL:-}" | grep -Eq '^https://[^[:space:]]+$' ||
   ! printf '%s' "${DOTNET_SDK_LINUX_X64_SHA512:-}" | grep -Eq '^[0-9a-f]{128}$'; then
  echo "ERROR: '$ENVIRONMENT_FILE' contains missing or invalid environment pins."
  exit 1
fi

load_local_build_secret() {
  secret_name="$1"

  case "$secret_name" in
    RETROMIND_*[!A-Z0-9_]*|*[!A-Z0-9_]*|RETROMIND_)
      echo "WARNING: Ignoring invalid local build-secret filename '$secret_name'."
      return
      ;;
  esac

  if secret_value="$(printenv "$secret_name" 2>/dev/null)" && [ -n "$secret_value" ]; then
    return
  fi

  secret_file="$LOCAL_BUILD_SECRETS_DIR/$secret_name"
  if [ ! -f "$secret_file" ]; then
    return
  fi

  secret_value="$(cat "$secret_file")"
  if [ -z "$secret_value" ]; then
    echo "WARNING: Local build secret '$secret_name' is empty."
    return
  fi

  export "$secret_name=$secret_value"
}

if [ -d "$LOCAL_BUILD_SECRETS_DIR" ]; then
  for secret_file in "$LOCAL_BUILD_SECRETS_DIR"/RETROMIND_*; do
    [ -f "$secret_file" ] || continue
    load_local_build_secret "$(basename "$secret_file")"
  done
fi

cleanup_container() {
  if [ -n "${CID:-}" ]; then
    docker rm "$CID" >/dev/null 2>&1 || true
    CID=""
  fi
}

cleanup_on_exit() {
  cleanup_status=$?
  trap - EXIT

  cleanup_container

  if [ "$BUILDX_CLEANUP_ENABLED" -eq 1 ]; then
    echo "Limit Retromind Docker build cache to $BUILDX_CACHE_LIMIT..."
    # Cargo's named cache mounts avoid downloading and recompiling every Comet
    # dependency whenever cache pressure removes the intermediate builder stage.
    if ! docker buildx prune \
        --builder "$BUILDX_BUILDER" \
        --force \
        --filter "type!=exec.cachemount" \
        --max-used-space "$BUILDX_CACHE_LIMIT"; then
      echo "WARNING: Could not prune the Retromind Docker build cache."
    fi
  fi

  exit "$cleanup_status"
}

trap cleanup_on_exit EXIT

# Keep the packaging tool and embedded runtime reproducible. The type2-runtime
# project currently publishes only a mutable "continuous" release, so its
# commit and checksum are pinned here. A changed upstream asset must therefore
# be reviewed and updated explicitly instead of silently entering a release.
APPIMAGETOOL_VERSION="1.9.1"
APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-x86_64.AppImage"
APPIMAGETOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
APPIMAGE_RUNTIME_COMMIT="fedfad51acc2860b2916a1eef32ba4a500ff31ef"
APPIMAGE_RUNTIME_URL="https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64"
APPIMAGE_RUNTIME_SHA256="617aa77314d57c8b8e4fae9c3f0b35af7b30d5529cb69e98f64f92510ed98a46"

RETROMIND_VERSION="$(
  sed -n 's:.*<InformationalVersion>\([^<]*\)</InformationalVersion>.*:\1:p' \
    "$PROJECT_ROOT/Retromind.csproj" | head -n 1
)"
if [ -z "$RETROMIND_VERSION" ] || ! printf '%s' "$RETROMIND_VERSION" | grep -Eq '^[0-9A-Za-z][0-9A-Za-z._-]*$'; then
  echo "ERROR: Could not read a filename-safe InformationalVersion from Retromind.csproj."
  exit 1
fi

case "$RETROMIND_VERSION" in
  *-*) APPIMAGE_UPDATE_CHANNEL="latest-all" ;;
  *)   APPIMAGE_UPDATE_CHANNEL="latest" ;;
esac

APPIMAGE_NAME="Retromind-$RETROMIND_VERSION-x86_64.AppImage"
APPIMAGE_PATH="$OUT_DIR/$APPIMAGE_NAME"
MANIFEST_NAME="$APPIMAGE_NAME.manifest.tsv"
MANIFEST_PATH="$OUT_DIR/$MANIFEST_NAME"
ZSYNC_PATH="$APPIMAGE_PATH.zsync"
LEGACY_ZSYNC_NAME="Retromind-$RETROMIND_VERSION-linux-x86_64.AppImage.zsync"
LEGACY_ZSYNC_PATH="$OUT_DIR/$LEGACY_ZSYNC_NAME"
APPIMAGE_UPDATE_INFORMATION="gh-releases-zsync|Dark574|Retromind|$APPIMAGE_UPDATE_CHANNEL|Retromind-*-x86_64.AppImage.zsync"
BUILD_TIMESTAMP_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
GIT_COMMIT="$(git -C "$PROJECT_ROOT" rev-parse HEAD)"
if [ -n "$(git -C "$PROJECT_ROOT" status --porcelain --untracked-files=normal)" ]; then
  GIT_WORKTREE_STATE="dirty"
else
  GIT_WORKTREE_STATE="clean"
fi

echo "[1/8] Prepare folders..."
rm -rf "$WORK_DIR"
mkdir -p "$OUT_DIR" "$WORK_DIR"

echo "[2/8] Build Debian Bookworm appimage builder image..."
BUILDX_CLEANUP_ENABLED=1
PREVIOUS_BUILDER_IMAGE_ID="$(
  docker image inspect --format '{{.Id}}' "$BUILDER_IMAGE" 2>/dev/null || true
)"

if ! docker buildx inspect "$BUILDX_BUILDER" >/dev/null 2>&1; then
  echo "Create isolated Docker builder '$BUILDX_BUILDER'..."
  docker buildx create \
    --name "$BUILDX_BUILDER" \
    --driver docker-container \
    --buildkitd-config "$BUILDKIT_CONFIG_FILE" \
    >/dev/null
fi

docker buildx inspect "$BUILDX_BUILDER" --bootstrap >/dev/null
set -- \
  --builder "$BUILDX_BUILDER" \
  --load \
  -f "$BUILD_DIR/Dockerfile.appimage" \
  -t "$BUILDER_IMAGE" \
  --build-arg "COMET_RUST_IMAGE=$COMET_RUST_IMAGE" \
  --build-arg "DEBIAN_BOOKWORM_IMAGE=$DEBIAN_BOOKWORM_IMAGE" \
  --build-arg "DEBIAN_SNAPSHOT_MAIN=$DEBIAN_SNAPSHOT_MAIN" \
  --build-arg "DEBIAN_SNAPSHOT_SECURITY=$DEBIAN_SNAPSHOT_SECURITY" \
  --build-arg "DOTNET_SDK_VERSION=$DOTNET_SDK_VERSION" \
  --build-arg "DOTNET_RUNTIME_VERSION=$DOTNET_RUNTIME_VERSION" \
  --build-arg "DOTNET_SDK_LINUX_X64_URL=$DOTNET_SDK_LINUX_X64_URL" \
  --build-arg "DOTNET_SDK_LINUX_X64_SHA512=$DOTNET_SDK_LINUX_X64_SHA512"

if [ -n "${RETROMIND_SCREENSCRAPER_DEVELOPER_ID:-}" ] ||
   [ -n "${RETROMIND_SCREENSCRAPER_DEVELOPER_PASSWORD:-}" ]; then
  if [ -z "${RETROMIND_SCREENSCRAPER_DEVELOPER_ID:-}" ] ||
     [ -z "${RETROMIND_SCREENSCRAPER_DEVELOPER_PASSWORD:-}" ]; then
    echo "ERROR: Both ScreenScraper application credential variables must be set."
    exit 1
  fi

  # BuildKit deliberately does not invalidate cached layers when a secret
  # changes. A non-secret per-build value ensures the published assembly is
  # regenerated without deriving any build argument from the credentials.
  SCREENSCRAPER_BUILD_REVISION="$(date -u +%Y%m%d%H%M%S)-$$"
  set -- "$@" \
    --secret id=retromind_screenscraper_developer_id,env=RETROMIND_SCREENSCRAPER_DEVELOPER_ID \
    --secret id=retromind_screenscraper_developer_password,env=RETROMIND_SCREENSCRAPER_DEVELOPER_PASSWORD \
    --build-arg "RETROMIND_SCREENSCRAPER_BUILD_REVISION=$SCREENSCRAPER_BUILD_REVISION"
else
  echo "Notice: ScreenScraper application access is not embedded in this AppImage build."
fi

docker buildx build "$@" "$PROJECT_ROOT"

echo "[3/8] Export publish output + runtime bundles from container..."
CID="$(docker create "$BUILDER_IMAGE")"

docker cp "$CID:/out/publish" "$WORK_DIR/publish"
docker cp "$CID:/out/vlc" "$WORK_DIR/vlc"
docker cp "$CID:/out/tools" "$WORK_DIR/tools"
docker cp "$CID:/out/runtime-libs" "$WORK_DIR/runtime-libs"
docker cp "$CID:/out/legendary-licenses" "$WORK_DIR/legendary-licenses"
docker cp "$CID:/out/comet-licenses" "$WORK_DIR/comet-licenses"
docker cp "$CID:/out/build-environment" "$WORK_DIR/build-environment"
cleanup_container

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

for helper in sidplayfp ffplay secret-tool legendary comet; do
  if [ -f "$WORK_DIR/tools/bin/$helper" ]; then
    echo "Bundling $helper from Debian bookworm builder image."
    cp "$WORK_DIR/tools/bin/$helper" "$APPDIR/usr/bin/$helper"
    chmod +x "$APPDIR/usr/bin/$helper"
  else
    echo "Notice: $helper not found in builder output (feature may be unavailable in AppImage)."
  fi
done

if [ ! -x "$APPDIR/usr/bin/legendary" ]; then
  echo "ERROR: Legendary $LEGENDARY_VERSION was not exported by the builder image."
  exit 1
fi

if [ ! -x "$APPDIR/usr/bin/comet" ]; then
  echo "ERROR: Comet $COMET_VERSION was not exported by the builder image."
  exit 1
fi

comet_magic="$(head -c 4 "$APPDIR/usr/bin/comet" | od -An -tx1 | tr -d ' \n')"
if [ "$comet_magic" != "7f454c46" ]; then
  echo "ERROR: Bundled Comet $COMET_VERSION is not an ELF executable."
  exit 1
fi

if [ "$("$APPDIR/usr/bin/comet" --version)" != "comet $COMET_VERSION" ]; then
  echo "ERROR: Bundled Comet version does not match $COMET_VERSION."
  exit 1
fi

legendary_magic="$(head -c 4 "$APPDIR/usr/bin/legendary" | od -An -tx1 | tr -d ' \n')"
if [ "$legendary_magic" != "7f454c46" ]; then
  echo "ERROR: Bundled Legendary $LEGENDARY_VERSION is not an ELF executable."
  exit 1
fi

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
Categories=Game;Utility;
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

# Exact license material exported with the pinned Legendary/Python build.
if [ -d "$WORK_DIR/legendary-licenses" ]; then
  mkdir -p "$DOC_DIR/Licenses/Legendary-Bundle"
  cp -r "$WORK_DIR/legendary-licenses/." "$DOC_DIR/Licenses/Legendary-Bundle/"
else
  echo "ERROR: Legendary bundle license material is missing from the builder output."
  exit 1
fi

# Exact license material exported with the pinned Comet/protocol build.
if [ -d "$WORK_DIR/comet-licenses" ]; then
  mkdir -p "$DOC_DIR/Licenses/Comet-Bundle"
  cp -r "$WORK_DIR/comet-licenses/." "$DOC_DIR/Licenses/Comet-Bundle/"
else
  echo "ERROR: Comet bundle license material is missing from the builder output."
  exit 1
fi

MANIFEST_INTERNAL_PATH="$DOC_DIR/AppImageBuildManifest.tsv"

manifest_record() {
  manifest_section="$1"
  manifest_name="$2"
  manifest_value="$3"
  manifest_detail="${4:-}"
  printf '%s\t%s\t%s\t%s\n' \
    "$manifest_section" "$manifest_name" "$manifest_value" "$manifest_detail" \
    >> "$MANIFEST_INTERNAL_PATH"
}

echo "Generate AppImage build manifest..."
printf 'section\tname\tvalue\tdetail\n' > "$MANIFEST_INTERNAL_PATH"
manifest_record build retromind-version "$RETROMIND_VERSION"
manifest_record build git-commit "$GIT_COMMIT" "$GIT_WORKTREE_STATE worktree"
manifest_record build timestamp-utc "$BUILD_TIMESTAMP_UTC"
manifest_record environment debian-base-image "$DEBIAN_BOOKWORM_IMAGE" "immutable OCI digest"
manifest_record environment debian-main-snapshot "$DEBIAN_SNAPSHOT_MAIN" "bookworm and bookworm-updates"
manifest_record environment debian-security-snapshot "$DEBIAN_SNAPSHOT_SECURITY" "bookworm-security"
manifest_record environment dotnet-sdk-artifact "$DOTNET_SDK_LINUX_X64_URL" "SHA-512 $DOTNET_SDK_LINUX_X64_SHA512"

if [ ! -s "$WORK_DIR/build-environment/facts.tsv" ] ||
   [ ! -s "$WORK_DIR/build-environment/debian-packages.tsv" ] ||
   [ ! -s "$WORK_DIR/build-environment/elf-dependencies.tsv" ]; then
  echo "ERROR: Builder environment facts required for the manifest are incomplete."
  exit 1
fi

while IFS="$(printf '\t')" read -r fact_name fact_value; do
  [ -n "$fact_name" ] || continue
  manifest_record environment "$fact_name" "$fact_value" "measured in final builder stage"
done < "$WORK_DIR/build-environment/facts.tsv"

while IFS="$(printf '\t')" read -r package_name package_version package_architecture; do
  [ -n "$package_name" ] || continue
  manifest_record debian-package "$package_name" "$package_version" "$package_architecture"
done < "$WORK_DIR/build-environment/debian-packages.tsv"

manifest_record component legendary "$LEGENDARY_VERSION" "commit $LEGENDARY_SOURCE_COMMIT"
manifest_record component uv "$UV_VERSION" "Legendary build tool"
manifest_record component comet "$COMET_VERSION" "commit $COMET_SOURCE_COMMIT; release $COMET_FORK_RELEASE"
manifest_record component gog-protocols "$COMET_PROTOCOLS_COMMIT" "Comet protocol source"
manifest_record component comet-rust-builder "$COMET_RUST_IMAGE" "immutable OCI digest"
DOCKERFILE_FRONTEND="$(sed -n 's/^# syntax=//p' "$BUILD_DIR/Dockerfile.appimage" | head -n 1)"
if ! printf '%s' "$DOCKERFILE_FRONTEND" | grep -Eq '^docker/dockerfile:[^@]+@sha256:[0-9a-f]{64}$'; then
  echo "ERROR: Dockerfile frontend must be pinned by digest."
  exit 1
fi
manifest_record component dockerfile-frontend "$DOCKERFILE_FRONTEND" "immutable OCI digest"
manifest_record component appimagetool "$APPIMAGETOOL_VERSION" "SHA-256 $APPIMAGETOOL_SHA256"
manifest_record component appimage-runtime "$APPIMAGE_RUNTIME_COMMIT" "SHA-256 $APPIMAGE_RUNTIME_SHA256"

RCHEEVOS_VERSION="$(sed -n 's/.*return "\([0-9][0-9.]*\)";.*/\1/p' "$PROJECT_ROOT/native/rcheevos/retromind_rhash.c" | head -n 1)"
LIBCHDR_VERSION="$(sed -n 's/^## \[\([0-9][0-9.]*\)\].*/\1/p' "$PROJECT_ROOT/native/libchdr/CHANGELOG.md" | head -n 1)"
DR_FLAC_VERSION="$(sed -n 's/^dr_flac - v\([0-9][0-9.]*\).*/\1/p' "$PROJECT_ROOT/native/libchdr/include/dr_libs/dr_flac.h" | head -n 1)"
DR_FLAC_PATCHES="$(
  sed -n '/^Local deviation:/,/^are included\.$/p' "$PROJECT_ROOT/native/rcheevos/README.md" \
    | grep -Eo '`[0-9a-f]{7,40}`' \
    | tr -d '`'
)"
if [ -z "$RCHEEVOS_VERSION" ] || [ -z "$LIBCHDR_VERSION" ] || [ -z "$DR_FLAC_VERSION" ] ||
   [ -z "$DR_FLAC_PATCHES" ]; then
  echo "ERROR: Could not derive vendored native component versions."
  exit 1
fi
manifest_record component rcheevos "$RCHEEVOS_VERSION" "vendored source"
manifest_record component libchdr "$LIBCHDR_VERSION" "vendored source"
manifest_record component dr_flac "$DR_FLAC_VERSION" "vendored through libchdr"
for dr_flac_patch in $DR_FLAC_PATCHES; do
  manifest_record patch "dr_flac-$dr_flac_patch" "$dr_flac_patch" "targeted upstream security fix"
done

tail -n +2 "$WORK_DIR/build-environment/elf-dependencies.tsv" >> "$MANIFEST_INTERNAL_PATH"

find "$APPDIR/usr/lib" -type f -print | LC_ALL=C sort | while IFS= read -r native_library; do
  relative_library="${native_library#"$APPDIR"/}"
  library_sha256="$(sha256sum "$native_library" | awk '{print $1}')"
  manifest_record bundled-native-library "$relative_library" "$library_sha256" "SHA-256"
done

# The internal manifest intentionally has no checksum for its enclosing
# AppImage. The external copy receives that checksum after packaging.

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
rm -f "$APPIMAGE_PATH" "$ZSYNC_PATH" "$LEGACY_ZSYNC_PATH"
ARCH=x86_64 VERSION="$RETROMIND_VERSION" APPIMAGE_EXTRACT_AND_RUN=1 \
  "$APPIMAGETOOL" \
  --runtime-file "$APPIMAGE_RUNTIME" \
  --updateinformation "$APPIMAGE_UPDATE_INFORMATION" \
  "$APPDIR" "$APPIMAGE_PATH"

# appimagetool writes the zsync file to its current working directory even
# when the AppImage destination is absolute. Move both release assets together.
GENERATED_ZSYNC_PATH="$WORK_DIR/$APPIMAGE_NAME.zsync"
if [ -f "$GENERATED_ZSYNC_PATH" ]; then
  mv "$GENERATED_ZSYNC_PATH" "$ZSYNC_PATH"
fi

if [ ! -f "$ZSYNC_PATH" ]; then
  echo "ERROR: appimagetool did not create the expected zsync file at '$ZSYNC_PATH'."
  exit 1
fi

# Retromind 0.1.9 and earlier search GitHub releases using the former
# *-linux-x86_64.AppImage.zsync pattern. Keep a tiny metadata alias so those
# AppImages can discover the first and later releases with the canonical name.
# The copied zsync header still points to APPIMAGE_NAME, so no duplicate
# AppImage asset is required.
cp "$ZSYNC_PATH" "$LEGACY_ZSYNC_PATH"

APPIMAGE_SHA256="$(sha256sum "$APPIMAGE_PATH" | awk '{print $1}')"
cp "$MANIFEST_INTERNAL_PATH" "$MANIFEST_PATH"
printf 'artifact\t%s\t%s\tSHA-256 of the release AppImage\n' \
  "$APPIMAGE_NAME" "$APPIMAGE_SHA256" >> "$MANIFEST_PATH"

echo "Done: $APPIMAGE_PATH"
echo "Build manifest: $MANIFEST_PATH"
echo "Delta metadata: $ZSYNC_PATH"
echo "Legacy update metadata: $LEGACY_ZSYNC_PATH"
echo "Run it with: $APPIMAGE_PATH"

echo "Clean up obsolete Retromind builder images..."
CURRENT_BUILDER_IMAGE_ID="$(
  docker image inspect --format '{{.Id}}' "$BUILDER_IMAGE" 2>/dev/null || true
)"
if [ -n "$PREVIOUS_BUILDER_IMAGE_ID" ] && \
   [ "$PREVIOUS_BUILDER_IMAGE_ID" != "$CURRENT_BUILDER_IMAGE_ID" ]; then
  if ! docker image rm "$PREVIOUS_BUILDER_IMAGE_ID" >/dev/null 2>&1; then
    echo "WARNING: Could not remove the preceding Retromind builder image."
  fi
fi

if ! docker image prune -f --filter "label=$BUILDER_IMAGE_LABEL" >/dev/null; then
  echo "WARNING: Could not remove obsolete Retromind builder images."
fi
