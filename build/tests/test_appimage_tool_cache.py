"""Offline checks for verified packaging tools and the early build preflight."""
import hashlib
import os
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import time
import unittest


BUILD = Path(__file__).resolve().parents[1]
ELF = b"\x7fELFtest packaging tool\n"
RUNTIME = b"\x7fELFtest type2 runtime\n"


class AppImageToolCacheTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="retromind-tool-cache-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.log = self.root / "commands.log"
        self.payload = self.root / "download"
        self.payload.write_bytes(ELF)
        self.checksum = hashlib.sha256(ELF).hexdigest()
        self.cache = self.root / "cache with spaces" / self.checksum / "appimagetool"
        self.env = {key: value for key, value in os.environ.items()
                    if not key.startswith("RETROMIND_")}
        self.env.update(PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                        RETROMIND_TEST_LOG=str(self.log), RETROMIND_TEST_DOWNLOAD=str(self.payload))
        self.executable("curl", """
destination= url=
while [ "$#" -gt 0 ]; do
  case "$1" in
    -o) destination="$2"; shift 2 ;;
    https://*) url="$1"; shift ;;
    *) shift ;;
  esac
done
printf 'curl %s\n' "$url" >> "$RETROMIND_TEST_LOG"
download="$RETROMIND_TEST_DOWNLOAD"
case "$url" in
  */runtime-x86_64) download="${RETROMIND_TEST_RUNTIME_DOWNLOAD:-$download}" ;;
esac
cp "$download" "$destination"
if [ -n "${RETROMIND_TEST_BARRIER:-}" ]; then
  touch "$RETROMIND_TEST_BARRIER/$$.ready"
  while [ ! -f "$RETROMIND_TEST_BARRIER/release" ]; do sleep 0.01; done
fi
exit "${RETROMIND_TEST_CURL_EXIT:-0}"
""")

    def executable(self, name, body):
        path = self.bin / name
        path.write_text("#!/bin/sh\nset -eu\n" + body)
        path.chmod(0o755)

    def command(self, checksum=None, destination=None):
        return ["sh", str(BUILD / "cache-appimage-tool.sh"), "test tool",
                "https://example.invalid/tool", checksum or self.checksum,
                str(destination or self.cache)]

    def prepare(self, **kwargs):
        return subprocess.run(self.command(**kwargs), env=self.env, capture_output=True,
                              text=True, timeout=10)

    def assert_no_temporary_downloads(self):
        self.assertEqual([], list(self.root.rglob("*.tmp.*")))

    def test_download_then_offline_reuse_rechecks_bytes_and_restores_execute_permission(self):
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(ELF, self.cache.read_bytes())
        self.assertTrue(os.access(self.cache, os.X_OK))
        self.cache.chmod(0o600)
        self.env["RETROMIND_TEST_CURL_EXIT"] = "22"
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("Using verified cached", result.stdout)
        self.assertTrue(os.access(self.cache, os.X_OK))
        self.assertEqual(1, len(self.log.read_text().splitlines()))
        self.assert_no_temporary_downloads()

    def test_corrupted_cached_tool_is_replaced_by_verified_download(self):
        self.cache.parent.mkdir(parents=True)
        self.cache.write_bytes(b"\x7fELFcorrupted cache")
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("Ignoring invalid cached", result.stdout)
        self.assertEqual(ELF, self.cache.read_bytes())
        self.assert_no_temporary_downloads()

    def test_failed_or_wrong_checksum_download_never_replaces_existing_file(self):
        for curl_exit in ("22", "0"):
            with self.subTest(curl_exit=curl_exit):
                self.cache.parent.mkdir(parents=True, exist_ok=True)
                previous = b"previous invalid cache entry"
                self.cache.write_bytes(previous)
                self.payload.write_bytes(b"\x7fELFpartial or changed upstream artifact")
                self.env["RETROMIND_TEST_CURL_EXIT"] = curl_exit
                result = self.prepare()
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Download failed" if curl_exit == "22" else "SHA-256 verification failed",
                              result.stderr)
                self.assertEqual(previous, self.cache.read_bytes())
                self.assert_no_temporary_downloads()

    def test_html_and_empty_payloads_are_rejected_even_with_matching_checksum(self):
        for data in (b"<html>Gateway timeout</html>", b""):
            with self.subTest(data=data):
                self.payload.write_bytes(data)
                result = self.prepare(checksum=hashlib.sha256(data).hexdigest())
                self.assertNotEqual(0, result.returncode)
                self.assertIn("not a valid ELF", result.stderr)
                self.assertFalse(self.cache.exists())
                self.assert_no_temporary_downloads()

    def test_new_pin_uses_distinct_entry_and_preserves_previous_verified_version(self):
        self.assertEqual(0, self.prepare().returncode)
        updated = ELF + b"new version"
        self.payload.write_bytes(updated)
        checksum = hashlib.sha256(updated).hexdigest()
        destination = self.cache.parent.parent / checksum / "appimagetool"
        result = self.prepare(checksum=checksum, destination=destination)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(updated, destination.read_bytes())
        self.assertEqual(ELF, self.cache.read_bytes())

    def test_orphaned_partial_download_is_not_used_as_cached_tool(self):
        self.cache.parent.mkdir(parents=True)
        orphan = self.cache.with_name(self.cache.name + ".tmp.abandoned")
        orphan.write_bytes(ELF)
        self.env["RETROMIND_TEST_CURL_EXIT"] = "22"
        result = self.prepare()
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(self.cache.exists())
        self.assertEqual([orphan], list(self.root.rglob("*.tmp.*")))

    def start_blocked_download(self):
        process = subprocess.Popen(self.command(), env=self.env, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, text=True, start_new_session=True)
        self.addCleanup(self.stop_process, process)
        return process

    @staticmethod
    def stop_process(process):
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
        process.communicate(timeout=10)

    def wait_for_downloads(self, count):
        barrier = Path(self.env["RETROMIND_TEST_BARRIER"])
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if len(list(barrier.glob("*.ready"))) == count:
                return barrier
            time.sleep(0.01)
        self.fail("Download fixture did not reach the barrier")

    def prepare_barrier(self):
        barrier = self.root / "barrier"
        barrier.mkdir()
        self.env["RETROMIND_TEST_BARRIER"] = str(barrier)

    def test_concurrent_callers_publish_complete_tool_without_sharing_temporary_file(self):
        self.prepare_barrier()
        processes = [self.start_blocked_download() for _ in range(2)]
        barrier = self.wait_for_downloads(2)
        self.assertEqual(2, len(list(self.root.rglob("*.tmp.*"))))
        self.assertFalse(self.cache.exists())
        (barrier / "release").touch()
        for process in processes:
            _, stderr = process.communicate(timeout=10)
            self.assertEqual(0, process.returncode, stderr)
        self.assertEqual(ELF, self.cache.read_bytes())
        self.assert_no_temporary_downloads()

    def test_terminated_download_leaves_no_published_tool_or_temporary_file(self):
        self.prepare_barrier()
        process = self.start_blocked_download()
        self.wait_for_downloads(1)
        os.killpg(process.pid, signal.SIGTERM)
        process.communicate(timeout=10)
        self.assertNotEqual(0, process.returncode)
        self.assertFalse(self.cache.exists())
        self.assert_no_temporary_downloads()

    def test_unexpected_directory_or_symlink_destination_is_rejected_without_download(self):
        target = self.root / "unrelated"
        target.write_bytes(ELF)
        for kind in ("directory", "symlink"):
            with self.subTest(kind=kind):
                destination = self.root / kind
                if kind == "directory":
                    destination.mkdir()
                else:
                    destination.symlink_to(target)
                result = self.prepare(destination=destination)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("not a regular file", result.stderr)
                self.assertFalse(self.log.exists())
                self.assertEqual(ELF, target.read_bytes())

    def build_fixture(self):
        project = self.root / "project with spaces"
        build = project / "build"
        build.mkdir(parents=True)
        for name in ("legendary-version.env", "comet-version.env", "appimage-environment.env",
                     "buildkitd.appimage.toml", "cache-appimage-tool.sh"):
            shutil.copy2(BUILD / name, build / name)
        # Keep all production orchestration, with tiny artifacts in place of the
        # public upstream pins. Nothing from this fixture executes as a tool.
        script = (BUILD / "build-appimage.sh").read_text()
        script = script.replace("ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0", self.checksum)
        script = script.replace("617aa77314d57c8b8e4fae9c3f0b35af7b30d5529cb69e98f64f92510ed98a46",
                                hashlib.sha256(RUNTIME).hexdigest())
        (build / "build-appimage.sh").write_text(script)
        (project / "Retromind.csproj").write_text("<InformationalVersion>0.0.0-test</InformationalVersion>\n")
        runtime = self.root / "runtime-download"
        runtime.write_bytes(RUNTIME)
        self.env["RETROMIND_TEST_RUNTIME_DOWNLOAD"] = str(runtime)
        work = project / ".build-work"
        work.mkdir()
        (work / "previous-build").write_text("preserve until tools are ready")
        self.executable("git", """
case "$*" in
  *rev-parse*) printf 'test-commit\n' ;;
  *status*) ;;
  *) exit 98 ;;
esac
""")
        self.executable("docker", """
printf 'docker %s\n' "$*" >> "$RETROMIND_TEST_LOG"
case "$*" in
  'image inspect '*) printf 'test-image\n' ;;
  'buildx inspect '*) ;;
  'buildx build '*) exit 78 ;;
  'buildx prune '*) ;;
  *) exit 97 ;;
esac
""")
        return project, ["sh", str(build / "build-appimage.sh")]

    def test_download_failure_stops_before_work_cleanup_or_any_docker_command(self):
        project, command = self.build_fixture()
        self.env["RETROMIND_TEST_CURL_EXIT"] = "22"
        result = subprocess.run(command, env=self.env, capture_output=True, text=True, timeout=10)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Download failed", result.stderr)
        self.assertTrue((project / ".build-work/previous-build").exists())
        self.assertEqual(1, len(self.log.read_text().splitlines()))
        self.assertTrue(self.log.read_text().startswith("curl "))
        self.assert_no_temporary_downloads()

    def test_runtime_failure_keeps_cached_packager_and_previous_work_without_docker(self):
        project, command = self.build_fixture()
        tool = project / ".build-cache/appimage-tools" / self.checksum / "appimagetool"
        tool.parent.mkdir(parents=True)
        tool.write_bytes(ELF)
        self.env["RETROMIND_TEST_CURL_EXIT"] = "22"
        result = subprocess.run(command, env=self.env, capture_output=True, text=True, timeout=10)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("continuous runtime may have changed", result.stdout)
        self.assertEqual(ELF, tool.read_bytes())
        self.assertTrue((project / ".build-work/previous-build").exists())
        self.assertTrue(all(line.startswith("curl ") for line in self.log.read_text().splitlines()))
        self.assert_no_temporary_downloads()

    def test_cold_build_verifies_both_downloads_before_first_docker_command(self):
        project, command = self.build_fixture()
        result = subprocess.run(command, env=self.env, capture_output=True, text=True, timeout=10)
        self.assertEqual(78, result.returncode, result.stderr)
        commands = self.log.read_text().splitlines()
        self.assertTrue(commands[0].startswith("curl "))
        self.assertTrue(commands[1].startswith("curl "))
        self.assertTrue(commands[2].startswith("docker "))
        for name, data in (("appimagetool", ELF), ("runtime-x86_64", RUNTIME)):
            tool = project / ".build-cache/appimage-tools" / hashlib.sha256(data).hexdigest() / name
            self.assertEqual(data, tool.read_bytes())
            self.assertTrue(os.access(tool, os.X_OK))
        self.assertFalse((project / ".build-work/previous-build").exists())
        self.assert_no_temporary_downloads()

    def test_both_verified_cached_tools_survive_work_cleanup_and_reach_docker_offline(self):
        project, command = self.build_fixture()
        tools = []
        for name, data in (("appimagetool", ELF), ("runtime-x86_64", RUNTIME)):
            tool = project / ".build-cache/appimage-tools" / hashlib.sha256(data).hexdigest() / name
            tool.parent.mkdir(parents=True)
            tool.write_bytes(data)
            tools.append(tool)
        self.env["RETROMIND_TEST_CURL_EXIT"] = "22"
        result = subprocess.run(command, env=self.env, capture_output=True, text=True, timeout=10)
        # Stop at the fake build, without executing a container or packaging tool.
        self.assertEqual(78, result.returncode, result.stderr)
        self.assertEqual(2, result.stdout.count("Using verified cached"))
        self.assertTrue(all(tool.exists() for tool in tools))
        self.assertFalse((project / ".build-work/previous-build").exists())
        self.assertNotIn("curl ", self.log.read_text())
        self.assertIn("docker buildx build ", self.log.read_text())


if __name__ == "__main__":
    unittest.main()
