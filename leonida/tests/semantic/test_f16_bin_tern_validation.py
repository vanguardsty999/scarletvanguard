"""Operand, encoding, and modifier validation tests for the 13 binary/ternary f16 VALU ops.

Covers:
  - VOP2-only literal FMA ops: v_fmamk_f16, v_fmaak_f16
  - VOP2/VOP3 (+ SDWA/DPP) binary ops: v_add_f16, v_sub_f16, v_subrev_f16, v_mul_f16, v_min_f16, v_max_f16
  - VOP3-only ternary ops: v_fma_f16, v_med3_f16, v_min3_f16, v_max3_f16
  - VOP2/VOP3 exponent-scaling op: v_ldexp_f16

Verifies negative rejection (rdna2spirv.Unsupported) across malformed encodings, arities,
destinations, source widths/modifiers, and instruction modifiers, plus positive controls
executed in spirv_mini_interp and validated with spirv-as + spirv-val --target-env vulkan1.2.
Standard library only; no GPU, no game files.
"""
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
TOOLS = os.environ.get("LEONIDA_TOOLS_DIR") or os.path.join(ROOT, "tools")

sys.dont_write_bytecode = True
for p in (TOOLS, HERE):
    if p not in sys.path:
        sys.path.insert(0, p)

import rdna2spirv  # noqa: E402
import spirv_mini_interp as si  # noqa: E402

STUB_DXBC = b"DXBC" + bytes(24) + struct.pack("<I", 0) + bytes(4)
_TMP = None
_SRC_PATH = None

VOP2_FMA_OPS = ("v_fmamk_f16", "v_fmaak_f16")
BIN_F16_OPS = ("v_add_f16", "v_sub_f16", "v_subrev_f16", "v_mul_f16", "v_min_f16", "v_max_f16")
TERN_VOP3_OPS = ("v_fma_f16", "v_med3_f16", "v_min3_f16", "v_max3_f16")
ALL_13_OPS = VOP2_FMA_OPS + BIN_F16_OPS + TERN_VOP3_OPS + ("v_ldexp_f16",)

OLD_DST = 0xABCD1234


def setUpModule():
    global _TMP, _SRC_PATH
    _TMP = tempfile.TemporaryDirectory()
    disasm_dir = os.path.join(_TMP.name, "disasm")
    os.mkdir(disasm_dir)
    _SRC_PATH = os.path.join(disasm_dir, "ps_f16_bin_tern_val.s")
    with open(os.path.join(_TMP.name, "ps_f16_bin_tern_val.dxbc"), "wb") as f:
        f.write(STUB_DXBC)


def tearDownModule():
    global _TMP, _SRC_PATH
    if _TMP is not None:
        _TMP.cleanup()
        _TMP = None
        _SRC_PATH = None


def _translate(lines: list[str]) -> str:
    if _TMP is None:
        setUpModule()
    asm = "; ps_f16_bin_tern_val\n" + "".join(f"\t{ln}\n" for ln in lines)
    with open(_SRC_PATH, "w", encoding="utf-8", newline="\n") as f:
        f.write(asm)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", ResourceWarning)
        return rdna2spirv.translate(_SRC_PATH)


def _translate_single(insn_line: str, regs: dict[int, int] | None = None) -> str:
    preamble = [
        f"v_mov_b32 v0, {OLD_DST:#x}",
        "v_mov_b32 v1, 0x40003c00",  # hi=2.0, lo=1.0
        "v_mov_b32 v2, 0x42003800",  # hi=3.0, lo=0.5
        "v_mov_b32 v3, 0x4400bc00",  # hi=4.0, lo=-1.0
        "s_mov_b32 s0, 0x3c00",
        "s_mov_b32 s1, 0x4000",
    ]
    if regs:
        preamble.extend(f"v_mov_b32 v{r}, {val:#x}" for r, val in regs.items())
    return _translate(preamble + [insn_line, "exp mrt0 v0, v0, v0, v0 done vm", "s_endpgm"])


def _run_ps(insn_line: str, regs: dict[int, int] | None = None) -> tuple[int, str]:
    spv = _translate_single(insn_line, regs=regs)
    return si.run_fragment(spv)["%out_mrt0"][0], spv


