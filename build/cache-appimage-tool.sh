#!/bin/sh
set -eu

if [ "$#" -ne 4 ]; then
  echo "Usage: $0 <name> <https-url> <sha256> <cache-file>" >&2
  exit 1
fi

artifact_name="$1"
artifact_url="$2"
expected_sha256="$3"
artifact_destination="$4"

if ! printf '%s' "$expected_sha256" | grep -Eq '^[0-9a-f]{64}$'; then
  echo "ERROR: Invalid SHA-256 pin for $artifact_name." >&2
  exit 1
fi

case "$artifact_url" in
  https://*) ;;
  *) echo "ERROR: $artifact_name requires an HTTPS download URL." >&2; exit 1 ;;
esac

if ! command -v sha256sum >/dev/null 2>&1; then
  echo "ERROR: sha256sum is required to verify AppImage packaging tools." >&2
  exit 1
fi

is_elf_file() {
  [ -f "$1" ] || return 1
  [ "$(head -c 4 "$1" | od -An -tx1 | tr -d ' \n')" = "7f454c46" ]
}

has_expected_checksum() {
  [ "$(sha256sum < "$1" | awk '{print $1}')" = "$expected_sha256" ]
}

# Never follow an unexpected cache link or move a download into a directory.
if [ -L "$artifact_destination" ] || [ -d "$artifact_destination" ]; then
  echo "ERROR: Cache destination is not a regular file: $artifact_destination" >&2
  exit 1
fi

if is_elf_file "$artifact_destination" && has_expected_checksum "$artifact_destination"; then
  chmod +x "$artifact_destination"
  echo "Using verified cached $artifact_name: $artifact_destination"
  exit 0
fi

if [ -e "$artifact_destination" ]; then
  echo "Ignoring invalid cached $artifact_name: $artifact_destination"
fi

mkdir -p "$(dirname "$artifact_destination")"
# Publish only a completely verified file. Distinct temporary files also keep
# interrupted downloads and concurrent callers from corrupting the cache entry.
artifact_tmp="$(mktemp "$artifact_destination.tmp.XXXXXX")"
trap 'rm -f "$artifact_tmp"' EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

echo "Downloading $artifact_name from: $artifact_url"
if ! curl --fail --location \
    --connect-timeout 15 --max-time 180 \
    --retry 5 --retry-delay 2 --retry-connrefused --retry-all-errors \
    -o "$artifact_tmp" "$artifact_url"; then
  echo "ERROR: Download failed for $artifact_url" >&2
  exit 1
fi

if ! is_elf_file "$artifact_tmp"; then
  echo "ERROR: Downloaded $artifact_name is not a valid ELF binary." >&2
  exit 1
fi

if ! has_expected_checksum "$artifact_tmp"; then
  echo "ERROR: SHA-256 verification failed for $artifact_name." >&2
  echo "       Expected: $expected_sha256" >&2
  echo "       Actual:   $(sha256sum < "$artifact_tmp" | awk '{print $1}')" >&2
  exit 1
fi

chmod +x "$artifact_tmp"
mv -f "$artifact_tmp" "$artifact_destination"
