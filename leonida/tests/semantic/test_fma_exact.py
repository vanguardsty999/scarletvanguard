"""--fma exact (SHADER-497): fused multiply-adds emulated from single-rounded f32 operations (Gen.fma_exact).

RDNA2's v_fma_f32, v_fmac_f32, v_fmaak_f32, v_fmamk_f32 and v_fma_mix* round once. GLSL Fma may be split into two
roundings by a driver (Intel UHD Graphics, Xe-LP, Windows driver 101.7088: cs_fma_fused words 0, 2, 3 and 5), and a
device without VK_KHR_shader_fma can't take --fma khr. --fma exact builds the single rounding from NoContraction
FMul/FAdd/FSub: Dekker's exact product, TwoSum, a round-to-odd sum of the two error terms and one final rounding
(Boldo and Melquiond, IEEE Transactions on Computers 57(4), 2008), with power-of-two scaling that keeps every
intermediate in the normal range. The interpreter rounds every FMul/FAdd/FSub to binary32, so these tests run the
emitted instructions as a driver would, and compare each word with an exact rational reference (si.fma_f32).
"""
import math
import os
import random
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TOOLS_DIR = os.environ.get("LEONIDA_TOOLS_DIR") or os.path.join(ROOT, "tools")
sys.path.insert(0, TOOLS_DIR)
sys.path.insert(0, HERE)

import rdna2spirv  # noqa: E402
import spirv_mini_interp as si  # noqa: E402
from support import mrt0_words, translate_lines  # noqa: E402

EXIT = ["exp mrt0 v0, v1, v2, v3 done vm", "s_endpgm"]
EXACT_FMA = si.fma_f32


def split_fma(x, y, z):
    """GLSL Fma on a driver that rounds the product first, as the Intel UHD driver does for cs_fma_fused."""
    return si.f32(si.f32(x * y) + z)


def run_split(spv):
    """mrt0_words with the interpreter's Fma split into two roundings, so only the emulation can be exact."""
    si.fma_f32 = split_fma
    try:
        return mrt0_words(spv)
    finally:
        si.fma_f32 = EXACT_FMA


def run_split_flushing(spv):
    """run_split on a driver that also flushes every f32 denormal result to a zero of its sign."""
    keep = si.f32

    def f32_ftz(v):
        r = keep(v)
        return math.copysign(0.0, r) if r != 0.0 and abs(r) < 2.0 ** -126 else r

    si.f32 = f32_ftz
    try:
        return run_split(spv)
    finally:
        si.f32 = keep


def f32_of(w):
    return struct.unpack("<f", struct.pack("<I", w))[0]


def bits_of(x):
    return struct.unpack("<I", struct.pack("<f", x))[0]


def translate(lines, mode):
    old = rdna2spirv.FMA_KHR, rdna2spirv.FMA_EXACT
    rdna2spirv.FMA_KHR, rdna2spirv.FMA_EXACT = mode == "khr", mode == "exact"
    try:
        return translate_lines("ps_fma_exact", lines + EXIT)
    finally:
        rdna2spirv.FMA_KHR, rdna2spirv.FMA_EXACT = old


def fma_words(triples, mode="exact", run=None):
    """Run v_fma_f32 on up to four (x, y, z) words and return the four result words."""
    lines = []
    for i, (x, y, z) in enumerate(triples):
        lines += [f"v_mov_b32 v{8 + 3 * i}, 0x{x:08x}", f"v_mov_b32 v{9 + 3 * i}, 0x{y:08x}",
                  f"v_mov_b32 v{10 + 3 * i}, 0x{z:08x}", f"v_fma_f32 v{i}, v{8 + 3 * i}, v{9 + 3 * i}, v{10 + 3 * i}"]
    lines += [f"v_mov_b32 v{i}, 0" for i in range(len(triples), 4)]
    return (run or run_split)(translate(lines, mode))[:len(triples)]


