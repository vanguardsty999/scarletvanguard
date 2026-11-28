"""Semantic tests for SMEM s_load_dword* using `vcc` (s[106:107]) as the 64-bit base pointer.

RDNA2 SMEM `s_load_dword*` takes a 2-SGPR-aligned 64-bit base address in `SBASE`, which hardware encodes as
`sbase * 2`. Register pair `s[106:107]` is `vcc` (`{vcc_lo, vcc_hi}`), emitted by the compiler when VCC is dead
(79 pipeline-library compute shaders: 72 `s_load_dwordx4 sgpr, vcc`, 6 `s_load_dwordx8 sgpr, vcc`, and 1
`s_load_dwordx4 [s104,s105,vcc_lo,vcc_hi], vcc`).

Checks SPIR-V generation, 64-bit address construction from `%vcc`/`%vcc_hi`, destination aliasing when `vcc` is
also written, rejection of `s_buffer_load*` with 2-dword `vcc`, and `spirv-as` + `spirv-val` when on PATH.
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TOOLS_DIR = os.environ.get("LEONIDA_TOOLS_DIR") or os.path.join(ROOT, "tools")

sys.dont_write_bytecode = True
if TOOLS_DIR not in sys.path:
    sys.path.insert(0, TOOLS_DIR)
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import rdna2spirv  # noqa: E402
from shader_ir import parse_line  # noqa: E402
import support  # noqa: E402


def _assemble_and_validate(spvasm: str) -> None:
    spirv_as = shutil.which("spirv-as")
    spirv_val = shutil.which("spirv-val")
    if not (spirv_as and spirv_val):
        return
    with tempfile.TemporaryDirectory() as tmp:
        asm_path = os.path.join(tmp, "s.spvasm")
        spv_path = os.path.join(tmp, "s.spv")
        with open(asm_path, "w", encoding="utf-8", newline="\n") as f:
            f.write(spvasm)
        p1 = subprocess.run([spirv_as, "--target-env", "vulkan1.3", asm_path, "-o", spv_path],
                            capture_output=True, text=True)
        if p1.returncode != 0:
            raise AssertionError(f"spirv-as failed:\n{p1.stderr}")
        p2 = subprocess.run([spirv_val, "--target-env", "vulkan1.3", spv_path],
                            capture_output=True, text=True)
        if p2.returncode != 0:
            raise AssertionError(f"spirv-val failed:\n{p2.stderr}")


def _translate_cs(lines: list[str]) -> str:
    with tempfile.TemporaryDirectory() as tmp:
        disasm_dir = os.path.join(tmp, "disasm")
        fspx_dir = os.path.join(tmp, "fspx")
        os.makedirs(disasm_dir)
        os.makedirs(fspx_dir)
        with open(os.path.join(fspx_dir, "cs_sload_vcc_base.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump({
                "threads": [64, 1, 1],
                "wave32": True,
                "user_sgprs": 8,
                "tgid_enable": [1, 0, 0],
                "tidig_comp_cnt": 0,
                "lds_bytes": 0,
                "rsrc2": 0x90,
            }, f)
        s_path = os.path.join(disasm_dir, "cs_sload_vcc_base.s")
        with open(s_path, "w", encoding="utf-8", newline="\n") as f:
            f.write("; synthetic compute shader test\n")
            for line in lines:
                f.write(line + "\n")
        res = rdna2spirv.translate(s_path)
        return res[0] if isinstance(res, tuple) else res


class SloadVccBaseTest(unittest.TestCase):
    def test_scalar_load_reads_vcc_lo_and_vcc_hi_for_64bit_base(self):
        for op, dst in (
            ("s_load_dword", "s10"),
            ("s_load_dwordx2", "s[10:11]"),
            ("s_load_dwordx4", "s[12:15]"),
            ("s_load_dwordx8", "s[16:23]"),
            ("s_load_dwordx16", "s[24:39]"),
            ("s_load_dwordx4", "[s104,s105,vcc_lo,vcc_hi]"),
        ):
            with self.subTest(op=op, dst=dst):
                g = rdna2spirv.Gen("cs")
                ins = parse_line(f"{op} {dst}, vcc, 0x20")
                g.scalar_load(ins["op"], ins["args"], ins["mods"])
                body = "\n".join(g.body)
                self.assertIn("OpLoad %uint %vcc", body)
                self.assertIn("OpLoad %uint %vcc_hi", body)
                self.assertTrue(g.uses_psb)

    def test_s_buffer_load_with_vcc_base_is_still_rejected(self):
        # `vcc` is a 2-dword pair, not a 4-dword V# buffer resource descriptor.
        g = rdna2spirv.Gen("cs")
        ins = parse_line("s_buffer_load_dword s10, vcc, 0x0")
        with self.assertRaises(rdna2spirv.Unsupported) as ctx:
            g.scalar_load(ins["op"], ins["args"], ins["mods"])
        self.assertIn("s_buffer_load without V#", str(ctx.exception))

    def test_full_compute_shader_with_vcc_base_validates(self):
        spvasm = _translate_cs([
            "\ts_mov_b32 vcc_lo, s0",
            "\ts_mov_b32 vcc_hi, s1",
            "\ts_load_dword s10, vcc, 0x0",
            "\ts_load_dwordx4 s[12:15], vcc, 0x10",
            "\ts_load_dwordx8 s[16:23], vcc, s2 offset:0x40",
            "\ts_load_dwordx4 [s104,s105,vcc_lo,vcc_hi], vcc, 0x80",
            "\ts_waitcnt lgkmcnt(0)",
            "\tv_mov_b32_e32 v1, s10",
            "\tv_add_nc_u32_e32 v1, s12, v1",
            "\tv_add_nc_u32_e32 v1, s16, v1",
            "\tv_add_nc_u32_e32 v1, vcc_lo, v1",
            "\tv_add_nc_u32_e32 v1, vcc_hi, v1",
            "\tbuffer_store_dword v1, v0, s[4:7], 0 offen",
            "\ts_endpgm",
        ])
        self.assertIn("OpLoad %uint %vcc", spvasm)
        self.assertIn("OpLoad %uint %vcc_hi", spvasm)
        _assemble_and_validate(spvasm)

    def test_vcc_base_pointer_half_order_executes(self):
        # Pin vcc_lo as bits [31:0] and vcc_hi as bits [63:32]; swapping or dropping a half reads the wrong address.
        spv = support.translate_lines("ps_sload_vcc_order", [
            "s_mov_b32 vcc_hi, 0x3",
            "s_mov_b32 vcc_lo, 0x2000",
            "s_load_dword s10, vcc, 0x4",
            "s_waitcnt lgkmcnt(0)",
            "v_mov_b32 v4, s10",
            "exp mrt0 v4, v4, v4, v4 done vm",
            "s_endpgm",
        ])
        _assemble_and_validate(spv)
        got = support.mrt0_words(spv, memory={
            0x00000003_00002004: 0xCAFEBABE,
            0x00002000_00000007: 0xDEADBEEF,
            0x00000000_00002004: 0xBAD00001,
        })
        self.assertEqual(got, [0xCAFEBABE, 0xCAFEBABE, 0xCAFEBABE, 0xCAFEBABE])

    def test_vcc_base_aliasing_destination_executes(self):
        # Destination [s104,s105,vcc_lo,vcc_hi] aliases the `vcc` base pair; both base halves must be read first.
        spv = support.translate_lines("ps_sload_vcc_alias", [
            "s_mov_b32 vcc_hi, 0x2",
            "s_mov_b32 vcc_lo, 0x4000",
            "s_load_dwordx4 [s104,s105,vcc_lo,vcc_hi], vcc, 0x10",
            "s_waitcnt lgkmcnt(0)",
            "v_mov_b32 v4, s104",
            "v_mov_b32 v5, s105",
            "v_mov_b32 v6, vcc_lo",
            "v_mov_b32 v7, vcc_hi",
            "exp mrt0 v4, v5, v6, v7 done vm",
            "s_endpgm",
        ])
        _assemble_and_validate(spv)
        got = support.mrt0_words(spv, memory={
            0x00000002_00004010: 0x11223344,
            0x00000002_00004014: 0x55667788,
            0x00000002_00004018: 0x99AABBCC,
            0x00000002_0000401C: 0xDDEEFF00,
        })
        self.assertEqual(got, [0x11223344, 0x55667788, 0x99AABBCC, 0xDDEEFF00])


if __name__ == "__main__":
    unittest.main()
