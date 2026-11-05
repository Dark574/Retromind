"""Offline regression checks for the AppImage APT bootstrap and lock publication."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


BUILD = Path(__file__).resolve().parents[1]


class AppImageEnvironmentTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="retromind-build-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.log = self.root / "commands.log"
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                        RETROMIND_TEST_LOG=str(self.log))
        # Runner defaults must not select a test case implicitly.
        self.env.pop("DEBIAN_FRONTEND", None)

    def executable(self, name, contents):
        path = self.bin / name
        path.write_text("#!/bin/sh\nset -eu\n" + contents)
        path.chmod(0o755)

    def prepare_apt(self, apt="2.6.2", library="2.6.2"):
        self.env.update(RETROMIND_TEST_APT=apt, RETROMIND_TEST_LIBRARY=library)
        self.executable("apt-get", """
printf '%s|%s\n' "${DEBIAN_FRONTEND:-}" "$*" >> "$RETROMIND_TEST_LOG"
case "$*" in
  *' update') exit "${RETROMIND_TEST_UPDATE_EXIT:-0}" ;;
  *' upgrade -y') exit "${RETROMIND_TEST_UPGRADE_EXIT:-0}" ;;
  *) exit 99 ;;
esac
""")
        self.executable("dpkg-query", """
case "$*" in
  *libapt-pkg6.0) printf '%s' "$RETROMIND_TEST_LIBRARY" ;;
  *) printf '%s' "$RETROMIND_TEST_APT" ;;
esac
""")
        # The host need not be Debian. Return fixture comparison results here;
        # actual dpkg version checks are also exercised in the Debian container.
        self.executable("dpkg", """
case "$*" in
  '--compare-versions 2.6.2 ge 2.6.2'|'--compare-versions 2.6.2+deb12u1 ge 2.6.2'|'--compare-versions 2.6.3 ge 2.6.2') exit 0 ;;
  '--compare-versions 2.6.1 ge 2.6.2'|'--compare-versions 2.6.2~rc1 ge 2.6.2') exit 1 ;;
  *) exit 99 ;;
esac
""")

    def upgrade(self):
        return subprocess.run(["sh", str(BUILD / "upgrade-bookworm-packages.sh")], env=self.env,
                              capture_output=True, text=True, timeout=10)

    def test_fixed_versions_pass_with_retries_disabled_before_any_apt_request(self):
        self.prepare_apt()
        for frontend in (None, "noninteractive", "dialog"):
            with self.subTest(frontend=frontend):
                self.log.unlink(missing_ok=True)
                self.env.pop("DEBIAN_FRONTEND", None)
                if frontend is not None:
                    self.env["DEBIAN_FRONTEND"] = frontend
                result = self.upgrade()
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual([
                    f"{frontend or ''}|-o Acquire::Retries=0 -o APT::Update::Error-Mode=any update",
                    "noninteractive|-o Acquire::Retries=0 upgrade -y",
                ], self.log.read_text().splitlines())
                self.assertIn("Validated apt=2.6.2", result.stdout)
                self.assertIn("Validated libapt-pkg6.0=2.6.2", result.stdout)

    def test_old_apt_or_library_and_prerelease_are_rejected(self):
        for apt, library in [("2.6.1", "2.6.2"), ("2.6.2", "2.6.1"), ("2.6.2~rc1", "2.6.2")]:
            with self.subTest(apt=apt, library=library):
                self.prepare_apt(apt, library)
                result = self.upgrade()
                self.assertNotEqual(0, result.returncode)
                self.assertIn("APT security update missing", result.stderr)

    def test_newer_security_revision_is_accepted(self):
        self.prepare_apt("2.6.2+deb12u1", "2.6.3")
        result = self.upgrade()
        self.assertEqual(0, result.returncode, result.stderr)

    def test_failed_index_update_stops_before_upgrade(self):
        self.prepare_apt()
        self.env["RETROMIND_TEST_UPDATE_EXIT"] = "42"
        result = self.upgrade()
        self.assertEqual(42, result.returncode)
        self.assertEqual(1, len(self.log.read_text().splitlines()))
        self.assertNotIn("Validated", result.stdout)

    def test_failed_upgrade_is_not_reported_as_success(self):
        self.prepare_apt()
        self.env["RETROMIND_TEST_UPGRADE_EXIT"] = "43"
        result = self.upgrade()
        self.assertEqual(43, result.returncode)
        self.assertNotIn("Validated", result.stdout)

    def prepare_update(self):
        build = self.root / "build"
        build.mkdir()
        for script in ("update-appimage-environment.sh", "configure-bookworm-snapshot.sh", "upgrade-bookworm-packages.sh"):
            shutil.copy2(BUILD / script, build / script)
        self.lock = build / "appimage-environment.env"
        self.lock.write_text("original reviewed lock\n")
        archive = self.root / "sdk.tar.gz"
        archive.write_bytes(b"verified test SDK artifact")
        metadata = self.root / "releases.json"
        metadata.write_text(json.dumps({"latest-sdk": "10.0.999", "releases": [{
            "sdk": {"version": "10.0.999", "files": [{
                "rid": "linux-x64", "name": "dotnet-sdk-linux-x64.tar.gz",
                "url": "https://example.invalid/dotnet-sdk-10.0.999-linux-x64.tar.gz",
                "hash": hashlib.sha512(archive.read_bytes()).hexdigest()}]},
            "runtime": {"version": "10.0.99"}}]}))
        ca = self.root / "ca.crt"
        ca.write_text("fake CA for the fake container")
        self.env.update(SSL_CERT_FILE=str(ca), RETROMIND_TEST_METADATA=str(metadata),
                        RETROMIND_TEST_ARCHIVE=str(archive), RETROMIND_TEST_FIRST_REQUEST=str(self.root / "first-request"))
        self.executable("curl", """
