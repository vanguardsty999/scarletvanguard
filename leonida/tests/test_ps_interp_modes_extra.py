"""Additional unit tests for tools/ps_interp_modes.py (interp_class, read_method, chunk_table)."""

from __future__ import annotations

import argparse
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "tools"))

import ps_interp_modes as pim  # noqa: E402


class TestPsInterpModesExtra(unittest.TestCase):
    def test_interp_class_system_and_unknown_modes(self) -> None:
        self.assertEqual(pim.interp_class(12, 2), ("system", 0))
        self.assertEqual(pim.interp_class(0, 99), ("other", 0))
        self.assertEqual(pim.interp_class(0, 2), pim.MODE_CLASS[2])

    def test_read_method_flags(self) -> None:
        self.assertEqual(
            pim.read_method(0x01 | 0x08),
            {"mov": True, "p1p2": False, "param16": True, "f16_ops": False},
        )
        self.assertEqual(
            pim.read_method(0x02 | 0x10),
            {"mov": False, "p1p2": True, "param16": False, "f16_ops": True},
        )

    def test_chunk_table_and_at_least_one(self) -> None:
        self.assertEqual(pim.chunk_table(b"NOTDXBC" + b"\x00" * 32), {})
        self.assertEqual(pim._at_least_one("1"), 1)
        with self.assertRaises(argparse.ArgumentTypeError):
            pim._at_least_one("0")


if __name__ == "__main__":
    unittest.main()
