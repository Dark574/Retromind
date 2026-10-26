#!/bin/sh
set -eu

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="$PROJECT_ROOT/build"
LOCK_FILE="$BUILD_DIR/appimage-environment.env"
SNAPSHOT_CONFIGURATOR="$BUILD_DIR/configure-bookworm-snapshot.sh"
DOTNET_METADATA_URL="https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json"
BASE_IMAGE_TAG="debian:bookworm"
TEMP_DIR="$(mktemp -d "$BUILD_DIR/.appimage-environment-update.XXXXXX")"
CANDIDATE_LOCK="$TEMP_DIR/appimage-environment.env"

cleanup() {
  rm -rf "$TEMP_DIR"
}
trap cleanup EXIT HUP INT TERM

for command_name in curl date docker jq sha512sum; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "ERROR: Required command '$command_name' is unavailable." >&2
    exit 1
  fi
done

if [ ! -x "$SNAPSHOT_CONFIGURATOR" ]; then
  echo "ERROR: Snapshot configurator is missing or not executable: $SNAPSHOT_CONFIGURATOR" >&2
  exit 1
fi

timestamp_is_valid() {
  printf '%s\n' "$1" | grep -Eq '^[0-9]{8}T[0-9]{6}Z$'
}

latest_snapshot() {
  archive_name="$1"
  after_timestamp="$2"
  before_timestamp="$3"
  response_file="$TEMP_DIR/snapshot-${archive_name}.json"

  curl --fail --silent --show-error --location \
    --retry 5 --retry-delay 2 --retry-connrefused \
    "https://snapshot.debian.org/mr/timestamp/?archive=${archive_name}&after=${after_timestamp}&before=${before_timestamp}" \
    -o "$response_file"

  jq -er --arg archive "$archive_name" '[.result[$archive][]] | max // empty' "$response_file"
}

echo "Resolve current immutable Debian Bookworm image digest..."
base_digest="$(docker buildx imagetools inspect "$BASE_IMAGE_TAG" --format '{{.Manifest.Digest}}')"
if ! printf '%s\n' "$base_digest" | grep -Eq '^sha256:[0-9a-f]{64}$'; then
  echo "ERROR: Docker returned an invalid Debian image digest: $base_digest" >&2
  exit 1
fi
base_image="$BASE_IMAGE_TAG@$base_digest"

before_snapshot="$(date -u +%Y%m%dT%H%M%SZ)"
after_snapshot="$(date -u -d '21 days ago' +%Y%m%dT%H%M%SZ)"

echo "Resolve current Debian and Debian Security snapshot imports..."
main_snapshot="$(latest_snapshot debian "$after_snapshot" "$before_snapshot")"
security_snapshot="$(latest_snapshot debian-security "$after_snapshot" "$before_snapshot")"
if ! timestamp_is_valid "$main_snapshot" || ! timestamp_is_valid "$security_snapshot"; then
  echo "ERROR: Snapshot service returned invalid timestamps." >&2
  exit 1
fi

echo "Resolve and verify the current .NET 10 SDK artifact..."
dotnet_metadata="$TEMP_DIR/dotnet-releases.json"
curl --fail --silent --show-error --location \
  --retry 5 --retry-delay 2 --retry-connrefused \
  "$DOTNET_METADATA_URL" -o "$dotnet_metadata"

dotnet_sdk_version="$(jq -er '."latest-sdk"' "$dotnet_metadata")"
dotnet_release="$(jq -cer --arg version "$dotnet_sdk_version" '.releases[] | select(.sdk.version == $version)' "$dotnet_metadata")"
dotnet_runtime_version="$(printf '%s\n' "$dotnet_release" | jq -er '.runtime.version')"
dotnet_sdk_url="$(printf '%s\n' "$dotnet_release" | jq -er '.sdk.files[] | select(.rid == "linux-x64" and (.name | endswith(".tar.gz"))) | .url')"
dotnet_sdk_sha512="$(printf '%s\n' "$dotnet_release" | jq -er '.sdk.files[] | select(.rid == "linux-x64" and (.name | endswith(".tar.gz"))) | .hash' | tr 'A-F' 'a-f')"

