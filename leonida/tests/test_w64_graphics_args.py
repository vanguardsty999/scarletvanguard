"""Tests for command-line fixture argument handling in w64_graphics.py (TEST-649)."""
import contextlib
import importlib.util
import io
import os
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SCRIPT_PATH = os.path.join(ROOT, "runtime", "test", "shaders", "w64_graphics.py")

spec = importlib.util.spec_from_file_location("w64_graphics", SCRIPT_PATH)
w64_graphics = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w64_graphics)


class TestW64GraphicsArgParsing(unittest.TestCase):
    def test_rejects_unknown_fixture_single(self) -> None:
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            rc = w64_graphics.main(["dummy_spirv", "bad_fixture_typo"])
        self.assertEqual(rc, 1)
        self.assertIn("Unknown fixture name: bad_fixture_typo", stderr.getvalue())

    def test_rejects_unknown_fixtures_multiple(self) -> None:
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            rc = w64_graphics.main(["dummy_spirv", "bad1", "bad2"])
        self.assertEqual(rc, 1)
        self.assertIn("Unknown fixture names: bad1, bad2", stderr.getvalue())

    def test_rejects_unknown_fixture_with_tools_dir(self) -> None:
        with tempfile.TemporaryDirectory() as tmp_tools:
            stderr = io.StringIO()
            with contextlib.redirect_stderr(stderr):
                rc = w64_graphics.main(["dummy_spirv", tmp_tools, "bad_with_tools"])
            self.assertEqual(rc, 1)
            self.assertIn("Unknown fixture name: bad_with_tools", stderr.getvalue())


class TestW64GraphicsHarnessTimeout(unittest.TestCase):
    def test_a_timeout_fails_that_fixture_and_the_rest_still_run(self) -> None:
        # #9764: the first vk_harness call hangs past its timeout; later ones report the device as unsupported.
        calls = []

        def fake_run(cmd, **kwargs):
            calls.append(cmd)
            if len(calls) == 1:
                raise w64_graphics.subprocess.TimeoutExpired(cmd, kwargs.get("timeout"))
            return w64_graphics.subprocess.CompletedProcess(cmd, w64_graphics.UNSUPPORTED, "", "no device\n")

        with tempfile.TemporaryDirectory() as work, tempfile.TemporaryDirectory() as tools:
            os.mkdir(os.path.join(work, "spirv"))
            for name in ("ps_w64_a_skip", "gs_w64_a_ring", "vs_ids"):
                open(os.path.join(work, "spirv", name + ".spv"), "wb").close()
            out = io.StringIO()
            with patch.object(w64_graphics.subprocess, "run", side_effect=fake_run), \
                    patch.dict(os.environ, W64G_REF="", W64G_SAVE_REF=""), \
                    patch.dict(w64_graphics._applied, {}, clear=True), contextlib.redirect_stdout(out):
                rc = w64_graphics.main([work, tools, "ps_w64_a_skip", "gs_w64_a_ring"])
        lines = out.getvalue().splitlines()
        self.assertEqual(rc, 1)
        self.assertEqual(len(lines), 2, out.getvalue())
        self.assertIn("ps_w64_a_skip", lines[0])
        self.assertIn("vk_harness timed out after 120 s", lines[0])
        self.assertIn("gs_w64_a_ring", lines[1])
        self.assertIn("SKIP", lines[1])


if __name__ == "__main__":
    unittest.main()
