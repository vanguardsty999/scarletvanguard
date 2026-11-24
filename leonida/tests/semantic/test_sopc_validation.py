"""Validation and positive-control semantic tests for all 18 RDNA2 SOPC instructions.

Covers the exact 18 RDNA2 SOPC mnemonics (tools/rdna2_isa_tables.py SOPC):
  - 12 32-bit scalar compares: s_cmp_{eq,lg,gt,ge,lt,le}_{i32,u32}
  - 2  64-bit scalar compares: s_cmp_{eq,lg}_u64
  - 4  scalar bit tests:       s_bitcmp{0,1}_{b32,b64}

Verifies:
  - Exact ISA table parity with tools/rdna2_isa_tables.SOPC.
  - Rejection (Unsupported) of nonexistent s_cmp_* / s_bitcmp* mnemonics.
  - Rejection of encoding suffixes (_e32/_e64/_sdwa/_dpp), instruction modifiers, wrong arity,
    operand modifiers (neg/abs/sext), VGPRs/vlist/slist, and mismatched 32-bit vs 64-bit widths.
  - Positive controls across all 18 ops and valid scalar operand kinds (including negative integer
    immediates, float immediates, m0, null, vcc_lo/hi, exec_lo/hi, scc/src_scc, 64-bit pairs,
    vcc, exec), validated with spirv-as + spirv-val --target-env vulkan1.2 and executed in
    spirv_mini_interp.
"""
from __future__ import annotations

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
if HERE not in sys.path:
    sys.path.insert(0, HERE)
if TOOLS_DIR not in sys.path:
    sys.path.insert(0, TOOLS_DIR)

import rdna2_isa_tables  # noqa: E402
import rdna2spirv  # noqa: E402
import shader_ir  # noqa: E402
import spirv_mini_interp as si  # noqa: E402

STUB_DXBC = b"DXBC" + bytes(24) + struct.pack("<I", 0) + bytes(4)
VSHARP = [0, 0, 0xFFFFFFFF, 0x31016FAC]

ALL_CMP32 = sorted(rdna2spirv.SOPC_CMP32_OPS)
ALL_CMP64 = sorted(rdna2spirv.SOPC_CMP64_OPS)
ALL_BITCMP = sorted(rdna2spirv.SOPC_BITCMP_OPS)
ALL_SOPC = ALL_CMP32 + ALL_CMP64 + ALL_BITCMP


def _valid_pair_asm(op: str) -> tuple[str, str]:
    if op in rdna2spirv.SOPC_CMP64_OPS:
        return "s[4:5]", "s[6:7]"
    if op.endswith("_b64"):
        return "s[4:5]", "s6"
    return "s4", "s5"


def _run_insn_line(line: str) -> rdna2spirv.Gen:
    ins = shader_ir.parse_line(line)
    assert ins is not None, f"failed to parse line: {line!r}"
    g = rdna2spirv.Gen("cs")
    g.insn(ins)
    return g


def _require_or_skip_spirv_tools(test_case: unittest.TestCase) -> tuple[str, str]:
    spirv_as = shutil.which("spirv-as")
    spirv_val = shutil.which("spirv-val")
    if spirv_as and spirv_val:
        return spirv_as, spirv_val
    msg = "spirv-as and spirv-val must be on PATH"
    if os.environ.get("LEONIDA_REQUIRE_SPIRV_TOOLS") == "1":
        test_case.fail(msg)
    test_case.skipTest(msg)


def _translate_cs(name: str, asm_lines: list[str], threads: int = 64) -> str:
    with tempfile.TemporaryDirectory() as tmp:
        os.mkdir(os.path.join(tmp, "disasm"))
        os.mkdir(os.path.join(tmp, "fssm"))
        s_path = os.path.join(tmp, "disasm", f"{name}.s")
        with open(s_path, "w", encoding="utf-8", newline="\n") as f:
            f.write(f"; {name} synthetic\n")
            for ln in asm_lines:
                f.write(f"\t{ln}\n")
        with open(os.path.join(tmp, f"{name}.dxbc"), "wb") as f:
            f.write(STUB_DXBC)
        with open(os.path.join(tmp, "fssm", f"{name}.fssm"), "wb") as f:
            f.write(bytes(0x138) + struct.pack("<4I", 0x8090, threads, 1, 1))
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", ResourceWarning)
            spv = rdna2spirv.translate(s_path)
            gc.collect()
        return spv