def _validate_spv(spv_text: str) -> None:
    spirv_as = shutil.which("spirv-as")
    spirv_val = shutil.which("spirv-val")
    if not spirv_as or not spirv_val:
        raise unittest.SkipTest("spirv-as / spirv-val not on PATH")
    with tempfile.TemporaryDirectory() as td:
        asm_p = os.path.join(td, "shader.spvasm")
        spv_p = os.path.join(td, "shader.spv")
        with open(asm_p, "w", encoding="utf-8", newline="\n") as f:
            f.write(spv_text)
        r_as = subprocess.run([spirv_as, "--target-env", "vulkan1.2", asm_p, "-o", spv_p],
                              capture_output=True, text=True, check=False)
        if r_as.returncode != 0:
            raise AssertionError(f"spirv-as failed:\n{r_as.stderr}\n{r_as.stdout}")
        r_val = subprocess.run([spirv_val, "--target-env", "vulkan1.2", spv_p],
                               capture_output=True, text=True, check=False)
        if r_val.returncode != 0:
            raise AssertionError(f"spirv-val failed:\n{r_val.stderr}\n{r_val.stdout}")


class F16BinTernValidationNegativeTests(unittest.TestCase):
    def _assert_rejected(self, line: str):
        with self.subTest(line=line), self.assertRaises(rdna2spirv.Unsupported):
            _translate_single(line)

    def test_vop2_fma_negative(self):
        for op in VOP2_FMA_OPS:
            args = "v0, v1, 0x3800, v2" if op == "v_fmamk_f16" else "v0, v1, v2, 0x3800"
            bad_lines = [
                # Disallowed encodings
                f"{op}_e64 {args}",
                f"{op}_sdwa {args} dst_sel:WORD_0 dst_unused:UNUSED_PAD",
                f"{op}_dpp {args} quad_perm:[0,1,2,3]",
                # Wrong arity
                f"{op} v0, v1, v2",
                f"{op} {args}, v3",
                # Bad destinations
                f"{op} s0, v1, 0x3800, v2",
                f"{op} v[0:1], v1, 0x3800, v2",
                f"{op} vcc_lo, v1, 0x3800, v2",
                f"{op} -v0, v1, 0x3800, v2",
                f"{op} |v0|, v1, 0x3800, v2",
                f"{op} sext(v0), v1, 0x3800, v2",
                # Wide sources
                f"{op} v0, v[1:2], 0x3800, v2",
                f"{op} v0, s[0:1], 0x3800, v2",
                f"{op} v0, v1, 0x3800, v[2:3]",
                # Source modifiers (neg, abs, sext) on bare and _e32
                f"{op} v0, -v1, 0x3800, v2",
                f"{op}_e32 v0, |v1|, 0x3800, v2",
                f"{op} v0, v1, neg(0.5), v2",
                f"{op} v0, v1, |0.5|, v2",
                f"{op} v0, v1, 0x3800, -v2",
                f"{op} v0, sext(v1), 0x3800, v2",
                f"{op} v0, v1, 0x3800, sext(v2)",
                # Disallowed instruction modifiers on bare and _e32
                f"{op} {args} clamp",
                f"{op}_e32 {args} clamp",
                f"{op} {args} mul:2",
                f"{op} {args} div:2",
                f"{op} {args} op_sel:[0,0,0,0]",
                f"{op} {args} dst_sel:WORD_0",
                f"{op} {args} bogus_mod",
            ]
            for line in bad_lines:
                self._assert_rejected(line)

    def test_binary_f16_negative(self):
        for op in BIN_F16_OPS:
            bad_lines = [
                # Wrong arity
                f"{op} v0, v1",
                f"{op} v0, v1, v2, v3",
                f"{op}_e32 v0, v1",
                f"{op}_e64 v0, v1, v2, v3",
                f"{op}_sdwa v0, v1 dst_sel:WORD_0",
                f"{op}_dpp v0, v1 quad_perm:[0,1,2,3]",
                # Bad destinations
                f"{op} s0, v1, v2",
                f"{op}_e64 v[0:1], v1, v2",
                f"{op}_e64 vcc_lo, v1, v2",
                f"{op}_e64 -v0, v1, v2",
                f"{op}_e64 |v0|, v1, v2",
                f"{op}_e64 sext(v0), v1, v2",
                f"{op}_dpp -v0, v1, v2 quad_perm:[0,1,2,3]",
                # Wide sources and sext
                f"{op}_e64 v0, v[1:2], v2",
                f"{op}_e64 v0, v1, s[0:1]",
                f"{op}_e64 v0, sext(v1), v2",
                f"{op}_e64 v0, v1, sext(v2)",
                f"{op}_sdwa v0, sext(v1), v2 dst_sel:WORD_0 dst_unused:UNUSED_PAD src0_sel:WORD_0 src1_sel:WORD_0",
                f"{op}_sdwa v0, v1, sext(v2) dst_sel:WORD_0 dst_unused:UNUSED_PAD src0_sel:WORD_0 src1_sel:WORD_0",
                f"{op}_dpp v0, sext(v1), v2 quad_perm:[0,1,2,3]",
                f"{op}_dpp v0, v1, sext(v2) quad_perm:[0,1,2,3]",
                # _e32 rejects neg/abs and all mods
                f"{op}_e32 v0, -v1, v2",
                f"{op}_e32 v0, |v1|, v2",
                f"{op}_e32 v0, v1, -v2",
                f"{op}_e32 v0, v1, |v2|",
                f"{op}_e32 v0, v1, v2 clamp",
                f"{op}_e32 v0, v1, v2 mul:2",
                f"{op}_e32 v0, v1, v2 op_sel:[0,0,0]",
                f"{op}_e32 v0, v1, v2 dst_sel:WORD_0",
                f"{op}_e32 v0, v1, v2 bogus",
                # _dpp rejects output/non-DPP mods
                f"{op}_dpp v0, v1, v2 quad_perm:[0,1,2,3] clamp",
                f"{op}_dpp v0, v1, v2 quad_perm:[0,1,2,3] mul:2",
                f"{op}_dpp v0, v1, v2 quad_perm:[0,1,2,3] div:2",
                f"{op}_dpp v0, v1, v2 quad_perm:[0,1,2,3] op_sel:[0,0,0]",
                f"{op}_dpp v0, v1, v2 quad_perm:[0,1,2,3] dst_sel:WORD_0",
                f"{op}_dpp v0, v1, v2 quad_perm:[0,1,2,3] bogus",
                # _sdwa rejects bad/duplicate/conflicting/unknown mods
                f"{op}_sdwa v0, v1, v2 src0_sel:BOGUS",
                f"{op}_sdwa v0, v1, v2 dst_sel:QWORD",
                f"{op}_sdwa v0, v1, v2 dst_unused:UNUSED_ZERO",
                f"{op}_sdwa v0, v1, v2 src0_sel:WORD_0 src0_sel:WORD_1",
                f"{op}_sdwa v0, v1, v2 dst_sel:WORD_0 dst_sel:WORD_1",
                f"{op}_sdwa v0, v1, v2 dst_unused:UNUSED_PAD dst_unused:UNUSED_PRESERVE",
                f"{op}_sdwa v0, v1, v2 clamp clamp",
                f"{op}_sdwa v0, v1, v2 mul:2 div:2",
                f"{op}_sdwa v0, v1, v2 op_sel:[0,0,0]",
                f"{op}_sdwa v0, v1, v2 bogus",
                # VOP3 (bare / _e64) rejects malformed/duplicate/conflicting/unknown mods
                f"{op}_e64 v0, v1, v2 op_sel:[]",
                f"{op}_e64 v0, v1, v2 op_sel:[0,1]",
                f"{op}_e64 v0, v1, v2 op_sel:[0,0,0,0]",
                f"{op}_e64 v0, v1, v2 op_sel:[0,2,0]",
                f"{op}_e64 v0, v1, v2 op_sel:1",
                f"{op}_e64 v0, v1, v2 op_sel:[0,0,0] op_sel:[1,0,0]",
                f"{op}_e64 v0, v1, v2 clamp clamp",
                f"{op}_e64 v0, v1, v2 mul:2 mul:2",
                f"{op}_e64 v0, v1, v2 mul:2 div:2",
                f"{op}_e64 v0, v1, v2 mul:8",
                f"{op}_e64 v0, v1, v2 dst_sel:WORD_0",
                f"{op} v0, v1, v2 op_sel_hi:[0,0]",
                f"{op} v0, v1, v2 bogus",
            ]
            for line in bad_lines:
                self._assert_rejected(line)

    def test_ternary_vop3_negative(self):
        for op in TERN_VOP3_OPS:
            bad_lines = [
                # Disallowed encodings
                f"{op}_e32 v0, v1, v2, v3",
                f"{op}_sdwa v0, v1, v2, v3 dst_sel:WORD_0 dst_unused:UNUSED_PAD",
                f"{op}_dpp v0, v1, v2, v3 quad_perm:[0,1,2,3]",
                # Wrong arity
                f"{op} v0, v1, v2",
                f"{op}_e64 v0, v1, v2, v3, v1",
                # Bad destinations
                f"{op} s0, v1, v2, v3",
                f"{op}_e64 v[0:1], v1, v2, v3",
                f"{op} -v0, v1, v2, v3",
                f"{op} |v0|, v1, v2, v3",
                f"{op} sext(v0), v1, v2, v3",
                # Wide sources and sext
                f"{op} v0, v[1:2], v2, v3",
                f"{op} v0, v1, s[0:1], v3",
                f"{op} v0, v1, v2, v[2:3]",
                f"{op} v0, sext(v1), v2, v3",
                f"{op} v0, v1, sext(v2), v3",
                f"{op} v0, v1, v2, sext(v3)",
                # Malformed/duplicate/conflicting/unknown VOP3 mods
                f"{op} v0, v1, v2, v3 op_sel:[]",
                f"{op} v0, v1, v2, v3 op_sel:[0,0,0]",
                f"{op} v0, v1, v2, v3 op_sel:[0,0,0,0,0]",
                f"{op} v0, v1, v2, v3 op_sel:[0,1,2,0]",
                f"{op}_e64 v0, v1, v2, v3 op_sel:[0,0,0,0] op_sel:[1,0,0,0]",
                f"{op}_e64 v0, v1, v2, v3 clamp clamp",
                f"{op}_e64 v0, v1, v2, v3 mul:2 mul:4",
                f"{op}_e64 v0, v1, v2, v3 div:2 div:2",
                f"{op} v0, v1, v2, v3 dst_sel:WORD_0",
                f"{op} v0, v1, v2, v3 op_sel_hi:[0,0,0]",
                f"{op} v0, v1, v2, v3 neg_lo:[1,0,0]",
                f"{op} v0, v1, v2, v3 bogus",
            ]
            for line in bad_lines:
                self._assert_rejected(line)

    def test_ldexp_f16_negative(self):
        bad_lines = [
            # Disallowed encodings
            "v_ldexp_f16_sdwa v0, v1, v2 dst_sel:WORD_0 dst_unused:UNUSED_PAD",
            "v_ldexp_f16_dpp v0, v1, v2 quad_perm:[0,1,2,3]",
            # Wrong arity
            "v_ldexp_f16 v0, v1",
            "v_ldexp_f16_e64 v0, v1, v2, v3",
            # Bad destinations
            "v_ldexp_f16 s0, v1, v2",
            "v_ldexp_f16_e64 v[0:1], v1, v2",
            "v_ldexp_f16 -v0, v1, v2",
            "v_ldexp_f16 |v0|, v1, v2",
            "v_ldexp_f16 sext(v0), v1, v2",
            # Wide sources and sext
            "v_ldexp_f16 v0, v[1:2], v2",
            "v_ldexp_f16 v0, v1, s[0:1]",
            "v_ldexp_f16 v0, sext(v1), v2",
            "v_ldexp_f16 v0, v1, sext(v2)",
            # Reject neg/abs on integer exponent a[2]
            "v_ldexp_f16 v0, v1, -v2",
            "v_ldexp_f16 v0, v1, |v2|",
            "v_ldexp_f16_e64 v0, v1, neg(2)",
            "v_ldexp_f16_e64 v0, v1, |2|",
            # _e32 rejects neg/abs and all mods
            "v_ldexp_f16_e32 v0, -v1, v2",
            "v_ldexp_f16_e32 v0, |v1|, v2",
            "v_ldexp_f16_e32 v0, v1, v2 clamp",
            "v_ldexp_f16_e32 v0, v1, v2 mul:2",
            "v_ldexp_f16_e32 v0, v1, v2 op_sel:[0,0,0]",
            # Malformed/duplicate/conflicting/unknown VOP3 mods
            "v_ldexp_f16 v0, v1, v2 op_sel:[]",
            "v_ldexp_f16 v0, v1, v2 op_sel:[0,1]",
            "v_ldexp_f16 v0, v1, v2 op_sel:[0,0,0,0]",
            "v_ldexp_f16 v0, v1, v2 op_sel:[0,2,0]",
            "v_ldexp_f16_e64 v0, v1, v2 op_sel:[0,0,0] op_sel:[0,1,0]",
            "v_ldexp_f16_e64 v0, v1, v2 clamp clamp",
            "v_ldexp_f16_e64 v0, v1, v2 mul:2 div:2",
            "v_ldexp_f16_e64 v0, v1, v2 mul:4 mul:4",
            "v_ldexp_f16_e64 v0, v1, v2 dst_sel:WORD_0",
            "v_ldexp_f16_e64 v0, v1, v2 bogus",
        ]
        for line in bad_lines:
            self._assert_rejected(line)


