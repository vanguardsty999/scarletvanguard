"""make_game.py rejects a --out/--work that is a file (or sits under one) before doing any work, instead of a
traceback from os.makedirs or a run that reports success without creating the destination.

    python -I tests/test_make_game_destinations.py
"""
import contextlib
import io
import os
import sys
import tempfile
import unittest
from unittest import mock
from unittest.mock import patch

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)
import make_game


class DestinationTests(unittest.TestCase):
    def run_main(self, *argv):
        """main() over the given arguments, with the game check stubbed out. Returns (exit code, stderr)."""
        err = io.StringIO()
        with patch.object(sys, "argv", ["make_game.py", *argv]), \
             patch.object(make_game, "below_normal"), \
             patch.object(make_game, "log"), \
             patch.object(make_game, "check", return_value={"title": "test", "version": "1.0"}), \
             patch.object(make_game, "preflight", side_effect=AssertionError("preflight was reached")), \
             contextlib.redirect_stderr(err):
            with self.assertRaises(SystemExit) as raised:
                make_game.main()
        return raised.exception.code, err.getvalue()

    def test_out_is_a_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            game = os.path.join(tmp, "game")
            os.makedirs(game)
            out = os.path.join(tmp, "out.txt")
            open(out, "w").close()
            code, err = self.run_main("--game", game, "--out", out, "--work", os.path.join(tmp, "work"))
            self.assertEqual(code, 1, err)
            self.assertIn("--out is not a folder", err)

    def test_work_is_a_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            game = os.path.join(tmp, "game")
            os.makedirs(game)
            work = os.path.join(tmp, "work.txt")
            open(work, "w").close()
            code, err = self.run_main("--game", game, "--out", os.path.join(tmp, "out"), "--work", work)
            self.assertEqual(code, 1, err)
            self.assertIn("--work is not a folder", err)
            self.assertFalse(os.path.exists(os.path.join(tmp, "out")), "the build created --out before rejecting --work")

    def test_out_under_a_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            game = os.path.join(tmp, "game")
            os.makedirs(game)
            blocker = os.path.join(tmp, "blocker.txt")
            open(blocker, "w").close()
            code, err = self.run_main("--game", game, "--out", os.path.join(blocker, "out"),
                                      "--work", os.path.join(tmp, "work"))
            self.assertEqual(code, 1, err)
            self.assertIn("--out is not a folder", err)

    def test_missing_destination_is_accepted(self):
        # A destination that does not exist yet is fine as long as its nearest existing parent is a folder.
        with tempfile.TemporaryDirectory() as tmp, \
             patch.object(sys, "argv", ["make_game.py", "--game", os.path.join(tmp, "game"), "--check",
                                        "--out", os.path.join(tmp, "new", "out"),
                                        "--work", os.path.join(tmp, "new", "work")]), \
             patch.object(make_game, "below_normal"), patch.object(make_game, "log"), \
             patch.object(make_game, "check", return_value={"title": "test", "version": "1.0"}), \
             contextlib.redirect_stderr(io.StringIO()):
            os.makedirs(os.path.join(tmp, "game"))
            self.assertEqual(make_game.main(), 0)


class DriveRootGameTests(unittest.TestCase):
    """--game at a drive root: the game folder is the dump's own top-level manifest names, so an unrelated --out on
    the same drive is allowed and the rerun that resumes a build is not rejected."""

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.tmp = tmp.name
        self.game = self.tmp   # the drive root itself: every top-level folder here is "on the drive"
        os.makedirs(os.path.join(self.game, "Content"))
        self.version = {"title": "test", "version": "1.0", "expected_exe": "Game.exe",
                        "files": {"Content/a.bin": {}, "Game.exe": {}}}

    def main_at_drive_root(self, out, err):
        """main() with the game folder reported as a drive root. Returns nothing; it stops at preflight."""
        real_splitdrive = os.path.splitdrive
        real_game = os.path.normcase(os.path.realpath(self.game))

        def fake_splitdrive(path):
            return ("X:", "\\") if os.path.normcase(path) == real_game else real_splitdrive(path)

        with patch.object(sys, "argv", ["make_game.py", "--game", self.game, "--out", out,
                                        "--work", os.path.join(self.tmp, "work")]), \
             patch.object(make_game, "below_normal"), patch.object(make_game, "log"), \
             patch.object(make_game, "check", return_value=self.version), \
             patch.object(make_game.os.path, "splitdrive", fake_splitdrive), \
             patch.object(make_game, "preflight", side_effect=AssertionError("preflight was reached")), \
             contextlib.redirect_stderr(err):
            make_game.main()

    def test_out_on_the_same_drive_is_accepted_on_resume(self):
        # The folder is already there, as after a first run: listing the drive would count it as the game's own.
        out = os.path.join(self.tmp, "Leonida_Port")
        os.makedirs(out)
        with self.assertRaisesRegex(AssertionError, "preflight was reached"):
            self.main_at_drive_root(out, io.StringIO())

    def test_out_inside_a_top_level_manifest_folder_is_rejected(self):
        err = io.StringIO()
        with self.assertRaises(SystemExit) as raised:
            self.main_at_drive_root(os.path.join(self.game, "Content", "out"), err)
        self.assertEqual(raised.exception.code, 1, err.getvalue())
        self.assertIn("--out and --work must be outside the game folder.", err.getvalue())