def _validate_spirv(name: str, spv: str, spirv_as: str, spirv_val: str) -> None:
    with tempfile.TemporaryDirectory() as tmp:
        spvasm = os.path.join(tmp, f"{name}.spvasm")
        spvbin = os.path.join(tmp, f"{name}.spv")
        with open(spvasm, "w", encoding="utf-8", newline="\n") as f:
            f.write(spv)
        r1 = subprocess.run(
            [spirv_as, "--target-env", "vulkan1.2", spvasm, "-o", spvbin],
            capture_output=True, text=True,
        )
        assert r1.returncode == 0, f"spirv-as failed:\n{r1.stderr}"
        r2 = subprocess.run(
            [spirv_val, "--target-env", "vulkan1.2", spvbin],
            capture_output=True, text=True,
        )
        assert r2.returncode == 0, f"spirv-val failed:\n{r2.stderr}"


def _store_scc(slot: int) -> list[str]:
    return [
        "s_cselect_b32 s20, 1, 0",
        "v_mov_b32_e32 v4, s20",
        f"v_mov_b32_e32 v5, {slot * 4}",
        "buffer_store_dword v4, v5, s[0:3], 0 offen",
    ]


class TestSopcIsaParity(unittest.TestCase):
    def test_exact_18_sopc_ops_match_isa_table(self):
        isa_sopc = {entry[0] for entry in rdna2_isa_tables.SOPC.values()}
        translator_sopc = rdna2spirv.SOPC_CMP32_OPS | rdna2spirv.SOPC_CMP64_OPS | rdna2spirv.SOPC_BITCMP_OPS
        self.assertEqual(len(isa_sopc), 18)
        self.assertEqual(translator_sopc, isa_sopc)


class TestSopcNegativeValidation(unittest.TestCase):
    def test_nonexistent_mnemonics_rejected(self):
        bad_lines = [
            "s_cmp_eq_f32 s0, s1",
            "s_cmp_lt_f16 s0, s1",
            "s_cmp_eq_f64 s[0:1], s[2:3]",
            "s_cmp_ne_i32 s0, s1",
            "s_cmp_ne_u32 s0, s1",
            "s_cmp_f_u32 s0, s1",
            "s_cmp_t_i32 s0, s1",
            "s_cmp_eq_u16 s0, s1",
            "s_cmp_lt_u64 s[0:1], s[2:3]",
            "s_cmp_le_u64 s[0:1], s[2:3]",
            "s_cmp_gt_u64 s[0:1], s[2:3]",
            "s_cmp_ge_u64 s[0:1], s[2:3]",
            "s_cmp_eq_i64 s[0:1], s[2:3]",
            "s_cmp_lg_i64 s[0:1], s[2:3]",
            "s_bitcmp2_b32 s0, s1",
            "s_bitcmp0_b16 s0, s1",
            "s_bitcmp1_b16 s0, s1",
            "s_bitcmp_b32 s0, s1",
        ]
        for line in bad_lines:
            with self.subTest(line=line):
                with self.assertRaises(rdna2spirv.Unsupported):
                    _run_insn_line(line)

    def test_invalid_encoding_suffixes_rejected_all_18(self):
        for op in ALL_SOPC:
            a0, a1 = _valid_pair_asm(op)
            cases = [
                f"{op}_e32 {a0}, {a1}",
                f"{op}_e64 {a0}, {a1}",
                f"{op}_sdwa {a0}, {a1}",
                f"{op}_sdwa {a0}, {a1} src0_sel:WORD_0",
                f"{op}_dpp {a0}, {a1} quad_perm:[0,1,2,3]",
                f"{op}_dpp v0, v1 quad_perm:[0,1,2,3]",
            ]
            for line in cases:
                with self.subTest(op=op, line=line):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(line)

    def test_instruction_modifiers_rejected_all_18(self):
        for op in ALL_SOPC:
            a0, a1 = _valid_pair_asm(op)
            for mod in ("clamp", "mul:2", "op_sel:[0,0]", "glc"):
                line = f"{op} {a0}, {a1} {mod}"
                with self.subTest(op=op, mod=mod):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(line)

    def test_wrong_arity_rejected_all_18(self):
        for op in ALL_SOPC:
            a0, a1 = _valid_pair_asm(op)
            for line in (op, f"{op} {a0}", f"{op} {a0}, {a1}, s8"):
                with self.subTest(op=op, line=line):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(line)

    def test_operand_modifiers_rejected_all_18(self):
        for op in ALL_SOPC:
            a0, a1 = _valid_pair_asm(op)
            bad_a0 = [f"-{a0}", f"|{a0}|", f"neg({a0})", f"abs({a0})", f"sext({a0})", "neg(1)", "|1|"]
            bad_a1 = [f"-{a1}", f"|{a1}|", f"neg({a1})", f"abs({a1})", f"sext({a1})", "neg(1)", "|1|"]
            if op in rdna2spirv.SOPC_CMP32_OPS or op in ("s_bitcmp0_b32", "s_bitcmp1_b32"):
                bad_a0.extend(["neg(1.0)", "|1.0|"])
                bad_a1.extend(["neg(1.0)", "|1.0|"])
            for x in bad_a0:
                with self.subTest(op=op, slot=0, arg=x):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(f"{op} {x}, {a1}")
            for y in bad_a1:
                with self.subTest(op=op, slot=1, arg=y):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(f"{op} {a0}, {y}")

    def test_vgpr_vlist_slist_ttmp_rejected_all_18(self):
        bad_operands = ["v0", "v[0:1]", "[v0, v1]", "[s0, s1]", "ttmp0"]
        for op in ALL_SOPC:
            a0, a1 = _valid_pair_asm(op)
            for bad in bad_operands:
                with self.subTest(op=op, slot=0, arg=bad):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(f"{op} {bad}, {a1}")
                with self.subTest(op=op, slot=1, arg=bad):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(f"{op} {a0}, {bad}")

    def test_width_mismatch_rejected_32bit_ops(self):
        wide_operands = ["s[0:1]", "s[0:3]", "vcc", "exec"]
        for op in ALL_CMP32 + ["s_bitcmp0_b32", "s_bitcmp1_b32"]:
            for bad in wide_operands:
                with self.subTest(op=op, slot=0, arg=bad):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(f"{op} {bad}, s5")
                with self.subTest(op=op, slot=1, arg=bad):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(f"{op} s4, {bad}")

    def test_width_mismatch_rejected_64bit_cmp(self):
        bad_64 = ["s0", "s[0:3]", "vcc_lo", "vcc_hi", "exec_lo", "exec_hi", "m0", "scc", "src_scc", "1.0"]
        for op in ALL_CMP64:
            for bad in bad_64:
                with self.subTest(op=op, slot=0, arg=bad):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(f"{op} {bad}, s[6:7]")
                with self.subTest(op=op, slot=1, arg=bad):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(f"{op} s[4:5], {bad}")

    def test_width_mismatch_rejected_64bit_bitcmp(self):
        bad_src0 = ["s0", "s[0:3]", "vcc_lo", "vcc_hi", "exec_lo", "exec_hi", "m0", "scc", "src_scc", "1.0"]
        bad_src1 = ["s[0:1]", "s[0:3]", "vcc", "exec"]
        for op in ("s_bitcmp0_b64", "s_bitcmp1_b64"):
            for bad in bad_src0:
                with self.subTest(op=op, slot=0, arg=bad):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(f"{op} {bad}, 5")
            for bad in bad_src1:
                with self.subTest(op=op, slot=1, arg=bad):
                    with self.assertRaises(rdna2spirv.Unsupported):
                        _run_insn_line(f"{op} s[4:5], {bad}")