def word(rng, lo, hi):
    """A random binary32 word with an unbiased exponent in [lo, hi] (normal) and a random sign and mantissa."""
    return (rng.getrandbits(1) << 31) | ((rng.randint(lo, hi) + 127) << 23) | rng.getrandbits(23)


def cases(rng, n):
    """Triples of words that stress each path: plain, cancelling, near ties, tiny, huge, and an overflowing product
    brought back by the addend."""
    out = []
    for i in range(n):
        k = i % 7
        if k == 0:
            t = word(rng, -60, 60), word(rng, -60, 60), word(rng, -120, 120)
        elif k == 1:
            x, y = word(rng, -40, 40), word(rng, -40, 40)
            p = si.f32(f32_of(x) * f32_of(y))
            t = x, y, (bits_of(-p) + rng.randint(-3, 3)) & 0xFFFFFFFF
        elif k == 2:
            t = 0x3F800000 | rng.getrandbits(23), 0x3F800000 | rng.getrandbits(23), word(rng, -30, 1)
        elif k == 3:
            t = word(rng, -120, -20), word(rng, -100, 20), word(rng, -126, -40)
        elif k == 4:
            t = word(rng, 40, 127), word(rng, -10, 87), word(rng, 60, 127)
        elif k == 5:
            x, y = word(rng, 60, 127) & 0x7FFFFFFF, word(rng, 60, 127) & 0x7FFFFFFF
            t = x, y, 0xFF7FFFFF - rng.getrandbits(22)          # -FLT_MAX and a little below
        else:
            t = word(rng, -126, 127), word(rng, -126, 127), word(rng, -126, 127)
        out.append(t)
    return out


def big_ties(rng, n):
    """(x, y, z): x*y at least 2^126 and exactly halfway between two binary32 values, z non-zero below 2^-62.

    (1 + a 2^-12)(1 + b 2^-12) = 1 + (a + b) 2^-12 + ab 2^-24 with a, b odd and below 1600 (so the product is below
    2): ab 2^-24 ends in a lone 2^-24, half an ulp. z is a denormal or a normal from 2^-100 to 2^-63."""
    out = []
    for _ in range(n):
        a, b = rng.randrange(1, 1600, 2), rng.randrange(1, 1600, 2)
        x = rng.getrandbits(1) << 31 | 127 << 23 | a << 11
        y = rng.getrandbits(1) << 31 | (rng.randint(126, 127) + 127) << 23 | b << 11
        if rng.random() < 0.3:
            mag = rng.randint(1, 0x7FFFFF)
        else:
            mag = (rng.randint(-100, -63) + 127) << 23 | rng.getrandbits(23)
        out.append((x, y, rng.getrandbits(1) << 31 | mag))
    return out


def wanted(x, y, z):
    return bits_of(EXACT_FMA(f32_of(x), f32_of(y), f32_of(z)))


