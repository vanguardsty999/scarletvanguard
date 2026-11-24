"""Translate one f64 VALU instruction with tools/rdna2spirv.py and run it in tests/spirv_interp.py.

The translator side of the test_valu_{rcp,rsq,sqrt}_f64.py and test_valu_cvt_f32_f64.py oracle files: each of them
checks the word the translated instruction leaves against its own oracle. The source pair is v[2:3]; the result is
v[4:5] (or v4 for a 32-bit result), which starts as 0xdeadbeef so a missing write shows. Standard library only; no
game data, no GPU.
"""
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TESTS_DIR = os.path.dirname(HERE)
for p in (HERE, TESTS_DIR):
    if p not in sys.path:
        sys.path.insert(0, p)

import spirv_interp  # noqa: E402
from support import translate_lines  # noqa: E402  (support puts tools/ on sys.path)

MASK32 = 0xFFFFFFFF


def f64_bits(x):
    """The binary64 word of a Python float."""
    return struct.unpack("<Q", struct.pack("<d", x))[0]


def run_f64(op, src_bits, *, src="v[2:3]", mods="", wide=True):
    """Run `op v[4:5], <src><mods>` (`op v4, ...` when not `wide`) with v[2:3] = src_bits; return the result word."""
    dst = "v[4:5]" if wide else "v4"
    spv = translate_lines("ps_f64_oracle", [
        f"v_mov_b32 v2, {src_bits & MASK32:#x}",
        f"v_mov_b32 v3, {(src_bits >> 32) & MASK32:#x}",
        "v_mov_b32 v4, 0xdeadbeef",
        "v_mov_b32 v5, 0xdeadbeef",
        f"{op} {dst}, {src}{mods}",
        "exp mrt0 v4, v5, v4, v5 done vm",
        "s_endpgm",
    ])
    state = spirv_interp.run(spv)
    lo, hi = state.vgpr[4] & MASK32, state.vgpr[5] & MASK32
    return (hi << 32) | lo if wide else lo
