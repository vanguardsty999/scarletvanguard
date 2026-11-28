"""SHADER-768: v_writelane_b32 keeps its destination's other lanes, for read_before_write_vgprs and reg_reads_writes.

v_writelane_b32 vD, sSRC, sLANE writes one lane of vD and keeps every other lane's value (RDNA2 ISA; the translator's
emission loads the old vD and selects per lane). read_before_write_vgprs counted it as a full write, so a VGPR whose
first touch is a writelane and whose kept lanes are then read was not reported. Counting every writelane destination
as a read is too much the other way (@shdann on #4297): a VS that spills an SGPR into one lane of v40 around the
fetch call, and reads it back with v_readlane, would be rejected with `fetch mapping`. So the kept lanes are
*carried*: they become a read when something other than v_readlane_b32 reads the register before a full write.
reg_reads_writes (per slice) counts the destination as read. Standard library only; no GPU, no game files.
"""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.dont_write_bytecode = True
sys.path.insert(0, os.environ.get("LEONIDA_TOOLS_DIR") or os.path.join(ROOT, "tools"))
sys.path.insert(0, HERE)

import rdna2spirv  # noqa: E402
import shader_ir  # noqa: E402
import support  # noqa: E402


def parse(*lines):
    return [shader_ir.parse_line(ln) for ln in lines]


def rbw(*lines):
    return rdna2spirv.read_before_write_vgprs(parse(*lines))


def translate(name, lines):
    asm = support.translate_lines(name, lines)
    return asm[0] if isinstance(asm, tuple) else asm


VS_SPILL = ["v_writelane_b32 v40, s2, 0", "s_swappc_b64 s[4:5], s[4:5]", "v_readlane_b32 s2, v40, 0",
            "v_mov_b32_e32 v1, s2", "exp pos0 v1, v1, v1, v1 done", "s_endpgm"]


class WritelaneReadBeforeWrite(unittest.TestCase):
    def test_kept_lanes_read_afterwards_are_a_read(self):
        self.assertEqual(rbw("v_writelane_b32 v5, s1, 3", "v_mov_b32 v6, v5"), {5})
        self.assertEqual(rbw("v_writelane_b32 v5, s1, 3", "v_writelane_b32 v5, s2, 4", "v_add_nc_u32 v6, v5, v5"),
                         {5})

    def test_carried_only_is_not_a_read(self):
        self.assertEqual(rbw("v_writelane_b32 v5, s1, 3"), set())
        self.assertEqual(rbw("v_writelane_b32 v5, s1, 3", "v_readlane_b32 s6, v5, 3"), set())   # the spill pattern

    def test_a_full_write_ends_the_carry(self):
        self.assertEqual(rbw("v_writelane_b32 v5, s1, 3", "v_mov_b32 v5, 0", "v_mov_b32 v6, v5"), set())

    def test_written_first_is_not_a_read(self):
        self.assertEqual(rbw("v_mov_b32 v5, 0", "v_writelane_b32 v5, s1, 3", "v_mov_b32 v6, v5"), set())

    def test_lane_operands_are_scalar(self):
        self.assertEqual(rbw("v_writelane_b32 v7, 0x1234, s2", "v_mov_b32 v8, v7"), {7})

    def test_reg_reads_writes_counts_the_destination(self):
        self.assertEqual(rdna2spirv.reg_reads_writes(parse("v_writelane_b32 v5, s1, 3")), ({5}, {5}))

    def test_readlane_and_fmac_unchanged(self):
        self.assertEqual(rbw("v_readlane_b32 s5, v9, 3"), {9})
        self.assertEqual(rbw("v_fmac_f32 v4, v1, v2"), {1, 2, 4})

    def test_vs_spill_around_the_fetch_call_translates(self):
        # shdann's listing: v40 holds a spilled SGPR in lane 0 across the fetch call; it is no vertex attribute
        self.assertIn("OpEntryPoint", translate("vs_writelane_spill", VS_SPILL))
        self.assertIn("OpEntryPoint", translate("vs_writelane_spill2", ["v_mov_b32_e32 v40, 0"] + VS_SPILL))

    def test_ps_kept_lanes_of_v0_get_the_barycentric(self):
        # the other lanes of v0 keep the hardware's barycentric i and v_mov reads them, so v0 is a PS input
        asm = translate("ps_writelane_v0", ["v_writelane_b32 v0, s2, 0", "v_mov_b32_e32 v20, v0",
                                            "exp mrt0 v20, v20, v20, v20 done vm", "s_endpgm"])
        self.assertIn("BaryCoord", asm)


if __name__ == "__main__":
    unittest.main()