class F16BinTernValidationPositiveTests(unittest.TestCase):
    def test_vop2_fma_positive(self):
        # v1.lo=1.0 (0x3c00), v2.lo=0.5 (0x3800)
        cases = [
            # 1.0 * 2.0 + 0.5 = 2.5 (0x4100), hi preserved (0xabcd)
            ("v_fmamk_f16 v0, v1, 2.0, v2", 0xABCD4100),
            # 1.0 * (-0.5) + 0.5 = 0.0 (0x0000), plain negative float literal preserved
            ("v_fmamk_f16_e32 v0, v1, -0.5, v2", 0xABCD0000),
            # 1.0 * 0.5 + 2.0 = 2.5 (0x4100)
            ("v_fmaak_f16 v0, v1, v2, 2.0", 0xABCD4100),
            ("v_fmaak_f16_e32 v0, v1, v2, 0x4000", 0xABCD4100),
        ]
        for line, want in cases:
            with self.subTest(line=line):
                got, spv = _run_ps(line)
                self.assertEqual(got, want)
                _validate_spv(spv)

    def test_binary_f16_positive(self):
        # v1 = 0x40003c00 (hi=2.0, lo=1.0), v2 = 0x42003800 (hi=3.0, lo=0.5), v0 = 0xabcd1234
        cases = [
            # _e32 forms
            ("v_add_f16_e32 v0, v1, v2", 0xABCD3E00),       # 1.0 + 0.5 = 1.5
            ("v_sub_f16_e32 v0, v1, v2", 0xABCD3800),       # 1.0 - 0.5 = 0.5
            ("v_subrev_f16_e32 v0, v1, v2", 0xABCDB800),    # 0.5 - 1.0 = -0.5
            ("v_mul_f16_e32 v0, v1, v2", 0xABCD3800),       # 1.0 * 0.5 = 0.5
            ("v_min_f16_e32 v0, v1, v2", 0xABCD3800),       # min(1.0, 0.5) = 0.5
            ("v_max_f16_e32 v0, v1, v2", 0xABCD3C00),       # max(1.0, 0.5) = 1.0
            # VOP3 (_e64 / bare) with op_sel, neg/abs, clamp, omod
            ("v_add_f16_e64 v0, v1, v2 op_sel:[1,1,1] clamp", 0x3C001234),          # 2.0 + 3.0 -> clamp 1.0 in hi
            ("v_sub_f16 v0, v1, -v2 op_sel:[0,0,0] mul:2", 0xABCD3E00),             # 1.0 - (-0.5) = 1.5 (omod no-op)
            ("v_subrev_f16_e64 v0, -v1, v2 op_sel:[0,0,1]", 0x3E001234),            # 0.5 - (-1.0) = 1.5 in hi
            ("v_mul_f16_e64 v0, |v1|, v2 op_sel:[1,0,0] div:2", 0xABCD3C00),        # |2.0| * 0.5 = 1.0
            ("v_min_f16_e64 v0, -v1, v2 op_sel:[1,1,0]", 0xABCDC000),               # min(-2.0, 3.0) = -2.0
            ("v_max_f16_e64 v0, v1, -v2 op_sel:[0,1,1] clamp", 0x3C001234),         # max(1.0, -3.0) = 1.0 in hi
            # _sdwa forms
            ("v_add_f16_sdwa v0, v1, v2 dst_sel:WORD_1 dst_unused:UNUSED_PRESERVE src0_sel:WORD_0 src1_sel:WORD_0",
             0x3E001234),
            ("v_sub_f16_sdwa v0, v1, v2 dst_sel:WORD_0 dst_unused:UNUSED_PAD src0_sel:WORD_1 src1_sel:WORD_0 clamp",
             0x00003C00),
            ("v_mul_f16_sdwa v0, -v1, v2 dst_sel:WORD_1 dst_unused:UNUSED_PRESERVE src0_sel:WORD_1 src1_sel:WORD_0 mul:2",
             0xBC001234),
            ("v_min_f16_sdwa v0, v1, v2 dst_sel:WORD_0 dst_unused:UNUSED_PAD src0_sel:WORD_0 src1_sel:WORD_1",
             0x00003C00),
            ("v_max_f16_sdwa v0, v1, v2 dst_sel:WORD_0 dst_unused:UNUSED_PAD src0_sel:WORD_0 src1_sel:WORD_1",
             0x00004200),
        ]
        for line, want in cases:
            with self.subTest(line=line):
                got, spv = _run_ps(line)
                self.assertEqual(got, want)
                _validate_spv(spv)

    def test_binary_f16_dpp_positive(self):
        # Verify all 6 binary f16 _dpp forms translate and pass spirv-as + spirv-val.
        for op in BIN_F16_OPS:
            line = f"{op}_dpp v0, v1, v2 quad_perm:[0,1,2,3] row_mask:0xf bank_mask:0xf"
            with self.subTest(line=line):
                spv = _translate_single(line)
                _validate_spv(spv)

    def test_ternary_vop3_positive(self):
        # v1 = (2.0, 1.0), v2 = (3.0, 0.5), v3 = (4.0, -1.0)
        cases = [
            ("v_fma_f16 v0, v1, v2, v3", 0xABCDB800),                               # 1.0*0.5 + (-1.0) = -0.5
            ("v_fma_f16_e64 v0, v1, v2, |v3| op_sel:[1,0,0,1] clamp mul:2", 0x3C001234),  # 2.0*0.5 + 1.0 = 2.0 -> 1.0
            ("v_med3_f16 v0, v1, v2, v3", 0xABCD3800),                              # med3(1.0, 0.5, -1.0) = 0.5
            ("v_med3_f16_e64 v0, v1, v2, v3 op_sel:[1,1,1,1]", 0x42001234),         # med3(2.0, 3.0, 4.0) = 3.0 in hi
            ("v_min3_f16 v0, v1, v2, v3", 0xABCDBC00),                              # min3(1.0, 0.5, -1.0) = -1.0
            ("v_min3_f16_e64 v0, v1, v2, |v3| op_sel:[0,0,0,1] div:2", 0x38001234), # min3(1.0, 0.5, 1.0) = 0.5 in hi
            ("v_max3_f16 v0, v1, v2, v3", 0xABCD3C00),                              # max3(1.0, 0.5, -1.0) = 1.0
            ("v_max3_f16_e64 v0, v1, v2, v3 op_sel:[1,1,1,0] clamp", 0xABCD3C00),   # max3(2.0, 3.0, 4.0) -> 1.0
        ]
        for line, want in cases:
            with self.subTest(line=line):
                got, spv = _run_ps(line)
                self.assertEqual(got, want)
                _validate_spv(spv)

    def test_ldexp_f16_positive(self):
        # v1 = 0x40003c00 (hi=2.0, lo=1.0), v2 = 0x00020001 (hi=2, lo=1)
        regs = {2: 0x00020001}
        cases = [
            ("v_ldexp_f16 v0, v1, v2", 0xABCD4000),                                 # 1.0 * 2^1 = 2.0
            ("v_ldexp_f16_e32 v0, v1, v2", 0xABCD4000),                             # VOP2 _e32 form
            ("v_ldexp_f16_e64 v0, -v1, -2", 0xABCDB400),                            # -1.0 * 2^-2 = -0.25 (plain neg imm)
            ("v_ldexp_f16_e64 v0, |v1|, v2 op_sel:[1,1,1] clamp mul:2", 0x3C001234),# 2.0 * 2^2 = 8.0 -> clamp 1.0 in hi
        ]
        for line, want in cases:
            with self.subTest(line=line):
                got, spv = _run_ps(line, regs=regs)
                self.assertEqual(got, want)
                _validate_spv(spv)


if __name__ == "__main__":
    unittest.main()
