"""Operand, encoding, and modifier validation for the 5 IEEE-754 division VALU ops in tools/rdna2spirv.py.

Covers:
  - v_div_fixup_f16, v_div_fixup_f32, v_div_fixup_f64
  - v_div_fmas_f32, v_div_fmas_f64

Checks arity, destination register kind/width/modifiers, source widths, sext rejection,
VOP3-only encoding enforcement (rejecting _e32/_sdwa/_dpp), op_sel syntax/arity/bit-value/duplicate
validation on v_div_fixup_f16 and op_sel rejection on f32/f64 forms, unknown/duplicate/conflicting
output modifiers, plus positive controls with live SPIR-V assembly and validation.
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

DIV_1DWORD_OPS = ("v_div_fixup_f16", "v_div_fixup_f32", "v_div_fmas_f32")
DIV_2DWORD_OPS = ("v_div_fixup_f64", "v_div_fmas_f64")
ALL_DIV_OPS = (*DIV_1DWORD_OPS, *DIV_2DWORD_OPS)
STRICT_OMOD_OPS = ("v_div_fixup_f32", "v_div_fmas_f32", "v_div_fmas_f64")


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
    lines = list(body_lines) + ["exp mrt0 v0, v1, v0, v1 done vm", "s_endpgm"]
    return translate_lines(name, lines)


def _valid_insn(op, suffix="", mods=""):
    tail = f" {mods}" if mods else ""
    if op in DIV_1DWORD_OPS:
        return f"{op}{suffix} v0, v1, v2, v3{tail}"
    return f"{op}{suffix} v[0:1], v[2:3], v[4:5], v[6:7]{tail}"


class DivValuPositiveTests(unittest.TestCase):
    def test_valid_forms_translate_and_validate(self):
        for op in ALL_DIV_OPS:
            for suffix in ("", "_e64"):
                with self.subTest(op=op, suffix=suffix):
                    spv = _translate_ps(f"ps_{op}{suffix}", [_valid_insn(op, suffix)])
                    _validate_spirv(spv)

        valid_cases = [
            "v_div_fixup_f16 v0, -v1, |v2|, -|v3| op_sel:[1,0,1,1] clamp mul:2",
            "v_div_fixup_f32 v0, -v1, |v2|, -|v3| clamp mul:2",
            "v_div_fixup_f32_e64 v0, v1, v2, v3 div:2",
            "v_div_fixup_f32 null, v1, v2, v3",
            "v_div_fixup_f64 v[0:1], -v[2:3], |v[4:5]|, v[6:7] clamp mul:4",
            "v_div_fmas_f32 v0, -v1, |v2|, v3 clamp mul:2",
            "v_div_fmas_f32 null, v1, v2, v3",
            "v_div_fmas_f64 v[0:1], -v[2:3], |v[4:5]|, v[6:7] clamp div:2",
        ]
        for idx, insn in enumerate(valid_cases):
            with self.subTest(insn=insn):
                spv = _translate_ps(f"ps_div_pos_{idx}", [insn])
                _validate_spirv(spv)


class DivValuValidationTests(unittest.TestCase):
    def assert_rejected(self, insn):
        with self.assertRaises(rdna2spirv.Unsupported):
            _translate_ps("ps_div_bad", [insn])

    def test_arity_rejected(self):
        for op in DIV_1DWORD_OPS:
            for bad in (f"{op} v0, v1, v2", f"{op} v0, v1, v2, v3, v4"):
                with self.subTest(insn=bad):
                    self.assert_rejected(bad)
        for op in DIV_2DWORD_OPS:
            for bad in (
                f"{op} v[0:1], v[2:3], v[4:5]",
                f"{op} v[0:1], v[2:3], v[4:5], v[6:7], v[8:9]",
            ):
                with self.subTest(insn=bad):
                    self.assert_rejected(bad)

    def test_destination_kind_width_and_modifiers_rejected(self):
        for op in DIV_1DWORD_OPS:
            wrong_dsts = ("s0", "v[0:1]", "vcc_lo", "exec_lo", "-v0", "|v0|", "sext(v0)")
            if op == "v_div_fixup_f16":
                wrong_dsts = (*wrong_dsts, "null")
            for dst in wrong_dsts:
                insn = f"{op} {dst}, v1, v2, v3"
                with self.subTest(insn=insn):
                    self.assert_rejected(insn)
        for op in DIV_2DWORD_OPS:
            for dst in ("s[0:1]", "v0", "v[0:3]", "null", "-v[0:1]", "|v[0:1]|", "sext(v[0:1])"):
                insn = f"{op} {dst}, v[2:3], v[4:5], v[6:7]"
                with self.subTest(insn=insn):
                    self.assert_rejected(insn)

    def test_source_width_and_sext_rejected(self):
        for op in DIV_1DWORD_OPS:
            for bad in (
                f"{op} v0, v[1:2], v3, v4",
                f"{op} v0, v1, v[2:3], v4",
                f"{op} v0, v1, v2, v[3:4]",
                f"{op} v0, s[0:1], v2, v3",
                f"{op} v0, sext(v1), v2, v3",
                f"{op} v0, v1, sext(v2), v3",
                f"{op} v0, v1, v2, sext(v3)",
            ):
                with self.subTest(insn=bad):
                    self.assert_rejected(bad)
        for op in DIV_2DWORD_OPS:
            for bad in (
                f"{op} v[0:1], sext(v[2:3]), v[4:5], v[6:7]",
                f"{op} v[0:1], v[2:3], sext(v[4:5]), v[6:7]",
                f"{op} v[0:1], v[2:3], v[4:5], sext(v[6:7])",
            ):
                with self.subTest(insn=bad):
                    self.assert_rejected(bad)

    def test_vop3_only_encodings_rejected(self):
        for op in ALL_DIV_OPS:
            for enc, extra in (
                ("_e32", ""),
                ("_sdwa", "dst_sel:DWORD dst_unused:UNUSED_PAD src0_sel:DWORD"),
                ("_dpp", "quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf"),
            ):
                insn = _valid_insn(op, enc, extra)
                with self.subTest(insn=insn):
                    self.assert_rejected(insn)

    def test_modifiers_and_opsel_rejected(self):
        for op in ALL_DIV_OPS:
            for mod in ("op_sel_hi:[0,0,0,0]", "dst_sel:DWORD", "glc", "mul:8", "clamp clamp"):
                insn = _valid_insn(op, "", mod)
                with self.subTest(insn=insn):
                    self.assert_rejected(insn)

        for op in STRICT_OMOD_OPS:
            for mod in ("mul:2 mul:4", "mul:2 div:2", "div:2 div:2"):
                insn = _valid_insn(op, "", mod)
                with self.subTest(insn=insn):
                    self.assert_rejected(insn)

        # op_sel is only valid on v_div_fixup_f16
        for op in ("v_div_fixup_f32", "v_div_fixup_f64", "v_div_fmas_f32", "v_div_fmas_f64"):
            with self.subTest(op=op, mod="op_sel"):
                self.assert_rejected(_valid_insn(op, "", "op_sel:[0,0,0,0]"))

        # Invalid op_sel spellings on v_div_fixup_f16
        for bad_sel in (
            "op_sel:bogus",
            "op_sel:[]",
            "op_sel:[0,2,0,0]",
            "op_sel:[0,0,0,0,0]",
            "op_sel:[0,0,0,0] op_sel:[1,0,0,0]",
        ):
            with self.subTest(bad_sel=bad_sel):
                self.assert_rejected(f"v_div_fixup_f16 v0, v1, v2, v3 {bad_sel}")


if __name__ == "__main__":
    unittest.main()
