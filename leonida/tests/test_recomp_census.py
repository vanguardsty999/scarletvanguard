"""Unit tests for tools/recomp_census.py: the .pdata recompiler census.

The PE fixture is built byte by byte — a minimal PE32+ image whose .pdata lists
three functions over .text: one clean, one containing an unhandled (x87) insn,
one exercising indirect control flow — so the test needs no game files and no
third-party packages.

    python -I tests/test_recomp_census.py
"""
import os
import struct
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)
TOOLS_DIR = os.path.join(ROOT, "tools")
if TOOLS_DIR not in sys.path:
    sys.path.insert(0, TOOLS_DIR)

import recomp_census as rc  # noqa: E402
import pe_inspect  # noqa: E402

TEXT_RVA = 0x1000
TEXT_RAW = 0x200
TEXT_SIZE = 0x2000
IMAGE_BASE = 0x140000000


def build_pe() -> bytes:
    """PE32+ with .text holding 3 functions and .pdata/.xdata describing them."""
    opt_size = 240
    buf = bytearray(TEXT_RAW + TEXT_SIZE)
    buf[0:2] = b"MZ"
    struct.pack_into("<I", buf, 0x3C, 0x40)
    buf[0x40:0x44] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", buf, 0x44, 0x8664, 2, 0, 0, 0, opt_size, 0x0022)
    opt = 0x58
    struct.pack_into("<H", buf, opt, 0x20B)
    struct.pack_into("<I", buf, opt + 20, TEXT_RVA + 0x50)            # AddressOfEntryPoint
    struct.pack_into("<Q", buf, opt + 24, IMAGE_BASE)                 # ImageBase
    struct.pack_into("<I", buf, opt + 56, 0x4000)                     # SizeOfImage
    struct.pack_into("<I", buf, opt + 60, TEXT_RAW)                   # SizeOfHeaders
    struct.pack_into("<I", buf, opt + 108, 16)                        # NumberOfRvaAndSizes
    # exception directory: .pdata @ 0x2000, unwind info @ 0x2100 (same section)
    struct.pack_into("<II", buf, opt + 112 + 3 * 8, 0x2000, 3 * 12)
    sec = opt + opt_size
    buf[sec:sec + 8] = b".text\0\0\0"
    struct.pack_into("<IIII", buf, sec + 8, TEXT_SIZE, TEXT_RVA, TEXT_SIZE, TEXT_RAW)

    def place(rva: int, blob: bytes) -> None:
        at = TEXT_RAW + (rva - TEXT_RVA)
        buf[at:at + len(blob)] = blob

    # fn1 @ 0x1000: mov eax,5; add eax,3; ret          (clean)
    place(0x1000, bytes.fromhex("b805000000" "83c003" "c3"))
    # fn2 @ 0x1010: mov eax,1; fldz (x87); ret         (gap)
    place(0x1010, bytes.fromhex("b801000000" "d9e8" "c3"))
    # fn3 @ 0x1020: mov rax,target; jmp rax; ret       (indirect)
    target = IMAGE_BASE + 0x102F
    place(0x1020, bytes.fromhex("48b8" + target.to_bytes(8, "little").hex() + "ffe0" "c3"))
    place(0x102F, bytes.fromhex("c3"))
    # .pdata: (begin, end, unwind_rva)
    entries = [(0x1000, 0x1009, 0x2100), (0x1010, 0x1018, 0x2104), (0x1020, 0x1030, 0x2108)]
    for i, (b, e, u) in enumerate(entries):
        place(0x2000 + i * 12, struct.pack("<III", b, e, u))
    # UNWIND_INFO headers: ver=1, flags=0, codes=1
    for i in range(3):
        place(0x2100 + i * 4, bytes([0x01, 0x01, 0x00, 0x00]))
    return bytes(buf)


class CensusTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        cls.path = os.path.join(cls.tmp, "fixture.exe")
        with open(cls.path, "wb") as f:
            f.write(build_pe())
        cls.r = rc.census(cls.path)

    def test_pdata_walk(self):
        r = self.r
        self.assertEqual(r["pdata_functions"], 3)
        self.assertGreaterEqual(r["insns"], 6)

    def test_coverage_split(self):
        r = self.r
        # fn1 and fn3 translate (indirect jmp goes through the dispatch table);
        # fn2 has the x87 gap.
        self.assertEqual(r["functions_fully_covered"], 2)
        self.assertEqual(r["functions_with_gaps"], 1)

    def test_unhandled_and_indirect(self):
        r = self.r
        unh = dict(r["unhandled"])
        self.assertIn("x87", unh)
        ind = r["indirect_control_flow"]
        self.assertGreaterEqual(ind.get("jmp", 0), 1)
        self.assertGreaterEqual(ind.get("ret", 0), 2)

    def test_unwind_flags(self):
        r = self.r
        self.assertEqual(r["unwind_flags"].get("none"), 3)

    def test_mnemonic_histogram(self):
        r = self.r
        names = {k for k, _ in r["top_mnemonics"]}
        self.assertIn("mov", names)
        self.assertIn("ret", names)

    def test_markdown_report(self):
        md = rc.to_markdown(self.r)
        self.assertIn(".pdata functions: 3", md)
        self.assertIn("x87", md)


class EdgeTests(unittest.TestCase):
    def test_non_x64_rejected(self):
        buf = bytearray(build_pe())
        struct.pack_into("<H", buf, 0x44, 0x14C)  # x86, not x86-64
        with tempfile.NamedTemporaryFile(suffix=".exe", delete=False) as f:
            f.write(bytes(buf))
            path = f.name
        try:
            with self.assertRaises(pe_inspect.PeFormatError):
                rc.census(path)
        finally:
            os.unlink(path)


if __name__ == "__main__":
    unittest.main()
