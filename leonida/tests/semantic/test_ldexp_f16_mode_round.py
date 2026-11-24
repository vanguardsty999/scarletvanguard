"""v_ldexp_f16 after a MODE.FP_ROUND[3:2] write (f16/f64 rounding), run in tests/semantic/spirv_mini_interp.py.

`docs/design.md` (Known approximations, MODE) has every VALU f16 result except the transcendentals round toward zero
after `s_setreg_imm32_b32 hwreg(HW_REG_MODE, 2, 2), 3`. v_ldexp_f16 only rounds when the result leaves the normal
range: an overflow gives +-65504 instead of +-inf, and a result below the smallest normal is a denormal truncated
instead of rounded to nearest even. Exact results, zeros, infinities and NaNs are the same in both modes. The
toward-zero words are not measured on hardware for this op; tools/mode_round_probe.py has the cases to check them.

The reference is exact rational arithmetic (fractions.Fraction) and shares no code with the translator.
"""
import os
import random
import sys
import unittest
from fractions import Fraction as Q

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TOOLS_DIR = os.environ.get("LEONIDA_TOOLS_DIR") or os.path.join(ROOT, "tools")

sys.dont_write_bytecode = True
if HERE not in sys.path:
    sys.path.insert(0, HERE)
if TOOLS_DIR not in sys.path:
    sys.path.insert(0, TOOLS_DIR)

from support import translate_lines, mrt0_words  # noqa: E402

RNE = "s_setreg_imm32_b32 hwreg(HW_REG_MODE, 2, 2), 0"
RTZ = "s_setreg_imm32_b32 hwreg(HW_REG_MODE, 2, 2), 3"


def run(mode_line, ops):
    """ops: up to four (x, n) pairs; returns the low 16 bits of v_ldexp_f16_e32 for each, in that MODE."""
    lines = [mode_line] if mode_line else []
    for i, (x, n) in enumerate(ops):
        lines += [f"v_mov_b32 v{2 * i}, 0x{x:04x}", f"v_mov_b32 v{2 * i + 1}, {n}",
                  f"v_ldexp_f16_e32 v{8 + i}, v{2 * i}, v{2 * i + 1}"]
    lines += ["exp mrt0 v8, v9, v10, v11 done vm", "s_endpgm"]
    return [w & 0xFFFF for w in mrt0_words(translate_lines("ps_ldexp_mode", lines))[:len(ops)]]


def half_value(h):
    e, f = h >> 10 & 0x1F, h & 0x3FF
    q = Q(f, 1 << 24) if e == 0 else Q(0x400 | f, 1 << 10) * Q(2) ** (e - 15)
    return -q if h & 0x8000 else q


def exponent(a):
    """The integer E with 2^E <= a < 2^(E+1), for a > 0."""
    e = 0
    while a >= Q(2) ** (e + 1):
        e += 1
    while a < Q(2) ** e:
        e -= 1
    return e


def ref_ldexp(x, n, rtz):
    """x a finite non-zero half, n an integer; x * 2^n rounded to binary16 by the mode (nearest even or toward zero)."""
    q = half_value(x) * Q(2) ** n
    sign, a = (0x8000 if q < 0 else 0), abs(q)
    ulp = Q(2) ** (max(exponent(a), -14) - 10)
    k, r = divmod(a, ulp)
    if not rtz and (r > ulp / 2 or (r == ulp / 2 and k % 2)):
        k += 1
    v = k * ulp
    if v >= 65536:
        return sign | (0x7BFF if rtz else 0x7C00)
    if v < Q(1, 1 << 14):
        return sign | int(v * (1 << 24))
    e = exponent(v)
    return sign | (e + 15) << 10 | int(v / Q(2) ** (e - 10)) - 0x400


