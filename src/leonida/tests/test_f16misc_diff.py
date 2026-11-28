"""tools/f16misc_diff.py: the f16 reference against the words measured on RDNA2 hardware and the semantic tests'
references, the generator's encoding rules, the sweep and edge grid, and short seeded differential runs. Standard
library only; no GPU, no game files.

The family is v_min3_f16, v_max3_f16, v_frexp_mant_f16, v_frexp_exp_i16_f16, v_ldexp_f16, v_fmamk_f16, v_fmaak_f16,
v_pk_fmac_f16, v_fma_mix_f32, v_fma_mixlo_f16 and v_fma_mixhi_f16.

The two single-rounding cases below are worked out by hand (f16: ulp 2^-10 on [1, 2), ties at the odd multiples of
2^-11; f32: ulp 2^-23 on [1, 2)). x * y is 2^-11 (1 + d / 2^21), so x * y + z is off an f16 tie by |d| 2^-32, less
than half an f32 ulp:

  P  0x2816 * 0x23d5 = 2^-11 (1 + 78 / 2^21); + 1.0 (0x3c00): once -> above the tie -> 0x3c01; f32 lands on the tie,
     even is 0x3c00
  M  0x2817 * 0x23d3 = 2^-11 (1 - 11 / 2^21); + (1 + 2^-10) (0x3c01): once -> below the tie -> 0x3c01; f32 lands on
     the tie 1 + 3 * 2^-11, even is 0x3c02
"""
import inspect
import io
import os
import random
import re
import sys
import unittest
from collections import Counter
from contextlib import redirect_stdout

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import f16misc_diff as f  # noqa: E402  (puts tests/semantic on sys.path)
import test_alu_frexp_ldexp_f16 as fl  # noqa: E402
import test_alu_min3_max3_med3_16_hw as mm  # noqa: E402  (the gfx1036-measured min3/max3/med3 words)
import test_alu_vop3_mods_hw as vm  # noqa: E402  (the gfx1036-measured v_fma_mix_* words, among others)

M16 = 0xFFFF
BY_HAND = (   # (x, y, z, single rounding, double rounding)
    (0x2816, 0x23D5, 0x3C00, 0x3C01, 0x3C00),
    (0x2817, 0x23D3, 0x3C01, 0x3C01, 0x3C02),
)


def base_op(line):
    return re.sub(r"_e(32|64)$", "", line.split()[0])


def measured(rows):
    """Run the hardware-oracle rows (text, v1, v2, v3, word in v4) of the family through the reference.
    Returns (Counter of ok / unjudged reasons, the mismatching rows)."""
    res, bad = Counter(), []
    for text, v1, v2, v3, want in rows:
        lines = text.split("\n")
        if base_op(lines[-1]) not in f.FAMILY:
            continue
        mc = f.Machine()
        for r, w in ((1, v1), (2, v2), (3, v3), (4, 0xDEADBEEF)):
            mc.write_word(r, w)
        try:
            for line in lines:
                op, args, _ = f.split_line(line)
                if op == "s_mov_b32":
                    mc.s[int(args[0][1:])] = int(args[1], 16)
                elif op == "v_mov_b32":
                    mc.write_word(mc.dst(args[0]), mc.v[int(args[1][1:])])
                else:
                    f.step(line, mc)
        except f.NotJudged as e:
            res[str(e)] += 1
            continue
        none = [None, None]
        if f.compare([mc.v[4], 0, 0, 0], [mc.state(4), none, none, none], [want, 0, 0, 0]):
            bad.append((text, hex(v1), hex(v2), hex(v3), hex(want), hex(mc.v[4])))
        else:
            res["ok " + base_op(lines[-1])] += 1
    return res, bad


def one(line, **regs):
    """The word left in v8 by ``line`` after the given v<n> words."""
    mc = f.Machine()
    mc.write_word(8, 0x5A5A5A5A)
    for name, w in regs.items():
        mc.write_word(int(name[1:]), w)
    f.step(line, mc)
    return mc.v[8], mc.state(8)


