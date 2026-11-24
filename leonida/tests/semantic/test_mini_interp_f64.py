"""spirv_mini_interp.py runs the translator's binary64 arithmetic in binary64.

Until this file, GLSL.std.450 Sqrt and InverseSqrt rounded a double result to binary32 (the translated v_sqrt_f64 of
2.0 gave 0x3ff6a09e60000000), and OpFAdd, OpFSub and OpFDiv on binary64 raised UnsupportedConstruct, so v_add_f64 and
v_rcp_f64 could not run here. Each case translates one instruction with tools/rdna2spirv.py and checks the exported
pair against a correctly rounded reference: Python's own binary64 operations for the sum, the quotient and the root,
and a 60-digit decimal for the inverse root (the interpreter's rsqrt_f64 is checked against it, not against itself).
The translator lowers these ops to correctly rounded SPIR-V (docs/design.md, the v_rcp_f64 / v_rsq_f64 / v_sqrt_f64
and v_add_f64 rows). Standard library only; no game data, no GPU.
"""
import math
import os
import random
import struct
import sys
import unittest
from decimal import Decimal, localcontext

HERE = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True
sys.path.insert(0, HERE)

import spirv_mini_interp as si  # noqa: E402
import support  # noqa: E402

MASK32 = 0xFFFFFFFF


def bits(x):
    return struct.unpack("<Q", struct.pack("<d", x))[0]


def double(word):
    return struct.unpack("<d", struct.pack("<Q", word))[0]


def run(op, a, b=None):
    """The word pair `op v[4:5], v[0:1][, v[2:3]]` leaves, with the binary64 operands a and b."""
    lines = [f"v_mov_b32 v0, {bits(a) & MASK32:#x}", f"v_mov_b32 v1, {bits(a) >> 32:#x}"]
    if b is not None:
        lines += [f"v_mov_b32 v2, {bits(b) & MASK32:#x}", f"v_mov_b32 v3, {bits(b) >> 32:#x}"]
    lines += [f"{op} v[4:5], v[0:1]" + (", v[2:3]" if b is not None else ""), "exp mrt0 v4, v5, v4, v5 done vm",
              "s_endpgm"]
    words = support.mrt0_words(support.translate_lines("ps_mini_f64", lines))
    return (words[1] << 32) | words[0]


def rsqrt_reference(x):
    """1 / sqrt(x) to 60 digits, then the nearest binary64 (ties to even)."""
    with localcontext() as ctx:
        ctx.prec = 60
        r = 1 / Decimal(x).sqrt()
        y = float(r)
        best = min((math.nextafter(y, 0.0), y, math.nextafter(y, math.inf)),
                   key=lambda c: (abs(Decimal(c) - r), bits(c) & 1))
    return best


FINITE = (1.0, 2.0, 3.0, 4.0, 0.25, 0.1, 7.0, 1 / 3, 1e300, 1e-300, 1.7976931348623157e308, 2.2250738585072014e-308,
          5e-324, 2.225073858507201e-308)


class Binary64Arithmetic(unittest.TestCase):
    def test_sqrt_stays_binary64(self):
        self.assertEqual(hex(run("v_sqrt_f64", 2.0)), "0x3ff6a09e667f3bcd")      # was 0x3ff6a09e60000000
        for x in FINITE + (math.inf, 0.0):
            with self.subTest(x=x):
                self.assertEqual(hex(run("v_sqrt_f64", x)), hex(bits(math.sqrt(x))))

    def test_inverse_sqrt_is_rounded_once(self):
        self.assertEqual(hex(run("v_rsq_f64", 2.0)), "0x3fe6a09e667f3bcd")       # was 0x3fe6a09e60000000
        self.assertEqual(run("v_rsq_f64", math.inf), 0)
        rng = random.Random(0x72737121)
        xs = list(FINITE) + [double(rng.randrange(1, 0x7FF0000000000000)) for _ in range(40)]
        for x in xs:
            with self.subTest(x=x):
                self.assertEqual(hex(run("v_rsq_f64", x)), hex(bits(rsqrt_reference(x))))

    def test_rsqrt_f64_against_the_decimal_reference(self):
        # The helper on its own, over more inputs than the translated cases: 1.0 / math.sqrt(x) misses by an ulp
        # on about a quarter of random doubles, which this would catch.
        rng = random.Random(0x7273)
        for _ in range(2000):
            x = double(rng.randrange(1, 0x7FF0000000000000))
            self.assertEqual(si.rsqrt_f64(x), rsqrt_reference(x), x)

    def test_reciprocal_runs_in_binary64(self):
        self.assertEqual(hex(run("v_rcp_f64", 3.0)), "0x3fd5555555555555")
        for x in FINITE + (-3.0, -1e-300, math.inf, -math.inf):
            with self.subTest(x=x):
                self.assertEqual(hex(run("v_rcp_f64", x)), hex(bits(1.0 / x)))

    def test_reciprocal_of_zero_stays_undefined(self):
        # SPIR-V leaves OpFDiv by zero undefined, as for binary32: the interpreter refuses it.
        with self.assertRaises(si.UnsupportedConstruct):
            run("v_rcp_f64", 0.0)

    def test_sum_runs_in_binary64(self):
        cases = [(3.0, 2.0), (1.0, 2.0 ** -52), (1.0, 2.0 ** -53), (1.0 + 2.0 ** -52, 2.0 ** -53), (0.1, 0.2),
                 (1e308, 1e308), (-0.0, 0.0), (-0.0, -0.0), (5e-324, 5e-324), (math.inf, 1.0), (1e300, -1e300)]
        for a, b in cases:
            with self.subTest(a=a, b=b):
                self.assertEqual(hex(run("v_add_f64", a, b)), hex(bits(a + b)))

    def test_infinity_minus_infinity_is_a_nan(self):
        word = run("v_add_f64", math.inf, -math.inf)
        self.assertEqual(word & 0x7FF0000000000000, 0x7FF0000000000000)
        self.assertNotEqual(word & 0x000FFFFFFFFFFFFF, 0)


if __name__ == "__main__":
    unittest.main()