class LdexpF16ModeRound(unittest.TestCase):
    # (mode line, x, n, want)
    CASES = (
        (None, 0x7BFF, 1, 0x7C00),        # 65504 * 2 overflows to inf under nearest even (no MODE write)
        (RTZ, 0x7BFF, 1, 0x7BFF),         # and saturates to 65504 toward zero
        (RTZ, 0xFBFF, 1, 0xFBFF),
        (RTZ, 0x3C00, 100, 0x7BFF),       # n clamped to 64: still a finite overflow
        (RTZ, 0x7C00, 1, 0x7C00),         # inf stays inf
        (RTZ, 0x8000, 5, 0x8000),         # a signed zero stays one
        (None, 0x3C03, -15, 0x0202),      # (1 + 3 * 2^-10) * 2^-15 = 513.5 * 2^-24: nearest even rounds up
        (RTZ, 0x3C03, -15, 0x0201),       # toward zero truncates
        (RTZ, 0xBC03, -15, 0x8201),
        (RTZ, 0x3C01, -15, 0x0200),       # 512.5 * 2^-24: a tie that both modes take to 0x0200
        (None, 0x3BFF, -14, 0x0400),      # 1023.5 * 2^-24: nearest even carries into the smallest normal
        (RTZ, 0x3BFF, -14, 0x03FF),       # toward zero stays a denormal
        (None, 0x3E00, -25, 0x0001),      # 0.75 * 2^-24
        (RTZ, 0x3E00, -25, 0x0000),
        (RTZ, 0x0003, 3, 0x0018),         # exact: a denormal operand, a denormal result
        (RTZ, 0x5BFF, 2, 0x63FF),         # exact: a normal result
        (RNE, 0x7BFF, 1, 0x7C00),         # back to nearest even
    )

    def test_cases(self):
        for mode, x, n, want in self.CASES:
            with self.subTest(mode=mode, x=hex(x), n=n):
                self.assertEqual(run(mode, [(x, n)])[0], want)

    def test_reference_agrees_with_the_cases(self):
        for mode, x, n, want in self.CASES:
            if x & 0x7C00 == 0x7C00 or not x & 0x7FFF:
                continue
            with self.subTest(mode=mode, x=hex(x), n=n):
                self.assertEqual(ref_ldexp(x, max(-64, min(64, n)), mode == RTZ), want)

    def test_mode_switches_back_and_vop3(self):
        lines = ["v_mov_b32 v1, 0x7bff", "v_mov_b32 v2, 1", RTZ, "v_ldexp_f16 v8, -v1, v2", RNE,
                 "v_ldexp_f16 v9, -v1, v2", "exp mrt0 v8, v9, v9, v9 done vm", "s_endpgm"]
        w = mrt0_words(translate_lines("ps_ldexp_switch", lines))
        self.assertEqual((w[0] & 0xFFFF, w[1] & 0xFFFF), (0xFBFF, 0xFC00))

    def test_seeded_sweep_against_exact_reference(self):
        rng = random.Random(1369)
        for mode in (RNE, RTZ):
            for _ in range(10):
                ops = []
                for _ in range(4):
                    k = rng.random()
                    if k < 0.3:                                   # near the overflow edge
                        x, n = rng.getrandbits(1) << 15 | rng.randint(26, 30) << 10 | rng.getrandbits(10), \
                            rng.randint(0, 4)
                    elif k < 0.8:                                 # into the denormal range
                        x, n = rng.getrandbits(1) << 15 | rng.randint(1, 30) << 10 | rng.getrandbits(10), \
                            rng.randint(-30, -1)
                    else:
                        x, n = rng.getrandbits(1) << 15 | rng.randint(1, 0x3FF), rng.randint(-4, 30)
                    ops.append((x, n))
                got = run(mode, ops)
                for (x, n), g in zip(ops, got):
                    with self.subTest(mode=mode, x=hex(x), n=n):
                        self.assertEqual(g, ref_ldexp(x, n, mode == RTZ))


    def test_probe_cases_match_the_translator(self):
        """tools/mode_round_probe.py's f16 words in both modes are the translator's, so a GPU run of the probe checks
        the translation (the f64 cases are covered by tests/test_mode_round.py)."""
        import mode_round_probe as probe
        for name, setup, op, is_64, want_rne, want_rtz in probe.CASES:
            if is_64:
                continue
            mask = 0xFFFFFFFF if want_rne > 0xFFFF else 0xFFFF
            for mode, want in ((RNE, want_rne), (RTZ, want_rtz)):
                with self.subTest(case=name, mode=mode):
                    lines = list(setup) + [mode, op, "exp mrt0 v3, v3, v3, v3 done vm", "s_endpgm"]
                    self.assertEqual(mrt0_words(translate_lines("ps_mode_probe", lines))[0] & mask, want)


if __name__ == "__main__":
    unittest.main()
