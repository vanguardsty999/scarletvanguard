"""Validation and semantic tests for 8 32-bit integer 2-source VALU ops in tools/rdna2spirv.py.

Covers:
  VOP3-only: v_mul_lo_u32, v_mul_hi_u32, v_mul_hi_i32, v_bcnt_u32_b32
  VOP2/VOP3: v_min_u32, v_max_u32, v_min_i32, v_max_i32

Verifies that invalid destinations (non-VGPR / multi-VGPR), multi-register sources,
operand modifiers (neg/abs/sext where illegal), instruction modifiers (clamp, mul:*,
div:*, op_sel:*, unknown), unsupported _dpp/_sdwa forms, and wrong arity raise
rdna2spirv.Unsupported, while valid forms (including plain negative integer immediates
and valid _sdwa/_dpp on VOP2 min/max) assemble, pass spirv-val --target-env vulkan1.2,
and match reference semantics in spirv_mini_interp.
"""
import gc
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
import warnings

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TOOLS_DIR = os.environ.get("LEONIDA_TOOLS_DIR") or os.path.join(ROOT, "tools")

sys.dont_write_bytecode = True
if TOOLS_DIR not in sys.path:
    sys.path.insert(0, TOOLS_DIR)
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import rdna2spirv  # noqa: E402
import spirv_mini_interp as si  # noqa: E402

MASK32 = 0xFFFFFFFF
STUB_DXBC = b"DXBC" + bytes(24) + struct.pack("<I", 0) + bytes(4)

VOP3_ONLY_OPS = (
    "v_mul_lo_u32",
    "v_mul_hi_u32",
    "v_mul_hi_i32",
    "v_bcnt_u32_b32",
)
VOP2_MINMAX_OPS = (
    "v_min_u32",
    "v_max_u32",
    "v_min_i32",
    "v_max_i32",
)
ALL_OPS = VOP3_ONLY_OPS + VOP2_MINMAX_OPS

SPIRV_AS = shutil.which("spirv-as")
SPIRV_VAL = shutil.which("spirv-val")

_TMP_DIR = None
_SRC_PATH = None


def setUpModule():
    global _TMP_DIR, _SRC_PATH
    _TMP_DIR = tempfile.TemporaryDirectory()
    disasm_dir = os.path.join(_TMP_DIR.name, "disasm")
    os.mkdir(disasm_dir)
    _SRC_PATH = os.path.join(disasm_dir, "ps_int32_valu_val.s")
    with open(os.path.join(_TMP_DIR.name, "ps_int32_valu_val.dxbc"), "wb") as f:
        f.write(STUB_DXBC)


def tearDownModule():
    global _TMP_DIR, _SRC_PATH
    if _TMP_DIR is not None:
        gc.collect()
        _TMP_DIR.cleanup()
        _TMP_DIR = None
        _SRC_PATH = None


def translate_lines(lines) -> str:
    if _TMP_DIR is None:
        setUpModule()
    asm = "; test_int32_valu_validation\n" + "".join(f"\t{ln}\n" for ln in lines) + "\ts_endpgm\n"
    with open(_SRC_PATH, "w", encoding="utf-8", newline="\n") as f:
        f.write(asm)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", ResourceWarning)
        return rdna2spirv.translate(_SRC_PATH)


