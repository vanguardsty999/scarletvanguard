"""Regression tests for indented SALU lines in tools/hw_oracle/asm.py."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "tools" / "hw_oracle"))

import asm  # noqa: E402


class TestHwOracleAsmWhitespace(unittest.TestCase):
    def test_indented_literals_and_skip_reason(self) -> None:
        self.assertEqual(asm.literals("  s_add_i32 s4, 0x12345678, 0x87654321"), {0x12345678, 0x87654321})
        self.assertIn("GFX11", asm.skip_reason(["\ts_ff0_i32_b32 s4, s5"], "gfx1150"))
        self.assertIn(
            "two different literals",
            asm.skip_reason(["  s_add_i32 s4, 0x12345678, 0x87654321"], "gfx1010"),
        )


if __name__ == "__main__":
    unittest.main()
