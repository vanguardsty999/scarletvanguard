"""VOP3 `clamp` of a NaN result gives +0 (Gen.clamp_mods).

The game's MODE has DX10_CLAMP 1, and with it the clamp bit turns a NaN result into +0: measured on RDNA2 (gfx1036)
for v_rcp_iflag_f32 (docs/shader_validation.md, "32-bit float VALU on the GPU": a NaN result +0 except with DX10_CLAMP
off, 169 programs) and for v_fmac_f16, v_fma_mixlo_f16 and v_fma_mix_f32 (SHADER-186, "f16 output modifiers, clamp and
v_fma_mix* operands on RDNA2").
GLSL.std.450 leaves FClamp undefined for a NaN operand, so the translator selects +0 for a NaN before FClamp, as the
div_fixup and cvt_pknorm clamps already did. Before that the shared helper emitted a bare FClamp, which the CPU
interpreter refuses (UnsupportedConstruct) and which a driver may return as the NaN.

Each program computes one value in v4 and exports it. The NaN comes from a NaN operand (quiet, negative, signalling)
or from an invalid operation (inf - inf, -inf * 0); controls without a NaN check that the clamp itself is unchanged.
Standard library only; no game data.
"""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from support import mrt0_words, translate_lines  # noqa: E402

QNAN = 0x7FC00000
NEG_QNAN = 0xFFC00000
SNAN = 0x7F800001
POS_INF = 0x7F800000
NEG_INF = 0xFF800000
ONE = 0x3F800000
TWO = 0x40000000
QUARTER = 0x3E800000
NEG_ONE = 0xBF800000
F16_QNAN = 0x7E00
F16_NEG_QNAN = 0xFE00
F16_ONE = 0x3C00
F16_HALF = 0x3800


def run(op_line, v1, v2, v3=0):
    """mrt0.x after `op_line` (which writes v4) with v1..v3 set to the given words."""
    spv = translate_lines("ps_clamp_nan", [
        f"v_mov_b32 v1, {v1:#x}",
        f"v_mov_b32 v2, {v2:#x}",
        f"v_mov_b32 v3, {v3:#x}",
        "v_mov_b32 v4, 0xdeadbeef",
        op_line,
        "exp mrt0 v4, v4, v4, v4 done vm",
        "s_endpgm",
    ])
    return mrt0_words(spv)[0]


class ClampNanF32Tests(unittest.TestCase):
    def test_nan_operand_clamps_to_plus_zero(self):
        for nan in (QNAN, NEG_QNAN, SNAN):
            for line in ("v_add_f32_e64 v4, v1, v2 clamp",
                         "v_sub_f32_e64 v4, v1, v2 clamp",
                         "v_mul_f32_e64 v4, v1, v2 clamp",
                         "v_fma_f32 v4, v1, v2, v2 clamp",
                         "v_mul_f32_e64 v4, v1, v2 mul:2 clamp"):
                with self.subTest(op=line, nan=hex(nan)):
                    self.assertEqual(run(line, nan, ONE), 0)

    def test_invalid_operation_clamps_to_plus_zero(self):
        self.assertEqual(run("v_sub_f32_e64 v4, v1, v2 clamp", POS_INF, POS_INF), 0)       # inf - inf
        self.assertEqual(run("v_add_f32_e64 v4, v1, v2 clamp", POS_INF, NEG_INF), 0)       # inf + -inf
        self.assertEqual(run("v_mul_f32_e64 v4, v1, v2 clamp", NEG_INF, 0), 0)             # -inf * 0
        self.assertEqual(run("v_fma_f32 v4, v1, v2, v3 clamp", POS_INF, 0, ONE), 0)        # inf * 0 + 1

    def test_all_nan_min_max_clamps_to_plus_zero(self):
        # Without clamp an all-NaN min/max keeps src0's NaN (docs/design.md); the clamp then gives +0.
        for op in ("v_min_f32_e64", "v_max_f32_e64"):
            with self.subTest(op=op):
                self.assertEqual(run(f"{op} v4, v1, v2 clamp", QNAN, NEG_QNAN), 0)
        for op in ("v_min3_f32", "v_max3_f32"):
            with self.subTest(op=op):
                self.assertEqual(run(f"{op} v4, v1, v2, v3 clamp", QNAN, SNAN, NEG_QNAN), 0)

    def test_nan_without_clamp_is_still_nan(self):
        # The select is only on the clamp path: without clamp a NaN result stays a NaN.
        w = run("v_add_f32_e64 v4, v1, v2", QNAN, ONE)
        self.assertEqual(w & 0x7F800000, 0x7F800000)
        self.assertNotEqual(w & 0x007FFFFF, 0)

    def test_clamp_controls_unchanged(self):
        self.assertEqual(run("v_add_f32_e64 v4, v1, v2 clamp", TWO, TWO), ONE)
        self.assertEqual(run("v_add_f32_e64 v4, v1, v2 clamp", NEG_ONE, 0), 0)
        self.assertEqual(run("v_add_f32_e64 v4, v1, v2 clamp", QUARTER, 0), QUARTER)
        self.assertEqual(run("v_mul_f32_e64 v4, v1, v2 clamp", POS_INF, ONE), ONE)
        self.assertEqual(run("v_mul_f32_e64 v4, v1, v2 clamp", NEG_INF, ONE), 0)


    def test_legacy_ops_nan_clamps_to_plus_zero(self):
        # v_mul_legacy_f32 ... clamp is the most common clamp form in the game's listings (docs/design.md).
        self.assertEqual(run("v_mul_legacy_f32_e64 v4, v1, v2 clamp", QNAN, ONE), 0)
        self.assertEqual(run("v_mad_legacy_f32 v4, v1, v2, v3 clamp", QNAN, ONE, 0), 0)


