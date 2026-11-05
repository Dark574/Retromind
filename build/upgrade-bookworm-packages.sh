#!/bin/sh
set -eu

# HTTPS trust must be bootstrapped without an unprotected APT download.
if [ ! -s /etc/ssl/certs/ca-certificates.crt ]; then
  echo "ERROR: A trusted CA bundle is required before updating Bookworm packages." >&2
  exit 1
fi

# Keep retries disabled on the command line as well as in the shared source
# configurator. Fail on any index download error rather than accepting stale lists.
apt-get -o Acquire::Retries=0 -o APT::Update::Error-Mode=any update
DEBIAN_FRONTEND=noninteractive apt-get -o Acquire::Retries=0 upgrade -y

# DLA-4880-1 / CVE-2026-109518: both binaries come from the APT source package.
# Do not allow subsequent package installs or publication of an environment lock
# when a pinned snapshot predates the fix, even if upgrade itself succeeded.
for package_name in apt libapt-pkg6.0; do
  installed_version="$(dpkg-query -W -f='${Version}' "$package_name")"
  if ! dpkg --compare-versions "$installed_version" ge 2.6.2; then
    echo "ERROR: APT security update missing: $package_name=$installed_version (requires >= 2.6.2)." >&2
    exit 1
  fi
  echo "Validated $package_name=$installed_version (>= 2.6.2)."
done