destination= url=
while [ "$#" -gt 0 ]; do
  case "$1" in
    -o) destination="$2"; shift 2 ;;
    https://*) url="$1"; shift ;;
    *) shift ;;
  esac
done
case "$url" in
  *timestamp*)
    if [ "${RETROMIND_TEST_EMPTY_RECENT:-0}" = 1 ] && [ ! -e "$RETROMIND_TEST_FIRST_REQUEST" ]; then
      touch "$RETROMIND_TEST_FIRST_REQUEST"
      printf '%s\n' '{"result":{"debian":[],"debian-security":[]}}' > "$destination"
    else
      printf '%s\n' '{"result":{"debian":["20261105T000000Z"],"debian-security":["20261105T000000Z"]}}' > "$destination"
    fi
    ;;
  *releases.json) cp "$RETROMIND_TEST_METADATA" "$destination" ;;
  *.tar.gz) cp "$RETROMIND_TEST_ARCHIVE" "$destination" ;;
  *) exit 97 ;;
esac
""")
        self.executable("docker", """
case "$*" in
  'buildx imagetools inspect '*) printf 'sha256:%064d\n' 0 ;;
  'run '*)
    printf '%s\n' "$*" > "$RETROMIND_TEST_LOG"
    exit "${RETROMIND_TEST_CONTAINER_EXIT:-0}"
    ;;
  *) exit 98 ;;
esac
""")
        return build / "update-appimage-environment.sh"

    def test_failed_container_validation_preserves_lock_and_removes_candidate(self):
        script = self.prepare_update()
        self.env["RETROMIND_TEST_CONTAINER_EXIT"] = "44"
        result = subprocess.run(["sh", str(script)], env=self.env, capture_output=True, text=True, timeout=10)
        self.assertEqual(44, result.returncode, result.stderr)
        self.assertEqual("original reviewed lock\n", self.lock.read_text())
        self.assertEqual([], list(self.lock.parent.glob(".appimage-environment-update.*")))

    def test_successful_update_publishes_complete_verified_candidate(self):
        script = self.prepare_update()
        result = subprocess.run(["sh", str(script)], env=self.env, capture_output=True, text=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        lock = self.lock.read_text()
        self.assertIn("DEBIAN_SNAPSHOT_SECURITY=20261105T000000Z", lock)
        self.assertIn("DOTNET_SDK_VERSION=10.0.999", lock)
        self.assertIn("DOTNET_SDK_LINUX_X64_SHA512=" + hashlib.sha512(b"verified test SDK artifact").hexdigest(), lock)
        self.assertEqual([], list(self.lock.parent.glob(".appimage-environment-update.*")))
        command = self.log.read_text()
        self.assertIn("retromind-bootstrap-ca.crt:ro", command)
        self.assertIn("/usr/local/bin/upgrade-bookworm-packages", command)

    def test_invalid_sdk_checksum_preserves_lock_before_container_execution(self):
        script = self.prepare_update()
        Path(self.env["RETROMIND_TEST_ARCHIVE"]).write_bytes(b"tampered test artifact")
        result = subprocess.run(["sh", str(script)], env=self.env, capture_output=True, text=True, timeout=10)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("original reviewed lock\n", self.lock.read_text())
        self.assertFalse(self.log.exists())

    def test_empty_recent_snapshot_query_falls_back_to_original_window(self):
        script = self.prepare_update()
        self.env["RETROMIND_TEST_EMPTY_RECENT"] = "1"
        result = subprocess.run(["sh", str(script)], env=self.env, capture_output=True, text=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(Path(self.env["RETROMIND_TEST_FIRST_REQUEST"]).exists())
        self.assertIn("DEBIAN_SNAPSHOT_SECURITY=20261105T000000Z", self.lock.read_text())


if __name__ == "__main__":
    unittest.main()
