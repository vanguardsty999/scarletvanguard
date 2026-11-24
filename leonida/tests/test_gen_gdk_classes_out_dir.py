"""Regression test for --out parent directory creation in tools/gen_gdk_runtime_classes.py."""

from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import mock

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "tools"))

import gen_gdk_runtime_classes as gen  # noqa: E402


class TestGenGdkOutDir(unittest.TestCase):
    def test_main_creates_missing_out_parent_directory(self) -> None:
        with TemporaryDirectory() as tmp:
            docs = Path(tmp) / "docs"
            (docs / "COM").mkdir(parents=True)
            out_json = Path(tmp) / "nested" / "dir" / "classes.json"
            with mock.patch.object(gen, "revision", return_value="abc123"):
                rc = gen.main([str(docs), "--out", str(out_json)])
            self.assertEqual(rc, 0)
            self.assertTrue(out_json.is_file())
            self.assertEqual(json.loads(out_json.read_text(encoding="utf-8"))["classes"], {})


if __name__ == "__main__":
    unittest.main()
