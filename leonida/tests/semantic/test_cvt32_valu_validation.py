"""Operand, encoding, and modifier validation for 6 32-bit float/int conversion VALU ops.

Covers:
  - int -> f32: v_cvt_f32_u32, v_cvt_f32_i32
  - f32 -> int: v_cvt_u32_f32, v_cvt_i32_f32, v_cvt_flr_i32_f32, v_cvt_rpi_i32_f32

Checks arity, single-VGPR (or null) destination without modifiers, single-dword source width,
float neg/abs rejection on integer inputs and _e32 forms, sext restriction to _sdwa integer inputs,
SDWA selector/dst_unused/duplicate validation, DPP non-DPP modifier rejection, and clamp/omod
restriction to non-_e32 float-destination forms without duplicates.
Standard library only; no game files.
"""
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import support  # noqa: E402,F401
from support import translate_lines  # noqa: E402
import rdna2spirv  # noqa: E402

INT_TO_F32 = ("v_cvt_f32_u32", "v_cvt_f32_i32")
F32_TO_INT = ("v_cvt_u32_f32", "v_cvt_i32_f32", "v_cvt_flr_i32_f32", "v_cvt_rpi_i32_f32")
ALL_OPS = (*INT_TO_F32, *F32_TO_INT)


def _validate_spirv(spv_text):
    spirv_as = shutil.which("spirv-as")
    spirv_val = shutil.which("spirv-val")
    if not spirv_as or not spirv_val:
        return
    with tempfile.TemporaryDirectory() as tmp:
        asm_path = os.path.join(tmp, "shader.spvasm")
        bin_path = os.path.join(tmp, "shader.spv")
        with open(asm_path, "w", encoding="utf-8", newline="\n") as f:
            f.write(spv_text)
        r1 = subprocess.run(
            [spirv_as, "--target-env", "vulkan1.2", asm_path, "-o", bin_path],
            capture_output=True, text=True,
        )
        if r1.returncode != 0:
            raise AssertionError(f"spirv-as failed:\n{r1.stderr}")
        r2 = subprocess.run(
            [spirv_val, "--target-env", "vulkan1.2", bin_path],
            capture_output=True, text=True,
        )
        if r2.returncode != 0:
            raise AssertionError(f"spirv-val failed:\n{r2.stderr}")


def _translate_ps(name, body_lines):
    lines = list(body_lines) + ["exp mrt0 v0, v0, v0, v0 done vm", "s_endpgm"]
    return translate_lines(name, lines)


class Cvt32ValuPositiveTests(unittest.TestCase):
    def test_valid_encodings_and_modifiers_translate_and_validate(self):
        for op in ALL_OPS:
            for suffix in ("", "_e32", "_e64"):
                with self.subTest(op=op, suffix=suffix):
                    spv = _translate_ps(f"ps_{op}{suffix}", [f"{op}{suffix} v0, v1"])
                    _validate_spirv(spv)
            with self.subTest(op=op, enc="_sdwa"):
                spv = _translate_ps(
                    f"ps_{op}_sdwa",
                    [f"{op}_sdwa v0, v1 dst_sel:DWORD dst_unused:UNUSED_PAD src0_sel:DWORD"],
                )
                _validate_spirv(spv)
            with self.subTest(op=op, enc="_dpp"):
                spv = _translate_ps(
                    f"ps_{op}_dpp",
                    [f"{op}_dpp v0, v1 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf"],
                )
                _validate_spirv(spv)

        valid_cases = [
            "v_cvt_f32_u32_e64 v0, v1 clamp mul:2",
            "v_cvt_f32_i32_e64 v0, -5 clamp div:2",
            "v_cvt_f32_u32_sdwa v0, sext(v1) mul:2 clamp dst_sel:DWORD dst_unused:UNUSED_PAD src0_sel:BYTE_0",
            "v_cvt_f32_i32_sdwa v0, sext(v1) dst_sel:DWORD dst_unused:UNUSED_PAD src0_sel:WORD_1",
            "v_cvt_u32_f32_e64 v0, |v1|",
            "v_cvt_i32_f32_e64 v0, -v1",
            "v_cvt_flr_i32_f32_e64 v0, -|v1|",
            "v_cvt_rpi_i32_f32_e64 v0, -v1",
            "v_cvt_i32_f32_sdwa v0, -v1 dst_sel:WORD_0 dst_unused:UNUSED_PRESERVE src0_sel:DWORD",
            "v_cvt_i32_f32_dpp v0, -v1 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf",
            "v_cvt_i32_f32_e32 v0, -1.5",
            "v_cvt_f32_i32_e32 v0, -7",
            "v_cvt_u32_f32 null, v1",
        ]
        for idx, insn in enumerate(valid_cases):
            with self.subTest(insn=insn):
                spv = _translate_ps(f"ps_cvt32_pos_{idx}", [insn])
                _validate_spirv(spv)


