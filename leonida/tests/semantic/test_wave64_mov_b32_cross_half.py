"""Wave64: cross-half s_mov_b32 lane-mask moves (exec_lo/hi, vcc_lo/hi, half-mask SGPRs).

In RDNA2 wave64, exec_lo / vcc_lo / s_lo hold bits for lanes 0..31 and exec_hi / vcc_hi / s_hi hold bits for
lanes 32..63. A cross-half move such as `s_mov_b32 exec_lo, exec_hi` or `s_mov_b32 vcc_hi, exec_lo` copies the
32-bit mask from one half into the other half while leaving the untouched half intact.

Each case builds distinct 64-bit masks in VCC, EXEC, and s[12:13] from push constants, runs one cross-half
`s_mov_b32`, and observes all 64 lanes via EXEC-guarded stores or `v_cndmask_b32` + `s_cbranch_vccz` using
`spirv_mini_interp.run_workgroup`. Standard library only; no LLVM, GPU, or game data.

    python -I -m unittest discover -s tests/semantic -p "test_wave64_mov_b32_cross_half.py" -v
"""
import gc
import os
import struct
import sys
import tempfile
import unittest
import warnings

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TOOLS_DIR = os.environ.get("LEONIDA_TOOLS_DIR") or os.path.join(ROOT, "tools")

sys.dont_write_bytecode = True
for p in (HERE, TOOLS_DIR):
    if p not in sys.path:
        sys.path.insert(0, p)

import rdna2spirv  # noqa: E402
import spirv_asm  # noqa: E402
import spirv_mini_interp as si  # noqa: E402

M32 = 0xFFFFFFFF
STUB_DXBC = b"DXBC" + bytes(24) + struct.pack("<I", 0) + bytes(4)
VSHARP = [0, 0, 0xFFFFFFFF, 0x31016FAC]


def build_mask_into_vcc(lo_sgpr, hi_sgpr):
    """Set VCC[lane] = bit (lane & 31) of (lo_sgpr if lane < 32 else hi_sgpr)."""
    return [
        (f"v_mov_b32_e32 v11, {hi_sgpr}", 4),
        ("v_cmp_lt_u32_e64 vcc, v0, 32", 8),
        (f"v_cndmask_b32_e64 v1, v11, {lo_sgpr}, vcc", 8),
        ("v_and_b32_e32 v2, 31, v0", 4),
        ("v_lshrrev_b32_e32 v3, v2, v1", 4),
        ("v_and_b32_e32 v3, 1, v3", 4),
        ("v_cmp_ne_u32_e64 vcc, 0, v3", 8),
    ]


def translate_cs(name, lines):
    with tempfile.TemporaryDirectory() as tmp:
        os.mkdir(os.path.join(tmp, "disasm"))
        os.mkdir(os.path.join(tmp, "fssm"))
        src = os.path.join(tmp, "disasm", name + ".s")
        with open(src, "w", encoding="utf-8", newline="\n") as f:
            f.write(f"; {name} synthetic\n")
            for text, size in lines:
                f.write(f"\t{text} ; encoding: [{','.join(['0x00'] * size)}]\n")
        with open(os.path.join(tmp, name + ".dxbc"), "wb") as f:
            f.write(STUB_DXBC)
        with open(os.path.join(tmp, "fssm", name + ".fssm"), "wb") as f:
            f.write(bytes(0x138) + struct.pack("<4I", 0x8020, 64, 1, 1))
        saved_w64g = dict(rdna2spirv.W64G_COUNTS)
        try:
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", ResourceWarning)
                spv = rdna2spirv.translate(src)
                gc.collect()
        finally:
            rdna2spirv.W64G_COUNTS.clear()
            rdna2spirv.W64G_COUNTS.update(saved_w64g)
        return spv


def run_cs(spv, s4, s5, s6, s7, s8, s9):
    pc = VSHARP + [s4 & M32, s5 & M32, s6 & M32, s7 & M32, s8 & M32, s9 & M32] + [0] * 22
    mem = si.run_workgroup(
        spv, 64, 64,
        inputs_for=lambda i: {"%wg_id": (0, 0, 0), "%local_id": (i, 0, 0), "%sg_lane": i},
        push_constants=pc, memory={},
    )
    return [mem[4 * l] for l in range(64)]


def mask_bits(mask64):
    return [1 if (mask64 >> l) & 1 else 0 for l in range(64)]


