#!/bin/sh
set -eu
export LC_ALL=C

tools=/out/tools
licenses=/out/gog-archive-licenses
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$tools/bin" "$tools/lib" "$licenses/Innoextract" "$licenses/7-Zip"
install -m 755 /usr/bin/innoextract "$tools/bin/innoextract"
cp /usr/share/doc/innoextract/copyright "$licenses/Innoextract/copyright"

ldd /usr/bin/innoextract > "$work/dependencies"
if grep -q 'not found' "$work/dependencies"; then
  cat "$work/dependencies" >&2
  exit 1
fi
awk '$2 == "=>" && $3 ~ /^\// {print $1, $3}' "$work/dependencies" | while read -r soname path; do
  case "$soname" in
    libc.so.6|libm.so.6|libpthread.so.0|librt.so.1|libdl.so.2|ld-linux-*.so.*|libstdc++.so.6|libgcc_s.so.1)
      continue
      ;;
  esac
  install -m 644 "$path" "$tools/lib/$soname"
  package="$(dpkg-query -S "*/$(basename "$(readlink -f "$path")")" | awk -F ': ' 'NR == 1 { print $1 }')"
  cp "/usr/share/doc/${package%%:*}/copyright" "$licenses/Innoextract/${package%%:*}.copyright"
  printf '%s\n' "$soname"
done > "$licenses/Innoextract/shared-libraries.txt"

# Debian's DFSG 7-Zip build does not include the RAR decoder. Use the pinned
# upstream static x86-64 binary so neither an extra plugin nor host 7-Zip is needed.
version=26.06
release_url="https://github.com/ip7z/7zip/releases/download/$version"
url="$release_url/7z2606-linux-x64.tar.xz"
sha256=2b539cf067b2ad48e1baa5093dbaa8818b73542d1222a0ffde9dc5f2922ea62e
source_archive=7z2606-src.tar.xz
source_url="$release_url/$source_archive"
source_sha256=a4e374c8f864f51df48c2cfd0e1413ec3b2ea030deae852d6a655402ebb61ac4

fetch_verified() {
  curl --fail --location --retry 5 --retry-delay 2 --retry-connrefused --output "$3" "$1"
  printf '%s  %s\n' "$2" "$3" | sha256sum --check --strict
}

fetch_verified "$url" "$sha256" "$work/7zip.tar.xz"
fetch_verified "$source_url" "$source_sha256" "$work/$source_archive"
tar -xJf "$work/7zip.tar.xz" --no-same-owner -C "$work" 7zzs License.txt
install -m 755 "$work/7zzs" "$tools/bin/7zz"
install -m 644 "$work/$source_archive" "$licenses/7-Zip/$source_archive"
cp "$work/License.txt" "$licenses/7-Zip/License.txt"
cp /usr/share/common-licenses/LGPL-2.1 "$licenses/7-Zip/LGPL-2.1"
cat > "$licenses/7-Zip/Source.txt" <<EOF
7-Zip $version, unmodified upstream 7zzs binary for Linux x86-64.
Binary: $url
SHA-256: $sha256
Corresponding source archive: $source_archive (in this directory)
Source URL: $source_url
Source SHA-256: $source_sha256
EOF