def validate_spirv(spvasm: str) -> None:
    if not SPIRV_AS or not SPIRV_VAL:
        raise unittest.SkipTest("spirv-as / spirv-val not found on PATH")
    spvasm_path = os.path.join(_TMP_DIR.name, "check.spvasm")
    spv_path = os.path.join(_TMP_DIR.name, "check.spv")
    with open(spvasm_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(spvasm)
    res_as = subprocess.run(
        [SPIRV_AS, "--target-env", "vulkan1.2", spvasm_path, "-o", spv_path],
        capture_output=True,
        text=True,
    )
    if res_as.returncode != 0:
        raise AssertionError(f"spirv-as failed:\n{res_as.stderr}")
    res_val = subprocess.run(
        [SPIRV_VAL, "--target-env", "vulkan1.2", spv_path],
        capture_output=True,
        text=True,
    )
    if res_val.returncode != 0:
        raise AssertionError(f"spirv-val failed:\n{res_val.stderr}")


def run_mrt0(lines, regs=("v0", "v0", "v0", "v0"), *, validate=True) -> list[int]:
    spv = translate_lines(list(lines) + [f"exp mrt0 {', '.join(regs)} done vm"])
    if validate:
        validate_spirv(spv)
    return list(si.run_fragment(spv)["%out_mrt0"])


def s32(x: int) -> int:
    v = x & MASK32
    return v - (1 << 32) if (v & 0x80000000) else v


def ref_op(op: str, x: int, y: int) -> int:
    ux, uy = x & MASK32, y & MASK32
    if op == "v_mul_lo_u32":
        return (ux * uy) & MASK32
    if op == "v_mul_hi_u32":
        return ((ux * uy) >> 32) & MASK32
    if op == "v_mul_hi_i32":
        return ((s32(ux) * s32(uy)) >> 32) & MASK32
    if op == "v_min_u32":
        return min(ux, uy)
    if op == "v_max_u32":
        return max(ux, uy)
    if op == "v_min_i32":
        return min(s32(ux), s32(uy)) & MASK32
    if op == "v_max_i32":
        return max(s32(ux), s32(uy)) & MASK32
    if op == "v_bcnt_u32_b32":
        return (ux.bit_count() + uy) & MASK32
    raise ValueError(f"unknown op {op!r}")


class Int32ValuNegativeValidationTests(unittest.TestCase):
    """Invalid destinations, operand widths, operand/instruction modifiers, forms, and arity."""

    def _assert_unsupported(self, body_lines):
        lines = [
            "v_mov_b32 v0, 1",
            "v_mov_b32 v1, 2",
            "v_mov_b32 v2, 3",
            "v_mov_b32 v3, 4",
            "s_mov_b32 s0, 5",
            "s_mov_b32 s1, 6",
            "s_mov_b32 s2, 7",
            "s_mov_b32 s3, 8",
            *body_lines,
            "exp mrt0 v0, v0, v0, v0 done vm",
        ]
        with self.assertRaises(rdna2spirv.Unsupported):
            translate_lines(lines)

    def test_non_vgpr_destinations_rejected(self):
        bad_dsts = ("s0", "vcc_lo", "null", "exec_lo", "m0")
        for op in ALL_OPS:
            for dst in bad_dsts:
                with self.subTest(op=op, dst=dst):
                    self._assert_unsupported([f"{op} {dst}, v1, v2"])

    def test_multi_register_destinations_rejected(self):
        for op in ALL_OPS:
            for dst in ("v[0:1]", "s[0:1]"):
                with self.subTest(op=op, dst=dst):
                    self._assert_unsupported([f"{op} {dst}, v1, v2"])

    def test_multi_register_sources_rejected(self):
        for op in ALL_OPS:
            for args in ("v0, v[1:2], v3", "v0, v1, v[2:3]", "v0, s[2:3], v1", "v0, v1, s[2:3]"):
                with self.subTest(op=op, args=args):
                    self._assert_unsupported([f"{op} {args}"])

    def test_operand_neg_abs_sext_rejected_on_plain_form(self):
        bad_arg_lists = (
            "-v0, v1, v2",
            "|v0|, v1, v2",
            "sext(v0), v1, v2",
            "v0, -v1, v2",
            "v0, |v1|, v2",
            "v0, abs(v1), v2",
            "v0, sext(v1), v2",
            "v0, v1, -v2",
            "v0, v1, |v2|",
            "v0, v1, abs(v2)",
            "v0, v1, sext(v2)",
            "v0, neg(1), v2",
            "v0, abs(1), v2",
            "v0, v1, neg(2)",
        )
        for op in ALL_OPS:
            for args in bad_arg_lists:
                with self.subTest(op=op, args=args):
                    self._assert_unsupported([f"{op} {args}"])

    def test_instruction_modifiers_rejected_on_plain_and_e64(self):
        bad_mods = ("clamp", "mul:2", "mul:4", "div:2", "op_sel:[1,0,0]", "bogus_mod")
        for op in ALL_OPS:
            for suffix in ("", "_e64"):
                for mod in bad_mods:
                    with self.subTest(op=op + suffix, mod=mod):
                        self._assert_unsupported([f"{op}{suffix} v0, v1, v2 {mod}"])

    def test_vop3_only_rejects_dpp_and_sdwa(self):
        for op in VOP3_ONLY_OPS:
            with self.subTest(op=op, form="_dpp"):
                self._assert_unsupported([f"{op}_dpp v0, v1, v2 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf"])
            with self.subTest(op=op, form="_sdwa"):
                self._assert_unsupported([
                    f"{op}_sdwa v0, v1, v2 dst_sel:DWORD dst_unused:UNUSED_PAD src0_sel:DWORD src1_sel:DWORD"
                ])

    def test_minmax_sdwa_rejects_bad_mods_and_operand_mods(self):
        for op in VOP2_MINMAX_OPS:
            bad_cases = (
                f"{op}_sdwa s0, v1, v2 dst_sel:DWORD dst_unused:UNUSED_PAD src0_sel:DWORD src1_sel:DWORD",
                f"{op}_sdwa v0, v1, v2 dst_sel:DWORD clamp",
                f"{op}_sdwa v0, v1, v2 dst_sel:DWORD mul:2",
                f"{op}_sdwa v0, v1, v2 dst_sel:DWORD div:2",
                f"{op}_sdwa v0, v1, v2 dst_sel:DWORD op_sel:[0,0,0]",
                f"{op}_sdwa v0, v1, v2 dst_sel:DWORD bogus_mod",
                f"{op}_sdwa v0, v1, v2 dst_sel:BAD",
                f"{op}_sdwa v0, v1, v2 dst_unused:BAD",
                f"{op}_sdwa sext(v0), v1, v2 dst_sel:DWORD src0_sel:BYTE_0 src1_sel:BYTE_0",
                f"{op}_sdwa v0, -v1, v2 dst_sel:DWORD src0_sel:BYTE_0 src1_sel:BYTE_0",
                f"{op}_sdwa v0, |v1|, v2 dst_sel:DWORD src0_sel:BYTE_0 src1_sel:BYTE_0",
                f"{op}_sdwa v0, v1, -v2 dst_sel:DWORD src0_sel:BYTE_0 src1_sel:BYTE_0",
                f"{op}_sdwa v0, v1, |v2| dst_sel:DWORD src0_sel:BYTE_0 src1_sel:BYTE_0",
            )
            for insn in bad_cases:
                with self.subTest(insn=insn):
                    self._assert_unsupported([insn])

    def test_minmax_dpp_rejects_non_dpp_mods_and_operand_mods(self):
        for op in VOP2_MINMAX_OPS:
            bad_cases = (
                f"{op}_dpp s0, v1, v2 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf",
                f"{op}_dpp v0, v1, v2 quad_perm:[1,0,3,2] clamp",
                f"{op}_dpp v0, v1, v2 quad_perm:[1,0,3,2] mul:2",
                f"{op}_dpp v0, v1, v2 quad_perm:[1,0,3,2] div:2",
                f"{op}_dpp v0, v1, v2 quad_perm:[1,0,3,2] op_sel:[0,0,0]",
                f"{op}_dpp v0, v1, v2 quad_perm:[1,0,3,2] bogus_mod",
                f"{op}_dpp v0, -v1, v2 quad_perm:[1,0,3,2]",
                f"{op}_dpp v0, |v1|, v2 quad_perm:[1,0,3,2]",
                f"{op}_dpp v0, sext(v1), v2 quad_perm:[1,0,3,2]",
                f"{op}_dpp v0, v1, -v2 quad_perm:[1,0,3,2]",
                f"{op}_dpp v0, v1, |v2| quad_perm:[1,0,3,2]",
                f"{op}_dpp v0, v1, sext(v2) quad_perm:[1,0,3,2]",
                f"{op}_dpp v0, v1, v[2:3] quad_perm:[1,0,3,2]",
            )
            for insn in bad_cases:
                with self.subTest(insn=insn):
                    self._assert_unsupported([insn])

    def test_wrong_arity_rejected(self):
        for op in ALL_OPS:
            for insn in (
                op,
                f"{op} v0",
                f"{op} v0, v1",
                f"{op} v0, v1, v2, v3",
            ):
                with self.subTest(insn=insn):
                    self._assert_unsupported([insn])


class Int32ValuPositiveControlTests(unittest.TestCase):
    """Positive controls verified with spirv-as, spirv-val --target-env vulkan1.2, and spirv_mini_interp."""

    def test_all_eight_ops_vgpr_and_negative_immediate(self):
        x = 0x80000013
        y = 0xFFFFFFF9  # -7
        for op in ALL_OPS:
            with self.subTest(op=op):
                got_vv, got_neg_imm, got_e64, _ = run_mrt0([
                    f"v_mov_b32 v1, {x:#x}",
                    f"v_mov_b32 v2, {y:#x}",
                    f"{op} v10, v1, v2",
                    f"{op} v11, -7, v1",
                    f"{op}_e64 v12, v1, -7",
                    "v_mov_b32 v13, 0",
                ], ("v10", "v11", "v12", "v13"))
                self.assertEqual(got_vv, ref_op(op, x, y))
                self.assertEqual(got_neg_imm, ref_op(op, y, x))
                self.assertEqual(got_e64, ref_op(op, x, y))

    def test_minmax_sdwa_positive_controls(self):
        v1 = 0x8001007F
        v2 = 0x0002FFFE
        words = run_mrt0([
            f"v_mov_b32 v1, {v1:#x}",
            f"v_mov_b32 v2, {v2:#x}",
            "v_min_i32_sdwa v10, sext(v1), sext(v2) dst_sel:DWORD dst_unused:UNUSED_PAD src0_sel:BYTE_3 src1_sel:BYTE_0",
            "v_max_i32_sdwa v11, sext(v1), sext(v2) dst_sel:DWORD dst_unused:UNUSED_PAD src0_sel:BYTE_3 src1_sel:BYTE_0",
            "v_min_u32_sdwa v12, v1, v2 dst_sel:DWORD dst_unused:UNUSED_PAD src0_sel:BYTE_3 src1_sel:BYTE_0",
            "v_max_u32_sdwa v13, v1, v2 dst_sel:DWORD dst_unused:UNUSED_PAD src0_sel:BYTE_3 src1_sel:BYTE_0",
        ], ("v10", "v11", "v12", "v13"))
        self.assertEqual(words, [0xFFFFFF80, 0xFFFFFFFE, 0x00000080, 0x000000FE])

    def test_minmax_dpp_positive_controls_validate(self):
        for op in VOP2_MINMAX_OPS:
            with self.subTest(op=op):
                spv = translate_lines([
                    "v_mov_b32 v1, 0x12345678",
                    "v_mov_b32 v2, 0x87654321",
                    f"{op}_dpp v0, v1, v2 quad_perm:[1,0,3,2] row_mask:0xf bank_mask:0xf",
                    "exp mrt0 v0, v0, v0, v0 done vm",
                ])
                validate_spirv(spv)


if __name__ == "__main__":
    unittest.main()