# An f16 result written to the low half keeps the high half of v4 (0xdead); op_sel:[0,0,1] and v_fma_mixhi_f16 write
# the high half and keep the low one (0xbeef).
LO_ZERO = 0xDEAD0000
HI_ZERO = 0x0000BEEF


class ClampNanF16Tests(unittest.TestCase):
    def test_f16_nan_clamps_to_plus_zero(self):
        for nan in (F16_QNAN, F16_NEG_QNAN):
            for line in ("v_add_f16_e64 v4, v1, v2 clamp",
                         "v_mul_f16_e64 v4, v1, v2 clamp",
                         "v_fmac_f16_e64 v4, v1, v2 clamp",
                         "v_fma_f16 v4, v1, v2, v3 clamp"):
                with self.subTest(op=line, nan=hex(nan)):
                    self.assertEqual(run(line, nan, F16_ONE), LO_ZERO)

    def test_f16_high_half_nan_clamps_to_plus_zero(self):
        self.assertEqual(run("v_add_f16_e64 v4, v1, v2 op_sel:[0,0,1] clamp", F16_QNAN, F16_ONE), HI_ZERO)
        self.assertEqual(run("v_fma_mixhi_f16 v4, v1, v2, v3 clamp", QNAN, ONE, 0), HI_ZERO)

    def test_f16_all_nan_med3_clamps_to_plus_zero(self):
        self.assertEqual(run("v_med3_f16 v4, v1, v2, v3 clamp", F16_QNAN, F16_NEG_QNAN, 0x7E01), LO_ZERO)

    def test_conversions_nan_clamp_to_plus_zero(self):
        self.assertEqual(run("v_cvt_f32_f16_e64 v4, v1 clamp", F16_QNAN, 0), 0)
        self.assertEqual(run("v_cvt_f16_f32_e64 v4, v1 clamp", QNAN, 0), LO_ZERO)
        self.assertEqual(run("v_cvt_f16_f32_e64 v4, v1 mul:2 clamp", NEG_QNAN, 0), LO_ZERO)

    def test_mix_ops_nan_clamp_to_plus_zero(self):
        # SHADER-186 measured +0 on gfx1036 for these (docs/shader_validation.md).
        self.assertEqual(run("v_fma_mix_f32 v4, v1, v2, v3 op_sel_hi:[1,1,1] clamp", F16_QNAN, F16_ONE, 0), 0)
        self.assertEqual(run("v_fma_mixlo_f16 v4, v1, v2, v3 clamp", QNAN, ONE, 0), LO_ZERO)

    def test_f16_clamp_controls_unchanged(self):
        self.assertEqual(run("v_add_f16_e64 v4, v1, v2 clamp", F16_ONE, F16_ONE), 0xDEAD0000 | F16_ONE)
        self.assertEqual(run("v_add_f16_e64 v4, v1, v2 clamp", F16_HALF, 0), 0xDEAD0000 | F16_HALF)
        self.assertEqual(run("v_fma_mixlo_f16 v4, v1, v2, v3 clamp", TWO, ONE, 0), 0xDEAD0000 | F16_ONE)


if __name__ == "__main__":
    unittest.main()