class Cvt32ValuValidationTests(unittest.TestCase):
    def assert_rejected(self, insn):
        with self.assertRaises(rdna2spirv.Unsupported):
            _translate_ps("ps_cvt32_bad", [insn])

    def test_arity_and_width_rejected(self):
        for op in ALL_OPS:
            for bad in (
                f"{op} v0",
                f"{op} v0, v1, v2",
                f"{op}_e32 v0",
                f"{op}_e64 v0, v1, v2",
                f"{op}_sdwa v0 dst_sel:DWORD",
                f"{op}_dpp v0 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf",
                f"{op} v0, v[1:2]",
                f"{op} v0, s[0:1]",
                f"{op}_sdwa v0, v[1:2] dst_sel:DWORD",
                f"{op}_dpp v0, v[1:2] quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf",
            ):
                with self.subTest(insn=bad):
                    self.assert_rejected(bad)

    def test_destination_kind_width_and_modifiers_rejected(self):
        for op in ALL_OPS:
            for dst in ("s0", "v[0:1]", "vcc_lo", "exec_lo", "-v0", "|v0|", "sext(v0)"):
                insn = f"{op} {dst}, v1"
                with self.subTest(insn=insn):
                    self.assert_rejected(insn)

    def test_source_modifiers_rejected(self):
        # Integer-input ops never accept float neg/abs, and reject sext outside _sdwa
        for op in INT_TO_F32:
            for bad in (
                f"{op} v0, -v1",
                f"{op} v0, |v1|",
                f"{op}_e64 v0, -v1",
                f"{op}_e64 v0, |v1|",
                f"{op}_e64 v0, neg(1)",
                f"{op}_e64 v0, abs(1)",
                f"{op}_sdwa v0, -v1 dst_sel:DWORD src0_sel:DWORD",
                f"{op}_sdwa v0, |v1| dst_sel:DWORD src0_sel:DWORD",
                f"{op}_dpp v0, -v1 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf",
                f"{op}_dpp v0, |v1| quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf",
                f"{op} v0, sext(v1)",
                f"{op}_e32 v0, sext(v1)",
                f"{op}_e64 v0, sext(v1)",
                f"{op}_dpp v0, sext(v1) quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf",
            ):
                with self.subTest(insn=bad):
                    self.assert_rejected(bad)

        # Float-input ops never accept sext, and reject neg/abs on _e32
        for op in F32_TO_INT:
            for bad in (
                f"{op} v0, sext(v1)",
                f"{op}_e64 v0, sext(v1)",
                f"{op}_sdwa v0, sext(v1) dst_sel:DWORD src0_sel:DWORD",
                f"{op}_dpp v0, sext(v1) quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf",
                f"{op}_e32 v0, -v1",
                f"{op}_e32 v0, |v1|",
            ):
                with self.subTest(insn=bad):
                    self.assert_rejected(bad)

    def test_instruction_modifiers_rejected(self):
        for op in ALL_OPS:
            for bad in (
                f"{op}_e32 v0, v1 clamp",
                f"{op}_e32 v0, v1 mul:2",
                f"{op}_e64 v0, v1 op_sel:[0,0]",
                f"{op}_e64 v0, v1 dst_sel:DWORD",
                f"{op}_e64 v0, v1 glc",
                f"{op}_sdwa v0, v1 src1_sel:DWORD",
                f"{op}_sdwa v0, v1 src0_sel:BOGUS",
                f"{op}_sdwa v0, v1 dst_sel:BOGUS",
                f"{op}_sdwa v0, v1 dst_unused:BOGUS",
                f"{op}_sdwa v0, v1 src0_sel:BYTE_0 src0_sel:BYTE_1",
                f"{op}_sdwa v0, v1 dst_sel:BYTE_0 dst_sel:WORD_0",
                f"{op}_dpp v0, v1 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf clamp",
                f"{op}_dpp v0, v1 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf mul:2",
                f"{op}_dpp v0, v1 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf src0_sel:DWORD",
            ):
                with self.subTest(insn=bad):
                    self.assert_rejected(bad)

        # Float-dst ops reject duplicate clamp and duplicate/conflicting omod
        for op in INT_TO_F32:
            for bad in (
                f"{op}_e64 v0, v1 clamp clamp",
                f"{op}_e64 v0, v1 mul:2 mul:4",
                f"{op}_e64 v0, v1 mul:2 div:2",
                f"{op}_sdwa v0, v1 mul:2 div:2 dst_sel:DWORD src0_sel:DWORD",
            ):
                with self.subTest(insn=bad):
                    self.assert_rejected(bad)

        # Integer-dst ops reject clamp and omod on every form
        for op in F32_TO_INT:
            for bad in (
                f"{op} v0, v1 clamp",
                f"{op} v0, v1 mul:2",
                f"{op}_e64 v0, v1 clamp",
                f"{op}_e64 v0, v1 div:2",
                f"{op}_sdwa v0, v1 clamp dst_sel:DWORD src0_sel:DWORD",
                f"{op}_sdwa v0, v1 mul:2 dst_sel:DWORD src0_sel:DWORD",
            ):
                with self.subTest(insn=bad):
                    self.assert_rejected(bad)


if __name__ == "__main__":
    unittest.main()
