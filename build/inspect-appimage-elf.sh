#!/bin/sh
set -eu
export LC_ALL=C

bundle_root="${1:-/appdir}"
report_path="${2:-$bundle_root/build-environment/elf-dependencies.tsv}"
loader="${RETROMIND_ELF_LOADER:-/lib64/ld-linux-x86-64.so.2}"
temp_dir="$(mktemp -d)"
elf_files="$temp_dir/elf-files"
needed_rows="$temp_dir/needed"
version_names="$temp_dir/versions"
direct_needed="$temp_dir/direct-needed"
loader_output="$temp_dir/loader-output"

cleanup() {
  rm -rf "$temp_dir"
}
trap cleanup EXIT HUP INT TERM

if [ ! -d "$bundle_root/usr/bin" ] || [ ! -d "$bundle_root/usr/lib" ]; then
  echo "ERROR: '$bundle_root' is not a completed Retromind AppDir." >&2
  exit 1
fi

bundle_root="$(readlink -f "$bundle_root")"
if [ ! -x "$loader" ]; then
  echo "ERROR: glibc loader is unavailable at '$loader'." >&2
  exit 1
fi

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
printf 'runtime-profile\tvlc\tusr/lib/vlc/lib:usr/lib/vlc/vlc\tmirrors AppRun; the glibc loader also applies each ELF object RPATH/RUNPATH\n' >> "$report_path"
printf 'runtime-profile\thelper\tusr/lib:usr/lib/vlc/lib:usr/lib/vlc/vlc\tmirrors AppImageToolResolver for bundled helper processes; the glibc loader also applies each ELF object RPATH/RUNPATH\n' >> "$report_path"
printf 'coverage\telf-dependencies\tDT_NEEDED\tdoes not attempt to discover libraries loaded only through runtime dlopen() calls\n' >> "$report_path"

vlc_library_path="$bundle_root/usr/lib/vlc/lib:$bundle_root/usr/lib/vlc/vlc"
helper_library_path="$bundle_root/usr/lib:$vlc_library_path"

while IFS= read -r elf_file; do
  relative_path="${elf_file#"$bundle_root"/}"
  case "$relative_path" in
    usr/bin/Retromind|usr/lib/vlc/*)
      runtime_profile="vlc"
      library_path="$vlc_library_path"
      ;;
    usr/bin/ffplay|usr/bin/sidplayfp|usr/bin/secret-tool|usr/bin/legendary|usr/bin/comet|usr/lib/*)
      runtime_profile="helper"
      library_path="$helper_library_path"
      ;;
    *)
      runtime_profile="vlc"
      library_path="$vlc_library_path"
      ;;
  esac

  readelf -d "$elf_file" 2>/dev/null \
    | sed -n 's/.*Shared library: \[\([^]]*\)\].*/\1/p' \
    > "$direct_needed"

  if [ -s "$direct_needed" ]; then
    if ! "$loader" --list --library-path "$library_path" "$elf_file" > "$loader_output" 2>&1; then
      echo "ERROR: The $runtime_profile runtime profile cannot load '$relative_path':" >&2
      sed 's/^/       /' "$loader_output" >&2
      exit 1
    fi

    search_tags="$(
      readelf -d "$elf_file" 2>/dev/null \
        | sed -n \
            's/.*Library rpath: \[\([^]]*\)\].*/RPATH=\1/p; s/.*Library runpath: \[\([^]]*\)\].*/RUNPATH=\1/p' \
        | paste -sd ';' -
    )"
    if [ -z "$search_tags" ]; then
      search_tags="no RPATH/RUNPATH"
    fi

    while IFS= read -r needed; do
      resolved_path="$(
        awk -v needed="$needed" '$1 == needed && $2 == "=>" { print $3; exit }' "$loader_output"
      )"

      if [ -z "$resolved_path" ]; then
        case "$needed" in
          ld-linux-*.so.*) resolved_path="$loader" ;;
          *)
            echo "ERROR: The loader did not report a resolution for '$needed', required by '$relative_path'." >&2
            exit 1
            ;;
        esac
      fi

      resolved_canonical="$(readlink -f "$resolved_path" 2>/dev/null || true)"
      case "$resolved_canonical" in
        "$bundle_root"/*)
          resolved_relative="${resolved_canonical#"$bundle_root"/}"
          printf 'bundled-required\t%s\t%s\tprofile=%s; resolved=%s; %s\n' \
            "$needed" "$relative_path" "$runtime_profile" "$resolved_relative" "$search_tags" \
            >> "$needed_rows"
          ;;
        *)
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
              echo "ERROR: '$needed', required by '$relative_path', resolves outside the AppDir to '$resolved_path' under the $runtime_profile runtime profile." >&2
              exit 1
              ;;
          esac

          printf '%s\t%s\t%s\t%s; profile=%s; resolved=%s; %s\n' \
            "$classification" "$needed" "$relative_path" "$detail" \
            "$runtime_profile" "$resolved_path" "$search_tags" >> "$needed_rows"
          ;;
      esac
    done < "$direct_needed"
  fi

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
  if [ -x "$bundle_root/usr/bin/$helper" ]; then
    state="bundled"
  else
    state="not-bundled"
  fi
  printf 'optional-component\t%s\t%s\tabsence disables only the corresponding optional integration\n' "$helper" "$state" >> "$report_path"
done

printf 'optional-component\tvlc-vdpau-and-unused-plugins\tnot-bundled\tintentionally removed; not required by Retromind local previews\n' >> "$report_path"