class Reference(unittest.TestCase):
    def test_min3_max3_match_every_measured_gfx1036_row(self):
        res, bad = measured(mm.MEASURED)
        self.assertEqual(bad, [])
        want = sum(base_op(r[0]) in ("v_min3_f16", "v_max3_f16") for r in mm.MEASURED)
        self.assertEqual(res["ok v_min3_f16"] + res["ok v_max3_f16"], want)
        self.assertGreaterEqual(want, 100)

    def test_fma_mix_matches_the_measured_gfx1036_rows(self):
        res, bad = measured(vm.MEASURED)
        self.assertEqual(bad, [])
        self.assertGreaterEqual(res["ok v_fma_mix_f32"], 600)
        self.assertGreaterEqual(res["ok v_fma_mixlo_f16"], 40)
        self.assertGreaterEqual(res["ok v_fma_mixhi_f16"], 5)
        self.assertEqual({k for k in res if not k.startswith("ok ")} - {"clamp of -0"}, set())

    def test_frexp_ldexp_measured_rows(self):
        for h, mant, ex in fl.MEASURED_FREXP:
            self.assertEqual(one("v_frexp_mant_f16_e32 v8, v9", v9=h)[0] & M16, mant, hex(h))
            self.assertEqual(one("v_frexp_exp_i16_f16_e32 v8, v9", v9=h)[0] & M16, ex, hex(h))
        for h, n, word in fl.MEASURED_LDEXP:
            got = one("v_ldexp_f16_e64 v8, v9, v9 op_sel:[0,1,0]", v9=h | ((n & M16) << 16))[0] & M16
            self.assertEqual(got, word, (hex(h), n))

    def test_frexp_ldexp_match_the_semantic_reference_for_every_half(self):
        mc = f.Machine()
        bad = []
        for h in range(1 << 16):
            mc.write_word(9, h)
            f.step("v_frexp_mant_f16_e32 v8, v9", mc)
            f.step("v_frexp_exp_i16_f16_e32 v10, v9", mc)
            if (mc.v[8] & M16, mc.v[10] & M16) != (fl.ref_mant(h), fl.ref_exp(h)):
                bad.append(("frexp", hex(h)))
            n = (-64, -25, -15, 1, 30, 100)[h % 6]
            mc.write_word(9, h | ((n & M16) << 16))
            f.step("v_ldexp_f16_e64 v8, v9, v9 op_sel:[0,1,0]", mc)
            if mc.v[8] & M16 != fl.ref_ldexp(h, n):
                bad.append(("ldexp", hex(h), n))
        self.assertEqual(bad[:5], [])

    def test_fma_rounds_once(self):
        for x, y, z, single, double in BY_HAND:
            exact = f.fma(f.decode16(x), f.decode16(y), f.decode16(z))
            self.assertEqual(f.encode16(f.round_to(exact, f.F16)), single)
            self.assertEqual(f.encode16(f.round_to(f.round_to(exact, f.F32), f.F16)), double)
            w, _ = one(f"v_fmamk_f16 v8, v9, 0x{y:04x}, v10", v9=x, v10=0xABCD0000 | z)
            self.assertEqual(w, 0x5A5A0000 | single)
            # lane 1 negates y and the accumulator: -(x * y + z)
            w, _ = one("v_pk_fmac_f16 v8, v9, v10", v8=z | ((z ^ 0x8000) << 16), v9=x | (x << 16),
                       v10=y | ((y ^ 0x8000) << 16))
            self.assertEqual(w, single | ((single ^ 0x8000) << 16))
            w, _ = one("v_fma_mixhi_f16 v8, v9, v10, v11 op_sel:[0,0,0] op_sel_hi:[1,1,1]", v9=x, v10=y, v11=z)
            self.assertEqual(w, (single << 16) | 0x5A5A)

    def test_near_one_pairs(self):
        self.assertGreaterEqual(len(f.NEAR_ONE), 20)
        for a, c, d in f.NEAR_ONE:
            self.assertEqual((1024 + a) * (2048 - c), (1 << 21) + d)
            self.assertTrue(0 < abs(d) < 256 and 1 <= c <= 1023, (a, c, d))
        self.assertTrue(any(d > 0 for _, _, d in f.NEAR_ONE) and any(d < 0 for _, _, d in f.NEAR_ONE))

    def test_the_edge_grid_tells_single_from_double_rounding(self):
        witnesses = Counter()
        for op, words, *_ in f.grid_cases():
            if f.FAMILY[op] not in ("fmamk", "fmaak", "pkfmac", "mixlo", "mixhi"):
                continue
            vals = [f.decode16(w) if w <= M16 else f.decode32(w) for w in words]
            exact = f.fma(*vals)
            if exact[0] == "num" and f.round_to(exact, f.F16) != f.round_to(f.round_to(exact, f.F32), f.F16):
                witnesses[op] += 1
        for op in ("v_fmamk_f16", "v_fmaak_f16", "v_pk_fmac_f16", "v_fma_mixlo_f16", "v_fma_mixhi_f16"):
            self.assertGreaterEqual(witnesses[op], 4, op)

    def test_min3_max3_nan_and_signed_zero(self):
        cases = (("v_min3_f16", (0x7E00, 0x3C00, 0x4000), 0x3C00), ("v_max3_f16", (0x7E00, 0x3C00, 0xFE00), 0x3C00),
                 ("v_min3_f16", (0x0000, 0x8000, 0x0000), 0x8000), ("v_max3_f16", (0x8000, 0x0000, 0x8000), 0x0000),
                 ("v_min3_f16", (0xFC00, 0x7E00, 0x0001), 0xFC00), ("v_max3_f16", (0x7C01, 0x0001, 0x8001), 0x0001))
        for op, (a, b, c), want in cases:
            w, st = one(f"{op} v8, v9, v10, v11", v9=a, v10=b, v11=c)
            self.assertEqual((w, st), (0x5A5A0000 | want, [None, None]), (op, a, b, c))
        w, st = one("v_min3_f16 v8, v9, v10, v11 op_sel:[0,0,0,1]", v9=0x7E00, v10=0x7D00, v11=0xFE00)
        self.assertEqual((w & M16, st), (0x5A5A, [None, "nan"]))      # every operand NaN: any NaN

    def test_output_modifiers(self):
        # mul:2/mul:4/div:2 leave an f16 result as it is and clamp still acts; frexp_exp ignores both. The words
        # main's translator gives, as measured on gfx1036 (docs/design.md).
        for line, want in (("v_min3_f16 v8, v9, v10, v11 mul:2", 0xB800),
                           ("v_max3_f16 v8, v9, v10, v11 clamp div:2", 0x3C00),
                           ("v_frexp_mant_f16_e64 v8, v11 mul:4", 0x3900),
                           ("v_frexp_exp_i16_f16_e64 v8, v11 clamp mul:2", 0x0002),
                           ("v_ldexp_f16_e64 v8, v9, v12 div:2", 0x4600),
                           ("v_ldexp_f16_e64 v8, v11, v12 clamp mul:2", 0x3C00)):
            w, st = one(line, v9=0x12343A00, v10=0x5678B800, v11=0x9ABC4100, v12=3)
            self.assertEqual((w, st), (0x5A5A0000 | want, [None, None]), line)

    def test_clamp(self):
        for line, v9, want in (("v_frexp_mant_f16_e64 v8, v9 clamp", 0x7E00, 0x0000),
                               ("v_frexp_mant_f16_e64 v8, v9 clamp", 0xC400, 0x0000),
                               ("v_ldexp_f16_e64 v8, v9, v9 op_sel:[0,1,0] clamp", 0x0003_4200, 0x3C00),
                               ("v_ldexp_f16_e64 v8, v9, v9 op_sel:[0,1,0] clamp", 0x0001_7E00, 0x0000),
                               ("v_min3_f16 v8, v9, v9, v9 clamp", 0x0000_4000, 0x3C00)):
            self.assertEqual(one(line, v9=v9)[0] & M16, want, line)
        with self.assertRaises(f.NotJudged):
            one("v_max3_f16 v8, v9, v9, v9 clamp", v9=0x7E00)       # FClamp of a NaN is undefined in SPIR-V

    def test_f32_operands_of_fma_mix_flush_denormals(self):
        # 1 * (+denormal) + -0: the denormal reads as +0, so +0 + -0 = +0 (unflushed it would be the denormal)
        w, _ = one("v_fma_mix_f32 v8, v9, v10, v11 op_sel_hi:[0,0,0]", v9=0x3F800000, v10=0x007FFFFF, v11=0x80000000)
        self.assertEqual(w, 0x00000000)
        # 1 * (-denormal) + -0 = -0 + -0 = -0
        w, _ = one("v_fma_mix_f32 v8, v9, v10, v11 op_sel_hi:[0,0,0]", v9=0x3F800000, v10=0x80000001, v11=0x80000000)
        self.assertEqual(w, 0x80000000)

    def test_unjudged(self):
        with self.assertRaises(f.NotJudged):     # an f32 denormal result
            one("v_fma_mix_f32 v8, v9, v10, v11 op_sel_hi:[0,0,0]", v9=0x00800000, v10=0x3F000000, v11=0)
        mc = f.Machine()
        mc.write_word(9, 0x7FC00000, "w32")
        with self.assertRaises(f.NotJudged):     # half of an f32 NaN read as an f16
            f.step("v_frexp_mant_f16_e32 v8, v9", mc)

    def test_compare(self):
        none = [None, None]
        self.assertIsNone(f.compare([0x12340000, 0, 0, 0], [[None, "nan"], none, none, none], [0x7FFF0000, 0, 0, 0]))
        self.assertIn("f16 NaN", f.compare([0x12340000, 0, 0, 0], [[None, "nan"], none, none, none],
                                           [0x7C000000, 0, 0, 0]))
        self.assertIn("expected 0x12340000", f.compare([0x12340000, 0, 0, 0], [[None, "nan"], none, none, none],
                                                        [0x7E000001, 0, 0, 0]))
        self.assertIsNone(f.compare([0, 0, 0, 0], [["w32", "w32"], none, none, none], [0xFFC00001, 0, 0, 0]))
        self.assertIn("f32 NaN", f.compare([0, 0, 0, 0], [["w32", "w32"], none, none, none], [0x7F800000, 0, 0, 0]))

    def test_harness_errors(self):
        for line in ("v_add_f16 v8, v9, v10", "v_fmamk_f16 v8, v9, 0x3c00, v10 mul:2",
                     "v_frexp_mant_f16_e32 v8, v9 div:2", "v_ldexp_f16_e64 v8, v9, -v9"):
            with self.assertRaises(f.HarnessError, msg=line):
                one(line, v9=0x3C00, v10=0x3C00)


