"""Additional unit tests for tools/fssm_header.py (directory_ok, wave_size, non_negative)."""

from __future__ import annotations

import argparse
import struct
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "tools"))

import fssm_header as fh  # noqa: E402


class TestFssmHeaderExtra(unittest.TestCase):
    def test_directory_ok_and_wave_size(self) -> None:
        self.assertFalse(fh.directory_ok(b"FSSM" + b"\x00" * 16))
        self.assertIsNone(fh.wave_size(b"FSSM" + b"\x00" * 16))

        blob = bytearray(0x140)
        blob[0:4] = fh.MAGIC
        struct.pack_into("<I", blob, fh.DIRECTORY_OFFSET, 0x100)
        struct.pack_into("<I", blob, fh.DIRECTORY_SIZE, 0x40)
        struct.pack_into("<I", blob, fh.DIRECTORY_NEXT, 0x140)
        self.assertTrue(fh.directory_ok(bytes(blob)))

        struct.pack_into("<I", blob, fh.DIRECTORY_NEXT, 0xDEADBEEF)
        self.assertFalse(fh.directory_ok(bytes(blob)))

        struct.pack_into("<I", blob, 0xD4, 0x20)
        self.assertEqual(fh.wave_size(bytes(blob)), 32)
        struct.pack_into("<I", blob, 0xD4, 0x00)
        self.assertEqual(fh.wave_size(bytes(blob)), 64)

    def test_non_negative_parser(self) -> None:
        self.assertEqual(fh.non_negative("0"), 0)
        self.assertEqual(fh.non_negative("5"), 5)
        with self.assertRaises(argparse.ArgumentTypeError):
            fh.non_negative("-1")


if __name__ == "__main__":
    unittest.main()
