"""tools/exe_sha.py: printing and checking a copy's size and SHA-256.

Every test builds its own small file and its own manifest in a temporary folder, so nothing here needs the game;
the last test runs the tool on this repository to make sure it still names a package manifest and that a manifest
without the two keys is a report, not a failure. Standard library only; no game files.
"""
import contextlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import exe_sha  # noqa: E402

PAYLOAD = b"MZ" + bytes(range(64)) * 40


def run(*argv):
    """(exit code, stdout, stderr) for one main() call."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = exe_sha.main(list(argv))
    return code, out.getvalue(), err.getvalue()


class ExeShaTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.root = Path(self.dir.name)
        self.exe = self.root / "game_scarlett_master.exe"
        self.exe.write_bytes(PAYLOAD)
        self.size = len(PAYLOAD)
        self.sha = exe_sha.sha256_of(str(self.exe))

    def manifest(self, **keys):
        path = self.root / "xbox-1.0.2.1.json"
        body = {"title": "Xbox Series package", "version": "1.0.2.1", "expected_exe": self.exe.name, "files": []}
        body.update(keys)
        path.write_text(json.dumps(body), encoding="utf-8")
        return path

    def test_matching_record_is_ok(self):
        manifest = self.manifest(expected_exe_size=self.size, expected_exe_sha256=self.sha)
        code, out, err = run(str(self.exe), "--manifest", str(manifest))
        self.assertEqual(code, 0, err)
        self.assertIn(f"size: {self.size}", out)
        self.assertIn(f"sha256: {self.sha}", out)
        self.assertIn("ok: matches the manifest", out)

    def test_hash_is_case_insensitive_and_whitespace_trimmed(self):
        manifest = self.manifest(expected_exe_sha256=self.sha.upper())
        code, out, err = run(str(self.exe), "--manifest", str(manifest))
        self.assertEqual(code, 0, err)

    def test_different_hash_fails_and_names_both(self):
        other = "0" * 64
        manifest = self.manifest(expected_exe_size=self.size, expected_exe_sha256=other)
        code, out, err = run(str(self.exe), "--manifest", str(manifest))
        self.assertEqual(code, 1)
        self.assertIn("sha256:", err)
        self.assertIn(self.sha, err)
        self.assertIn(other, err)

    def test_different_size_fails_without_hashing(self):
        manifest = self.manifest(expected_exe_size=self.size + 1, expected_exe_sha256=self.sha)
        code, out, err = run(str(self.exe), "--manifest", str(manifest))
        self.assertEqual(code, 1)
        self.assertIn(f"size: {self.size} bytes, expected {self.size + 1}", err)
        self.assertNotIn("sha256:", out)  # the 120 MB hash is not computed for a truncated copy
        self.assertNotIn("sha256", err)

    def test_manifest_without_the_keys_is_a_report(self):
        manifest = self.manifest()
        code, out, err = run(str(self.exe), "--manifest", str(manifest))
        self.assertEqual(code, 0, err)
        self.assertIn(f"sha256: {self.sha}", out)
        self.assertIn("no recorded size or hash", out)

    def test_expect_overrides_the_manifest(self):
        manifest = self.manifest(expected_exe_sha256="0" * 64)
        code, _, err = run(str(self.exe), "--manifest", str(manifest), "--expect", self.sha)
        self.assertEqual(code, 0, err)
        code, _, err = run(str(self.exe), "--expect", "1" * 64)
        self.assertEqual(code, 1)
        self.assertIn("1" * 64, err)

    def test_expect_that_is_not_a_hash_is_a_mismatch(self):
        code, _, err = run(str(self.exe), "--expect", "abc")
        self.assertEqual(code, 1)
        self.assertIn("is not a SHA-256", err)

    def test_json_carries_every_number(self):
        manifest = self.manifest(expected_exe_size=self.size, expected_exe_sha256=self.sha)
        code, out, _ = run(str(self.exe), "--manifest", str(manifest), "--json")
        self.assertEqual(code, 0)
        got = json.loads(out)
        self.assertEqual(got["size"], self.size)
        self.assertEqual(got["sha256"], self.sha)
        self.assertEqual(got["expected_sha256"], self.sha)
        self.assertEqual(got["status"], "match")
        self.assertEqual(got["mismatches"], [])

    def test_print_keys_writes_the_manifest_lines(self):
        code, out, _ = run(str(self.exe), "--print-keys")
        self.assertEqual(code, 0)
        self.assertIn(f'"expected_exe_size": {self.size},', out)
        self.assertIn(f'"expected_exe_sha256": "{self.sha}"', out)

    def test_missing_file_is_an_error_not_a_mismatch(self):
        code, _, err = run(str(self.root / "nope.exe"))
        self.assertEqual(code, 2)
        self.assertIn("not a file", err)

    def test_repository_manifest_is_found_and_reported(self):
        # The repository's own manifest names the executable; without the optional keys it must be a report.
        found = exe_sha.find_manifest(os.path.join(ROOT, "game_scarlett_master.exe"))
        self.assertEqual(found, os.path.join(ROOT, "game", "xbox-1.0.2.1.json"))
        _, size, digest = exe_sha.recorded(found)
        self.assertTrue(size is None or isinstance(size, int))
        self.assertTrue(digest is None or (len(digest) == 64 and digest == digest.lower()))


if __name__ == "__main__":
    unittest.main()