class Generator(unittest.TestCase):
    def test_generation_is_deterministic(self):
        self.assertEqual(f.program(random.Random(5), 6), f.program(random.Random(5), 6))
        self.assertEqual(f.grid_cases(), f.grid_cases())

    def test_ops_subset_is_respected(self):
        lines, used = f.program(random.Random(2), 20, ["v_ldexp_f16", "v_fma_mixhi_f16"])
        self.assertEqual(set(used), {"v_ldexp_f16", "v_fma_mixhi_f16"})
        self.assertTrue(all(base_op(line) in ("v_mov_b32", "s_mov_b32") + tuple(used) for line in lines))

    def test_sweep_covers_every_op_and_kind(self):
        cases = f.sweep_cases()
        self.assertEqual({c[0] for c in cases}, set(f.FAMILY))
        for op in f.FAMILY:
            kinds = {k for c in cases if c[0] == op for k in c[2][:1]}
            want = {"vgpr", "sgpr", "int", "float"} if f.FAMILY[op] in ("fmamk", "fmaak") else set(f.KINDS)
            self.assertEqual(kinds, want, op)
        # ldexp: 5 _e32 forms (src1 a VGPR) and 5 x 4 _e64 kind pairs but literal with literal
        self.assertEqual(sum(c[0] == "v_ldexp_f16" for c in cases), 5 + 19)

    def test_programs_are_encodable(self):
        rng = random.Random(9)
        for _ in range(400):
            lines, _ = f.program(rng, 6)
            for line in lines:
                op, args, mods = f.split_line(line)
                if op in ("v_mov_b32", "s_mov_b32"):
                    continue
                srcs = args[1:]
                lits = [a for a in srcs if a.startswith("0x")]
                sgprs = [a for a in srcs if a.lstrip("-|").startswith("s")]
                self.assertLessEqual(len(lits), 1, line)
                if op.endswith("_e32") or op in ("v_fmamk_f16", "v_fmaak_f16", "v_pk_fmac_f16"):
                    vsrc = [a for i, a in enumerate(srcs) if i > 0 and not (op in ("v_fmamk_f16", "v_fmaak_f16")
                                                                           and a.startswith("0x"))]
                    self.assertTrue(all(a.startswith("v") for a in vsrc), line)
                else:
                    self.assertLessEqual(len(lits) + len(sgprs), 2, line)
                if base_op(line) in f.MIX:
                    self.assertIn("op_sel_hi:", line)

    def test_edge_grid(self):
        cases = f.grid_cases()
        by_op = Counter(c[0] for c in cases)
        for op in ("v_fmamk_f16", "v_fmaak_f16", "v_pk_fmac_f16", "v_fma_mix_f32", "v_fma_mixlo_f16",
                   "v_fma_mixhi_f16"):
            self.assertEqual(by_op[op], 240, op)
        self.assertEqual(by_op["v_frexp_mant_f16"], 2 * len(f.F16_EDGES))          # plain and clamped
        self.assertEqual(by_op["v_frexp_exp_i16_f16"], len(f.F16_EDGES))
        self.assertEqual(by_op["v_ldexp_f16"], (12 + 3) * len(f.F16_EDGES))
        clamped = {c[0] for c in cases if c[2:] == ("clamp",)}
        self.assertEqual(clamped, {"v_frexp_mant_f16", "v_ldexp_f16"})
        nans = (0x7E00, 0x7D00)
        triples = [c[1] for c in cases if c[0] == "v_min3_f16"]
        self.assertEqual(len(triples), 10 ** 3 - 8)
        self.assertFalse(any(all(v in nans for v in w) for w in triples))

    def test_exhaustive_programs_cover_every_half(self):
        progs = list(f.exhaustive_programs(["v_frexp_exp_i16_f16"]))
        self.assertEqual(len(progs), (1 << 16) // f.GRID_PER_PROGRAM)
        seen = {int(line.split("0x")[1], 16) & M16 for _, lines in progs for line in lines
                if re.match(r"v_mov_b32 v1[2-5], ", line)}
        self.assertEqual(len(seen), 1 << 16)


class Translated(unittest.TestCase):
    def test_harness_canaries(self):
        with redirect_stdout(io.StringIO()) as out:
            ok = f.self_test()
        self.assertTrue(ok, out.getvalue())

    def test_short_seeded_run_matches(self):
        counts, failure = f.run_suite(20, 3, 5)
        self.assertEqual((failure, counts["skip"]), (None, {}))
        self.assertEqual(counts["pass"] + sum(counts["unjudged"].values()), 20)

    def test_sweep_matches_for_ldexp_and_mixhi(self):
        counts, failure = f.run_sweep(4, ["v_ldexp_f16", "v_fma_mixhi_f16"])
        self.assertEqual((failure, counts["skip"]), (None, {}))
        self.assertGreater(counts["pass"], 100)

    def test_edge_grid_matches_for_fmamk_and_min3(self):
        counts, failure = f.run_grid(["v_fmamk_f16", "v_min3_f16"])
        self.assertEqual((failure, counts["skip"]), (None, {}))
        self.assertGreater(counts["events"]["v_fmamk_f16"]["tie"], 10)
        self.assertGreater(counts["events"]["v_min3_f16"]["NaN"], 100)

    def test_each_program_is_checked_before_the_next_is_made(self):
        # a run holds one program's text at a time, not the whole --programs list
        made, seen = [], []
        real = f.program, f.sweep_program, f.grid_program, f.run_one

        def count(fn):
            return lambda *a: made.append(1) or fn(*a)
        try:
            f.program, f.sweep_program, f.grid_program = (count(fn) for fn in real[:3])
            f.run_one = lambda lines, name, tmp, events=None: seen.append(len(made)) or ("match", None)
            f.run_suite(5, 1)
            f.run_sweep(1, ["v_ldexp_f16"])
            f.run_grid(["v_min3_f16"])
        finally:
            f.program, f.sweep_program, f.grid_program, f.run_one = real
        self.assertGreater(len(seen), 10)
        self.assertEqual(seen, list(range(1, len(seen) + 1)))
        self.assertTrue(inspect.isgenerator(f.exhaustive_programs()))

    def test_fma_rounds_once_through_the_translator(self):
        for x, y, z, single, _ in BY_HAND:
            for line in (f"v_fmamk_f16 v8, v12, 0x{y:04x}, v8", f"v_fmaak_f16 v8, v12, v13, 0x{z:04x}",
                         "v_fma_mixlo_f16 v8, v12, v13, v14 op_sel:[0,0,0] op_sel_hi:[1,1,1]"):
                lines = [f"v_mov_b32 v{r}, 0x5a5a5a5a" for r in f.VGPRS]
                lines += ["v_mov_b32 v8, 0x%08x" % (0xABCD0000 | z), "v_mov_b32 v12, 0x%08x" % x,
                          "v_mov_b32 v13, 0x%08x" % y, "v_mov_b32 v14, 0x%08x" % z, line]
                self.assertEqual(f.reference(lines)[0][0] & M16, single, line)
                self.assertIsNone(f.check(lines), line)

    def test_a_wrong_translation_is_caught(self):
        x, y, z, single, double = BY_HAND[0]
        lines = [f"v_mov_b32 v{r}, 0x5a5a5a5a" for r in f.VGPRS]
        lines += [f"v_mov_b32 v8, 0x{z:08x}", f"v_mov_b32 v12, 0x{x:08x}", f"v_fmamk_f16 v8, v12, 0x{y:04x}, v8"]
        self.assertIsNone(f.check(lines))
        real = f.translate_listing
        try:
            f.translate_listing = lambda l, n, t: [(real(l, n, t)[0] & ~M16) | double] + real(l, n, t)[1:]
            self.assertIn(f"expected 0x0000{single:04x}, actual 0x0000{double:04x}", f.check(lines))
        finally:
            f.translate_listing = real


if __name__ == "__main__":
    unittest.main()
