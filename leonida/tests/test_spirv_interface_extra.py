"""Additional edge-case unit tests for tools/spirv_interface.py."""

from __future__ import annotations

import struct
import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "tools"))

import spirv_interface as si  # noqa: E402


class TestSpirvInterfaceExtra(unittest.TestCase):
    def test_parse_words_rejects_short_unaligned_and_bad_magic(self) -> None:
        for bad in (b"", b"\x00" * 16, b"\x00" * 21, struct.pack("<5I", 0x12345678, 0, 0, 1, 0)):
            with self.assertRaises(si.InterfaceError):
                si.parse_words(bad)

    def test_parse_words_accepts_big_endian_swapped_magic(self) -> None:
        words = struct.pack(">5I", si.SPIRV_MAGIC, 0x00010000, 0, 1, 0)
        parsed = si.parse_words(words)
        self.assertEqual(list(parsed[:2]), [si.SPIRV_MAGIC, 0x00010000])

    def test_decode_literal_string_reads_until_nul(self) -> None:
        w0 = int.from_bytes(b"main", "little")
        w1 = 0
        text, next_idx = si.decode_literal_string([w0, w1, 99])
        self.assertEqual(text, "main")
        self.assertEqual(next_idx, 2)


if __name__ == "__main__":
    unittest.main()
