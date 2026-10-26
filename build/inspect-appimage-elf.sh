#!/bin/sh
set -eu

bundle_root="${1:-/out}"
report_path="${2:-$bundle_root/build-environment/elf-dependencies.tsv}"
temp_dir="$(mktemp -d)"
bundled_names="$temp_dir/bundled-names"
elf_files="$temp_dir/elf-files"
needed_rows="$temp_dir/needed"
version_names="$temp_dir/versions"

cleanup() {
  rm -rf "$temp_dir"
}
trap cleanup EXIT HUP INT TERM

find "$bundle_root" \( -type f -o -type l \) -printf '%f\n' | sort -u > "$bundled_names"
: > "$elf_files"
: > "$needed_rows"
: > "$version_names"

find "$bundle_root" -type f -print | while IFS= read -r candidate; do
  if readelf -h "$candidate" >/dev/null 2>&1; then
    printf '%s\n' "$candidate" >> "$elf_files"
  fi
done

mkdir -p "$(dirname "$report_path")"
printf 'record\tname\tvalue\tdetail\n' > "$report_path"

while IFS= read -r elf_file; do
  relative_path="${elf_file#"$bundle_root"/}"
  readelf -d "$elf_file" 2>/dev/null \
    | sed -n 's/.*Shared library: \[\([^]]*\)\].*/\1/p' \
    | while IFS= read -r needed; do
        classification=""
        detail=""

        if grep -Fxq "$needed" "$bundled_names"; then
          classification="bundled-required"
          detail="ELF DT_NEEDED dependency is present in the AppImage payload"
        else
          case "$needed" in
            libc.so.6|libm.so.6|libpthread.so.0|librt.so.1|libdl.so.2|ld-linux-*.so.*)
              classification="host-required"
              detail="provided by the host glibc; maximum symbol version is validated separately"
              ;;
            libgcc_s.so.1|libstdc++.so.6)
              classification="host-required"
              detail="provided by the host compiler runtime; symbol-version ceiling is validated separately"
              ;;
            libfontconfig.so.*|libfreetype.so.*|libharfbuzz.so.*|libfribidi.so.*|libexpat.so.*)
              classification="host-provided"
              detail="intentionally uses the host font stack to remain compatible with host font configuration"
              ;;
            *)
              echo "ERROR: Required ELF dependency '$needed' for '$relative_path' is neither bundled nor documented as host-provided." >&2
              exit 1
              ;;
          esac
        fi

        printf '%s\t%s\t%s\t%s\n' "$classification" "$needed" "$relative_path" "$detail" >> "$needed_rows"
      done

  readelf --version-info "$elf_file" 2>/dev/null \
    | sed -n 's/.*Name: \(GLIBC_[0-9.]*\).*/\1/p; s/.*Name: \(GLIBCXX_[0-9.]*\).*/\1/p; s/.*Name: \(CXXABI_[0-9.]*\).*/\1/p' \
    >> "$version_names" || true
done < "$elf_files"

sort -u "$needed_rows" >> "$report_path"

max_version() {
  prefix="$1"
  grep "^${prefix}" "$version_names" | sort -V | tail -n 1 || true
}

glibc_max="$(max_version GLIBC_)"
glibcxx_max="$(max_version GLIBCXX_)"
cxxabi_max="$(max_version CXXABI_)"

if [ -n "$glibc_max" ] && [ "$(printf '%s\n%s\n' "$glibc_max" GLIBC_2.36 | sort -V | tail -n 1)" != "GLIBC_2.36" ]; then
  echo "ERROR: AppImage payload requires $glibc_max, newer than Debian 12 glibc 2.36." >&2
  exit 1
fi
if [ -n "$glibcxx_max" ] && [ "$(printf '%s\n%s\n' "$glibcxx_max" GLIBCXX_3.4.30 | sort -V | tail -n 1)" != "GLIBCXX_3.4.30" ]; then
  echo "ERROR: AppImage payload requires $glibcxx_max, newer than Debian 12's compiler runtime baseline." >&2
  exit 1
fi
if [ -n "$cxxabi_max" ] && [ "$(printf '%s\n%s\n' "$cxxabi_max" CXXABI_1.3.13 | sort -V | tail -n 1)" != "CXXABI_1.3.13" ]; then
  echo "ERROR: AppImage payload requires $cxxabi_max, newer than Debian 12's C++ ABI baseline." >&2
  exit 1
fi

printf 'compatibility\tglibc-symbol-maximum\t%s\tvalidated against Debian 12 baseline GLIBC_2.36\n' "${glibc_max:-none}" >> "$report_path"
printf 'compatibility\tglibcxx-symbol-maximum\t%s\tvalidated against Debian 12 baseline GLIBCXX_3.4.30\n' "${glibcxx_max:-none}" >> "$report_path"
printf 'compatibility\tcxxabi-symbol-maximum\t%s\tvalidated against Debian 12 baseline CXXABI_1.3.13\n' "${cxxabi_max:-none}" >> "$report_path"

# These are optional capabilities, not DT_NEEDED libraries. The distinction is
# explicit because every DT_NEEDED entry is mandatory to its owning ELF file.
for helper in ffplay sidplayfp secret-tool; do
  if [ -x "$bundle_root/tools/bin/$helper" ]; then
    state="bundled"
  else
    state="not-bundled"
  fi
  printf 'optional-component\t%s\t%s\tabsence disables only the corresponding optional integration\n' "$helper" "$state" >> "$report_path"
done

printf 'optional-component\tvlc-vdpau-and-unused-plugins\tnot-bundled\tintentionally removed; not required by Retromind local previews\n' >> "$report_path"