def _positive_control_program() -> tuple[list[str], list[tuple[str, int]]]:
    asm: list[str] = [
        "s_mov_b32 m0, 0x00000005",
        "s_mov_b32 s8, -1",
        "s_mov_b32 s9, 0x3f800000",   # 1.0f bits (bit 29 is 1, bit 30 is 0)
        "s_mov_b32 s10, 0xbf800000",  # -1.0f bits
        "s_mov_b32 s12, 0x00000010",  # bit 4 set
        "s_mov_b32 s13, 0x00000100",  # bit 40 set in s[12:13]
        "s_mov_b32 vcc_lo, s12",
        "s_mov_b32 vcc_hi, s13",
    ]
    expected: list[tuple[str, int]] = []

    def add_case(label: str, insn: str, want: int):
        slot = len(expected)
        asm.append(insn)
        asm.extend(_store_scc(slot))
        expected.append((label, want))

    # 12 32-bit scalar compares covering sgpr, null, m0, vcc_lo/hi, exec_lo/hi, scc/src_scc,
    # plain negative integer immediates, positive immediates, and float immediates.
    add_case("s_cmp_eq_i32 sgpr/neg_imm", "s_cmp_eq_i32 s8, -1", 1)
    add_case("s_cmp_lg_i32 sgpr/neg_imm", "s_cmp_lg_i32 s8, -16", 1)
    add_case("s_cmp_gt_i32 null/neg_imm", "s_cmp_gt_i32 null, -1", 1)
    add_case("s_cmp_ge_i32 m0/imm", "s_cmp_ge_i32 m0, 5", 1)
    add_case("s_cmp_lt_i32 neg_imm/null", "s_cmp_lt_i32 -1, null", 1)
    add_case("s_cmp_le_i32 scc/imm", "s_cmp_le_i32 scc, 1", 1)
    add_case("s_cmp_eq_u32 sgpr/fimm", "s_cmp_eq_u32 s9, 1.0", 1)
    add_case("s_cmp_eq_u32 sgpr/neg_fimm", "s_cmp_eq_u32 s10, -1.0", 1)
    add_case("s_cmp_lg_u32 vcc_lo/null", "s_cmp_lg_u32 vcc_lo, null", 1)
    add_case("s_cmp_gt_u32 vcc_hi/m0", "s_cmp_gt_u32 vcc_hi, m0", 1)
    add_case("s_cmp_ge_u32 exec_lo/exec_hi", "s_cmp_ge_u32 exec_lo, exec_hi", 1)
    add_case("s_cmp_lt_u32 src_scc/m0", "s_cmp_lt_u32 src_scc, m0", 1)
    add_case("s_cmp_le_u32 m0/vcc_lo", "s_cmp_le_u32 m0, vcc_lo", 1)

    # 2 64-bit scalar compares covering s[a:b], vcc, exec, null, 0, -1, hex imm.
    add_case("s_cmp_eq_u64 pair/vcc", "s_cmp_eq_u64 s[12:13], vcc", 1)
    add_case("s_cmp_lg_u64 pair/null", "s_cmp_lg_u64 s[12:13], null", 1)
    add_case("s_cmp_eq_u64 null/0", "s_cmp_eq_u64 null, 0", 1)
    add_case("s_cmp_eq_u64 exec/-1", "s_cmp_eq_u64 exec, -1", 1)
    add_case("s_cmp_lg_u64 exec/0", "s_cmp_lg_u64 exec, 0", 1)

    # 4 bit-test ops covering b32 and b64 with valid scalar operands.
    add_case("s_bitcmp1_b32 s12/4", "s_bitcmp1_b32 s12, 4", 1)
    add_case("s_bitcmp0_b32 m0/1", "s_bitcmp0_b32 m0, 1", 1)
    add_case("s_bitcmp1_b32 fimm/29", "s_bitcmp1_b32 1.0, 29", 1)
    add_case("s_bitcmp0_b32 fimm/30", "s_bitcmp0_b32 1.0, 30", 1)
    add_case("s_bitcmp0_b32 null/0", "s_bitcmp0_b32 null, 0", 1)
    add_case("s_bitcmp1_b32 exec_lo/0", "s_bitcmp1_b32 exec_lo, 0", 1)
    add_case("s_bitcmp1_b64 pair/40", "s_bitcmp1_b64 s[12:13], 40", 1)
    add_case("s_bitcmp0_b64 pair/41", "s_bitcmp0_b64 s[12:13], 41", 1)
    add_case("s_bitcmp1_b64 vcc/40", "s_bitcmp1_b64 vcc, 40", 1)
    add_case("s_bitcmp1_b64 exec/63", "s_bitcmp1_b64 exec, 63", 1)
    add_case("s_bitcmp0_b64 null/35", "s_bitcmp0_b64 null, 35", 1)
    add_case("s_bitcmp1_b64 neg_imm/63", "s_bitcmp1_b64 -1, 63", 1)
    add_case("s_bitcmp1_b64 pair/m0", "s_bitcmp1_b64 s[12:13], m0", 0)

    asm.append("s_endpgm")
    return asm, expected


class TestSopcPositiveControls(unittest.TestCase):
    def test_all_18_ops_and_valid_scalar_operands_interp(self):
        asm, expected = _positive_control_program()
        spv = _translate_cs("cs_sopc_pos", asm, threads=64)
        mem = si.run_workgroup(
            spv, 64, 64,
            inputs_for=lambda i: {"%wg_id": (0, 0, 0), "%local_id": (i, 0, 0), "%sg_lane": i},
            push_constants=VSHARP + [0] * 28,
            memory={},
        )
        for slot, (label, want) in enumerate(expected):
            with self.subTest(case=label):
                self.assertEqual(mem[slot * 4], want, label)

    def test_all_18_ops_and_valid_scalar_operands_spirv_val(self):
        spirv_as, spirv_val = _require_or_skip_spirv_tools(self)
        asm, _ = _positive_control_program()
        spv = _translate_cs("cs_sopc_pos", asm, threads=64)
        _validate_spirv("cs_sopc_pos", spv, spirv_as, spirv_val)


if __name__ == "__main__":
    unittest.main()