if ! printf '%s\n' "$dotnet_sdk_version" | grep -Eq '^10\.[0-9]+\.[0-9]+$' ||
   ! printf '%s\n' "$dotnet_runtime_version" | grep -Eq '^10\.0\.[0-9]+$' ||
   ! printf '%s\n' "$dotnet_sdk_url" | grep -Eq '^https://[^[:space:]]+/dotnet-sdk-10\.[0-9]+\.[0-9]+-linux-x64\.tar\.gz$' ||
   ! printf '%s\n' "$dotnet_sdk_sha512" | grep -Eq '^[0-9a-f]{128}$'; then
  echo "ERROR: The .NET release metadata did not contain a valid Linux x64 SDK/runtime pair." >&2
  exit 1
fi

dotnet_archive="$TEMP_DIR/dotnet-sdk.tar.gz"
curl --fail --silent --show-error --location \
  --retry 5 --retry-delay 2 --retry-connrefused \
  "$dotnet_sdk_url" -o "$dotnet_archive"
printf '%s  %s\n' "$dotnet_sdk_sha512" "$dotnet_archive" | sha512sum --check --strict

echo "Validate Debian 12, signed snapshot metadata, upgrades, glibc and FFmpeg major version..."
docker run --rm --platform linux/amd64 \
  -v "$SNAPSHOT_CONFIGURATOR:/usr/local/bin/configure-bookworm-snapshot:ro" \
  "$base_image" sh -eu -c '
    /usr/local/bin/configure-bookworm-snapshot "$1" "$2"
    apt-get update
    DEBIAN_FRONTEND=noninteractive apt-get upgrade -y
    . /etc/os-release
    test "$ID" = debian
    test "$VERSION_ID" = 12
    test "$VERSION_CODENAME" = bookworm
    test "$(getconf GNU_LIBC_VERSION)" = "glibc 2.36"
    libc_candidate="$(apt-cache policy libc6 | sed -n "s/^[[:space:]]*Candidate:[[:space:]]*//p")"
    ffmpeg_candidate="$(apt-cache policy ffmpeg | sed -n "s/^[[:space:]]*Candidate:[[:space:]]*//p")"
    case "$libc_candidate" in 2.36-*) ;; *) echo "Unexpected libc6 candidate: $libc_candidate" >&2; exit 1;; esac
    case "$ffmpeg_candidate" in 7:5.*) ;; *) echo "Unexpected FFmpeg candidate: $ffmpeg_candidate" >&2; exit 1;; esac
  ' sh "$main_snapshot" "$security_snapshot"

cat > "$CANDIDATE_LOCK" <<EOF
# Reviewed inputs for the Debian 12 AppImage build environment.
#
# Normal builds consume these immutable values and never refresh them. Run
# build/update-appimage-environment.sh to validate and atomically replace this
# file with current Bookworm/.NET security inputs.
DEBIAN_BOOKWORM_IMAGE=$base_image
DEBIAN_SNAPSHOT_MAIN=$main_snapshot
DEBIAN_SNAPSHOT_SECURITY=$security_snapshot
DOTNET_SDK_VERSION=$dotnet_sdk_version
DOTNET_RUNTIME_VERSION=$dotnet_runtime_version
DOTNET_SDK_LINUX_X64_URL=$dotnet_sdk_url
DOTNET_SDK_LINUX_X64_SHA512=$dotnet_sdk_sha512
EOF

# Validate the complete candidate as shell input and re-check every required
# value before the only operation that changes the tracked environment lock.
(
  set -eu
  . "$CANDIDATE_LOCK"
  test "$DEBIAN_BOOKWORM_IMAGE" = "$base_image"
  test "$DEBIAN_SNAPSHOT_MAIN" = "$main_snapshot"
  test "$DEBIAN_SNAPSHOT_SECURITY" = "$security_snapshot"
  test "$DOTNET_SDK_VERSION" = "$dotnet_sdk_version"
  test "$DOTNET_RUNTIME_VERSION" = "$dotnet_runtime_version"
  test "$DOTNET_SDK_LINUX_X64_URL" = "$dotnet_sdk_url"
  test "$DOTNET_SDK_LINUX_X64_SHA512" = "$dotnet_sdk_sha512"
)

chmod 0644 "$CANDIDATE_LOCK"
mv "$CANDIDATE_LOCK" "$LOCK_FILE"
echo "Updated $LOCK_FILE"
echo "  Debian image:    $base_image"
echo "  Debian snapshot: $main_snapshot"
echo "  Security:        $security_snapshot"
echo "  .NET SDK/runtime: $dotnet_sdk_version / $dotnet_runtime_version"