def other_drive_tmp():
    """A temporary folder on another drive than the default temp folder when there is one (the repository's drive,
    on a PC that keeps it apart from the system drive), else on the same drive: the split is then only by folder."""
    here = os.path.dirname(ROOT)
    if os.path.splitdrive(here)[0].lower() != os.path.splitdrive(tempfile.gettempdir())[0].lower():
        try:
            return tempfile.TemporaryDirectory(dir=here)
        except OSError:
            pass
    return tempfile.TemporaryDirectory()


class SeparateRootsTests(unittest.TestCase):
    """The package, --out (what the game runs from) and --work (the carved and exported files) can each be on a
    different drive: every stage gets the folder it was given, and a later run with another --work for the assets
    leaves the port's shader path on the first one."""

    def setUp(self):
        roots = [tempfile.TemporaryDirectory(), tempfile.TemporaryDirectory(), other_drive_tmp()]
        for r in roots:
            self.addCleanup(r.cleanup)
        self.game = os.path.join(roots[0].name, "package")
        self.out = os.path.join(roots[1].name, "Leonida")
        self.work = os.path.join(roots[2].name, "leonida-work")
        os.makedirs(self.game)
        self.build = os.path.join(roots[1].name, "build")
        os.makedirs(self.build)
        for name in make_game.RUNTIME_FILES:
            with open(os.path.join(self.build, name), "wb") as f:
                f.write(b"synthetic")
        self.version = {"title": "test", "version": "1.0", "expected_exe": "Game.exe", "files": {}}

    def run_main(self, *argv, **stage_mocks):
        preflight = mock.MagicMock()
        with patch.object(sys, "argv", ["make_game.py", "--game", self.game, "--out", self.out, "--no-tui", *argv]), \
             patch.object(make_game, "below_normal"), patch.object(make_game, "log"), \
             patch.object(make_game, "check", return_value=self.version), \
             patch.object(make_game, "preflight", preflight), contextlib.ExitStack() as stack:
            for name, stage in stage_mocks.items():
                stack.enter_context(patch.object(make_game, name, stage))
            self.assertEqual(make_game.main(), 0)
        return preflight

    def test_each_stage_gets_its_own_folder(self):
        assets, runtime = mock.MagicMock(), mock.MagicMock()
        preflight = self.run_main("--work", self.work, "--stages", "check,assets,runtime",
                                  assets=assets, runtime=runtime)
        self.assertEqual(preflight.call_args.args[1], self.work)   # free space is counted on the --work drive
        self.assertEqual(assets.call_args.args[:2], (self.game, self.work))
        self.assertEqual(runtime.call_args.args[:2], (self.game, self.out))
        self.assertEqual(runtime.call_args.kwargs["work"], self.work)

    def test_assets_in_another_work_folder_keep_the_shader_path(self):
        self.run_main("--work", self.work, "--stages", "check,runtime", "--runtime-build", self.build)
        with open(os.path.join(self.out, "game_path.txt"), encoding="utf-8") as f:
            self.assertEqual(f.read(), self.game)
        shader_path = os.path.join(self.out, "shader_path.txt")
        with open(shader_path, "rb") as f:
            recorded = f.read()
        self.assertEqual(recorded, os.fsencode(os.path.join(self.work, "shaders", "spirv")))

        assets_work = os.path.join(os.path.dirname(self.out), "assets-work")
        assets = mock.MagicMock()
        preflight = self.run_main("--work", assets_work, "--stages", "check,assets", assets=assets)
        self.assertEqual(preflight.call_args.args[1], assets_work)
        self.assertEqual(assets.call_args.args[:2], (self.game, assets_work))
        with open(shader_path, "rb") as f:
            self.assertEqual(f.read(), recorded)


if __name__ == "__main__":
    unittest.main()
