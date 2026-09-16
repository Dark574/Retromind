#!/bin/sh
set -eu

if [ "$#" -ne 1 ]; then
  echo "Usage: $0 <output-library>" >&2
  exit 2
fi

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
OUTPUT_PATH="$1"
OUTPUT_DIR="$(dirname -- "$OUTPUT_PATH")"
TEMP_PATH="$OUTPUT_PATH.$$.tmp"
CC_BIN="${CC:-cc}"

mkdir -p "$OUTPUT_DIR"
trap 'rm -f "$TEMP_PATH"' EXIT

"$CC_BIN" \
  -std=c99 \
  -O2 \
  -fPIC \
  -fvisibility=hidden \
  -D_GNU_SOURCE \
  -D_LARGEFILE64_SOURCE \
  -D_FILE_OFFSET_BITS=64 \
  -DRC_NO_THREADS \
  -DRC_SHARED \
  -I"$SCRIPT_DIR/upstream/include" \
  -I"$SCRIPT_DIR/upstream/src" \
  -I"$SCRIPT_DIR/upstream/src/rhash" \
  -shared \
  -Wl,-soname,libretromind-rhash.so \
  -Wl,--version-script="$SCRIPT_DIR/exports.map" \
  "$SCRIPT_DIR/retromind_rhash.c" \
  "$SCRIPT_DIR/upstream/src/rc_compat.c" \
  "$SCRIPT_DIR/upstream/src/rhash/aes.c" \
  "$SCRIPT_DIR/upstream/src/rhash/cdreader.c" \
  "$SCRIPT_DIR/upstream/src/rhash/hash.c" \
  "$SCRIPT_DIR/upstream/src/rhash/hash_disc.c" \
  "$SCRIPT_DIR/upstream/src/rhash/hash_encrypted.c" \
  "$SCRIPT_DIR/upstream/src/rhash/hash_rom.c" \
  "$SCRIPT_DIR/upstream/src/rhash/hash_zip.c" \
  "$SCRIPT_DIR/upstream/src/rhash/md5.c" \
  -o "$TEMP_PATH"

mv "$TEMP_PATH" "$OUTPUT_PATH"
trap - EXIT