class FmaExactTests(unittest.TestCase):
    def check(self, triples):
        for i in range(0, len(triples), 4):
            group = triples[i:i + 4]
            for (x, y, z), got in zip(group, fma_words(group)):
                want = wanted(x, y, z)
                if want & 0x7F800000 == 0 and want & 0x7FFFFFFF:
                    continue        # a denormal result is scaled back with a second rounding (documented)
                with self.subTest(x=f"{x:08x}", y=f"{y:08x}", z=f"{z:08x}"):
                    self.assertEqual(f"{got:08x}", f"{want:08x}")

    def test_cs_fma_fused_words(self):
        # The cases of runtime/test/shaders/cs_fma_fused.asm that a splitting driver gets wrong (words 0 and 2).
        a, p = 0x3F800800, bits_of(si.f32(f32_of(0x3F800800) ** 2))
        self.assertEqual(fma_words([(a, a, p | 0x80000000), (a, a, 0xBF800000)]), [0x33800000, 0x3A000400])
        # negative control: on the same splitting driver, --fma glsl gives the split words the Intel UHD gave
        self.assertEqual(fma_words([(a, a, p | 0x80000000), (a, a, 0xBF800000)], "glsl"), [0x00000000, 0x3A000000])

    def test_every_form_is_exact(self):
        forms = [
            ["v_mov_b32 v8, 0x3f800800", "v_mul_f32 v9, v8, v8", "v_fma_f32 v0, v8, v8, -v9"],
            ["v_mov_b32 v8, 0x3f800800", "v_mov_b32 v0, 0xbf801000", "v_fmac_f32 v0, v8, v8"],
            ["v_mov_b32 v8, 0x3f800800", "v_fmaak_f32 v0, v8, v8, 0xbf801000"],
            ["v_mov_b32 v8, 0x3f800800", "v_mov_b32 v9, 0xbf801000", "v_fmamk_f32 v0, v8, 0x3f800800, v9"],
            # cs_w32_fma_mix word 1 (#2211): all-f32 v_fma_mix_f32
            ["v_mov_b32 v8, 0x3f800800", "v_mov_b32 v9, -1.0", "v_fma_mix_f32 v0, v8, v8, v9"],
        ]
        want = [0x33800000, 0x33800000, 0x33800000, 0x33800000, 0x3A000400]
        for ops, w in zip(forms, want):
            with self.subTest(op=ops[-1]):
                spv = translate(ops + ["v_mov_b32 v1, 0", "v_mov_b32 v2, 0", "v_mov_b32 v3, 0"], "exact")
                self.assertEqual(run_split(spv)[0], w)

    def test_matches_the_exact_reference(self):
        self.check(cases(random.Random(497), 700))

    def test_edges(self):
        edge = [0x00000000, 0x80000000, 0x3F800000, 0xBF800000, 0x00800000, 0x7F7FFFFF, 0xFF7FFFFF, 0x3F800001,
                0x3F7FFFFF, 0x3F800800, 0xBF801000, 0x0D800000, 0x35800000, 0x19800000, 0x28800000, 0x7E800000]
        rng = random.Random(5)
        self.check([(rng.choice(edge), rng.choice(edge), rng.choice(edge)) for _ in range(96)])

    def test_signed_zero(self):
        # (+1)(-1) + 1 = +0; (-0)(1) + (-0) = -0; (+0)(-1) + (+0) = +0; (-1)(1) + 1 = +0
        got = fma_words([(0x3F800000, 0xBF800000, 0x3F800000), (0x80000000, 0x3F800000, 0x80000000),
                         (0x00000000, 0xBF800000, 0x00000000), (0xBF800000, 0x3F800000, 0x3F800000)])
        self.assertEqual(got, [0x00000000, 0x80000000, 0x00000000, 0x00000000])

    def test_zero_addend_keeps_the_sign_of_an_underflowing_product(self):
        # Normal x and y whose product still underflows after the 2^96 scale, plus +0: the exact result is -0 for a
        # negative product. The split path gave -0 + +0 = +0, with f32 denormals kept and flushed (reported in #5231).
        # (-2^-126)(2^-120) + 0; (2^-126)(2^-120) - 0; (1.5 2^-100)(-2^-30) + 0 is the denormal -1.5 2^-130;
        # (-1.5 2^-100)(2^-126) + 0
        triples = [(0x80800000, 0x03800000, 0x00000000), (0x00800000, 0x03800000, 0x80000000),
                   (0x0DC00000, 0xB0800000, 0x00000000), (0x8DC00000, 0x00800000, 0x00000000)]
        self.assertEqual(fma_words(triples), [0x80000000, 0x00000000, 0x800C0000, 0x80000000])
        self.assertEqual(fma_words(triples, run=run_split_flushing), [0x80000000, 0x00000000, 0x80000000, 0x80000000])
        rng = random.Random(245)
        group = []
        for _ in range(64):
            ex = rng.randint(-126, -121)
            x, y = word(rng, ex, ex), word(rng, -126, -247 - ex)           # |x*y| < 2^-245
            group.append((x, y, rng.getrandbits(1) << 31))
        for run in (run_split, run_split_flushing):
            for i in range(0, len(group), 4):
                for (x, y, z), got in zip(group[i:i + 4], fma_words(group[i:i + 4], run=run)):
                    with self.subTest(run=run.__name__, x=f"{x:08x}", y=f"{y:08x}", z=f"{z:08x}"):
                        self.assertEqual(f"{got:08x}", f"{(x ^ y) & 0x80000000:08x}")

    def test_tiny_addend_breaks_a_tie_of_a_big_product(self):
        # x*y = (1 + 2^-12)^2 * 2^126 is a tie; the 2^-64 scaling of that path used to turn z = 2^-100 into 0, which
        # rounded to the even neighbour 0x7e801000 instead of 0x7e801001.
        x, y = 0x3F800800, 0x7E800800
        self.assertEqual(fma_words([(x, y, 0x0D800000), (x, y, 0x8D800000), (x, y, 0x00000001), (x, y, 0)]),
                         [0x7E801001, 0x7E801000, 0x7E801001, 0x7E801000])
        # -(1 + 3 * 2^-12)(1 + 2^-12) * 2^126 is a tie whose even neighbour is the larger one: a positive z gives the
        # odd one, a negative z the even one
        x, y = 0x3F801800, 0xFE800800
        self.assertEqual(fma_words([(x, y, 0x0D800000), (x, y, 0x8D800000), (x, y, 0x1F800000), (x, y, 0x20800000)]),
                         [0xFE802001, 0xFE802002, 0xFE802001, 0xFE802001])
        self.check(big_ties(random.Random(62), 96))

    def test_every_form_keeps_the_tie_break(self):
        # Every caller of emit_fma goes through the same scaling: the tie above through each form.
        pre = ["v_mov_b32 v8, 0x3f800800", "v_mov_b32 v9, 0x7e800800", "v_mov_b32 v10, 0x0d800000"]
        forms = [
            ["v_fmac_f32 v10, v8, v9", "v_mov_b32 v0, v10"],
            ["v_fmaak_f32 v0, v8, v9, 0xd800000"],
            ["v_fmamk_f32 v0, v8, 0x7e800800, v10"],
            ["v_fma_mix_f32 v0, v8, v9, v10"],
            ["s_mov_b32 vcc_lo, 0", "v_div_fmas_f32 v0, v8, v9, v10"],
        ]
        for ops in forms:
            with self.subTest(op=ops[-1] if ops[-1].startswith("v_div") else ops[0]):
                spv = translate(pre + ops + ["v_mov_b32 v1, 0", "v_mov_b32 v2, 0", "v_mov_b32 v3, 0"], "exact")
                self.assertEqual(f"{run_split(spv)[0]:08x}", "7e801001")

    def test_tiny_addend_on_a_flushing_driver(self):
        # 2^-85 <= |z| < 2^-62 scaled by 2^-64 is an f32 denormal: kept above, but flushed to 0 on a driver in
        # denorm32 = 0, where only a normal sticky value survives.
        triples = [(0x3F800800, 0x7E800800, bits_of(2.0 ** -70)), (0x3F800800, 0x7E800800, bits_of(-(2.0 ** -63)))]
        triples += [t for t in big_ties(random.Random(70), 64) if t[2] & 0x7F800000]
        for i in range(0, len(triples), 4):
            group = triples[i:i + 4]
            for (x, y, z), got in zip(group, fma_words(group, run=run_split_flushing)):
                with self.subTest(x=f"{x:08x}", y=f"{y:08x}", z=f"{z:08x}"):
                    self.assertEqual(f"{got:08x}", f"{wanted(x, y, z):08x}")

    def test_infinite_and_nan_operands(self):
        inf, ninf, nan = 0x7F800000, 0xFF800000, 0x7FC00000
        got = fma_words([(inf, 0x3F800000, 0x3F800000), (0x3F800000, 0x3F800000, ninf),
                         (inf, 0x00000000, 0x3F800000), (nan, 0x3F800000, 0x3F800000)])
        self.assertEqual(got[:2], [inf, ninf])
        self.assertTrue(math.isnan(f32_of(got[2])) and math.isnan(f32_of(got[3])))
        # finite operands whose product is beyond the scaled range: +-inf
        self.assertEqual(fma_words([(0x7E000000, 0x7E000000, 0xFF7FFFFF)]), [inf])

    def test_infinite_addend_after_an_overflowing_product(self):
        # finite x * y beyond FLT_MAX plus an infinity is that infinity; the plain Fma split on the driver rounded
        # x * y to the other infinity first and gave a NaN
        inf, ninf = 0x7F800000, 0xFF800000
        got = fma_words([(0x7E000000, 0x7E000000, ninf), (0xFE000000, 0x7E000000, inf),
                         (0x7E000000, 0x7E000000, inf), (0x7F7FFFFF, 0xC0000000, inf)])
        self.assertEqual(got, [ninf, inf, inf, inf])
        self.assertEqual(fma_words([(0x7E000000, 0x7E000000, ninf)], run=run_split_flushing), [ninf])

    def test_other_modes_are_unchanged(self):
        ops = ["v_mov_b32 v8, 0x3f800800", "v_fma_f32 v0, v8, v8, -1.0", "v_mov_b32 v1, 0", "v_mov_b32 v2, 0",
               "v_mov_b32 v3, 0"]
        glsl, khr, exact = (translate(ops, m) for m in ("glsl", "khr", "exact"))
        self.assertEqual(glsl.count("%glsl Fma"), 1)
        self.assertNotIn("OpFmaKHR", exact)
        self.assertNotIn("FMAKHR", exact)
        self.assertIn("OpFmaKHR", khr)
        # exact keeps the plain Fma only for infinite or NaN operands, behind a select
        self.assertEqual(exact.count("%glsl Fma"), 1)
        self.assertGreater(exact.count("OpFMul"), glsl.count("OpFMul") + 10)
        no_fma = ["v_mov_b32 v0, 0x3f800000", "v_add_f32 v1, v0, v0", "v_mul_f32 v2, v1, v1", "v_mov_b32 v3, 0"]
        self.assertEqual(translate(no_fma, "glsl"), translate(no_fma, "exact"))

    def test_f16_fma_does_not_depend_on_it(self):
        f16 = ["v_mov_b32 v8, 0x6ce1", "v_mov_b32 v9, 0x4200", "v_mov_b32 v10, 0x8004", "v_fma_f16 v0, v8, v9, v10",
               "v_mov_b32 v1, 0", "v_mov_b32 v2, 0", "v_mov_b32 v3, 0"]
        self.assertEqual(translate(f16, "glsl"), translate(f16, "exact"))

    @unittest.skipUnless(shutil.which("spirv-as") and shutil.which("spirv-val"), "SPIRV-Tools not installed")
    def test_spirv_tools_accept_it(self):
        ops = ["v_mov_b32 v8, 0x3f800800", "v_fma_f32 v0, v8, v8, -1.0", "v_mov_b32 v1, 0", "v_mov_b32 v2, 0",
               "v_mov_b32 v3, 0"]
        spv = translate(ops, "exact")
        with tempfile.TemporaryDirectory() as tmp:
            asm, out = os.path.join(tmp, "m.spvasm"), os.path.join(tmp, "m.spv")
            with open(asm, "w", encoding="utf-8") as f:
                f.write(spv)
            made = subprocess.run(["spirv-as", asm, "-o", out, "--target-env", "vulkan1.2"], capture_output=True,
                                  text=True)
            self.assertEqual(made.returncode, 0, made.stderr)
            checked = subprocess.run(["spirv-val", "--target-env", "vulkan1.2", out], capture_output=True, text=True)
            self.assertEqual(checked.returncode, 0, checked.stderr)


if __name__ == "__main__":
    unittest.main()