class Wave64MovB32CrossHalf(unittest.TestCase):
    # Distinct bit patterns for EXEC (lo/hi), VCC (lo/hi), and s[12:13] (lo/hi).
    EXEC_LO, EXEC_HI = 0x0000000F, 0x00000003
    VCC_LO, VCC_HI = 0x00000055, 0x000000AA
    S_LO, S_HI = 0x0000001F, 0x000000C0

    def _setup(self):
        # s[12:13] <- (s8, s9), EXEC <- (s4, s5) via vcc, then VCC <- (s6, s7)
        # Note: build s[12:13] and VCC while EXEC is all-ones, then set EXEC last.
        return (
            build_mask_into_vcc("s8", "s9")
            + [("s_and_b64 s[12:13], vcc, exec", 4)]
            + build_mask_into_vcc("s4", "s5")
            + [("s_and_b64 s[14:15], vcc, exec", 4)]
            + build_mask_into_vcc("s6", "s7")
            + [("s_mov_b64 exec, s[14:15]", 4)]
        )

    def _run_exec_case(self, name, mov_insn):
        lines = (
            self._setup()
            + [(mov_insn, 4)]
            + [
                ("v_mov_b32_e32 v6, 0", 4),
                ("s_mov_b64 s[16:17], exec", 4),
                ("s_mov_b64 exec, -1", 4),
                ("v_mov_b32_e32 v6, 0", 4),
                ("s_mov_b64 exec, s[16:17]", 4),
                ("v_mov_b32_e32 v6, 1", 4),
                ("s_mov_b64 exec, -1", 4),
                ("v_lshlrev_b32_e32 v5, 2, v0", 4),
                ("buffer_store_dword v6, v5, s[0:3], 0 offen", 8),
                ("s_endpgm", 4),
            ]
        )
        spv = translate_cs(name, lines)
        binary = spirv_asm.assemble(spv)
        self.assertGreater(len(binary), 0)
        return run_cs(spv, self.EXEC_LO, self.EXEC_HI, self.VCC_LO, self.VCC_HI, self.S_LO, self.S_HI)

    def _run_vcc_case(self, name, mov_insn):
        lines = (
            self._setup()
            + [(mov_insn, 4)]
            + [
                ("s_mov_b64 exec, -1", 4),
                ("v_cndmask_b32_e64 v6, 0, 1, vcc", 8),
                ("v_lshlrev_b32_e32 v5, 2, v0", 4),
                ("buffer_store_dword v6, v5, s[0:3], 0 offen", 8),
                ("s_endpgm", 4),
            ]
        )
        spv = translate_cs(name, lines)
        binary = spirv_asm.assemble(spv)
        self.assertGreater(len(binary), 0)
        return run_cs(spv, self.EXEC_LO, self.EXEC_HI, self.VCC_LO, self.VCC_HI, self.S_LO, self.S_HI)

    def test_cross_half_exec_moves(self):
        cases = (
            ("exec_lo_from_exec_hi", "s_mov_b32 exec_lo, exec_hi", self.EXEC_HI | (self.EXEC_HI << 32)),
            ("exec_hi_from_exec_lo", "s_mov_b32 exec_hi, exec_lo", self.EXEC_LO | (self.EXEC_LO << 32)),
            ("exec_lo_from_vcc_hi", "s_mov_b32 exec_lo, vcc_hi", self.VCC_HI | (self.EXEC_HI << 32)),
            ("exec_hi_from_vcc_lo", "s_mov_b32 exec_hi, vcc_lo", self.EXEC_LO | (self.VCC_LO << 32)),
            ("exec_lo_from_s_hi", "s_mov_b32 exec_lo, s13", self.S_HI | (self.EXEC_HI << 32)),
            ("exec_hi_from_s_lo", "s_mov_b32 exec_hi, s12", self.EXEC_LO | (self.S_LO << 32)),
        )
        for name, insn, expected in cases:
            with self.subTest(case=name, insn=insn):
                self.assertEqual(self._run_exec_case("cs_" + name, insn), mask_bits(expected))

    def test_cross_half_vcc_moves(self):
        cases = (
            ("vcc_lo_from_vcc_hi", "s_mov_b32 vcc_lo, vcc_hi", self.VCC_HI | (self.VCC_HI << 32)),
            ("vcc_hi_from_vcc_lo", "s_mov_b32 vcc_hi, vcc_lo", self.VCC_LO | (self.VCC_LO << 32)),
            ("vcc_lo_from_exec_hi", "s_mov_b32 vcc_lo, exec_hi", self.EXEC_HI | (self.VCC_HI << 32)),
            ("vcc_hi_from_exec_lo", "s_mov_b32 vcc_hi, exec_lo", self.VCC_LO | (self.EXEC_LO << 32)),
            ("vcc_lo_from_s_hi", "s_mov_b32 vcc_lo, s13", self.S_HI | (self.VCC_HI << 32)),
            ("vcc_hi_from_s_lo", "s_mov_b32 vcc_hi, s12", self.VCC_LO | (self.S_LO << 32)),
        )
        for name, insn, expected in cases:
            with self.subTest(case=name, insn=insn):
                self.assertEqual(self._run_vcc_case("cs_" + name, insn), mask_bits(expected))


if __name__ == "__main__":
    unittest.main()
