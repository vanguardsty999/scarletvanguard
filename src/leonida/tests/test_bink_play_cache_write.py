"""Synthetic failure and overlap checks for the movie-name cache writer; no game data.

    python -I tests/test_bink_play_cache_write.py
"""
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import bink_play  # noqa: E402


class CachePublicationTest(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.archive, self.mount = self.directory / "source.csv", self.directory / "mount.xml"
        self.archive.write_bytes(b"synthetic source")
        self.mount.write_bytes(b"synthetic mount")
        self.cache = self.directory / "cache" / "names.csv"
        self.cache.parent.mkdir()
        self.previous = b"previous cache"
        self.cache.write_bytes(self.previous)
        self.neighbor = self.cache.parent / "bink_names.unrelated.part"
        self.neighbor.write_bytes(b"another writer's file")
        cache_patch = mock.patch.object(bink_play, "cache_path", return_value=self.cache)
        cache_patch.start()
        self.addCleanup(cache_patch.stop)
        names_patch = mock.patch.object(bink_play.bink_names, "main", side_effect=self.generate)
        names_patch.start()
        self.addCleanup(names_patch.stop)

    @staticmethod
    def generate(argv):
        Path(argv[argv.index("--out") + 1]).write_text(
            "path,offset,width,height,frames\nsynthetic.bk2,32,16,16,2\n",
            encoding="utf-8", newline="\n")
        return 0

    def assert_no_staging_files(self):
        self.assertEqual(set(self.cache.parent.iterdir()), {self.cache, self.neighbor})
        self.assertEqual(self.neighbor.read_bytes(), b"another writer's file")

    def test_cache_write_failure_removes_only_its_staging_file(self):
        with mock.patch.object(bink_play.csv, "writer", side_effect=OSError("write failed")):
            with self.assertRaisesRegex(OSError, "write failed"):
                bink_play.load_names(self.archive, self.mount)
        self.assertEqual(self.cache.read_bytes(), self.previous)
        self.assert_no_staging_files()

    def test_replace_failure_or_interruption_removes_only_its_staging_file(self):
        for error in (PermissionError("replace failed"), KeyboardInterrupt()):
            with self.subTest(error=type(error).__name__):
                with mock.patch.object(bink_play.os, "replace", side_effect=error):
                    with self.assertRaises(type(error)):
                        bink_play.load_names(self.archive, self.mount)
                self.assertEqual(self.cache.read_bytes(), self.previous)
                self.assert_no_staging_files()

    def test_failed_outer_save_preserves_another_writers_published_cache(self):
        real_replace = os.replace
        staged = []

        def interleave(source, destination):
            staged.append(source)
            if len(staged) == 1:
                self.assertEqual(bink_play.load_names(self.archive, self.mount),
                                 [("synthetic.bk2", 32, "16", "16", "2")])
                raise PermissionError("outer save failed")
            real_replace(source, destination)

        with mock.patch.object(bink_play.os, "replace", side_effect=interleave):
            with self.assertRaisesRegex(PermissionError, "outer save failed"):
                bink_play.load_names(self.archive, self.mount)
        self.assertEqual(len(set(staged)), 2)
        key = bink_play.stamp(self.archive) + "|" + bink_play.stamp(self.mount)
        self.assertEqual(bink_play._read_cache(self.cache, key), [("synthetic.bk2", 32, "16", "16", "2")])
        self.assert_no_staging_files()


if __name__ == "__main__":
    unittest.main()
