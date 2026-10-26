#!/bin/sh
set -eu

main_snapshot="${1:-}"
security_snapshot="${2:-}"

case "$main_snapshot" in
  ????????T??????Z) ;;
  *) echo "ERROR: Invalid Debian main snapshot timestamp '$main_snapshot'." >&2; exit 1 ;;
esac

case "$security_snapshot" in
  ????????T??????Z) ;;
  *) echo "ERROR: Invalid Debian security snapshot timestamp '$security_snapshot'." >&2; exit 1 ;;
esac

. /etc/os-release
if [ "${ID:-}" != "debian" ] || [ "${VERSION_ID:-}" != "12" ] || [ "${VERSION_CODENAME:-}" != "bookworm" ]; then
  echo "ERROR: AppImage package stages must use Debian 12 Bookworm." >&2
  exit 1
fi

if [ ! -f /usr/share/keyrings/debian-archive-keyring.gpg ]; then
  echo "ERROR: Debian archive signing keyring is unavailable." >&2
  exit 1
fi

# Remove the image's mutable mirrors so every configured Debian package source
# comes from the reviewed snapshots below. Snapshot metadata is often older
# than Valid-Until; disabling only that expiry check retains normal Release-file
# signature verification through Signed-By.
rm -f /etc/apt/sources.list
find /etc/apt/sources.list.d -maxdepth 1 -type f \
  \( -name '*.list' -o -name '*.sources' \) -delete

cat > /etc/apt/sources.list.d/retromind-bookworm.sources <<EOF
Types: deb
URIs: http://snapshot.debian.org/archive/debian/${main_snapshot}/
Suites: bookworm bookworm-updates
Components: main
Check-Valid-Until: no
Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg

Types: deb
URIs: http://snapshot.debian.org/archive/debian-security/${security_snapshot}/
Suites: bookworm-security
Components: main
Check-Valid-Until: no
Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg
EOF
